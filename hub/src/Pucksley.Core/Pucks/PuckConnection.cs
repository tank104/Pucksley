using System.IO.Ports;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace Pucksley.Core.Pucks;

/// <summary>
/// One link to a puck: Wi-Fi (WebSocket) or USB (serial). Carries JSON messages and 16 kHz mono
/// 16-bit audio (see docs/PROTOCOL.md). Events fire on the connection's reader thread.
/// </summary>
public abstract class PuckConnection
{
    int _closed;

    /// <summary>No HTML-style escaping: apostrophes stay apostrophes (lines must fit in 1024 bytes on USB).</summary>
    protected static readonly System.Text.Json.JsonSerializerOptions Relaxed = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>"wifi" or "usb".</summary>
    public abstract string Kind { get; }

    /// <summary>Address or port, for the log and the web page.</summary>
    public abstract string Description { get; }

    /// <summary>A USB puck is trusted without approval: someone has it in their hands.</summary>
    public bool IsTrusted => Kind == "usb";

    public bool IsClosed => Volatile.Read(ref _closed) != 0;

    public DateTimeOffset LastReceived { get; private set; } = DateTimeOffset.UtcNow;

    /// <summary>Restarts the silence clock (e.g. when a puck that waited for approval is accepted).</summary>
    internal void Touch() => LastReceived = DateTimeOffset.UtcNow;

    public event Action<PuckConnection, JsonObject>? MessageReceived;
    public event Action<PuckConnection, short[]>? AudioReceived;
    public event Action<PuckConnection, string>? Closed;

    public abstract void Send(JsonObject message);

    public abstract void SendAudio(ReadOnlySpan<short> samples);

    /// <summary>
    /// A talking-face frame: a JPEG to show when the puck's speaker reaches <paramref name="pts"/>
    /// (samples of the current utterance). Wi-Fi only; false where the link can't carry video.
    /// </summary>
    public virtual bool SendVideoFrame(ReadOnlySpan<byte> jpeg, uint pts) => false;

    /// <summary>Messages not yet written (to notice a puck that stopped reading).</summary>
    public abstract int QueuedMessages { get; }

    public void Close(string reason)
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;
        try { CloseTransport(); } catch { }
        Closed?.Invoke(this, reason);
    }

    protected abstract void CloseTransport();

    /// <summary>The other end said hello: it's a puck (on USB, anything else with the same chip is not).</summary>
    public bool SaidHello { get; private set; }

    protected void OnMessage(JsonObject message)
    {
        LastReceived = DateTimeOffset.UtcNow;
        if (message["t"] is JsonValue t && t.TryGetValue<string>(out var type) && type == "hello") SaidHello = true;
        MessageReceived?.Invoke(this, message);
    }

    protected void OnAudio(short[] samples)
    {
        LastReceived = DateTimeOffset.UtcNow;
        AudioReceived?.Invoke(this, samples);
    }

    /// <summary>Parses one JSON line/frame; ignores anything that isn't a JSON object.</summary>
    protected static JsonObject? Parse(string text)
    {
        if (text.Length == 0 || text[0] != '{') return null;
        try { return JsonNode.Parse(text) as JsonObject; }
        catch (System.Text.Json.JsonException) { return null; }
    }

    protected static short[] ToSamples(ReadOnlySpan<byte> bytes)
    {
        var samples = new short[bytes.Length / 2];
        MemoryMarshal.Cast<byte, short>(bytes[..(samples.Length * 2)]).CopyTo(samples);
        return samples;
    }
}

