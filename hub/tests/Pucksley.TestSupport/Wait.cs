namespace Pucksley.TestSupport;

public static class Wait
{
    /// <summary>Polls until <paramref name="condition"/> holds (for debounced notifications).</summary>
    public static async Task UntilAsync(Func<bool> condition, int timeoutMs = 3000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Condition not met in time.");
            await Task.Delay(20);
        }
    }
}
