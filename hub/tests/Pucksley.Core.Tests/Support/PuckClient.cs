using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace Pucksley.Core.Tests.Support;

/// <summary>Something the hub sent: a JSON message, or a binary audio frame.</summary>
public sealed record PuckFrame(JsonObject? Json, byte[]? Audio)
{
    public string? Type => Json?["t"]?.GetValue<string>();
    public bool IsAudio => Audio is not null;
    public override string ToString() => Json?.ToJsonString() ?? $"<audio {Audio?.Length} bytes>";
}

/// <summary>
/// A pretend puck on the hub's WebSocket (docs/PROTOCOL.md): JSON in text frames, 16 kHz PCM in
/// binary frames. Answers <c>ping</c> with <c>pong</c> like the firmware does (pings don't show up
/// in the inbox).
/// </summary>
public sealed class PuckClient : IAsyncDisposable
{
    readonly ClientWebSocket _ws = new();
    readonly Channel<PuckFrame> _inbox = Channel.CreateUnbounded<PuckFrame>();
    readonly List<PuckFrame> _seen = [];
    readonly SemaphoreSlim _sendGate = new(1, 1);
    readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    Task? _reader;
    int _pings;

    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    PuckClient(string id) => Id = id;

    public string Id { get; }
    public string? Token { get; set; }
    public bool AutoPong { get; set; } = true;
    public int PingsReceived => Volatile.Read(ref _pings);
    public bool IsClosed => _closed.Task.IsCompleted;
    public WebSocketCloseStatus? CloseStatus => _ws.CloseStatus;

    /// <summary>Every frame taken from the inbox so far, in order.</summary>
    public IReadOnlyList<PuckFrame> Seen { get { lock (_seen) return _seen.ToList(); } }

    public static string NewId() => Guid.NewGuid().ToString("n")[..16];

    public static async Task<PuckClient> ConnectAsync(int port, string? id = null)
    {
        var c = new PuckClient(id ?? NewId());
        await c._ws.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/puck"), CancellationToken.None);
        c._reader = Task.Run(c.ReadLoopAsync);
        return c;
    }

