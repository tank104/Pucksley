using Pucksley.Plugins.Pc.Common;

namespace Pucksley.Plugins.Windows.Common;

/// <summary>Runs a call on a fresh STA thread (the clipboard and some shell objects require one).</summary>
internal static class Sta
{
    /// <summary>With the same timeout and error wording as <see cref="Blocking.RunAsync{T}"/>.</summary>
    public static Task<T> RunAsync<T>(Func<T> func, TimeSpan timeout, string what, CancellationToken cancel)
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
        return Blocking.WaitAsync(done.Task, timeout, what, cancel);
    }
}
