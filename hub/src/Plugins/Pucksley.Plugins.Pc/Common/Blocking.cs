using System.Runtime.InteropServices;
using Pucksley.Sdk;

namespace Pucksley.Plugins.Pc.Common;

/// <summary>
/// Runs operating-system calls that could hang (the audio service, the shell, a clipboard owner)
/// off the caller's thread, with a timeout so a wedged call becomes a spoken error instead of a
/// stuck tool.
/// </summary>
internal static class Blocking
{
    /// <summary>
    /// On the thread pool. On Windows, pool threads are MTA, which is what Core Audio's
    /// free-threaded objects want (an STA caller would make every call a cross-apartment hop).
    /// </summary>
    public static Task<T> RunAsync<T>(Func<T> func, TimeSpan timeout, string what, CancellationToken cancel) =>
        WaitAsync(Task.Run(func, cancel), timeout, what, cancel);

    public static Task RunAsync(Action action, TimeSpan timeout, string what, CancellationToken cancel) =>
        RunAsync(() => { action(); return true; }, timeout, what, cancel);

    /// <summary>Waits for a call already under way, with the same timeout and error wording.</summary>
    public static async Task<T> WaitAsync<T>(Task<T> task, TimeSpan timeout, string what, CancellationToken cancel)
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
            // COM and Win32 failures: say which part of the system failed rather than "COMException"
            throw new PluginException($"{what} reported an error: {e.Message}", e);
        }
    }
}
