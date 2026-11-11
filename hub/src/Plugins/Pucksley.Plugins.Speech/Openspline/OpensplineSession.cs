using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Pucksley.Sdk;

namespace Pucksley.Plugins.Speech.Openspline;

/// <summary>
/// One Openspline avatar session (the wire protocol its Python SDK speaks):
/// <list type="bullet">
/// <item><c>POST /v1/sessions</c> with the portrait → id, a publisher token, and a viewer link holding the viewer token.</item>
/// <item><c>WS /v1/sessions/{id}/audio</c> (publisher): requests <c>{"type":"audio"|"end_turn"|"ping","id":…}</c>, each answered by an event with the same id.</item>
/// <item><c>WS /v1/sessions/{id}/playback</c> (viewer): <c>connected</c>, then <c>idle</c> and <c>media</c> events (a base64 JPEG and
/// 48 kHz s16le audio each) that must be acknowledged (<c>idle_played</c>, <c>played</c>), <c>turn_end</c>, <c>interrupted</c>.
/// The service keeps at most about a second of audio unacknowledged, so acknowledging as the hub forwards paces it.</item>
/// </list>
/// </summary>
public sealed class OpensplineSession : IAsyncDisposable
{
    readonly Uri _base;
    readonly HttpClient _http;
    readonly ILogger _log;
    readonly ClientWebSocket _audio = new(), _playback = new();
    readonly SemaphoreSlim _audioSend = new(1, 1), _playbackSend = new(1, 1);
    readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _pending = new();
    readonly CancellationTokenSource _stop = new();
    readonly TaskCompletionSource _viewerReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    Task? _audioLoop, _playbackLoop, _heartbeat;
    volatile int _epoch;
    Turn? _turn;

    public string Id { get; private set; } = "";
    string _publisherToken = "", _viewerToken = "";

    /// <summary>Both sockets are up and the service has our viewer.</summary>
    public bool IsOpen => !Failed && _viewerReady.Task.IsCompletedSuccessfully;

    /// <summary>A socket closed or failed: make a new session.</summary>
    public bool Failed { get; private set; }

    public static TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(30);

    OpensplineSession(Uri baseUrl, HttpClient http, ILogger log)
    {
        _base = baseUrl;
        _http = http;
        _log = log;
    }

