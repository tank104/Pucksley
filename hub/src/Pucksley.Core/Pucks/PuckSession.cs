using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Pucksley.Core.Settings;
using Pucksley.Sdk;

namespace Pucksley.Core.Pucks;

/// <summary>
/// A puck that is connected (over Wi-Fi, USB or both). Routes its messages, keeps it alive, and
/// owns its voice conversation.
/// </summary>
public sealed class PuckSession
{
    /// <summary>How often the hub pings each link (docs/PROTOCOL.md: 5 s). Tests shorten it.</summary>
    internal static TimeSpan PingInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>A link that sends nothing for this long is dropped (docs/PROTOCOL.md: 15 s). Tests shorten it.</summary>
    internal static TimeSpan SilenceTimeout { get; set; } = TimeSpan.FromSeconds(15);

    readonly PuckManager _manager;
    readonly ILogger _log;
    readonly List<PuckConnection> _connections = [];
    readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> _confirms = new();
    int _confirmSeq;
    DateTimeOffset _lastPing = DateTimeOffset.MinValue;

    internal PuckSession(PuckManager manager, string id, ILogger log)
    {
        _manager = manager;
        Id = id;
        _log = log;
        Voice = new VoiceSession(this, manager.Agent, manager.Speech, manager.Settings, log);
    }

    public string Id { get; }
    public VoiceSession Voice { get; }
    public bool Welcomed { get; internal set; }
    public string? PendingCode { get; internal set; }

    // What the puck last told us
    public string DeviceName { get; private set; } = "";
    public string Firmware { get; private set; } = "";
    public int Battery { get; private set; } = -1;
    public bool Charging { get; private set; }
    public bool UsbPower { get; private set; }
    public string? Ip { get; private set; }
    public int? Rssi { get; private set; }
    public string? Ssid { get; private set; }
    public string Face { get; private set; } = "";
    public DateTimeOffset ConnectedAt { get; } = DateTimeOffset.UtcNow;
    internal JsonObject? LastState { get; set; }
    /// <summary>What direct mode setup this puck was last sent (a fingerprint), on which link, and in words.</summary>
    internal string? DirectSent { get; set; }
    internal PuckConnection? DirectLink { get; set; }
    internal object DirectLock { get; } = new();
    public string DirectMode { get; internal set; } = "";
    internal string? FollowedSource { get; set; }

    public PuckRecord? Record => _manager.Record(Id);
    public string Name => Record?.DisplayName ?? (DeviceName.Length > 0 ? DeviceName : Id);
    public string SessionKey => "puck:" + Id;
    public ToolCaller Caller => new(Id, Name, string.IsNullOrWhiteSpace(Record?.Room) ? null : Record!.Room.Trim());

    public IReadOnlyList<PuckConnection> Connections { get { lock (_connections) return _connections.ToList(); } }

    /// <summary>Where messages go: Wi-Fi when it's up (audio needs it), else USB.</summary>
    public PuckConnection? Primary
    {
        get
        {
            lock (_connections)
                return _connections.FirstOrDefault(c => c.Kind == "wifi" && !c.IsClosed) ?? _connections.FirstOrDefault(c => !c.IsClosed);
        }
    }

    public bool IsConnected => Primary is not null;

    public void Send(JsonObject message)
    {
        bool voiceState = Str(message, "t") == "ai";
        Primary?.Send(message);
        // The page shows each puck's voice state: tell it (HubEvents throttles these)
        if (voiceState) _manager.StatusChanged();
    }

    public void SendAudio(ReadOnlySpan<short> samples) => Primary?.SendAudio(samples);

    internal void AddConnection(PuckConnection c)
    {
        lock (_connections)
        {
            if (!_connections.Contains(c)) _connections.Add(c);
        }
    }

    /// <summary>Returns true when that was the last connection.</summary>
    internal bool RemoveConnection(PuckConnection c)
    {
        lock (_connections)
        {
            _connections.Remove(c);
            _connections.RemoveAll(x => x.IsClosed);
            if (_connections.Count > 0) return false;
        }
        Voice.OnDisconnected();
        foreach (var (_, tcs) in _confirms) tcs.TrySetResult(false);
        return true;
    }

    internal void UpdateFromHello(JsonObject hello)
    {
        DeviceName = Str(hello, "name") ?? DeviceName;
        Firmware = Str(hello, "fw") ?? Firmware;
        if (hello["bat"] is JsonValue) Battery = Int(hello, "bat") ?? Battery;
        Charging = Bool(hello, "chg") ?? Charging;
        UsbPower = Bool(hello, "usb") ?? UsbPower;
        Ip = Str(hello, "ip") ?? Ip;
        Rssi = Int(hello, "rssi") ?? Rssi;
        Ssid = Str(hello, "ssid") ?? Ssid;
        Face = Str(hello, "face") ?? Face;
    }

