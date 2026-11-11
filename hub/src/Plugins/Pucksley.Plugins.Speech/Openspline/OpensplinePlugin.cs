using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Pucksley.Sdk;

namespace Pucksley.Plugins.Speech.Openspline;

public sealed class OpensplineSettings
{
    [Setting("Service address", Order = 1, Placeholder = "http://localhost:7860",
        Help = "Where Openspline runs: a PC with an NVIDIA GPU (docs.openspline.dev/get-started/self-host). From another machine, use that PC's address.")]
    public string Url { get; set; } = "http://localhost:7860";

    // The ready-made faces: Openspline/Faces/face-NN.jpg (and -thumb.jpg), embedded in this assembly
    [Setting("Face", Order = 2, Pictures = true, Help = "Who's on the puck while Pucksley speaks.",
        Options = ["face-01", "face-02", "face-03", "face-04", "face-05", "face-06", "face-07", "face-08", "face-09", "face-10",
                   "face-11", "face-12", "face-13", "face-14", "face-15", "face-16", "face-17", "face-18", "face-19", "face-20"])]
    public string Face { get; set; } = "face-01";

    [Setting("Your own picture", Order = 5, Advanced = true, Placeholder = "a .png or .jpg file",
        Help = "Instead of the faces above: a file path to a front-on photo or picture, head and shoulders, looking straight at the camera, on a plain black background (it's shown in a circle on black).")]
    public string Portrait { get; set; } = "";

    [Setting("Start command", Order = 3, Placeholder = "wsl.exe -d Ubuntu-24.04 --cd ~ -- sh .local/share/openspline/run.sh",
        Help = "Runs Openspline when it isn't answering, and stops it again when the hub stops (so the GPU is only busy while Pucksley runs). Empty: start it yourself.")]
    public string StartCommand { get; set; } = "";

    [Setting("Stop command", Order = 6, Advanced = true, Placeholder = "wsl.exe -d Ubuntu-24.04 -- pkill -f openspline_server",
        Help = "Stops Openspline however it was started (say, by the hub before it restarted), so one that gets stuck can be restarted by itself or with the Restart button. Empty: only an Openspline the hub started can be restarted.")]
    public string StopCommand { get; set; } = "";
}

/// <summary>
/// A talking face from Openspline: the reply's speech goes to the avatar service and comes back
/// as frames with their audio, which the hub streams to pucks that can show video.
/// </summary>
public sealed class OpensplinePlugin : PuckPlugin<OpensplineSettings>, ITalkingFace
{
    readonly SemaphoreSlim _connect = new(1, 1);
    OpensplineSession? _session;
    CancellationTokenSource? _keeper;
    Process? _service;   // Openspline, when this plugin started it
    CancellationTokenSource? _pendingStop;
    DateTime? _busySince;   // when Openspline started saying it has no free worker

    public override string Id => "openspline";
    public override string Name => "Talking face (Openspline)";
    public override string Description =>
        "Gives Pucksley an animated face on the puck while it speaks, generated live by Openspline on a PC with an NVIDIA GPU. Without it, the puck keeps its usual face.";
    public override string Icon => "tool";

    public bool IsReady => _session is { IsOpen: true };

    public override void Initialize(IPluginContext context)
    {
        base.Initialize(context);
        // The hub is closing: don't leave Openspline holding the GPU
        AppDomain.CurrentDomain.ProcessExit += (_, _) => StopService();
    }