    public static async Task<OpensplineSession> OpenAsync(Uri baseUrl, HttpClient http, byte[] portrait, string quality, ILogger log, CancellationToken cancel)
    {
        var s = new OpensplineSession(baseUrl, http, log);
        try
        {
            await s.CreateAsync(portrait, quality, cancel).ConfigureAwait(false);
            await s.ConnectAsync(cancel).ConfigureAwait(false);
            return s;
        }
        catch
        {
            await s.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    async Task CreateAsync(byte[] portrait, string quality, CancellationToken cancel)
    {
        using var form = new MultipartFormDataContent();
        var image = new ByteArrayContent(portrait);
        image.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(image, "portrait", "portrait");
        form.Add(new StringContent(quality), "quality");
        using var response = await _http.PostAsync(new Uri(_base, "v1/sessions"), form, cancel).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(cancel).ConfigureAwait(false);
        if ((int)response.StatusCode == 429)
            throw new OpensplineBusyException(Shorten(text));
        if (!response.IsSuccessStatusCode)
            throw new PluginException($"Openspline wouldn't start the avatar ({(int)response.StatusCode}): {Shorten(text)}");
        var body = JsonNode.Parse(text) ?? throw new PluginException("Openspline's answer was empty");
        Id = body["id"]?.GetValue<string>() ?? throw new PluginException("Openspline's answer had no session id");
        _publisherToken = body["publisher_token"]?.GetValue<string>() ?? throw new PluginException("Openspline's answer had no token");
        // The viewer link carries the viewer's own token: …/view#<id>:<token>
        var viewer = body["viewer_url"]?.GetValue<string>() ?? "";
        int hash = viewer.IndexOf('#');
        var pair = hash >= 0 ? viewer[(hash + 1)..].Split(':', 2) : [];
        _viewerToken = pair.Length == 2 ? pair[1] : _publisherToken;
    }

    async Task ConnectAsync(CancellationToken cancel)
    {
        var ws = new UriBuilder(_base) { Scheme = _base.Scheme == "https" ? "wss" : "ws" }.Uri;
        await _audio.ConnectAsync(new Uri(ws, $"v1/sessions/{Id}/audio"), cancel).ConfigureAwait(false);
        await SendAsync(_audio, _audioSend, new JsonObject { ["token"] = _publisherToken }, cancel).ConfigureAwait(false);
        await _playback.ConnectAsync(new Uri(ws, $"v1/sessions/{Id}/playback"), cancel).ConfigureAwait(false);
        await SendAsync(_playback, _playbackSend, new JsonObject { ["token"] = _viewerToken }, cancel).ConfigureAwait(false);
        _audioLoop = Task.Run(() => AudioLoopAsync(_stop.Token), CancellationToken.None);
        _playbackLoop = Task.Run(() => PlaybackLoopAsync(_stop.Token), CancellationToken.None);
        _heartbeat = Task.Run(() => HeartbeatAsync(_stop.Token), CancellationToken.None);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        await _viewerReady.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
    }

    // ---------------------------------------------------------------- turns

    /// <summary>Starts a reply. One at a time: a new one ends the last.</summary>
    public Turn BeginTurn()
    {
        var turn = new Turn(this, _epoch);
        Interlocked.Exchange(ref _turn, turn)?.Complete();
        return turn;
    }

    /// <summary>One reply's audio in, frames out.</summary>
    public sealed class Turn : ITalkingFaceTurn
    {
        readonly OpensplineSession _s;
        readonly Channel<(FaceMedia Media, long Samples, int Epoch)> _out = Channel.CreateUnbounded<(FaceMedia, long, int)>(new UnboundedChannelOptions { SingleReader = true });
        long _target = -1, _last;
        internal int Epoch;

        internal Turn(OpensplineSession s, int epoch)
        {
            _s = s;
            Epoch = epoch;
        }

        internal void Complete() => _out.Writer.TryComplete();

        internal void Media(FaceMedia media, long samples, int epoch)
        {
            _last = samples;
            _out.Writer.TryWrite((media, samples, epoch));
            if (_target >= 0 && _last >= _target) Complete();
        }

        internal void TurnEnd(long target)
        {
            _target = target;
            if (_last >= target) Complete();
        }

        public async Task SendAudioAsync(ReadOnlyMemory<short> pcm, int sampleRate, CancellationToken cancel)
        {
            // 100 ms per request, as the SDK does; each waits for the service to take it (backpressure)
            int step = sampleRate / 10;
            for (int off = 0; off < pcm.Length; off += step)
            {
                var part = pcm.Slice(off, Math.Min(step, pcm.Length - off));
                var bytes = MemoryMarshal.AsBytes(part.Span).ToArray();
                await _s.RequestAsync(new JsonObject
                {
                    ["type"] = "audio", ["epoch"] = Epoch, ["data"] = Convert.ToBase64String(bytes),
                    ["format"] = new JsonObject { ["sample_rate"] = sampleRate, ["channels"] = 1, ["encoding"] = "pcm_s16le" },
                }, cancel).ConfigureAwait(false);
            }
        }

        public Task EndAsync(CancellationToken cancel) =>
            _s.RequestAsync(new JsonObject { ["type"] = "end_turn", ["drain"] = true }, cancel);

        public async IAsyncEnumerable<FaceMedia> ReadAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancel)
        {
            await foreach (var (media, samples, epoch) in _out.Reader.ReadAllAsync(cancel).ConfigureAwait(false))
            {
                // Acknowledge as the hub takes it on: the service then stays about a second ahead of the puck
                await _s.PlaybackSendAsync(new JsonObject { ["type"] = "played", ["samples"] = samples, ["epoch"] = epoch }, cancel).ConfigureAwait(false);
                yield return media;
            }
        }

        public async Task InterruptAsync()
        {
            Complete();
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(_s._base, $"v1/sessions/{_s.Id}/interrupt"));
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _s._publisherToken);
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                using var response = await _s._http.SendAsync(request, cts.Token).ConfigureAwait(false);
                if (response.IsSuccessStatusCode && JsonNode.Parse(await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false))?["epoch"] is JsonValue e)
                    _s._epoch = e.GetValue<int>();
            }
            catch (Exception e) when (e is HttpRequestException or OperationCanceledException or JsonException)
            {
                _s._log.LogDebug("Openspline: interrupt failed: {Error}", e.Message);
            }
        }

        public ValueTask DisposeAsync()
        {
            Complete();
            Interlocked.CompareExchange(ref _s._turn, null, this);
            return ValueTask.CompletedTask;
        }
    }

    // ---------------------------------------------------------------- sockets

    async Task RequestAsync(JsonObject message, CancellationToken cancel)
    {
        var id = Guid.NewGuid().ToString("N");
        message["id"] = id;
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        try
        {
            await SendAsync(_audio, _audioSend, message, cancel).ConfigureAwait(false);
            await tcs.Task.WaitAsync(RequestTimeout, cancel).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new PluginException("Openspline didn't answer in time");
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    Task PlaybackSendAsync(JsonObject message, CancellationToken cancel) => SendAsync(_playback, _playbackSend, message, cancel);

    static async Task SendAsync(ClientWebSocket ws, SemaphoreSlim gate, JsonObject message, CancellationToken cancel)
    {
        var bytes = Encoding.UTF8.GetBytes(message.ToJsonString());
        await gate.WaitAsync(cancel).ConfigureAwait(false);
        try
        {
            if (ws.State != WebSocketState.Open) throw new PluginException("Openspline's connection is closed");
            await ws.SendAsync(bytes, WebSocketMessageType.Text, true, cancel).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    async Task AudioLoopAsync(CancellationToken cancel)
    {
        try
        {
            while (await ReceiveAsync(_audio, cancel).ConfigureAwait(false) is { } doc)
            {
                using (doc)
                {
                    var e = doc.RootElement;
                    if (e.TryGetProperty("epoch", out var ep) && ep.TryGetInt32(out var epoch) && epoch > _epoch) _epoch = epoch;
                    var type = Str(e, "type");
                    if (Str(e, "id") is { } id && _pending.TryGetValue(id, out var tcs))
                    {
                        if (type == "error") tcs.TrySetException(new PluginException($"Openspline: {Str(e, "message") ?? "error"}"));
                        else tcs.TrySetResult(e.Clone());
                    }
                    else if (type == "error")
                    {
                        _log.LogWarning("Openspline: {Message}", Str(e, "message"));
                    }
                }
            }
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException or JsonException)
        {
            if (!cancel.IsCancellationRequested) _log.LogInformation("Openspline: audio connection ended: {Error}", e.Message);
        }
        Fail();
    }

    async Task PlaybackLoopAsync(CancellationToken cancel)
    {
        try
        {
            while (await ReceiveAsync(_playback, cancel).ConfigureAwait(false) is { } doc)
            {
                using (doc)
                {
                    var e = doc.RootElement;
                    int epoch = e.TryGetProperty("epoch", out var ep) && ep.TryGetInt32(out var n) ? n : _epoch;
                    switch (Str(e, "type"))
                    {
                        case "connected":
                            _epoch = epoch;
                            await PlaybackSendAsync(new JsonObject { ["type"] = "ready" }, cancel).ConfigureAwait(false);
                            _viewerReady.TrySetResult();
                            break;
                        case "idle":
                            // Idle motion between replies: the puck keeps its own face, just acknowledge it
                            await PlaybackSendAsync(new JsonObject
                            {
                                ["type"] = "idle_played", ["epoch"] = epoch,
                                ["sequence"] = e.TryGetProperty("sequence", out var seq) ? JsonNode.Parse(seq.GetRawText()) : null,
                            }, cancel).ConfigureAwait(false);
                            break;
                        case "media":
                        {
                            long samples = e.TryGetProperty("samples", out var sp) && sp.TryGetInt64(out var s) ? s : 0;
                            var turn = _turn;
                            if (turn is null || epoch != _epoch)
                            {
                                // Nobody's listening (left over from an interrupted reply): keep the service moving
                                await PlaybackSendAsync(new JsonObject { ["type"] = "played", ["samples"] = samples, ["epoch"] = epoch }, cancel).ConfigureAwait(false);
                                break;
                            }
                            var jpeg = FaceFrames.ForPuck(Convert.FromBase64String(Str(e, "image") ?? ""));
                            var audioBytes = Convert.FromBase64String(Str(e, "audio") ?? "");
                            var pcm = new short[audioBytes.Length / 2];
                            Buffer.BlockCopy(audioBytes, 0, pcm, 0, pcm.Length * 2);
                            turn.Media(new FaceMedia(jpeg, pcm, 48000), samples, epoch);
                            break;
                        }
                        case "turn_end":
                            if (e.TryGetProperty("target_samples", out var t) && t.TryGetInt64(out var target)) _turn?.TurnEnd(target);
                            else _turn?.Complete();
                            break;
                        case "interrupted":
                            _epoch = epoch;
                            _turn?.Complete();
                            break;
                        case "error":
                            _log.LogWarning("Openspline playback: {Message}", Str(e, "message"));
                            break;
                    }
                }
            }
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException or JsonException or FormatException or PluginException)
        {
            if (!cancel.IsCancellationRequested) _log.LogInformation("Openspline: playback connection ended: {Error}", e.Message);
        }
        Fail();
    }

    async Task HeartbeatAsync(CancellationToken cancel)
    {
        try
        {
            while (!cancel.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(15), cancel).ConfigureAwait(false);
                await PlaybackSendAsync(new JsonObject { ["type"] = "ping" }, cancel).ConfigureAwait(false);
                await RequestAsync(new JsonObject { ["type"] = "ping" }, cancel).ConfigureAwait(false);
            }
        }
        catch (Exception e) when (e is OperationCanceledException or PluginException or WebSocketException)
        {
            if (!cancel.IsCancellationRequested) Fail();
        }
    }

    void Fail()
    {
        if (_stop.IsCancellationRequested) return;
        Failed = true;
        _viewerReady.TrySetException(new PluginException("Openspline closed the connection"));
        _turn?.Complete();
        foreach (var p in _pending.Values) p.TrySetException(new PluginException("Openspline closed the connection"));
    }

    static async Task<JsonDocument?> ReceiveAsync(ClientWebSocket ws, CancellationToken cancel)
    {
        var buffer = new byte[64 * 1024];
        using var ms = new MemoryStream();
        while (true)
        {
            var r = await ws.ReceiveAsync(buffer, cancel).ConfigureAwait(false);
            if (r.MessageType == WebSocketMessageType.Close) return null;
            ms.Write(buffer, 0, r.Count);
            if (r.EndOfMessage) break;
        }
        return JsonDocument.Parse(ms.ToArray());
    }

    static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    static string Shorten(string s) => s.Length <= 200 ? s : s[..199] + "…";

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _turn?.Complete();
        foreach (var ws in new[] { _audio, _playback })
        {
            try
            {
                if (ws.State == WebSocketState.Open)
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", cts.Token).ConfigureAwait(false);
                }
            }
            catch (Exception e) when (e is WebSocketException or OperationCanceledException or ObjectDisposedException) { }
            ws.Dispose();
        }
        if (!string.IsNullOrEmpty(Id))
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Delete, new Uri(_base, $"v1/sessions/{Id}"));
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _publisherToken);
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                using var _ = await _http.SendAsync(request, cts.Token).ConfigureAwait(false);
            }
            catch (Exception e) when (e is HttpRequestException or OperationCanceledException) { }
        }
        foreach (var t in new[] { _audioLoop, _playbackLoop, _heartbeat })
            if (t is not null) try { await t.ConfigureAwait(false); } catch { }
        _stop.Dispose();
    }
}

/// <summary>Openspline has no free worker for a new avatar (429): another session holds it, or it's stuck.</summary>
public sealed class OpensplineBusyException(string detail) : Exception($"Openspline has no free worker: {detail}");
