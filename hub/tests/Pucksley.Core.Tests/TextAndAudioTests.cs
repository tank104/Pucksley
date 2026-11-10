using System.Text.Json.Nodes;
using Pucksley.Core.Pucks;
using Pucksley.Core.Tests.Support;
using Pucksley.Core.Voice;

namespace Pucksley.Core.Tests;

public sealed class PuckTextTests
{
    [Fact]
    public void Plain_ascii_comes_back_untouched()
    {
        const string s = "Plain text, 21 degrees. Done!";
        Assert.Same(s, PuckText.Clean(s));
        Assert.Equal("", PuckText.Clean(""));
    }

    [Theory]
    [InlineData("It’s “quoted” ‘here’", "It's \"quoted\" 'here'")]
    [InlineData("Wait — then − minus ― bar", "Wait - then - minus - bar")]
    [InlineData("Tāmaki Makaurau, Māori, café, naïve, Zoë, Ångström", "Tamaki Makaurau, Maori, cafe, naive, Zoe, Angstrom")]
    [InlineData("Hi 👋 there 🎉!", "Hi there !")]
    [InlineData("Sunny ☀️ today", "Sunny today")]
    [InlineData("21°C • 5…10 – ok × 2 · ‹x›", "21°C • 5…10 – ok × 2 · ‹x›")]
    [InlineData("«Bonjour»", "‹Bonjour›")]
    [InlineData("Straße Æsir æble Øresund øl łódka", "Strasse AEsir aeble Oresund ol lodka")]
    [InlineData("€20, £5, ½, ¼, ¾, Fanta™", "EUR20, GBP5, 1/2, 1/4, 3/4, FantaTM")]
    [InlineData("10 km away now ok", "10 km away now ok")]
    [InlineData("line one\r\nline two\tend\u0007", "line one\nline two end")]
    [InlineData("東京 Tokyo", " Tokyo")]
    public void Clean_folds_text_into_the_pucks_font(string input, string expected) => Assert.Equal(expected, PuckText.Clean(input));

    [Fact]
    public void CleanStrings_cleans_every_string_in_a_message_in_place()
    {
        var m = JsonNode.Parse("""{"t":"ctl","n":"Café","opts":["Tāmaki","ok",3],"nested":{"x":"“hi”"},"v":5}""")!.AsObject();
        PuckText.CleanStrings(m);
        Assert.Equal("Cafe", m["n"]!.GetValue<string>());
        Assert.Equal("Tamaki", m["opts"]![0]!.GetValue<string>());
        Assert.Equal(3, m["opts"]![2]!.GetValue<int>());
        Assert.Equal("\"hi\"", m["nested"]!["x"]!.GetValue<string>());
        Assert.Equal(5, m["v"]!.GetValue<int>());
    }
}

public sealed class ResamplerTests
{
    [Fact]
    public void Converting_24k_to_16k_keeps_length_frequency_and_level()
    {
        var input = Signals.Sine(1000, 24000, 24000, 10000);
        var output = Resampler.Convert(input, 24000, 16000);
        Assert.Equal(16000, output.Length);
        var middle = output.AsSpan(400, 15000);
        Assert.InRange(Signals.Frequency(middle, 16000), 995, 1005);
        Assert.InRange(Signals.Rms(middle), Signals.Rms(input) * 0.95, Signals.Rms(input) * 1.05);
    }

    [Fact]
    public void Upsampling_8k_to_16k_works_too()
    {
        var output = Resampler.Convert(Signals.Sine(440, 8000, 8000, 8000), 8000, 16000);
        Assert.Equal(16000, output.Length);
        Assert.InRange(Signals.Frequency(output.AsSpan(400, 15000), 16000), 436, 444);
    }

