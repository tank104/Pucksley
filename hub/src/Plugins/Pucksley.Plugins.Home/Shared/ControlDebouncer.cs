namespace Pucksley.Plugins.Shared;

/// <summary>
/// Turning a dial on the puck sends ~8 values a second. This sends only the latest value per
/// control, once the dial has been still for <c>settle</c>, one call at a time, so lights and cloud
/// APIs aren't flooded. <see cref="Post"/> returns at once; failures go to <c>onError</c>.
/// </summary>
internal sealed class ControlDebouncer(TimeSpan settle) : IDisposable
{
    readonly object _lock = new();
    // key present = a worker is running for that control; value = the call still to make (null = none)
    readonly Dictionary<string, Func<CancellationToken, Task>?> _queued = [];
    readonly CancellationTokenSource _stop = new();

    public void Post(string key, Func<CancellationToken, Task> send, Action<Exception> onError)
    {
        lock (_lock)
        {
            bool running = _queued.ContainsKey(key);
            _queued[key] = send;
            if (running) return;
        }
        _ = Task.Run(() => WorkAsync(key, onError));
    }

    async Task WorkAsync(string key, Action<Exception> onError)
    {
        try
        {
            while (true)
            {
                if (settle > TimeSpan.Zero) await Task.Delay(settle, _stop.Token).ConfigureAwait(false);
                Func<CancellationToken, Task>? send;
                lock (_lock)
                {
                    send = _queued[key];
                    if (send is null)
                    {
                        _queued.Remove(key);
                        return;
                    }
                    _queued[key] = null;
                }
                try
                {
                    await send(_stop.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception e)
                {
                    onError(e);
                }
            }
        }
        catch (OperationCanceledException)
        {
            lock (_lock) _queued.Remove(key);
        }
    }

    /// <summary>Drops queued calls and stops the workers. (The token source isn't disposed: a worker that
    /// was just scheduled may still read its token.)</summary>
    public void Dispose() => _stop.Cancel();
}
