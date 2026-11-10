using Microsoft.Extensions.Logging;

namespace Pucksley.Plugins.Shared;

/// <summary>Runs <c>tick</c> every <c>interval</c> in the background until disposed. Exceptions are logged, not fatal.</summary>
internal sealed class Poller : IAsyncDisposable
{
    readonly CancellationTokenSource _stop = new();
    readonly Task _loop;

    public Poller(TimeSpan interval, Func<CancellationToken, Task> tick, ILogger log, string name)
    {
        _loop = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(interval);
            try
            {
                while (await timer.WaitForNextTickAsync(_stop.Token).ConfigureAwait(false))
                {
                    try
                    {
                        await tick(_stop.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (_stop.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (Exception e)
                    {
                        log.LogDebug(e, "{Name} poll failed", name);
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
        });
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        try
        {
            await _loop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        _stop.Dispose();
    }
}