    [Fact]
    public void Tones_above_the_new_nyquist_are_filtered_out()
    {
        var input = Signals.Sine(10000, 24000, 24000, 10000);   // 10 kHz can't exist at 16 kHz
        var output = Resampler.Convert(input, 24000, 16000);
        Assert.True(Signals.Rms(output.AsSpan(400, 15000)) < Signals.Rms(input) * 0.2, $"aliasing RMS {Signals.Rms(output.AsSpan(400, 15000)):0}");
    }

    static short[] Streamed(short[] input, int from, int to, int[] chunkSizes)
    {
        var r = new Resampler(from, to);
        var output = new List<short>();
        int off = 0, i = 0;
        while (off < input.Length)
        {
            int n = Math.Min(chunkSizes[i++ % chunkSizes.Length], input.Length - off);
            output.AddRange(r.Process(input.AsSpan(off, n)));
            off += n;
        }
        output.AddRange(r.Flush());
        return output.ToArray();
    }

    [Fact]
    public void Streaming_in_odd_chunks_gives_the_same_samples_as_one_go()
    {
        var input = Signals.Concat(Signals.Sine(440, 24000, 7001, 9000), Signals.Sine(3000, 24000, 5003, 4000));
        var whole = Streamed(input, 24000, 16000, [input.Length]);
        var odd = Streamed(input, 24000, 16000, [1, 7, 13, 333, 2, 1001, 64]);
        Assert.Equal(whole, odd);
        Assert.InRange(whole.Length, input.Length * 2 / 3, input.Length * 2 / 3 + 2);
    }

    [Fact]
    public void Streaming_from_22050_matches_one_go_to_within_rounding()
    {
        var input = Signals.Sine(500, 22050, 22050, 9000);
        var whole = Streamed(input, 22050, 16000, [input.Length]);
        var odd = Streamed(input, 22050, 16000, [5, 17, 999, 3]);
        Assert.Equal(whole.Length, odd.Length);
        for (int i = 0; i < whole.Length; i++) Assert.True(Math.Abs(whole[i] - odd[i]) <= 1, $"sample {i}: {whole[i]} vs {odd[i]}");
    }

    [Fact]
    public void The_same_rate_passes_through()
    {
        var r = new Resampler(16000, 16000);
        Assert.True(r.IsPassThrough);
        var s = Signals.Sine(440, 16000, 100, 1000);
        Assert.Equal(s, r.Process(s));
        Assert.Empty(r.Flush());
        Assert.Same(s, Resampler.Convert(s, 16000, 16000));
    }
}

public sealed class VoiceActivityDetectorTests
{
    const int Rate = 16000;

    /// <summary>Feeds audio in 20 ms pieces; returns the sample position where speech was first called ended (-1 if never).</summary>
    static int Feed(VoiceActivityDetector vad, short[] audio)
    {
        int endedAt = -1;
        for (int off = 0; off < audio.Length; off += 320)
        {
            vad.Process(audio.AsSpan(off, Math.Min(320, audio.Length - off)));
            if (endedAt < 0 && vad.SpeechEnded) endedAt = off + 320;
        }
        return endedAt;
    }

    [Fact]
    public void Silence_and_quiet_room_noise_are_not_speech()
    {
        var vad = new VoiceActivityDetector();
        Feed(vad, Signals.Concat(Signals.Silence(Rate), Signals.Hiss(Rate * 2)));
        Assert.False(vad.SpeechStarted);
        Assert.False(vad.SpeechEnded);
    }

    [Fact]
    public void A_burst_then_silence_starts_and_ends_at_the_right_places()
    {
        var vad = new VoiceActivityDetector(sensitivity: 3.0, endSilenceMs: 900);
        // 0.5 s quiet, 0.6 s "speech", 1.2 s quiet
        var endedAt = Feed(vad, Signals.Concat(Signals.Hiss(Rate / 2), Signals.Sine(300, Rate, Rate * 6 / 10, 3000), Signals.Hiss(Rate * 12 / 10)));
        Assert.True(vad.SpeechStarted);
        Assert.True(vad.SpeechEnded);
        Assert.InRange(vad.SpeechStartSample, (int)(Rate * 0.3) - 640, (int)(Rate * 0.3) + 640);   // 200 ms before the speech
        Assert.InRange(vad.LastSpeechSample, (int)(Rate * 1.1) - 640, (int)(Rate * 1.1) + 640);    // where it stopped
        Assert.InRange(endedAt, (int)(Rate * 2.0) - 640, (int)(Rate * 2.0) + 640);                  // after 900 ms of quiet
    }