    public override Task StartAsync(CancellationToken cancel)
    {
        _pendingStop?.Cancel();
        _pendingStop = null;
        _keeper?.Cancel();
        _keeper = new CancellationTokenSource();
        var token = _keeper.Token;
        SetStatus(StatusLevel.Idle, "Connecting to Openspline…");
        _ = Task.Run(() => KeepConnectedAsync(token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public override async Task StopAsync()
    {
        _keeper?.Cancel();
        _keeper = null;
        await CloseAsync().ConfigureAwait(false);
        // Saving the settings (a new face) stops and starts the plugin straight away, and loading
        // Openspline's models again takes half a minute: only stop it if the plugin stays stopped
        var pending = _pendingStop = new CancellationTokenSource();
        _ = Task.Delay(TimeSpan.FromSeconds(10), pending.Token)
            .ContinueWith(t => { if (!t.IsCanceled) StopService(); }, TaskScheduler.Default);
    }

    protected override void OnSettingsApplied()
    {
        // A new portrait or address: start again with them
        _ = CloseAsync();
    }

    /// <summary>Keeps one avatar session open (making one takes a few seconds, too long to wait at the start of a reply).</summary>
    async Task KeepConnectedAsync(CancellationToken cancel)
    {
        var wait = TimeSpan.FromSeconds(5);
        while (!cancel.IsCancellationRequested)
        {
            if (_session is not { IsOpen: true })
            {
                try
                {
                    await ConnectAsync(cancel).ConfigureAwait(false);
                    wait = TimeSpan.FromSeconds(5);
                    _busySince = null;
                    SetStatus(StatusLevel.Ok, $"Ready: avatar {_session!.Id[..8]}");
                }
                catch (OpensplineBusyException e)
                {
                    if (cancel.IsCancellationRequested) break;
                    // No free worker: another session has it (one left behind ends by itself
                    // within two minutes), or it's stuck. If that lasts, restart it: it comes back
                    // free in half a minute.
                    _busySince ??= DateTime.UtcNow;
                    var busyFor = DateTime.UtcNow - _busySince.Value;
                    if (busyFor > TimeSpan.FromSeconds(90) && CanRestart)
                    {
                        Log.LogWarning("Openspline has had no free worker for {Seconds} s: restarting it", (int)busyFor.TotalSeconds);
                        await RestartServiceAsync(cancel).ConfigureAwait(false);
                        wait = TimeSpan.Zero;
                    }
                    else
                    {
                        SetStatus(StatusLevel.Warning, busyFor > TimeSpan.FromSeconds(90)
                            ? $"Openspline has no free worker (another session has it, or it's stuck): restart Openspline, or set a stop command so the hub can. ({e.Message})"
                            : "Waiting for Openspline to free its worker…");
                        wait = TimeSpan.FromSeconds(10);
                    }
                }
                catch (Exception e) when (e is PluginException or HttpRequestException or System.Net.WebSockets.WebSocketException or OperationCanceledException or IOException or UriFormatException or System.Text.Json.JsonException)
                {
                    if (cancel.IsCancellationRequested) break;
                    bool notAnswering = e is HttpRequestException or System.Net.WebSockets.WebSocketException;
                    // Not running: start it (once at a time) and try again as soon as it's up
                    if (notAnswering && await StartServiceAsync(cancel).ConfigureAwait(false))
                    {
                        wait = TimeSpan.Zero;
                        continue;
                    }
                    SetStatus(StatusLevel.Warning, notAnswering
                        ? $"Openspline isn't answering at {Settings.Url}" + (string.IsNullOrWhiteSpace(Settings.StartCommand) ? " (start it, or set a start command)" : "")
                        : e.Message);
                    wait = TimeSpan.FromSeconds(Math.Min(60, wait.TotalSeconds * 2));
                }
            }
            try { await Task.Delay(wait, cancel).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>
    /// Runs the start command (if set, and not already running from here), then waits for the service
    /// to say it's ready (loading the models takes a while). True once it answers.
    /// </summary>
    async Task<bool> StartServiceAsync(CancellationToken cancel)
    {
        var command = Settings.StartCommand?.Trim() ?? "";
        if (command.Length == 0) return false;
        if (_service is not { HasExited: false })
        {
            _service?.Dispose();
            // Its output goes straight to a log in the plugin's folder, not through the hub: if the hub
            // is killed, Openspline mustn't be left writing into a broken pipe (that crashes its worker)
            var log = Path.Combine(Context.DataDirectory, "openspline.log");
            var info = OperatingSystem.IsWindows()
                ? new ProcessStartInfo("cmd.exe", $"/s /c \"{command} > \"{log}\" 2>&1\"")
                : new ProcessStartInfo("/bin/sh", ["-c", command + " > \"$0\" 2>&1", log]);
            info.UseShellExecute = false;
            info.CreateNoWindow = true;
            try
            {
                _service = Process.Start(info);
            }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                SetStatus(StatusLevel.Warning, $"Couldn't run the start command: {e.Message}");
                return false;
            }
            if (_service is null) return false;
            Log.LogInformation("Openspline: started it ({Command})", command);
        }
        SetStatus(StatusLevel.Idle, "Starting Openspline (loading its models)…");
        var ready = new Uri(new Uri(Settings.Url.Trim().TrimEnd('/') + "/"), "readyz");
        var deadline = DateTime.UtcNow.AddMinutes(3);
        while (DateTime.UtcNow < deadline && !cancel.IsCancellationRequested)
        {
            if (_service is { HasExited: true })
            {
                SetStatus(StatusLevel.Warning, $"Openspline stopped while starting (exit code {_service.ExitCode}); see openspline.log in {Context.DataDirectory}");
                return false;
            }
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancel);
                cts.CancelAfter(TimeSpan.FromSeconds(3));
                using var response = await Context.Http.GetAsync(ready, cts.Token).ConfigureAwait(false);
                if (response.IsSuccessStatusCode) return true;
            }
            catch (Exception e) when (e is HttpRequestException or OperationCanceledException) { }
            try { await Task.Delay(2000, cancel).ConfigureAwait(false); }
            catch (OperationCanceledException) { return false; }
        }
        return false;
    }

    /// <summary>Whether this plugin can stop Openspline (it started it, or there's a stop command) and start it again.</summary>
    bool CanRestart =>
        !string.IsNullOrWhiteSpace(Settings.StartCommand) &&
        (_service is { HasExited: false } || !string.IsNullOrWhiteSpace(Settings.StopCommand));

    /// <summary>
    /// Stops Openspline (the copy this plugin started, or with the stop command) and waits until it
    /// has gone; the keeper then starts it again, fresh, with its worker free.
    /// </summary>
    async Task RestartServiceAsync(CancellationToken cancel)
    {
        _busySince = null;
        await CloseAsync().ConfigureAwait(false);
        SetStatus(StatusLevel.Idle, "Restarting Openspline…");
        StopService();
        var stop = Settings.StopCommand?.Trim() ?? "";
        if (stop.Length > 0)
        {
            try
            {
                var info = OperatingSystem.IsWindows() ? new ProcessStartInfo("cmd.exe", $"/s /c \"{stop}\"") : new ProcessStartInfo("/bin/sh", ["-c", stop]);
                info.UseShellExecute = false;
                info.CreateNoWindow = true;
                using var p = Process.Start(info);
                if (p is not null)
                {
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancel);
                    cts.CancelAfter(TimeSpan.FromSeconds(20));
                    try { await p.WaitForExitAsync(cts.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (!cancel.IsCancellationRequested) { }
                }
                Log.LogInformation("Openspline: ran the stop command ({Command})", stop);
            }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                Log.LogWarning("Openspline: couldn't run the stop command: {Message}", e.Message);
            }
        }
        // Gone once it stops answering (or 20 s)
        var ready = new Uri(new Uri(Settings.Url.Trim().TrimEnd('/') + "/"), "readyz");
        for (int i = 0; i < 20 && !cancel.IsCancellationRequested; i++)
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancel);
                cts.CancelAfter(TimeSpan.FromSeconds(2));
                using var _ = await Context.Http.GetAsync(ready, cts.Token).ConfigureAwait(false);
            }
            catch (Exception e) when (e is HttpRequestException or OperationCanceledException) { break; }
            try { await Task.Delay(1000, cancel).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    public override IReadOnlyList<PluginAction> GetActions() =>
        [new("restart", "Restart Openspline", "Stops Openspline and starts it again (about half a minute): for when the face is stuck waiting for its worker.")];

    public override async Task<ActionResult> RunActionAsync(string actionId, IReadOnlyDictionary<string, string> input, CancellationToken cancel)
    {
        if (actionId != "restart") return new ActionResult("Unknown action", false);
        if (!CanRestart)
            return new ActionResult(string.IsNullOrWhiteSpace(Settings.StartCommand)
                ? "Set a start command first, so the hub can start Openspline again."
                : "Openspline wasn't started by the hub: set a stop command (Advanced) so the hub can stop it.", false);
        // The keeper starts it again once it has gone
        await RestartServiceAsync(cancel).ConfigureAwait(false);
        return new ActionResult("Restarting Openspline: the face is back in about half a minute.");
    }

    /// <summary>Stops the copy of Openspline this plugin started (one started some other way is left alone).</summary>
    void StopService()
    {
        var p = Interlocked.Exchange(ref _service, null);
        if (p is null) return;
        try
        {
            if (!p.HasExited) p.Kill(entireProcessTree: true);
            Log.LogInformation("Openspline: stopped it");
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        p.Dispose();
    }

    async Task ConnectAsync(CancellationToken cancel)
    {
        await _connect.WaitAsync(cancel).ConfigureAwait(false);
        try
        {
            if (_session is { IsOpen: true }) return;
            if (_session is not null) await _session.DisposeAsync().ConfigureAwait(false);
            _session = null;
            var portrait = await PortraitAsync(cancel).ConfigureAwait(false);
            var url = new Uri(Settings.Url.Trim().TrimEnd('/') + "/");
            await EndLeftoverSessionAsync(url).ConfigureAwait(false);
            await EnsureQualityAsync(url, Quality, cancel).ConfigureAwait(false);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            cts.CancelAfter(TimeSpan.FromSeconds(90)); // the first one loads the models
            _session = await OpensplineSession.OpenAsync(url, Context.Http, portrait, Quality, Log, cts.Token).ConfigureAwait(false);
            Log.LogInformation("Openspline: avatar session {Id} ready", _session.Id);
            RememberSession(url, _session);
        }
        finally
        {
            _connect.Release();
        }
    }

    /// <summary>Always low quality: high can't keep up live on one GPU and unsettles Openspline.</summary>
    const string Quality = "low";

    /// <summary>
    /// Openspline's worker loads one quality at a time: if it was left on the other one (its own
    /// demo page can do that), ask it to switch (its models load again, which takes a while) and
    /// wait until it has.
    /// </summary>
    async Task EnsureQualityAsync(Uri url, string quality, CancellationToken cancel)
    {
        JsonNode? ready;
        using (var response = await Context.Http.GetAsync(new Uri(url, "readyz"), cancel).ConfigureAwait(false))
            ready = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancel).ConfigureAwait(false));
        var workers = ready?["workers"] as JsonArray;
        if (workers is null || workers.Any(w => w?["quality"]?.GetValue<string>() == quality)) return;
        var switchable = (ready?["demo_qualities"] as JsonArray)?.Any(q => q?.GetValue<string>() == quality) ?? false;
        if (!switchable || workers.Count != 1)
            throw new PluginException($"Openspline has no {quality}-quality worker: add one to its workers.yaml, or choose the other quality");

        SetStatus(StatusLevel.Idle, $"Switching Openspline to {quality} quality (loading its models)…");
        Log.LogInformation("Openspline: switching its worker to {Quality} quality", quality);
        string text;
        using (var response = await Context.Http.PostAsJsonAsync(new Uri(url, "v1/demo/quality"), new { quality }, cancel).ConfigureAwait(false))
        {
            text = await response.Content.ReadAsStringAsync(cancel).ConfigureAwait(false);
            if ((int)response.StatusCode == 429) throw new OpensplineBusyException(text);
            if (!response.IsSuccessStatusCode)
                throw new PluginException($"Openspline couldn't switch to {quality} quality ({(int)response.StatusCode}): {text}");
        }
        var id = JsonNode.Parse(text)?["id"]?.GetValue<string>();
        if (id is null) return;   // it already had it
        var deadline = DateTime.UtcNow.AddMinutes(5);
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(2000, cancel).ConfigureAwait(false);
            JsonNode? job;
            using (var response = await Context.Http.GetAsync(new Uri(url, $"v1/demo/quality/{id}"), cancel).ConfigureAwait(false))
                job = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancel).ConfigureAwait(false));
            switch (job?["state"]?.GetValue<string>())
            {
                case "ready":
                    Log.LogInformation("Openspline: now at {Quality} quality", quality);
                    return;
                case "error":
                    throw new PluginException($"Openspline couldn't switch to {quality} quality: {job?["error"]?.GetValue<string>()}");
            }
        }
        throw new PluginException($"Openspline took too long switching to {quality} quality");
    }

    /// <summary>The picture to animate: the person's own file when set, otherwise the chosen face.</summary>
    async Task<byte[]> PortraitAsync(CancellationToken cancel)
    {
        if (!string.IsNullOrWhiteSpace(Settings.Portrait))
        {
            var p = Settings.Portrait.Trim().Trim('"');
            return File.Exists(p) ? await File.ReadAllBytesAsync(p, cancel).ConfigureAwait(false) : throw new PluginException($"There's no picture at {p}");
        }
        return FacePicture(Settings.Face) ?? FacePicture("face-01")!;
    }

    /// <summary>The ready-made faces' names (the Face setting's choices).</summary>
    internal static IReadOnlyList<string> FaceNames { get; } =
        typeof(OpensplineSettings).GetProperty(nameof(OpensplineSettings.Face))!.GetCustomAttributes(typeof(SettingAttribute), false)
            .Cast<SettingAttribute>().Single().Options!;

    /// <summary>A ready-made face (or its thumbnail), from the embedded pictures; null if there's no such face.</summary>
    internal static byte[]? FacePicture(string? name, bool thumbnail = false)
    {
        if (name is null || !FaceNames.Contains(name)) return null;   // only the listed names reach the resource lookup
        using var stream = typeof(OpensplinePlugin).Assembly.GetManifestResourceStream($"Openspline.Faces.{name}{(thumbnail ? "-thumb" : "")}.jpg");
        if (stream is null) return null;
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    /// <summary>The faces' thumbnails for the settings page (pictures/face/face-NN).</summary>
    public override Task<PluginHttpResponse?> HandleHttpAsync(PluginHttpRequest request, CancellationToken cancel)
    {
        var parts = request.Path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (request.Method is "GET" && parts is ["pictures", "face", var name] && FacePicture(name, thumbnail: true) is { } jpeg)
            return Task.FromResult<PluginHttpResponse?>(PluginHttpResponse.Bytes(jpeg, "image/jpeg"));
        return Task.FromResult<PluginHttpResponse?>(null);
    }

    async Task CloseAsync()
    {
        await _connect.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_session is not null) await _session.DisposeAsync().ConfigureAwait(false);
            _session = null;
            ForgetSession();
        }
        finally
        {
            _connect.Release();
        }
    }

    // The open session is written down, so if the hub dies without ending it (killed, crashed),
    // the next start ends it: otherwise it holds Openspline's only worker until it times out.
    string SessionFile => Path.Combine(Context.DataDirectory, "session.json");

    void RememberSession(Uri url, OpensplineSession session)
    {
        try
        {
            File.WriteAllText(SessionFile, new JsonObject { ["url"] = url.ToString(), ["id"] = session.Id, ["token"] = session.PublisherToken }.ToJsonString());
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    void ForgetSession()
    {
        try { File.Delete(SessionFile); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    async Task EndLeftoverSessionAsync(Uri url)
    {
        JsonNode? left;
        try { left = File.Exists(SessionFile) ? JsonNode.Parse(await File.ReadAllTextAsync(SessionFile).ConfigureAwait(false)) : null; }
        catch (Exception e) when (e is IOException or System.Text.Json.JsonException) { left = null; }
        ForgetSession();
        if (left?["id"]?.GetValue<string>() is not { Length: > 0 } id || left["token"]?.GetValue<string>() is not { Length: > 0 } token) return;
        // Only on the service it was opened on
        if (!string.Equals(left["url"]?.GetValue<string>(), url.ToString(), StringComparison.OrdinalIgnoreCase)) return;
        Log.LogInformation("Openspline: ending session {Id} left over from before", id[..Math.Min(8, id.Length)]);
        await OpensplineSession.EndAsync(url, Context.Http, id, token).ConfigureAwait(false);
    }

    public Task<ITalkingFaceTurn> BeginTurnAsync(CancellationToken cancel)
    {
        var s = _session;
        if (s is not { IsOpen: true }) throw new PluginException("Openspline isn't connected");
        return Task.FromResult<ITalkingFaceTurn>(s.BeginTurn());
    }
}
