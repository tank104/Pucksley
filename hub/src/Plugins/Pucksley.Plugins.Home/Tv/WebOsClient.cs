using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Authentication;
using System.Text.Json.Nodes;
using Pucksley.Plugins.Shared;
using Pucksley.Sdk;

namespace Pucksley.Plugins.Tv;

/// <summary>
/// One connection to an LG webOS TV ("SSAP" over WebSocket): <c>wss://host:3001/</c>, falling back
/// to <c>ws://host:3000/</c> for older sets. Requests are matched to replies by id; a background loop
/// reads replies. Thread-safe.
/// </summary>
internal sealed class WebOsClient : IAsyncDisposable
{
    readonly ClientWebSocket _ws;
    readonly ConcurrentDictionary<string, TaskCompletionSource<JsonObject>> _pending = new();
    readonly SemaphoreSlim _sendGate = new(1, 1);
    readonly CancellationTokenSource _stop = new();
    readonly Task _loop;
    int _nextId;

    WebOsClient(ClientWebSocket ws)
    {
        _ws = ws;
        _loop = Task.Run(ReceiveLoopAsync);
    }

    public bool IsOpen => _ws.State == WebSocketState.Open && !_loop.IsCompleted;

    public static async Task<WebOsClient> ConnectAsync(string host, CancellationToken cancel)
    {
        Exception? last = null;
        foreach (var uri in new[] { new Uri($"wss://{host}:3001/"), new Uri($"ws://{host}:3000/") })
        {
            try
            {
                return new WebOsClient(await TvSocket.ConnectAsync(uri, cancel).ConfigureAwait(false));
            }
            catch (Exception e) when (e is WebSocketException or HttpRequestException or IOException or AuthenticationException or SocketException)
            {
                last = e;
            }
        }
        throw last!;
    }

    /// <summary>
    /// Registers with the TV and returns the client-key to keep. With a valid <paramref name="clientKey"/>
    /// this is silent; without one the TV asks the person to allow it and this waits (bounded by
    /// <paramref name="cancel"/>) for them to answer.
    /// </summary>
    public async Task<string> RegisterAsync(string? clientKey, CancellationToken cancel)
    {
        var reply = await SendAsync("register", null, LgManifest.Registration(clientKey), cancel).ConfigureAwait(false);
        if (Json.Str(reply, "type") == "registered" && Json.Str(reply, "payload.client-key") is { Length: > 0 } key) return key;
        var error = Json.Str(reply, "error") ?? "it didn't register";
        throw new PluginException(error.Contains("denied", StringComparison.OrdinalIgnoreCase) || error.Contains("cancel", StringComparison.OrdinalIgnoreCase)
            ? "The TV said no to pairing."
            : $"The TV wouldn't pair ({error}).");
    }

    /// <summary>An <c>ssap://</c> request; returns the reply payload or throws with the TV's error.</summary>
    public async Task<JsonObject> RequestAsync(string uri, JsonObject? payload, CancellationToken cancel)
    {
        var reply = await SendAsync("request", uri, payload, cancel).ConfigureAwait(false);
        if (Json.Str(reply, "type") == "error")
            throw new PluginException($"The TV refused {uri.Replace("ssap://", "")} ({Json.Str(reply, "error") ?? "error"}).");
        var result = reply["payload"] as JsonObject ?? new JsonObject();
        if (Json.Bool(result, "returnValue") == false)
            throw new PluginException($"The TV refused {uri.Replace("ssap://", "")} ({Json.Str(result, "errorText") ?? "error"}).");
        return result;
    }

    async Task<JsonObject> SendAsync(string type, string? uri, JsonObject? payload, CancellationToken cancel)
    {
        var id = $"{type}_{Interlocked.Increment(ref _nextId)}";
        var reply = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = reply;
        try
        {
            var message = new JsonObject { ["type"] = type, ["id"] = id };
            if (uri is not null) message["uri"] = uri;
            if (payload is not null) message["payload"] = payload;
            await _sendGate.WaitAsync(cancel).ConfigureAwait(false);
            try
            {
                await TvSocket.SendAsync(_ws, message.ToJsonString(), cancel).ConfigureAwait(false);
            }
            finally
            {
                _sendGate.Release();
            }
            if (_loop.IsCompleted) throw new WebSocketException("The TV closed the connection.");
            using var registration = cancel.Register(() => reply.TrySetCanceled(cancel));
            return await reply.Task.ConfigureAwait(false);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    async Task ReceiveLoopAsync()
    {
        try
        {
            while (_ws.State == WebSocketState.Open)
            {
                var text = await TvSocket.ReceiveAsync(_ws, _stop.Token).ConfigureAwait(false);
                if (text is null) break;
                if (Json.Parse(text) is not JsonObject message || Json.Str(message, "id") is not { } id) continue;
                if (!_pending.TryGetValue(id, out var waiter)) continue;
                // While pairing the TV first says it's showing the prompt; the answer comes later as "registered"
                if (Json.Str(message, "type") == "response" && Json.Str(message, "payload.pairingType") == "PROMPT") continue;
                waiter.TrySetResult(message);
            }
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException or InvalidDataException or ObjectDisposedException)
        {
        }
        finally
        {
            foreach (var waiter in _pending.Values) waiter.TrySetException(new WebSocketException("The TV closed the connection."));
        }
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        await TvSocket.CloseAsync(_ws).ConfigureAwait(false);
        try
        {
            await _loop.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // already reported to waiters
        }
        _stop.Dispose();
    }
}