    [Fact]
    public void A_click_is_not_speech()
    {
        var vad = new VoiceActivityDetector();
        Feed(vad, Signals.Concat(Signals.Hiss(Rate / 2), Signals.Sine(1000, Rate, 320, 8000), Signals.Hiss(Rate)));
        Assert.False(vad.SpeechStarted);
    }

    [Fact]
    public void The_pucks_own_listen_beep_doesnt_deafen_it()
    {
        // The firmware starts the mic and then beeps, so the first frames the hub gets are the beep.
        // They mustn't become the "room noise" every later frame is compared against.
        var vad = new VoiceActivityDetector();
        Feed(vad, Signals.Concat(
            Signals.Sine(1000, Rate, Rate / 10, 6000),   // 100 ms beep
            Signals.Hiss(Rate * 3 / 10),                // 300 ms of room
            Signals.Sine(300, Rate, Rate, 2500),        // 1 s of normal speech
            Signals.Hiss(Rate * 3 / 2)));
        Assert.True(vad.SpeechStarted, "speech after the beep wasn't heard");
        Assert.True(vad.SpeechEnded);
        Assert.InRange(vad.SpeechStartSample, (int)(Rate * 0.2) - 640, (int)(Rate * 0.2) + 640);
    }

    [Fact]
    public void A_beep_filling_most_of_the_first_200_ms_doesnt_deafen_it_either()
    {
        // The firmware's listen tone: two decaying notes over ~150 ms, starting right away
        var beep = Signals.Concat(Signals.Sine(880, Rate, Rate * 8 / 100, 6000), Signals.Sine(1320, Rate, Rate * 8 / 100, 2500));
        var vad = new VoiceActivityDetector();
        Feed(vad, Signals.Concat(beep, Signals.Hiss(Rate * 4 / 100), Signals.Hiss(Rate * 3 / 10), Signals.Sine(300, Rate, Rate, 2000), Signals.Hiss(Rate * 3 / 2)));
        Assert.True(vad.SpeechStarted, "speech after a long beep wasn't heard");
        Assert.True(vad.SpeechEnded);
    }

    [Fact]
    public void A_loud_start_is_forgotten_once_the_room_is_quiet()
    {
        // Every one of the first 200 ms is loud (e.g. someone was mid-word): the floor comes back down
        var vad = new VoiceActivityDetector();
        Feed(vad, Signals.Concat(Signals.Sine(500, Rate, Rate / 4, 3000), Signals.Hiss(Rate / 2), Signals.Sine(300, Rate, Rate, 2500), Signals.Hiss(Rate * 3 / 2)));
        Assert.True(vad.SpeechStarted, "speech after a loud start wasn't heard");
        Assert.True(vad.SpeechEnded);
    }

    [Fact]
    public void A_shorter_end_silence_ends_sooner()
    {
        var vad = new VoiceActivityDetector(endSilenceMs: 400);
        var endedAt = Feed(vad, Signals.Concat(Signals.Hiss(Rate / 2), Signals.Sine(300, Rate, Rate / 2, 3000), Signals.Hiss(Rate)));
        Assert.InRange(endedAt, (int)(Rate * 1.4) - 640, (int)(Rate * 1.4) + 640);
    }
}

