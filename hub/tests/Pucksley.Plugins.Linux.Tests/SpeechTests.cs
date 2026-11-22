using Pucksley.Plugins.Linux.Common;
using Pucksley.Plugins.Linux.Speech;
using Pucksley.Sdk;
using Pucksley.TestSupport;
using Xunit.Abstractions;

namespace Pucksley.Plugins.Linux.Tests;

public class LinuxSpeechTests
{
    const string Listing = """
        Pty Language       Age/Gender VoiceName          File                 Other Languages
         5  af              --/M      Afrikaans          gmw/af
         2  en-gb           --/M      English_(Great_Britain) gmw/en               (en 2)
         2  en-us           --/M      English_(America)  gmw/en-US            (en 3)
         5  en-gb-scotland  --/M      English_(Scotland) gmw/en-GB-scotland   (en 4)
         5  fr-fr           --/M      French_(France)    roa/fr               (fr 5)
        """;

    static readonly List<EspeakVoice> Voices = LinuxSpeechPlugin.ParseVoices(Listing);

    [Fact]
    public void The_voices_are_read_from_espeaks_listing()
    {
        Assert.Equal(5, Voices.Count);
        Assert.Equal(new EspeakVoice("English (Great Britain)", "en-gb"), Voices[1]);
        Assert.Empty(LinuxSpeechPlugin.ParseVoices("Pty Language Age/Gender VoiceName File Other Languages\n"));
    }

    [Theory]
    [InlineData(null, "en-NZ", "en-gb")]       // New Zealand: the nearest English accent
    [InlineData("", "en-US", "en-us")]         // this PC's own language
    [InlineData("scotland", "en-NZ", "en-gb-scotland")] // part of a name
    [InlineData("EN-US", "en-NZ", "en-us")]    // a language code
    [InlineData("fr-fr", "en-NZ", "fr-fr")]
    [InlineData("klingon", "en-NZ", "en-gb")]  // nothing like it: the usual choice
    public void A_voice_is_chosen_by_what_was_asked_or_by_the_pcs_language(string? wanted, string system, string expected) =>
        Assert.Equal(expected, LinuxSpeechPlugin.Choose(Voices, wanted, [system])?.Language);

    [Fact]
    public void With_no_voices_there_is_none_to_choose() => Assert.Null(LinuxSpeechPlugin.Choose([], null, ["en-NZ"]));

    [Theory]
    [InlineData(1.0, 1.0, "--stdin --stdout -s 175 -p 50 -v en-gb")]
    [InlineData(2.0, 1.5, "--stdin --stdout -s 350 -p 75 -v en-gb")]
    [InlineData(0.1, 9.0, "--stdin --stdout -s 88 -p 99 -v en-gb")]   // kept within what sounds like speech
    [InlineData(0, double.NaN, "--stdin --stdout -s 175 -p 50 -v en-gb")] // nonsense settings: normal
    public void Rate_and_pitch_become_espeaks_numbers(double rate, double pitch, string expected) =>
        Assert.Equal(expected, string.Join(' ', LinuxSpeechPlugin.Arguments(Voices[1], rate, pitch)));

    [Fact]
    public void It_is_the_voice_of_last_resort_on_linux()
    {
        var plugin = new LinuxSpeechPlugin();
        Assert.Equal(("linux-speech", "espeak", true), (plugin.Id, plugin.EngineId, plugin.EnabledByDefault));
        Assert.Contains("basic", plugin.EngineName);
        Assert.Empty(plugin.GetTools());
        Assert.Equal("list-voices", Assert.Single(plugin.GetActions()).Id);
    }
}

[Trait("Category", "Live")]
public class LinuxSpeechLiveTests(ITestOutputHelper output)
{
    /// <summary>Makes speech in memory with this PC's eSpeak NG: nothing is played.</summary>
    [Fact]
    public async Task Espeak_speaks_into_memory()
    {
        var plugin = new LinuxSpeechPlugin();
        plugin.Initialize(new FakePluginContext());
        plugin.ApplySettings(new LinuxSpeechSettings());
        await plugin.StartAsync(default);
        output.WriteLine($"Status: {plugin.Status.Message}");
        if (!plugin.IsReady)
        {
            Assert.Equal(StatusLevel.Idle, plugin.Status.Level);
            return;
        }
        Assert.Equal(StatusLevel.Ok, plugin.Status.Level);

        var chunks = new List<AudioChunk>();
        await foreach (var chunk in plugin.SynthesizeAsync("The kitchen is warm. The lounge is a little cold, so shall I turn the heating up?", default)) chunks.Add(chunk);
        double seconds = chunks.Sum(c => (double)c.Samples.Length / c.SampleRate);
        output.WriteLine($"{chunks.Count} chunks, {seconds:0.0} s at {chunks[0].SampleRate} Hz");
        Assert.InRange(chunks.Count, 1, 3);
        Assert.InRange(seconds, 2, 15);
        Assert.Contains(chunks, c => c.Samples.Any(s => Math.Abs((int)s) > 1000)); // not silence

        var voices = await plugin.RunActionAsync("list-voices", new Dictionary<string, string>(), default);
        output.WriteLine(voices.Message.Length > 200 ? voices.Message[..200] + "…" : voices.Message);
        Assert.True(voices.Ok);
    }
}