    async Task ReadLoopAsync()
    {
        var buf = new byte[64 * 1024];
        using var message = new MemoryStream();
        try
        {
            while (_ws.State is WebSocketState.Open or WebSocketState.CloseSent)
            {
                var r = await _ws.ReceiveAsync(buf, CancellationToken.None);
                if (r.MessageType == WebSocketMessageType.Close)
                {
                    try { await _ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None); }
                    catch (Exception e) when (e is WebSocketException or ObjectDisposedException or InvalidOperationException) { }
                    break;
                }
                message.Write(buf, 0, r.Count);
                if (!r.EndOfMessage) continue;
                var data = message.ToArray();
                message.SetLength(0);
                if (r.MessageType == WebSocketMessageType.Text)
                {
                    var json = JsonNode.Parse(Encoding.UTF8.GetString(data))!.AsObject();
                    if (json["t"]?.GetValue<string>() == "ping")
                    {
                        Interlocked.Increment(ref _pings);
                        if (AutoPong) _ = SendAsync("""{"t":"pong"}""");
                        continue;
                    }
                    _inbox.Writer.TryWrite(new PuckFrame(json, null));
                }
                else _inbox.Writer.TryWrite(new PuckFrame(null, data));
            }
        }
        catch (Exception e) when (e is WebSocketException or ObjectDisposedException or OperationCanceledException) { }
        finally
        {
            _closed.TrySetResult();
            _inbox.Writer.TryComplete();
        }
    }

    // ---------------------------------------------------------------- sending

    public async Task SendAsync(string json)
    {
        await _sendGate.WaitAsync();
        try
        {
            if (_ws.State != WebSocketState.Open) return;
            await _ws.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, CancellationToken.None);
        }
        catch (Exception e) when (e is WebSocketException or ObjectDisposedException) { }
        finally { _sendGate.Release(); }
    }

    public Task SendAsync(JsonObject message) => SendAsync(message.ToJsonString());

    /// <summary>Mic audio, in 20 ms (640-byte) frames like the firmware.</summary>
    public async Task SendAudioAsync(short[] samples, int frameSamples = 320)
    {
        for (int off = 0; off < samples.Length; off += frameSamples)
        {
            var part = samples.AsSpan(off, Math.Min(frameSamples, samples.Length - off));
            var bytes = MemoryMarshal.AsBytes(part).ToArray();
            await _sendGate.WaitAsync();
            try
            {
                if (_ws.State != WebSocketState.Open) return;
                await _ws.SendAsync(bytes, WebSocketMessageType.Binary, true, CancellationToken.None);
            }
            finally { _sendGate.Release(); }
        }
    }

    public Task HelloAsync(string? token = null, string name = "Puck-TEST") => SendAsync(new JsonObject
    {
        ["t"] = "hello", ["v"] = 1, ["fw"] = "0.1.0", ["id"] = Id, ["name"] = name, ["token"] = token ?? Token ?? "",
        ["bat"] = 87, ["chg"] = false, ["usb"] = true, ["ip"] = "192.168.1.50", ["rssi"] = -55, ["ssid"] = "Home",
        ["face"] = "assistant", ["caps"] = new JsonArray("mic", "spk"),
    });

    public Task TalkAsync(string action, string? via = null)
    {
        var m = new JsonObject { ["t"] = "talk", ["a"] = action };
        if (via is not null) m["via"] = via;
        return SendAsync(m);
    }

    public Task SpeakerDoneAsync(int id) => SendAsync(new JsonObject { ["t"] = "spk", ["s"] = "done", ["id"] = id });

    // ---------------------------------------------------------------- receiving

    /// <summary>The next frame, or null when the link closed or nothing came in time.</summary>
    public async Task<PuckFrame?> NextAsync(TimeSpan? timeout = null)
    {
        using var cts = new CancellationTokenSource(timeout ?? DefaultTimeout);
        try
        {
            if (!await _inbox.Reader.WaitToReadAsync(cts.Token)) return null;
            if (!_inbox.Reader.TryRead(out var f)) return null;
            lock (_seen) _seen.Add(f);
            return f;
        }
        catch (OperationCanceledException) { return null; }
    }

    /// <summary>Takes frames until a JSON message of <paramref name="type"/> (matching <paramref name="match"/>) arrives.</summary>
    public async Task<JsonObject> WaitForAsync(string type, Func<JsonObject, bool>? match = null, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? DefaultTimeout);
        var skipped = new List<string>();
        while (true)
        {
            var left = deadline - DateTime.UtcNow;
            var f = left > TimeSpan.Zero ? await NextAsync(left) : null;
            if (f is null)
                throw new TimeoutException($"Puck {Id}: no '{type}' message (closed={IsClosed}). Skipped: {string.Join(" | ", skipped.TakeLast(25))}");
            if (f.Type == type && (match is null || match(f.Json!))) return f.Json!;
            skipped.Add(f.IsAudio ? "<audio>" : f.Json!.ToJsonString());
        }
    }

    /// <summary>Takes frames up to and including the first one matching <paramref name="stop"/>.</summary>
    public async Task<List<PuckFrame>> CollectUntilAsync(Func<PuckFrame, bool> stop, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? DefaultTimeout);
        var frames = new List<PuckFrame>();
        while (true)
        {
            var left = deadline - DateTime.UtcNow;
            var f = left > TimeSpan.Zero ? await NextAsync(left) : null;
            if (f is null)
                throw new TimeoutException($"Puck {Id}: stop condition not met (closed={IsClosed}). Got: {string.Join(" | ", frames.Where(x => !x.IsAudio).Select(x => x.ToString()).TakeLast(25))}");
            frames.Add(f);
            if (stop(f)) return frames;
        }
    }

    /// <summary>Frames that arrive within <paramref name="window"/> (doesn't fail when none do).</summary>
    public async Task<List<PuckFrame>> DrainAsync(TimeSpan window)
    {
        var frames = new List<PuckFrame>();
        var deadline = DateTime.UtcNow + window;
        while (true)
        {
            var left = deadline - DateTime.UtcNow;
            if (left <= TimeSpan.Zero) return frames;
            var f = await NextAsync(left);
            if (f is null) return frames;
            frames.Add(f);
        }
    }

    public async Task<bool> WaitClosedAsync(TimeSpan? timeout = null) =>
        await Task.WhenAny(_closed.Task, Task.Delay(timeout ?? DefaultTimeout)) == _closed.Task;

    /// <summary>Hangs up without a close handshake (Wi-Fi gone, battery out…).</summary>
    public void Abort() => _ws.Abort();

    public async Task CloseAsync()
    {
        try { await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", new CancellationTokenSource(TimeSpan.FromSeconds(3)).Token); }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException or ObjectDisposedException or InvalidOperationException) { _ws.Abort(); }
    }

    public async ValueTask DisposeAsync()
    {
        if (_ws.State == WebSocketState.Open) await CloseAsync();
        else _ws.Abort();
        if (_reader is not null) await Task.WhenAny(_reader, Task.Delay(2000));
        _ws.Dispose();
    }
}