public sealed class SentenceSplitterTests
{
    static List<string> Split(string text, int deltaSize, out string? rest)
    {
        var s = new SentenceSplitter();
        var pieces = new List<string>();
        for (int i = 0; i < text.Length; i += deltaSize) pieces.AddRange(s.Add(text.Substring(i, Math.Min(deltaSize, text.Length - i))));
        rest = s.Flush();
        return pieces;
    }

    [Fact]
    public void The_first_sentence_comes_out_as_soon_as_it_is_complete()
    {
        var s = new SentenceSplitter();
        Assert.Empty(s.Add("Sure, the kitchen lights"));
        Assert.Equal(["Sure, the kitchen lights are on."], s.Add(" are on. They"));
        Assert.Equal("They", s.Flush());
        Assert.Null(s.Flush());
    }

    [Fact]
    public void A_very_short_first_sentence_waits_for_more()
    {
        var s = new SentenceSplitter();
        Assert.Empty(s.Add("OK. "));
        Assert.Equal(["OK. The kitchen lights are on now."], s.Add("The kitchen lights are on now. And"));
    }

    [Fact]
    public void Later_pieces_are_at_least_a_short_phrase_long()
    {
        var pieces = Split("Done, the lounge is warm now. Yes. No. It is twenty one degrees in there right now. Bye.", 5, out var rest);
        Assert.Equal(["Done, the lounge is warm now.", "Yes. No. It is twenty one degrees in there right now."], pieces);
        Assert.Equal("Bye.", rest);
    }

    [Fact]
    public void Abbreviations_and_decimals_dont_end_sentences()
    {
        var pieces = Split("I spoke to Dr. Smith about it, e.g. the heating. It is 21.5 degrees in St. Kilda right now, approx. 3 more than here. Ok then", 3, out var rest);
        Assert.Equal(["I spoke to Dr. Smith about it, e.g. the heating.", "It is 21.5 degrees in St. Kilda right now, approx. 3 more than here."], pieces);
        Assert.Equal("Ok then", rest);
    }

    [Fact]
    public void Questions_exclamations_and_paragraphs_end_sentences()
    {
        var pieces = Split("Would you like the lounge too? Great choice, that's lovely!\n\nThe next part of the answer goes here. End", 4, out var rest);
        Assert.Equal(["Would you like the lounge too?", "Great choice, that's lovely!\n\nThe next part of the answer goes here."], pieces);
        Assert.Equal("End", rest);
    }

    [Fact]
    public void A_long_run_without_full_stops_is_cut_at_a_comma()
    {
        var text = string.Join(", ", Enumerable.Range(1, 30).Select(i => $"item number {i}"));
        var pieces = Split(text, 7, out var rest);
        Assert.NotEmpty(pieces);
        Assert.All(pieces, p => Assert.True(p.Length <= 221 && p.EndsWith(','), p));
        Assert.Equal(text, string.Join(" ", pieces.Append(rest!)));
    }

    [Fact]
    public void Streaming_in_tiny_pieces_matches_one_go()
    {
        const string text = "Here you go. The weather in Wellington is 18 degrees and sunny, with a light northerly. Tomorrow looks wet. Take a coat!";
        var one = Split(text, text.Length, out var restOne);
        var tiny = Split(text, 1, out var restTiny);
        Assert.Equal(one, tiny);
        Assert.Equal(restOne, restTiny);
    }
}

public sealed class SpeakableTextTests
{
    [Theory]
    [InlineData("**Bold** and `code` and __under__", "Bold and code and under")]
    [InlineData("See [the docs](https://example.com/docs) or https://example.com/x?y=1 now", "See the docs or the link now")]
    [InlineData("- one\n- two\n1. three", "one two three")]
    [InlineData("It's 21°C inside, 18° out", "It's 21 degrees inside, 18 degrees out")]
    [InlineData("Done ✅ 🎉", "Done")]
    [InlineData("## Heading\nText", "Heading Text")]
    public void SpeakableText_drops_what_cant_be_said(string input, string expected) => Assert.Equal(expected, SpeechService.SpeakableText(input));
}
