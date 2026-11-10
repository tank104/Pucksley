using Pucksley.Sdk;
using Pucksley.TestSupport;

namespace Pucksley.Plugins.Home.Tests;

/// <summary>Starts a plugin the way the hub does, against a fake context and fake HTTP.</summary>
public static class TestPlugins
{
    public static async Task<(T Plugin, FakePluginContext Context)> StartAsync<T>(object settings, HttpClient? http = null) where T : IPlugin, new()
    {
        var context = new FakePluginContext(http: http);
        var plugin = new T();
        plugin.Initialize(context);
        plugin.ApplySettings(settings);
        await plugin.StartAsync(CancellationToken.None);
        return (plugin, context);
    }

    /// <summary>Waits (up to <paramref name="timeoutMs"/>) for <paramref name="condition"/>; false if it never held.</summary>
    public static async Task<bool> WaitUntilAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) return false;
            await Task.Delay(25);
        }
        return true;
    }

    public static string Hex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();

    public static byte[] Bytes(string hex) => Convert.FromHexString(hex);
}
