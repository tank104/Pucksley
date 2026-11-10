using Pucksley.Plugins.Windows.Speech;

namespace Pucksley.Plugins.Windows.Tests;

public class VoicePickerTests
{
    static readonly string[] NewZealand = ["en-NZ", "en-GB"];
    static readonly (string, string)[] UsVoices = [("Microsoft David", "en-US"), ("Microsoft Zira", "en-US"), ("Microsoft Mark", "en-US")];

    static string? Pick((string Name, string Language)[] voices, string? wanted = null, string? defaultVoice = "Microsoft David")
    {
        int i = VoicePicker.Pick(voices, wanted, NewZealand, defaultVoice);
        return i < 0 ? null : voices[i].Name;
    }

    [Fact]
    public void With_only_US_voices_it_keeps_the_Windows_default() => Assert.Equal("Microsoft David", Pick(UsVoices));

    [Fact]
    public void Prefers_a_voice_in_the_system_language() =>
        Assert.Equal("Microsoft Molly", Pick([.. UsVoices, ("Microsoft Hazel", "en-GB"), ("Microsoft Molly", "en-NZ")]));

    [Fact]
    public void Prefers_British_or_Australian_English_over_American() =>
        Assert.Equal("Microsoft Catherine", Pick([.. UsVoices, ("Microsoft Catherine", "en-AU")]));

    [Fact]
    public void Prefers_natural_voices() =>
        Assert.Equal("Microsoft Libby (Natural)", Pick([.. UsVoices, ("Microsoft Molly", "en-NZ"), ("Microsoft Libby (Natural)", "en-GB")]));

    [Fact]
    public void A_named_voice_wins() => Assert.Equal("Microsoft Zira", Pick(UsVoices, wanted: "zira"));

    [Fact]
    public void An_unknown_name_falls_back_to_the_automatic_choice() => Assert.Equal("Microsoft David", Pick(UsVoices, wanted: "Morgan Freeman"));

    [Fact]
    public void Without_English_voices_it_uses_the_Windows_default() =>
        Assert.Equal("Microsoft Haruka", Pick([("Microsoft Hedda", "de-DE"), ("Microsoft Haruka", "ja-JP")], defaultVoice: "Microsoft Haruka"));

    [Fact]
    public void No_voices_means_none() => Assert.Null(Pick([]));

    [Theory]
    [InlineData(new[] { "en-US" }, "en-NZ", 0)]
    [InlineData(new[] { "en-US", "en-GB" }, "en-NZ", 1)] // NZ English is closer to British
    [InlineData(new[] { "en-US", "en-GB" }, "en-US", 0)]
    [InlineData(new[] { "fr-FR", "de-DE" }, "de-AT", 1)]
    [InlineData(new[] { "fr-FR", "en-US" }, null, 1)]     // no language given: English
    [InlineData(new[] { "fr-FR", "de-DE" }, "ja-JP", 0)]  // nothing close: the first
    [InlineData(new string[0], "en-US", -1)]
    public void Picks_the_closest_recognizer(string[] installed, string? language, int expected) =>
        Assert.Equal(expected, VoicePicker.PickRecognizer(installed, language));
}

public class SentenceSplitterTests
{
    [Fact]
    public void Splits_after_sentence_ends() =>
        Assert.Equal(new[] { "The kitchen is warm.", "The lounge is cold!", "Shall I turn the heat on?" },
            SentenceSplitter.Split("The kitchen is warm. The lounge is cold! Shall I turn the heat on?", minLength: 10));

    [Fact]
    public void Short_pieces_join_the_next_one() =>
        Assert.Equal(new[] { "Dr. Smith called about the results.", "Call him back today, before five." },
            SentenceSplitter.Split("Dr. Smith called about the results. Call him back today, before five.", minLength: 20));

    [Fact]
    public void Numbers_and_line_breaks_are_handled() =>
        Assert.Equal(new[] { "It is 3.5 degrees outside.", "Wrap up warm." },
            SentenceSplitter.Split("It is 3.5 degrees outside.\nWrap up warm.", minLength: 5));

    [Fact]
    public void Closing_quotes_stay_with_their_sentence() =>
        Assert.Equal(new[] { "She said \"see you soon.\"", "Then she left." },
            SentenceSplitter.Split("She said \"see you soon.\" Then she left.", minLength: 5));

    [Fact]
    public void A_short_tail_joins_the_previous_sentence() =>
        Assert.Equal(new[] { "Your timer for the pasta is done. Enjoy!" },
            SentenceSplitter.Split("Your timer for the pasta is done. Enjoy!", minLength: 20));

    [Fact]
    public void Empty_text_gives_nothing() => Assert.Empty(SentenceSplitter.Split("   "));
}

public class ResamplerTests
{
    [Fact]
    public void Same_rate_is_untouched()
    {
        short[] samples = [1, 2, 3];
        Assert.Same(samples, Resampler.Linear(samples, 16000, 16000));
    }

    [Fact]
    public void Length_scales_with_the_rate()
    {
        var samples = new short[22050];
        Assert.Equal(16000, Resampler.Linear(samples, 22050, 16000).Length);
        Assert.Equal(48000, Resampler.Linear(new short[16000], 16000, 48000).Length);
    }

    [Fact]
    public void A_ramp_stays_a_ramp()
    {
        var ramp = Enumerable.Range(0, 1000).Select(i => (short)(i * 10)).ToArray();
        var output = Resampler.Linear(ramp, 24000, 16000);
        for (int i = 1; i < output.Length; i++) Assert.True(output[i] >= output[i - 1]);
        Assert.Equal(0, output[0]);
        Assert.InRange(output[^1], 9900, 9990);
    }
}