/// <summary>A puck on Wi-Fi: text frames = JSON, binary frames = audio.</summary>
public sealed class WebSocketPuckConnection : PuckConnection
{
    readonly WebSocket _socket;
    readonly string _remote;
    readonly Channel<(WebSocketMessageType Type, byte[] Data)> _outbox =
        Channel.CreateBounded<(WebSocketMessageType, byte[])>(new BoundedChannelOptions(1024) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    readonly CancellationTokenSource _cts = new();

    public WebSocketPuckConnection(WebSocket socket, string remote)
    {
        _socket = socket;
        _remote = remote;
    }

    public override string Kind => "wifi";
    public override string Description => _remote;
    public override int QueuedMessages => _outbox.Reader.Count;

    /// <summary>Runs until the socket closes. Call from the request handler (it must stay alive for the socket).</summary>
    public async Task RunAsync(CancellationToken cancel)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancel, _cts.Token);
        var writer = WriteLoop(linked.Token);
        try
        {
            var buffer = new byte[16 * 1024];
            using var message = new MemoryStream();
            while (!linked.IsCancellationRequested && _socket.State == WebSocketState.Open)
            {
                var r = await _socket.ReceiveAsync(buffer, linked.Token).ConfigureAwait(false);
                if (r.MessageType == WebSocketMessageType.Close) break;
                message.Write(buffer, 0, r.Count);
                if (!r.EndOfMessage)
                {
                    if (message.Length > 1024 * 1024) break; // nothing legitimate is this big
                    continue;
                }
                var data = message.GetBuffer().AsSpan(0, (int)message.Length);
                if (r.MessageType == WebSocketMessageType.Text)
                {
                    var json = Parse(Encoding.UTF8.GetString(data));
                    if (json is not null) OnMessage(json);
                }
                else if (data.Length >= 2)
                {
                    OnAudio(ToSamples(data));
                }
                message.SetLength(0);
            }
            Close("disconnected");
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException or IOException or ObjectDisposedException)
        {
            Close(e is OperationCanceledException ? "closed" : e.Message);
        }
        try { await writer.ConfigureAwait(false); } catch { }
    }

    async Task WriteLoop(CancellationToken cancel)
    {
        try
        {
            await foreach (var (type, data) in _outbox.Reader.ReadAllAsync(cancel).ConfigureAwait(false))
            {
                if (_socket.State != WebSocketState.Open) break;
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                await _socket.SendAsync(data, type, true, timeout.Token).ConfigureAwait(false);
            }
            // The outbox was completed by Close(): everything queued before it (a "forget", a
            // rejection) has gone out; now say goodbye. Only this loop sends, so this can't overlap a send.
            if (_socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
                timeout.CancelAfter(TimeSpan.FromSeconds(2));
                await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", timeout.Token).ConfigureAwait(false);
            }
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException or IOException or ObjectDisposedException)
        {
            Close("send failed");
            _cts.Cancel();
        }
    }

    public override void Send(JsonObject message)
    {
        if (IsClosed) return;
        PuckText.CleanStrings(message);
        if (!_outbox.Writer.TryWrite((WebSocketMessageType.Text, Encoding.UTF8.GetBytes(message.ToJsonString(Relaxed)))))
            Close("the puck stopped reading");
    }

    public override void SendAudio(ReadOnlySpan<short> samples)
    {
        if (IsClosed || samples.Length == 0) return;
        var bytes = MemoryMarshal.AsBytes(samples).ToArray();
        if (!_outbox.Writer.TryWrite((WebSocketMessageType.Binary, bytes)))
            Close("the puck stopped reading");
    }

    /// <summary>Binary message: "PVF1", u32 pts (little-endian), then the JPEG (see docs/PROTOCOL.md).</summary>
    public override bool SendVideoFrame(ReadOnlySpan<byte> jpeg, uint pts)
    {
        if (IsClosed || jpeg.Length == 0) return false;
        var bytes = new byte[8 + jpeg.Length];
        "PVF1"u8.CopyTo(bytes);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), pts);
        jpeg.CopyTo(bytes.AsSpan(8));
        if (!_outbox.Writer.TryWrite((WebSocketMessageType.Binary, bytes)))
        {
            Close("the puck stopped reading");
            return false;
        }
        return true;
    }

    protected override void CloseTransport()
    {
        // Let the writer flush what's queued and send the close frame (cancelling straight away lost
        // the last messages, e.g. "forget" or "Not approved"). The reader ends when the puck answers
        // the close, or after a moment anyway.
        _outbox.Writer.TryComplete();
        try { _cts.CancelAfter(TimeSpan.FromSeconds(3)); }
        catch (ObjectDisposedException) { }
    }
}

/// <summary>
/// A puck on its USB cable (ESP32-S3 USB-Serial-JTAG). NDJSON lines, audio as base64 in "au" lines.
/// DTR/RTS stay off: toggling them can reset the chip.
/// </summary>
public sealed class SerialPuckConnection : PuckConnection
{
    const int MaxLine = 1024;
    const int AudioBytesPerLine = 640;   // 20 ms

