using System.Diagnostics;
using Pucksley.Sdk;
using static Pucksley.Plugins.Speech.Tests.TestPlugins;

namespace Pucksley.Plugins.Speech.Tests;

public class PcmStreamTests
{
    [Theory]
    [InlineData(24000, new[] { 1, 3, 4097 })]
    [InlineData(16000, new[] { 1 })]
    [InlineData(16000, new[] { 3, 1, 1, 65536 })]
    [InlineData(22050, new[] { 4097 })]
    [InlineData(24000, new[] { 7, 4801, 2, 3 })]
    public async Task Reassembles_samples_across_odd_read_boundaries(int rate, int[] readSizes)
    {
        var samples = Audio.Signal(rate + 123);
        var stream = new TrickleStream(Audio.ToBytes(samples), readSizes);

        var chunks = await Audio.CollectAsync(PcmStream.ReadChunksAsync(stream, rate, "Test", Ct));

        Assert.Equal(samples, Audio.Join(chunks));
        Assert.All(chunks, c => Assert.Equal(rate, c.SampleRate));
        // 50–100 ms each, so playback can start early without tiny packets; only the last may be shorter.
        foreach (var chunk in chunks.SkipLast(1)) Assert.InRange(chunk.Samples.Length, rate / 20, rate / 10);
    }

    [Fact]
    public async Task Drops_a_dangling_final_byte()
    {
        var samples = Audio.Signal(101);
        var bytes = Audio.ToBytes(samples).Append((byte)0x7F).ToArray();

        var chunks = await Audio.CollectAsync(PcmStream.ReadChunksAsync(new TrickleStream(bytes, [7]), 16000, "Test", Ct));

        Assert.Equal(samples, Audio.Join(chunks));
    }

    [Fact]
    public async Task Empty_stream_yields_nothing()
    {
        var chunks = await Audio.CollectAsync(PcmStream.ReadChunksAsync(new MemoryStream(), 16000, "Test", Ct));
        Assert.Empty(chunks);
    }

    [Fact]
    public async Task Yields_audio_before_the_stream_ends()
    {
        var gate = new TaskCompletionSource();
        var stream = new TrickleStream(Audio.ToBytes(Audio.Signal(16000)), [3200], async (read, cancel) =>
        {
            if (read == 1) await gate.Task.WaitAsync(cancel);  // the "server" is still synthesizing
        });

        await using var e = PcmStream.ReadChunksAsync(stream, 16000, "Test", Ct).GetAsyncEnumerator(Ct);
        Assert.True(await e.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5), Ct));
        Assert.Equal(1600, e.Current.Samples.Length);

        gate.SetResult();
        int total = e.Current.Samples.Length;
        while (await e.MoveNextAsync()) total += e.Current.Samples.Length;
        Assert.Equal(16000, total);
    }

    [Fact]
    public async Task Cancelling_mid_stream_stops_promptly()
    {
        using var cts = new CancellationTokenSource();
        var stream = new TrickleStream(Audio.ToBytes(Audio.Signal(16000)), [3200], async (read, cancel) =>
        {
            if (read == 1) await Task.Delay(Timeout.Infinite, cancel);  // a stalled server
        });
        await using var e = PcmStream.ReadChunksAsync(stream, 16000, "Test", cts.Token).GetAsyncEnumerator(cts.Token);
        Assert.True(await e.MoveNextAsync());

        var next = e.MoveNextAsync().AsTask();
        var clock = Stopwatch.StartNew();
        cts.CancelAfter(50);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => next);
        Assert.InRange(clock.ElapsedMilliseconds, 0, 2000);
    }

    [Fact]
    public async Task A_broken_stream_is_a_plugin_error()
    {
        var stream = new TrickleStream(Audio.ToBytes(Audio.Signal(16000)), [3200], (read, _) =>
            read == 2 ? throw new IOException("connection reset") : Task.CompletedTask);

        var e = await Assert.ThrowsAsync<PluginException>(() => Audio.CollectAsync(PcmStream.ReadChunksAsync(stream, 16000, "OpenAI", Ct)));

        Assert.Contains("audio from OpenAI was cut off", e.Message);
        Assert.Equal(ServiceErrorKind.Network, e.Kind());
    }
}
