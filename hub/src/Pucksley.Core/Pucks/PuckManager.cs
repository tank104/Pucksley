using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Pucksley.Core.Agent;
using Pucksley.Core.Plugins;
using Pucksley.Core.Settings;
using Pucksley.Core.Voice;
using Pucksley.Core.Web;
using Pucksley.Sdk;

namespace Pucksley.Core.Pucks;

/// <summary>A puck waiting for someone to approve it in the web page.</summary>
public sealed record PendingPuck(string Id, string Name, string Code, string Address, DateTimeOffset Since);

/// <summary>
/// All the pucks: who's connected, approving new ones, and keeping each one's screen up to date
/// (settings, controls, timers, what's playing). Also delivers the plugins' notifications,
/// announcements and confirmations.
/// </summary>
public sealed partial class PuckManager : IAsyncDisposable
{
    readonly ILoggerFactory _loggers;
    readonly ILogger _log;
    readonly HubEvents _events;
    readonly ConcurrentDictionary<string, PuckSession> _sessions = new(StringComparer.OrdinalIgnoreCase);
    readonly ConcurrentDictionary<PuckConnection, PuckSession> _byConnection = new();
    readonly ConcurrentDictionary<string, (PendingPuck Info, PuckConnection Connection, JsonObject Hello)> _pending = new(StringComparer.OrdinalIgnoreCase);
    readonly CancellationTokenSource _stop = new();
    List<string> _controlIds = [];
    Task? _loop;

    /// <summary>How often the pucks' direct mode setup is checked for changes. Tests shorten it.</summary>
    internal static TimeSpan DirectCheckInterval { get; set; } = TimeSpan.FromSeconds(15);

    public PuckManager(SettingsStore settings, PluginManager plugins, AgentService agent, SpeechService speech, HubEvents events, ILoggerFactory loggers)
    {
        Settings = settings;
        Plugins = plugins;
        Agent = agent;
        Speech = speech;
        _events = events;
        _loggers = loggers;
        _log = loggers.CreateLogger("Pucks");
        plugins.ControlsChanged += BroadcastControls;
        plugins.ControlValueChanged += OnControlValue;
        plugins.NowPlayingChanged += source => _ = RefreshFollowersAsync(source);
    }

    internal SettingsStore Settings { get; }
    internal PluginManager Plugins { get; }
    internal AgentService Agent { get; }
    internal SpeechService Speech { get; }

    public IReadOnlyList<PuckSession> Sessions => _sessions.Values.Where(s => s.Welcomed && s.IsConnected).ToList();
    public IReadOnlyList<PendingPuck> Pending => _pending.Values.Select(p => p.Info).ToList();

    public PuckSession? Session(string id) => _sessions.TryGetValue(id, out var s) && s.Welcomed && s.IsConnected ? s : null;

    public PuckRecord? Record(string id) => Settings.Current.Pucks.FirstOrDefault(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    internal void StatusChanged() => _events.StatusChanged("pucks");

    public void Start() => _loop = Task.Run(() => LoopAsync(_stop.Token));

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        foreach (var c in _byConnection.Keys.ToList()) c.Close("hub stopping");
        if (_loop is not null) try { await _loop.ConfigureAwait(false); } catch { }
    }

    // ------------------------------------------------------------------ connections

