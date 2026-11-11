using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Pucksley.Sdk;

namespace Pucksley.Plugins.Speech;

/// <summary>
/// One live recognition on Deepgram's streaming API (wss://…/v1/listen): the audio goes up as it's
/// heard, interim and final words come back, and Deepgram says when the person has finished
/// ("speech_final" after a pause, or "UtteranceEnd" from the words' timing, which background noise
/// can't hold open).
/// </summary>
internal sealed class DeepgramStream : ISpeechStream
{
    readonly ClientWebSocket _ws = new();
    readonly Channel<byte[]> _outgoing = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
    readonly CancellationTokenSource _stop = new();
    readonly ILogger _log;
    readonly object _gate = new();
    readonly List<string> _finals = [];
    string _interim = "";
    Task? _sender, _receiver;
    volatile bool _ended, _failed, _finishing;

    // A control message, queued in order with the audio
    static readonly byte[] CloseMarker = [];

    DeepgramStream(ILogger log) => _log = log;

    public event Action? Changed;

    public string Text
    {
        get
        {
            lock (_gate) return Join(_interim.Length > 0 ? [.. _finals, _interim] : _finals);
        }
    }

    public bool Ended => _ended;
    public bool Failed => _failed;

    public static async Task<DeepgramStream> OpenAsync(Uri url, string key, ILogger log, CancellationToken cancel)
    {
        var s = new DeepgramStream(log);
        s._ws.Options.SetRequestHeader("Authorization", $"Token {key}");
        s._ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(10);
        try
        {
            await s._ws.ConnectAsync(url, cancel).ConfigureAwait(false);
        }
        catch
        {
            s._ws.Dispose();
            throw;
        }
        s._sender = Task.Run(s.SendLoopAsync);
        s._receiver = Task.Run(s.ReceiveLoopAsync);
        return s;
    }

    public void Send(ReadOnlySpan<short> samples)
    {
        if (samples.IsEmpty || _finishing || _failed) return;
        _outgoing.Writer.TryWrite(System.Runtime.InteropServices.MemoryMarshal.AsBytes(samples).ToArray());
    }

    public async Task<string> FinishAsync(CancellationToken cancel)
    {
        _finishing = true;
        // CloseStream: Deepgram finishes the audio it has, sends the last results and closes
        _outgoing.Writer.TryWrite(CloseMarker);
        _outgoing.Writer.TryComplete();
        if (_receiver is { } receiver)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            try { await receiver.WaitAsync(timeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
            {
                _log.LogInformation("Deepgram: the last words took too long, using what was heard so far");
            }
        }
        cancel.ThrowIfCancellationRequested();
        if (_failed && Text.Length == 0) throw new PluginException("Deepgram's live recognition stopped working");
        return Text;
    }

    async Task SendLoopAsync()
    {
        try
        {
            while (true)
            {
                // Deepgram closes a stream that's quiet for ~10 s: keep it alive between chunks
                bool more;
                using (var idle = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token))
                {
                    idle.CancelAfter(TimeSpan.FromSeconds(5));
                    try { more = await _outgoing.Reader.WaitToReadAsync(idle.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (!_stop.IsCancellationRequested)
                    {
                        await SendTextAsync("{\"type\":\"KeepAlive\"}").ConfigureAwait(false);
                        continue;
                    }
                }
                if (!more) return;
                while (_outgoing.Reader.TryRead(out var chunk))
                {
                    await SendChunkAsync(chunk).ConfigureAwait(false);
                    if (ReferenceEquals(chunk, CloseMarker)) return;
                }
            }
        }
        catch (ChannelClosedException) { }
        catch (OperationCanceledException) { }
        catch (Exception e) when (e is WebSocketException or ObjectDisposedException or InvalidOperationException)
        {
            Fail(e.Message);
        }
    }

    Task SendChunkAsync(byte[] chunk) => ReferenceEquals(chunk, CloseMarker)
        ? SendTextAsync("{\"type\":\"CloseStream\"}")
        : _ws.SendAsync(chunk, WebSocketMessageType.Binary, true, _stop.Token);

    Task SendTextAsync(string json) => _ws.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, _stop.Token);

    async Task ReceiveLoopAsync()
    {
        var buffer = new byte[16 * 1024];
        var message = new MemoryStream();
        try
        {
            while (_ws.State == WebSocketState.Open || _ws.State == WebSocketState.CloseSent)
            {
                var r = await _ws.ReceiveAsync(buffer, _stop.Token).ConfigureAwait(false);
                if (r.MessageType == WebSocketMessageType.Close) break;
                message.Write(buffer, 0, r.Count);
                if (!r.EndOfMessage) continue;
                if (r.MessageType == WebSocketMessageType.Text) Handle(message.ToArray());
                message.SetLength(0);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e) when (e is WebSocketException or ObjectDisposedException)
        {
            if (!_finishing) Fail(e.Message);
        }
    }

    void Handle(byte[] json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
            bool changed = false;
            switch (type)
            {
                case "Results":
                {
                    var text = Transcript(root);
                    bool isFinal = root.TryGetProperty("is_final", out var f) && f.ValueKind == JsonValueKind.True;
                    bool speechFinal = root.TryGetProperty("speech_final", out var sf) && sf.ValueKind == JsonValueKind.True;
                    lock (_gate)
                    {
                        if (isFinal)
                        {
                            if (text.Length > 0) _finals.Add(text);
                            changed = _interim.Length > 0 || text.Length > 0;
                            _interim = "";
                        }
                        else if (text != _interim)
                        {
                            _interim = text;
                            changed = true;
                        }
                        if (speechFinal && _finals.Count > 0 && !_ended)
                        {
                            _ended = true;
                            changed = true;
                        }
                    }
                    break;
                }
                case "UtteranceEnd":
                    lock (_gate)
                    {
                        if (!_ended && (_finals.Count > 0 || _interim.Length > 0))
                        {
                            // Words, then a long enough gap after the last one: promote what's pending
                            if (_interim.Length > 0)
                            {
                                _finals.Add(_interim);
                                _interim = "";
                            }
                            _ended = true;
                            changed = true;
                        }
                    }
                    break;
                case "Error":
                    Fail(root.TryGetProperty("description", out var d) ? d.GetString() ?? "error" : "error");
                    return;
            }
            if (changed) Changed?.Invoke();
        }
        catch (JsonException) { }
    }

    static string Transcript(JsonElement root) =>
        root.TryGetProperty("channel", out var ch) && ch.TryGetProperty("alternatives", out var alts) &&
        alts.ValueKind == JsonValueKind.Array && alts.GetArrayLength() > 0 &&
        alts[0].TryGetProperty("transcript", out var tr) && tr.ValueKind == JsonValueKind.String
            ? tr.GetString()!.Trim()
            : "";

    static string Join(IEnumerable<string> parts) => string.Join(' ', parts.Where(p => p.Length > 0)).Trim();

    void Fail(string why)
    {
        if (_failed) return;
        _failed = true;
        _log.LogWarning("Deepgram live recognition failed: {Why}", why);
        Changed?.Invoke();
    }

    public async ValueTask DisposeAsync()
    {
        _finishing = true;
        _outgoing.Writer.TryComplete();
        _stop.Cancel();
        try
        {
            if (_ws.State == WebSocketState.Open)
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                await _ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", cts.Token).ConfigureAwait(false);
            }
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException or ObjectDisposedException) { }
        foreach (var task in new[] { _sender, _receiver })
            if (task is not null)
                try { await task.ConfigureAwait(false); }
                catch { }
        _ws.Dispose();
        _stop.Dispose();
    }
}