    /// <summary>A message from the puck (already welcomed).</summary>
    internal void Handle(PuckConnection from, JsonObject m)
    {
        var t = Str(m, "t");
        switch (t)
        {
            case "pong":
                break;
            case "talk":
                Voice.OnTalk(Str(m, "a") ?? "", Str(m, "via"));
                break;
            case "spk":
                if (Str(m, "s") == "done") Voice.OnSpeakerDone(Int(m, "id") ?? -1);
                break;
            case "ctl":
                var id = Str(m, "id") is { } shortId ? _manager.ControlGlobalId(shortId) : null;
                if (id is null) break;
                var input = new ControlInput
                {
                    Value = m["v"] is JsonValue ? Dbl(m, "v") : null,
                    On = Bool(m, "on"),
                    Pressed = Str(m, "a") == "press",
                };
                _ = _manager.Plugins.OnControlAsync(id, input, CancellationToken.None);
                break;
            case "media":
                _ = _manager.MediaFromPuckAsync(this, Str(m, "a") ?? "", Int(m, "v"));
                break;
            case "timer":
                _ = _manager.TimerFromPuckAsync(this, m);
                break;
            case "ans":
                if (Str(m, "id") is { } cid && _confirms.TryRemove(cid, out var tcs)) tcs.TrySetResult(Bool(m, "ok") == true);
                break;
            case "spkvol":
                if (Int(m, "v") is { } vol) _manager.UpdateRecord(Id, r => r.SpeakerVolume = Math.Clamp(vol, 0, 100), resendConfig: false);
                break;
            case "face":
                Face = Str(m, "f") ?? Face;
                _manager.StatusChanged();
                break;
            case "bat":
                Battery = Int(m, "p") ?? Battery;
                Charging = Bool(m, "chg") ?? Charging;
                UsbPower = Bool(m, "usb") ?? UsbPower;
                _manager.StatusChanged();
                break;
            case "net":
                Ip = Str(m, "ip") ?? Ip;
                Rssi = Int(m, "rssi") ?? Rssi;
                Ssid = Str(m, "ssid") ?? Ssid;
                _manager.StatusChanged();
                break;
            case "wifi":
                _log.LogInformation("{Puck} Wi-Fi: {State}", Name, Str(m, "s"));
                _manager.StatusChanged();
                break;
            case "log":
                _log.LogInformation("{Puck}: {Message}", Name, Str(m, "m"));
                break;
            case "diag":
                _log.LogInformation("{Puck} diagnostics ({Kind}): {Message}", Name, from.Kind, Str(m, "m"));
                break;
            case "hello":
                UpdateFromHello(m);
                break;
        }
    }

    /// <summary>Ask the person on the puck; false on no, timeout or disconnect.</summary>
    public async Task<bool> ConfirmAsync(string question, TimeSpan timeout, CancellationToken cancel)
    {
        if (!IsConnected) return false;
        var id = "c" + Interlocked.Increment(ref _confirmSeq);
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _confirms[id] = tcs;
        Send(new JsonObject { ["t"] = "confirm", ["id"] = id, ["text"] = question.Length > 160 ? question[..157] + "…" : question });
        try
        {
            return await tcs.Task.WaitAsync(timeout, cancel).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return false;
        }
        finally
        {
            if (_confirms.TryRemove(id, out _)) Send(new JsonObject { ["t"] = "confirm_end", ["id"] = id });
        }
    }

    /// <summary>Keepalive and timeouts, a few times a second.</summary>
    internal void Tick()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var c in Connections)
        {
            if (now - c.LastReceived > SilenceTimeout) c.Close($"no answer for {SilenceTimeout.TotalSeconds:0} s");
        }
        if (now - _lastPing > PingInterval)
        {
            _lastPing = now;
            foreach (var c in Connections) c.Send(new JsonObject { ["t"] = "ping" });
        }
        if (Welcomed) Voice.Tick();
    }

    internal static string? Str(JsonObject o, string k) => o[k] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
    internal static int? Int(JsonObject o, string k) => o[k] is JsonValue v ? (v.TryGetValue<int>(out var i) ? i : v.TryGetValue<double>(out var d) ? (int)Math.Round(d) : null) : null;
    internal static double? Dbl(JsonObject o, string k) => o[k] is JsonValue v ? (v.TryGetValue<double>(out var d) ? d : v.TryGetValue<int>(out var i) ? i : null) : null;
    internal static bool? Bool(JsonObject o, string k) => o[k] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;
}