    /// <summary>Takes a new link (WebSocket accepted, or a USB port opened).</summary>
    public void Attach(PuckConnection c)
    {
        c.MessageReceived += OnMessage;
        c.AudioReceived += OnAudio;
        c.Closed += OnClosed;
        _log.LogInformation("Puck link opened: {Kind} {Where}", c.Kind, c.Description);
        if (c.Kind == "usb")
        {
            // On USB the hub speaks first, and keeps saying hello until the puck answers
            _ = Task.Run(async () =>
            {
                for (int i = 0; i < 30 && !c.IsClosed && !_byConnection.ContainsKey(c); i++)
                {
                    c.Send(HubHello());
                    await Task.Delay(2000).ConfigureAwait(false);
                }
                // Another device with the same USB chip (another ESP32-S3 project): let go of its port
                if (!c.IsClosed && !c.SaidHello) c.Close("no answer: not a puck");
            });
        }
        else
        {
            // A Wi-Fi puck that never says hello is dropped
            _ = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                if (!_byConnection.ContainsKey(c) && !_pending.Values.Any(p => p.Connection == c)) c.Close("no hello");
            });
        }
    }

    JsonObject HubHello() => new()
    {
        ["t"] = "hello", ["v"] = 1, ["hub"] = Settings.Current.HubName, ["hubId"] = Settings.Current.HubId,
        ["app"] = typeof(PuckManager).Assembly.GetName().Version?.ToString(3) ?? "1.0.0",
    };

    void OnMessage(PuckConnection c, JsonObject m)
    {
        try
        {
            var t = PuckSession.Str(m, "t");
            if (_byConnection.TryGetValue(c, out var session))
            {
                if (session.Welcomed) session.Handle(c, m);
                else if (t == "hello") OnHello(c, m);
                return;
            }
            if (t == "hello") OnHello(c, m);
            else if (t is "log" or "diag") _log.LogInformation("Puck ({Where}): {Message}", c.Description, PuckSession.Str(m, "m"));
        }
        catch (Exception e)
        {
            _log.LogError(e, "Failed handling a puck message");
        }
    }

    void OnAudio(PuckConnection c, short[] samples)
    {
        if (_byConnection.TryGetValue(c, out var s) && s.Welcomed) s.Voice.OnAudio(samples);
    }

    void OnClosed(PuckConnection c, string reason)
    {
        foreach (var (id, p) in _pending)
            if (p.Connection == c) _pending.TryRemove(id, out _);
        if (_byConnection.TryRemove(c, out var s))
        {
            _log.LogInformation("{Puck} disconnected ({Kind}: {Reason})", s.Name, c.Kind, reason);
            if (s.RemoveConnection(c))
            {
                Settings.Update(st => { var r = st.Pucks.FirstOrDefault(p => p.Id == s.Id); if (r is not null) r.LastSeen = DateTimeOffset.UtcNow; });
            }
        }
        StatusChanged();
    }

    [GeneratedRegex("^[0-9a-fA-F]{8,32}$")]
    private static partial Regex IdPattern();

    void OnHello(PuckConnection c, JsonObject hello)
    {
        var id = PuckSession.Str(hello, "id");
        if (id is null || !IdPattern().IsMatch(id))
        {
            c.Send(new JsonObject { ["t"] = "welcome", ["ok"] = false, ["error"] = "Bad puck id" });
            return;
        }
        id = id.ToLowerInvariant();
        var token = PuckSession.Str(hello, "token") ?? "";
        var session = _sessions.GetOrAdd(id, i => new PuckSession(this, i, _loggers.CreateLogger("Puck")));
        session.UpdateFromHello(hello);
        var record = Record(id);

        if (record is not null && (FixedTimeEquals(record.Token, token) || c.IsTrusted))
        {
            Accept(c, session, record.Token);
            return;
        }
        if (c.IsTrusted)
        {
            var newRecord = CreateRecord(id, session.DeviceName);
            Accept(c, session, newRecord.Token);
            return;
        }
        // Unknown on Wi-Fi: wait for approval in the web page
        var code = RandomNumberGenerator.GetInt32(100, 1000).ToString();
        var existing = _pending.TryGetValue(id, out var prev) ? prev.Info.Code : null;
        code = existing ?? code;
        var info = new PendingPuck(id, session.DeviceName.Length > 0 ? session.DeviceName : id, code, c.Description, DateTimeOffset.UtcNow);
        _pending[id] = (info, c, hello);
        session.PendingCode = code;
        c.Send(new JsonObject { ["t"] = "welcome", ["ok"] = false, ["pending"] = true, ["code"] = code });
        _log.LogInformation("New puck {Name} at {Where} is waiting for approval (code {Code})", info.Name, c.Description, code);
        StatusChanged();
    }

    static bool FixedTimeEquals(string a, string b) =>
        a.Length > 0 && b.Length == a.Length && CryptographicOperations.FixedTimeEquals(System.Text.Encoding.UTF8.GetBytes(a), System.Text.Encoding.UTF8.GetBytes(b));

    PuckRecord CreateRecord(string id, string deviceName, string? name = null, string? room = null)
    {
        var record = new PuckRecord
        {
            Id = id,
            DeviceName = deviceName,
            Name = name ?? "",
            Room = room ?? "",
            Token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant(),
            ApprovedAt = DateTimeOffset.UtcNow,
            LastSeen = DateTimeOffset.UtcNow,
        };
        Settings.Update(s =>
        {
            s.Pucks.RemoveAll(p => p.Id == id);
            s.Pucks.Add(record);
        });
        return record;
    }

    void Accept(PuckConnection c, PuckSession session, string token)
    {
        _pending.TryRemove(session.Id, out _);
        session.PendingCode = null;
        c.Touch();   // it may have waited a long time for approval; the silence clock starts now
        session.AddConnection(c);
        _byConnection[c] = session;
        session.Welcomed = true;
        // One link per transport: a puck that reconnects before its old link was noticed dead
        // (half-open TCP after a Wi-Fi drop) replaces it. Otherwise everything keeps going to the old one.
        foreach (var old in session.Connections)
            if (old != c && old.Kind == c.Kind) old.Close("replaced by a new connection");
        var r = Record(session.Id)!;
        c.Send(new JsonObject
        {
            ["t"] = "welcome", ["ok"] = true, ["hub"] = Settings.Current.HubName, ["hubId"] = Settings.Current.HubId,
            ["name"] = r.DisplayName, ["token"] = token,
        });
        _log.LogInformation("{Puck} connected over {Kind} ({Where})", session.Name, c.Kind, c.Description);
        SendInitialState(session, c);
        StatusChanged();
    }

    /// <summary>Someone approved a waiting puck in the web page.</summary>
    public bool Approve(string id, string? name = null, string? room = null)
    {
        if (!_pending.TryRemove(id, out var p)) return false;
        var session = _sessions.GetOrAdd(p.Info.Id, i => new PuckSession(this, i, _loggers.CreateLogger("Puck")));
        var record = CreateRecord(p.Info.Id, session.DeviceName, name, room);
        if (!p.Connection.IsClosed) Accept(p.Connection, session, record.Token);
        return true;
    }

    public void Reject(string id)
    {
        if (_pending.TryRemove(id, out var p))
        {
            p.Connection.Send(new JsonObject { ["t"] = "welcome", ["ok"] = false, ["error"] = "Not approved" });
            p.Connection.Close("rejected");
        }
        StatusChanged();
    }

    /// <summary>Closes every puck link (this hub stopped taking pucks).</summary>
    public void DisconnectAll(string reason)
    {
        foreach (var c in _byConnection.Keys.ToList()) c.Close(reason);
        foreach (var (_, p) in _pending) p.Connection.Close(reason);
    }

    /// <summary>Forget a puck: it has to be approved again.</summary>
    public void Forget(string id)
    {
        Settings.Update(s => s.Pucks.RemoveAll(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase)));
        if (_sessions.TryGetValue(id, out var session))
        {
            session.Send(new JsonObject { ["t"] = "forget" });
            foreach (var c in session.Connections) c.Close("forgotten");
            _sessions.TryRemove(id, out _);
        }
        StatusChanged();
    }

    public void UpdateRecord(string id, Action<PuckRecord> change, bool resendConfig = true)
    {
        Settings.Update(s => { var r = s.Pucks.FirstOrDefault(p => p.Id == id); if (r is not null) change(r); });
        if (resendConfig && Session(id) is { } session) SendConfig(session);
    }

    // ------------------------------------------------------------------ keeping the screen up to date

    void SendInitialState(PuckSession s, PuckConnection c)
    {
        c.Send(TimeMessage());
        SendConfig(s, c);
        SendControls(s, c);
        if (c.Kind == "wifi") SendDirect(s);   // a new link always gets it (see DirectLink)
        if (Plugins.TimerHandler is { } th)
        {
            foreach (var t in th.ActiveTimers.Where(t => t.PuckId is null || t.PuckId == s.Id))
                c.Send(TimerMessage(t.Id, t.EndsAt, t.Label));
        }
        s.LastState = null;
        _ = RefreshNowPlayingAsync(s);
    }

    JsonObject TimeMessage()
    {
        var tz = TimeZone;
        var now = DateTimeOffset.UtcNow;
        return new JsonObject
        {
            ["t"] = "time", ["epoch"] = now.ToUnixTimeSeconds(), ["tz"] = (int)tz.GetUtcOffset(now).TotalMinutes,
            ["tzp"] = PosixTz.From(tz, now),   // so the puck keeps local time (and daylight saving) on its own
        };
    }

    /// <summary>
    /// Direct mode: the keys and requests a puck needs to ask Claude itself when it can't reach the
    /// hub. Wi-Fi links only (the messages don't fit USB's line limit, and direct mode needs Wi-Fi
    /// anyway). Sent when the puck connects and again whenever the setup changes.
    /// </summary>
    void SendDirect(PuckSession s)
    {
        var link = s.Connections.FirstOrDefault(c => c.Kind == "wifi" && !c.IsClosed);
        if (link is null) return;
        List<JsonObject> messages;
        try { messages = DirectSetup.Build(Settings.Current, Agent.ApiKey, Plugins.SttEngines, Plugins.TtsEngines, TimeZone); }
        catch (Exception e)
        {
            _log.LogWarning(e, "Couldn't put together the direct mode setup");
            return;
        }
        var fingerprint = DirectSetup.Fingerprint(messages);
        // the welcome and the periodic check can meet: one sends, the other sees it's done
        lock (s.DirectLock)
        {
            if (fingerprint == s.DirectSent && ReferenceEquals(link, s.DirectLink)) return;
            foreach (var m in messages) link.Send(m);
            s.DirectSent = fingerprint;
            s.DirectLink = link;
            s.DirectMode = DirectSetup.Describe(messages);
        }
        _log.LogInformation("{Puck}: direct mode {State}", s.Name, s.DirectMode);
        StatusChanged();
    }

    public TimeZoneInfo TimeZone
    {
        get
        {
            var id = Settings.Current.Household.TimeZone;
            if (!string.IsNullOrWhiteSpace(id))
            {
                try { return TimeZoneInfo.FindSystemTimeZoneById(id.Trim()); }
                catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException) { }
            }
            return TimeZoneInfo.Local;
        }
    }

    public void SendConfig(PuckSession s, PuckConnection? via = null)
    {
        var r = s.Record;
        if (r is null) return;
        var cfg = new JsonObject
        {
            ["t"] = "cfg",
            ["bright"] = Math.Clamp(r.Brightness, 10, 100),
            ["sound"] = r.Sounds,
            ["dim"] = Math.Max(0, r.DimSeconds),
            ["off"] = Math.Max(0, r.OffSeconds),
            ["sens"] = Math.Clamp(r.TurnSensitivity, 0.25, 4),
            ["flip"] = r.FlipTurn,
            ["spk"] = Math.Clamp(r.SpeakerVolume, 0, 100),
            ["mic"] = Math.Clamp(r.MicGain, 0, 37),
            ["lift"] = r.LiftToTalk,
            ["name"] = r.DisplayName,
        };
        if (via is not null) via.Send(cfg); else s.Send(cfg);
    }

    /// <summary>The controls list, short ids ("c0", "c1"…) so the puck's buffers stay small.</summary>
    void SendControls(PuckSession s, PuckConnection? via = null)
    {
        var controls = Plugins.Controls;
        lock (this) _controlIds = controls.Select(c => c.GlobalId).ToList();
        void Out(JsonObject m) { if (via is not null) via.Send(m); else s.Send(m); }
        Out(new JsonObject { ["t"] = "ctls", ["n"] = controls.Count });
        for (int i = 0; i < controls.Count; i++)
        {
            var c = controls[i].Control;
            var m = new JsonObject
            {
                ["t"] = "ctl", ["i"] = i, ["id"] = $"c{i}", ["g"] = Clip(c.Group ?? "", 20), ["n"] = Clip(c.Name, 24),
                ["k"] = c.Kind switch { ControlKind.Toggle => "bool", ControlKind.Action => "act", ControlKind.Select => "sel", _ => "num" },
            };
            switch (c.Kind)
            {
                case ControlKind.Number:
                    m["v"] = c.Value; m["min"] = c.Min; m["max"] = c.Max; m["step"] = c.Step <= 0 ? 1 : c.Step;
                    if (c.Unit is not null) m["u"] = c.Unit;
                    break;
                case ControlKind.Toggle:
                    m["on"] = c.On;
                    break;
                case ControlKind.Select:
                    m["v"] = c.Value;
                    m["opts"] = new JsonArray((c.Options ?? []).Take(12).Select(o => (JsonNode)Clip(o, 20)).ToArray());
                    break;
            }
            if (c.Text is not null) m["txt"] = Clip(c.Text, 24);
            Out(m);
        }
    }

    void BroadcastControls()
    {
        foreach (var s in Sessions) SendControls(s);
    }

    void OnControlValue(string globalId, double? value, bool? on, string? text)
    {
        int index;
        lock (this) index = _controlIds.IndexOf(globalId);
        if (index < 0) return;
        var m = new JsonObject { ["t"] = "ctlv", ["id"] = $"c{index}" };
        if (value is not null) m["v"] = value;
        if (on is not null) m["on"] = on;
        if (text is not null) m["txt"] = Clip(text, 24);
        foreach (var s in Sessions) s.Send(m.DeepClone().AsObject());
    }

    /// <summary>The puck turned/pressed a control: map its short id back to the plugin's.</summary>
    internal string? ControlGlobalId(string shortId)
    {
        if (!shortId.StartsWith('c') || !int.TryParse(shortId.AsSpan(1), out var i)) return shortId.Contains(':') ? shortId : null;
        lock (this) return i >= 0 && i < _controlIds.Count ? _controlIds[i] : null;
    }

    // ------------------------------------------------------------------ media (Home face)

    /// <summary>Which media source a puck shows and controls.</summary>
    public MediaSourceEntry? SourceFor(PuckSession s)
    {
        var sources = Plugins.MediaSources;
        if (sources.Count == 0) return null;
        var want = s.Record?.MediaSource;
        if (string.IsNullOrWhiteSpace(want)) want = Settings.Current.Household.DefaultMediaSource;
        if (!string.IsNullOrWhiteSpace(want))
        {
            want = want.Trim();
            var exact = sources.FirstOrDefault(x => x.GlobalId.Equals(want, StringComparison.OrdinalIgnoreCase));
            if (exact is not null) return exact;
            var parts = want.Split(':', 2);
            var pool = parts.Length == 2 ? sources.Where(x => x.Host.Id.Equals(parts[0], StringComparison.OrdinalIgnoreCase)).ToList() : sources.ToList();
            var byName = NameMatcher.Best(parts.Length == 2 ? parts[1] : want, pool, x => x.Name, 0.6);
            if (byName is not null) return byName;
        }
        if (s.Caller.Room is { } room && NameMatcher.Best(room, sources, x => x.Name, 0.75) is { } inRoom) return inRoom;
        return sources.FirstOrDefault(x => x.GlobalId == "pc-audio:pc") ?? sources[0];
    }

    async Task RefreshNowPlayingAsync(PuckSession s)
    {
        var src = SourceFor(s);
        s.FollowedSource = src?.GlobalId;
        JsonObject state;
        if (src is null)
        {
            state = new JsonObject { ["t"] = "state", ["title"] = "", ["artist"] = "", ["src"] = "", ["play"] = false };
        }
        else
        {
            NowPlaying? np = null;
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                np = await src.Provider.GetNowPlayingAsync(src.LocalId, cts.Token).ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OutOfMemoryException) { _log.LogDebug(e, "Now playing failed for {Source}", src.GlobalId); }
            state = new JsonObject
            {
                ["t"] = "state",
                ["title"] = Clip(np?.Title ?? "", 90),
                ["artist"] = Clip(np?.Artist ?? "", 60),
                ["src"] = Clip(np?.Source ?? src.Name, 30),
                ["play"] = np?.Playing ?? false,
            };
            if (np?.Volume is { } v) state["vol"] = v;
            if (np?.Muted is { } mu) state["mute"] = mu;
        }
        var json = state.ToJsonString();
        if (s.LastState?.ToJsonString() == json) return;
        s.LastState = state;
        // A copy: sending cleans strings in place (Don’t → Don't), and the remembered state must stay
        // as read, or every refresh of a title like that looks like a change and is sent again
        s.Send(state.DeepClone().AsObject());
    }

    async Task RefreshFollowersAsync(string globalSource)
    {
        foreach (var s in Sessions.Where(x => x.FollowedSource == globalSource))
            await RefreshNowPlayingAsync(s).ConfigureAwait(false);
    }

    internal async Task MediaFromPuckAsync(PuckSession s, string action, int? value)
    {
        var src = SourceFor(s);
        if (src is null)
        {
            s.Send(new JsonObject { ["t"] = "notify", ["text"] = "Nothing to control: no music plugins are on", ["kind"] = "info" });
            return;
        }
        MediaCommand? cmd = action switch
        {
            "toggle" => MediaCommand.Toggle,
            "next" => MediaCommand.Next,
            "prev" => MediaCommand.Previous,
            "mute" => MediaCommand.Mute,
            "vol" when value is not null => MediaCommand.SetVolume(value.Value),
            _ => null,
        };
        if (cmd is null) return;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await src.Provider.MediaCommandAsync(src.LocalId, cmd, cts.Token).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            _log.LogWarning("Media {Action} on {Source} failed: {Message}", action, src.GlobalId, e.Message);
            if (action != "vol") s.Send(new JsonObject { ["t"] = "notify", ["text"] = Clip(e.Message, 110), ["kind"] = "info" });
        }
        if (action != "vol")
        {
            await Task.Delay(400).ConfigureAwait(false);
            await RefreshNowPlayingAsync(s).ConfigureAwait(false);
        }
    }

    // ------------------------------------------------------------------ timers

    internal async Task TimerFromPuckAsync(PuckSession s, JsonObject m)
    {
        var handler = Plugins.TimerHandler;
        if (handler is null)
        {
            s.Send(new JsonObject { ["t"] = "notify", ["text"] = "Turn on the Timers plugin on the hub", ["kind"] = "info" });
            return;
        }
        try
        {
            switch (PuckSession.Str(m, "a"))
            {
                case "start" when PuckSession.Int(m, "sec") is { } sec && sec > 0:
                    await handler.StartFromPuckAsync(TimeSpan.FromSeconds(sec), PuckSession.Str(m, "label"), s.Caller, CancellationToken.None).ConfigureAwait(false);
                    break;
                case "cancel" when PuckSession.Str(m, "id") is { } id:
                    await handler.CancelFromPuckAsync(id, CancellationToken.None).ConfigureAwait(false);
                    break;
            }
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            _log.LogWarning(e, "Timer from the puck failed");
        }
    }

    static JsonObject TimerMessage(string id, DateTimeOffset? endsAt, string? label)
    {
        if (endsAt is null) return new JsonObject { ["t"] = "timer", ["id"] = id, ["cancel"] = true };
        var sec = Math.Max(0, (int)Math.Ceiling((endsAt.Value - DateTimeOffset.UtcNow).TotalSeconds));
        return new JsonObject { ["t"] = "timer", ["id"] = Clip(id, 24), ["sec"] = sec, ["label"] = Clip(label ?? "", 24) };
    }

    public void ShowTimer(string id, DateTimeOffset? endsAt, string? label, string? puckId)
    {
        var msg = TimerMessage(id, endsAt, label);
        foreach (var s in Targets(puckId, fallbackToAll: true)) s.Send(msg.DeepClone().AsObject());
    }

    // ------------------------------------------------------------------ for plugins

    IEnumerable<PuckSession> Targets(string? puckId, bool fallbackToAll)
    {
        if (puckId is null) return Sessions;
        var one = Session(puckId);
        if (one is not null) return [one];
        return fallbackToAll ? Sessions : [];
    }

    public Task NotifyAsync(string text, NotifyKind kind, bool chime, string? puckId)
    {
        var msg = new JsonObject
        {
            ["t"] = "notify", ["text"] = Clip(text, 120), ["chime"] = chime,
            ["kind"] = kind switch { NotifyKind.Ai => "ai", NotifyKind.Alert => "alert", _ => "info" },
        };
        foreach (var s in Targets(puckId, fallbackToAll: true)) s.Send(msg.DeepClone().AsObject());
        _events.Conversation("notify", "hub", "notify", text);
        return Task.CompletedTask;
    }

    public async Task SayAsync(string text, string? puckId, CancellationToken cancel)
    {
        var targets = Targets(puckId, fallbackToAll: true).ToList();
        await Task.WhenAll(targets.Select(s => s.Voice.SayAsync(text, cancel))).ConfigureAwait(false);
    }

    public Task<bool> ConfirmAsync(string question, ToolCaller caller, TimeSpan timeout, CancellationToken cancel)
    {
        if (caller.PuckId is null) return Task.FromResult(true); // typed on the hub's own page: deliberate
        var s = Session(caller.PuckId);
        return s is null ? Task.FromResult(false) : s.ConfirmAsync(question, timeout, cancel);
    }

    /// <summary>Send Wi-Fi credentials (normally over USB).</summary>
    public bool SendWifi(string puckId, string ssid, string password, string? hubUrl)
    {
        if (!_sessions.TryGetValue(puckId, out var s) || !s.IsConnected) return false;
        var usb = s.Connections.FirstOrDefault(c => c.Kind == "usb") ?? s.Primary!;
        usb.Send(new JsonObject { ["t"] = "wifi", ["ssid"] = ssid, ["pass"] = password });
        if (hubUrl is not null) usb.Send(new JsonObject { ["t"] = "hubaddr", ["url"] = hubUrl });
        return true;
    }

    // ------------------------------------------------------------------ the loop

    async Task LoopAsync(CancellationToken cancel)
    {
        var lastNowPlaying = DateTimeOffset.MinValue;
        var lastTime = DateTimeOffset.UtcNow;
        var lastDirect = DateTimeOffset.UtcNow;
        var lastPendingPing = DateTimeOffset.MinValue;
        while (!cancel.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(250, cancel).ConfigureAwait(false);
                foreach (var s in _sessions.Values) s.Tick();
                foreach (var (id, p) in _pending)
                    if (p.Connection.IsClosed || DateTimeOffset.UtcNow - p.Info.Since > TimeSpan.FromMinutes(10)) _pending.TryRemove(id, out _);
                var now = DateTimeOffset.UtcNow;
                // Pucks waiting for approval are pinged too: they say nothing while showing their
                // code, and the pongs keep the link (and NAT/Wi-Fi) alive until someone approves it
                if (now - lastPendingPing > PuckSession.PingInterval)
                {
                    lastPendingPing = now;
                    foreach (var (_, p) in _pending)
                        if (!p.Connection.IsClosed) p.Connection.Send(new JsonObject { ["t"] = "ping" });
                }
                if (now - lastNowPlaying > TimeSpan.FromSeconds(3))
                {
                    lastNowPlaying = now;
                    foreach (var s in Sessions) _ = RefreshNowPlayingAsync(s);
                }
                if (now - lastTime > TimeSpan.FromMinutes(10))
                {
                    lastTime = now;
                    var t = TimeMessage();
                    foreach (var s in Sessions) s.Send(t.DeepClone().AsObject());
                }
                if (now - lastDirect > DirectCheckInterval)
                {
                    // keys, engines or settings changed: the pucks get the new setup
                    lastDirect = now;
                    foreach (var s in Sessions) SendDirect(s);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception e) { _log.LogError(e, "Puck loop"); }
        }
    }

    static string Clip(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";
}
