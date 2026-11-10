using Microsoft.Extensions.Logging;
using Pucksley.Sdk;

namespace Pucksley.Plugins.Speech.Openspline;

public sealed class OpensplineSettings
{
    [Setting("Service address", Order = 1, Placeholder = "http://localhost:7860",
        Help = "Where Openspline runs: a PC with an NVIDIA GPU (docs.openspline.dev/get-started/self-host). From another machine, use that PC's address.")]
    public string Url { get; set; } = "http://localhost:7860";

    [Setting("Portrait", Order = 2, Placeholder = "portrait.png in the plugin's folder",
        Help = "The face: a front-on photo or picture, head and shoulders, plain dark background (it's shown in a circle on black). A file path; empty uses portrait.png or portrait.jpg in this plugin's data folder.")]
    public string Portrait { get; set; } = "";

    [Setting("Quality", Order = 3, Options = ["low", "high"], Advanced = true,
        Help = "Low keeps up live on one mid-range GPU (about 25 frames a second); high needs a much bigger one.")]
    public string Quality { get; set; } = "low";
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

    public override string Id => "openspline";
    public override string Name => "Talking face (Openspline)";
    public override string Description =>
        "Gives Pucksley an animated face on the puck while it speaks, generated live by Openspline on a PC with an NVIDIA GPU. Without it, the puck keeps its usual face.";
    public override string Icon => "tool";

    public bool IsReady => _session is { IsOpen: true };

    public override Task StartAsync(CancellationToken cancel)
    {
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
                    SetStatus(StatusLevel.Ok, $"Ready: avatar {_session!.Id[..8]} ({Settings.Quality} quality)");
                }
                catch (Exception e) when (e is PluginException or HttpRequestException or System.Net.WebSockets.WebSocketException or OperationCanceledException or IOException or UriFormatException)
                {
                    if (cancel.IsCancellationRequested) break;
                    SetStatus(StatusLevel.Warning, e is HttpRequestException or System.Net.WebSockets.WebSocketException
                        ? $"Openspline isn't answering at {Settings.Url} (is it running?)"
                        : e.Message);
                    wait = TimeSpan.FromSeconds(Math.Min(60, wait.TotalSeconds * 2));
                }
            }
            try { await Task.Delay(wait, cancel).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    async Task ConnectAsync(CancellationToken cancel)
    {
        await _connect.WaitAsync(cancel).ConfigureAwait(false);
        try
        {
            if (_session is { IsOpen: true }) return;
            if (_session is not null) await _session.DisposeAsync().ConfigureAwait(false);
            _session = null;
            var portrait = await File.ReadAllBytesAsync(PortraitPath(), cancel).ConfigureAwait(false);
            var url = new Uri(Settings.Url.Trim().TrimEnd('/') + "/");
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            cts.CancelAfter(TimeSpan.FromSeconds(90)); // the first one loads the models
            _session = await OpensplineSession.OpenAsync(url, Context.Http, portrait, Settings.Quality is "high" ? "high" : "low", Log, cts.Token).ConfigureAwait(false);
            Log.LogInformation("Openspline: avatar session {Id} ready", _session.Id);
        }
        finally
        {
            _connect.Release();
        }
    }

    string PortraitPath()
    {
        if (!string.IsNullOrWhiteSpace(Settings.Portrait))
        {
            var p = Settings.Portrait.Trim().Trim('"');
            return File.Exists(p) ? p : throw new PluginException($"There's no portrait at {p}");
        }
        foreach (var name in new[] { "portrait.png", "portrait.jpg", "portrait.jpeg", "portrait.webp" })
        {
            var p = Path.Combine(Context.DataDirectory, name);
            if (File.Exists(p)) return p;
        }
        throw new PluginException($"Choose a portrait: set one in the settings, or put portrait.png in {Context.DataDirectory}");
    }

    async Task CloseAsync()
    {
        await _connect.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_session is not null) await _session.DisposeAsync().ConfigureAwait(false);
            _session = null;
        }
        finally
        {
            _connect.Release();
        }
    }

    public Task<ITalkingFaceTurn> BeginTurnAsync(CancellationToken cancel)
    {
        var s = _session;
        if (s is not { IsOpen: true }) throw new PluginException("Openspline isn't connected");
        return Task.FromResult<ITalkingFaceTurn>(s.BeginTurn());
    }
}
