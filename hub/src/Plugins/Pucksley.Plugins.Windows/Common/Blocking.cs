using System.Runtime.InteropServices;
using Pucksley.Sdk;

namespace Pucksley.Plugins.Windows.Common;

/// <summary>
/// Runs Windows calls that could hang (audio service, shell, clipboard owners) off the caller's
/// thread, with a timeout so a wedged call becomes a spoken error instead of a stuck tool.
/// </summary>
internal static class Blocking
{
    /// <summary>
    /// On the thread pool. Pool threads are MTA, which is what Core Audio's free-threaded objects
    /// want (an STA caller would make every call a cross-apartment hop).
    /// </summary>
    public static Task<T> RunAsync<T>(Func<T> func, TimeSpan timeout, string what, CancellationToken cancel) =>
        WaitAsync(Task.Run(func, cancel), timeout, what, cancel);

    public static Task RunAsync(Action action, TimeSpan timeout, string what, CancellationToken cancel) =>
        RunAsync(() => { action(); return true; }, timeout, what, cancel);

    /// <summary>On a fresh STA thread (the clipboard and some shell objects require one).</summary>
    public static Task<T> RunStaAsync<T>(Func<T> func, TimeSpan timeout, string what, CancellationToken cancel)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { done.TrySetResult(func()); }
            catch (Exception e) { done.TrySetException(e); }
        })
        {
            IsBackground = true,
            Name = "Pucksley STA",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return WaitAsync(done.Task, timeout, what, cancel);
    }

    static async Task<T> WaitAsync<T>(Task<T> task, TimeSpan timeout, string what, CancellationToken cancel)
    {
        try
        {
            return await task.WaitAsync(timeout, cancel).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // The call carries on in the background; observe its eventual failure so it isn't unobserved
            _ = task.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
            throw new PluginException($"{what} didn't respond in time.");
        }
        catch (ExternalException e)
        {
            // COM and Win32 failures: say which part of Windows failed rather than "COMException"
            throw new PluginException($"{what} reported an error: {e.Message}", e);
        }
    }
}