    readonly SerialPort _port;
    readonly Channel<byte[]> _outbox = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(2048) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });

    public string PortName { get; }
    public override string Kind => "usb";
    public override string Description => PortName;
    public override int QueuedMessages => _outbox.Reader.Count;

    SerialPuckConnection(string portName, SerialPort port)
    {
        PortName = portName;
        _port = port;
    }

    public static SerialPuckConnection Open(string portName)
    {
        var port = new SerialPort(portName, 115200)
        {
            DtrEnable = false,
            RtsEnable = false,
            Handshake = Handshake.None,
            ReadTimeout = SerialPort.InfiniteTimeout,
            WriteTimeout = 2000,
        };
        port.Open();
        var c = new SerialPuckConnection(portName, port);
        new Thread(c.ReadLoop) { IsBackground = true, Name = $"Puck {portName} reader" }.Start();
        new Thread(c.WriteLoop) { IsBackground = true, Name = $"Puck {portName} writer" }.Start();
        return c;
    }

    void ReadLoop()
    {
        var buf = new byte[8192];
        var line = new List<byte>(MaxLine);
        bool overflow = false;
        try
        {
            var stream = _port.BaseStream;
            while (!IsClosed)
            {
                int n = stream.Read(buf, 0, buf.Length);
                if (n <= 0) break;
                for (int i = 0; i < n; i++)
                {
                    byte b = buf[i];
                    if (b == (byte)'\n')
                    {
                        if (!overflow && line.Count > 0) HandleLine(Encoding.UTF8.GetString(CollectionsMarshal.AsSpan(line)).TrimEnd('\r'));
                        line.Clear();
                        overflow = false;
                    }
                    else if (line.Count < MaxLine * 2) line.Add(b);
                    else overflow = true;
                }
            }
            Close("port closed");
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or UnauthorizedAccessException or OperationCanceledException or ObjectDisposedException)
        {
            Close(e.Message);
        }
    }

    void HandleLine(string text)
    {
        var json = Parse(text);
        if (json is null) return;
        // TryGetValue, not GetValue: a line like {"t":5} must be ignored, not end the reader (and the link)
        if (json["t"] is JsonValue t && t.TryGetValue<string>(out var type) && type == "au")
        {
            if (json["d"] is JsonValue d && d.TryGetValue<string>(out var b64) && b64.Length > 0)
            {
                try { OnAudio(ToSamples(Convert.FromBase64String(b64))); }
                catch (FormatException) { }
            }
            return;
        }
        OnMessage(json);
    }

    void WriteLoop()
    {
        try
        {
            var reader = _outbox.Reader;
            while (reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult())
            {
                while (reader.TryRead(out var data))
                {
                    if (IsClosed) return;
                    _port.BaseStream.Write(data, 0, data.Length);
                }
            }
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or TimeoutException or UnauthorizedAccessException or ObjectDisposedException)
        {
            Close(e.Message);
        }
    }

    public override void Send(JsonObject message)
    {
        if (IsClosed) return;
        PuckText.CleanStrings(message);
        var bytes = Encoding.UTF8.GetBytes(message.ToJsonString(Relaxed) + "\n");
        if (!_outbox.Writer.TryWrite(bytes)) Close("the puck stopped reading");
    }

    public override void SendAudio(ReadOnlySpan<short> samples)
    {
        if (IsClosed) return;
        var bytes = MemoryMarshal.AsBytes(samples);
        for (int off = 0; off < bytes.Length; off += AudioBytesPerLine)
        {
            var part = bytes.Slice(off, Math.Min(AudioBytesPerLine, bytes.Length - off));
            var line = Encoding.ASCII.GetBytes($"{{\"t\":\"au\",\"d\":\"{Convert.ToBase64String(part)}\"}}\n");
            if (!_outbox.Writer.TryWrite(line))
            {
                Close("the puck stopped reading");
                return;
            }
        }
    }

    protected override void CloseTransport()
    {
        _outbox.Writer.TryComplete();
        try { if (_port.IsOpen) _port.Close(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException) { }
        _port.Dispose();
    }
}
