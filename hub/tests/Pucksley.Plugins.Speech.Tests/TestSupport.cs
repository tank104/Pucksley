using System.Buffers.Binary;
using Pucksley.Sdk;
using Pucksley.TestSupport;

namespace Pucksley.Plugins.Speech.Tests;

public static class Audio
{
    /// <summary>A test signal where every sample differs from its neighbours, so a byte out of place shows.</summary>
    public static short[] Signal(int count, int seed = 0) =>
        Enumerable.Range(0, count).Select(i => (short)((i + seed) * 7919 % 65536 - 32768)).ToArray();

    public static byte[] ToBytes(short[] samples)
    {
        var bytes = new byte[samples.Length * 2];
        for (int i = 0; i < samples.Length; i++) BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 2), samples[i]);
        return bytes;
    }

    public static async Task<List<AudioChunk>> CollectAsync(IAsyncEnumerable<AudioChunk> chunks)
    {
        var list = new List<AudioChunk>();
        await foreach (var chunk in chunks) list.Add(chunk);
        return list;
    }

    public static short[] Join(IEnumerable<AudioChunk> chunks) => chunks.SelectMany(c => c.Samples).ToArray();
}

public static class TestPlugins
{
    public static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A started plugin whose shared HttpClient goes through <paramref name="handler"/>.</summary>
    public static async Task<T> StartAsync<T>(object settings, HttpMessageHandler? handler = null, string? dataDirectory = null) where T : IPlugin, new()
    {
        var plugin = Create<T>(settings, handler, dataDirectory, out _);
        await plugin.StartAsync(Ct);
        return plugin;
    }

    public static T Create<T>(object settings, HttpMessageHandler? handler, string? dataDirectory, out FakePluginContext context) where T : IPlugin, new()
    {
        context = new FakePluginContext(dataDirectory, handler is null ? null : new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) });
        var plugin = new T();
        plugin.Initialize(context);
        plugin.ApplySettings(settings);
        return plugin;
    }

    /// <summary>The <see cref="ServiceError"/> kind behind a speech call's exception.</summary>
    internal static ServiceErrorKind Kind(this PluginException e) => Assert.IsType<ServiceError>(e.InnerException).Kind;
}
