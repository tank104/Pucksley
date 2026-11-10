using Pucksley.Plugins.Windows.Speech;
using Pucksley.Sdk;
using Pucksley.TestSupport;
using Xunit.Abstractions;

namespace Pucksley.Plugins.Windows.Tests;

/// <summary>
/// Windows' own voices and recogniser, end to end. Audio is only synthesised into memory and fed
/// to the recogniser: nothing is played or recorded.
/// </summary>
[Trait("Category", "Live")]
public sealed class WindowsSpeechLiveTests(ITestOutputHelper output) : IAsyncLifetime
{
    readonly WindowsSpeechPlugin _plugin = new();

    public async Task InitializeAsync()
    {
        _plugin.Initialize(new FakePluginContext());
        _plugin.ApplySettings(new WindowsSpeechSettings());
        await _plugin.StartAsync(default);
        output.WriteLine($"Status: {_plugin.Status.Level}: {_plugin.Status.Message}");
    }

    public Task DisposeAsync() => _plugin.StopAsync();

    async Task<List<AudioChunk>> SynthesizeAsync(string text)
    {
        var chunks = new List<AudioChunk>();
        await foreach (var chunk in ((ITextToSpeech)_plugin).SynthesizeAsync(text, default)) chunks.Add(chunk);
        return chunks;
    }

    [Fact]
    public async Task Speech_from_a_windows_voice_is_recognised_offline()
    {
        if (!((ITextToSpeech)_plugin).IsReady)
        {
            output.WriteLine("No Windows voices installed: nothing to test");
            return;
        }
        const string sentence = "Please open the calculator and set a timer for ten minutes.";
        var chunks = await SynthesizeAsync(sentence);
        Assert.NotEmpty(chunks);
        // The hub hands speech engines 16 kHz mono, so do the same here
        var samples = chunks.SelectMany(c => Resampler.Linear(c.Samples, c.SampleRate, 16000)).ToArray();
        double seconds = samples.Length / 16000.0;
        output.WriteLine($"Synthesised {chunks.Count} chunk(s) at {chunks[0].SampleRate} Hz: {seconds:0.00} s");
        Assert.InRange(seconds, 1.5, 15);
        Assert.True(samples.Max(s => Math.Abs((int)s)) > 1000, "The synthesised audio is silent.");

        var stt = (ISpeechToText)_plugin;
        if (!stt.IsReady)
        {
            output.WriteLine("No Windows speech recogniser installed: stopping after synthesis");
            return;
        }
        var started = DateTime.UtcNow;
        string heard = await stt.TranscribeAsync(new AudioClip(samples, 16000), "en-NZ", default);
        output.WriteLine($"Heard in {(DateTime.UtcNow - started).TotalSeconds:0.0} s: \"{heard}\"");
        string[] expected = ["open", "calculator", "set", "timer", "ten", "minutes"];
        int hits = expected.Count(w => heard.Contains(w, StringComparison.OrdinalIgnoreCase));
        Assert.True(hits >= 3, $"Expected most of \"{sentence}\", heard \"{heard}\".");

        // A second utterance reuses the warm engine
        started = DateTime.UtcNow;
        var again = await stt.TranscribeAsync(new AudioClip(samples, 16000), "en-US", default);
        output.WriteLine($"Again in {(DateTime.UtcNow - started).TotalSeconds:0.0} s: \"{again}\"");
        Assert.False(string.IsNullOrWhiteSpace(again));
    }

    [Fact]
    public async Task Each_sentence_is_yielded_as_its_own_chunk()
    {
        if (!((ITextToSpeech)_plugin).IsReady) return;
        var chunks = await SynthesizeAsync("The front door is locked and the porch light is on. The heat pump is set to twenty degrees.");
        output.WriteLine($"{chunks.Count} chunks: {string.Join(", ", chunks.Select(c => $"{c.Samples.Length / (double)c.SampleRate:0.00} s"))}");
        Assert.Equal(2, chunks.Count);
        Assert.All(chunks, c => Assert.True(c.Samples.Length > c.SampleRate / 2));
    }

    [Fact]
    public async Task A_slower_rate_makes_longer_audio()
    {
        if (!((ITextToSpeech)_plugin).IsReady) return;
        const string text = "Your pasta timer has finished.";
        double normal = (await SynthesizeAsync(text)).Sum(c => c.Samples.Length / (double)c.SampleRate);
        await _plugin.StopAsync();
        _plugin.ApplySettings(new WindowsSpeechSettings { Rate = 0.6 });
        await _plugin.StartAsync(default);
        double slow = (await SynthesizeAsync(text)).Sum(c => c.Samples.Length / (double)c.SampleRate);
        output.WriteLine($"Normal {normal:0.00} s, rate 0.6: {slow:0.00} s");
        Assert.True(slow > normal * 1.3);
    }

    [Fact]
    public async Task List_voices_names_the_installed_voices()
    {
        var result = await _plugin.RunActionAsync("list-voices", new Dictionary<string, string>(), default);
        output.WriteLine(result.Message);
        if (((ITextToSpeech)_plugin).IsReady) Assert.Contains("In use:", result.Message);
    }

    [Fact]
    public async Task Empty_audio_transcribes_to_nothing()
    {
        var stt = (ISpeechToText)_plugin;
        if (!stt.IsReady) return;
        Assert.Equal("", await stt.TranscribeAsync(new AudioClip([], 16000), null, default));
        Assert.Equal("", await stt.TranscribeAsync(new AudioClip(new short[16000], 16000), null, default)); // a second of silence
    }
}
