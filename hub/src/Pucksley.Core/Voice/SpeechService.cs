using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Pucksley.Core.Plugins;
using Pucksley.Core.Settings;
using Pucksley.Sdk;

namespace Pucksley.Core.Voice;

/// <summary>Picks the speech engines from the Voice settings and runs them at 16 kHz.</summary>
public sealed partial class SpeechService(SettingsStore settings, PluginManager plugins, ILogger<SpeechService> log)
{
    public const int Rate = 16000;

    public ISpeechToText? Stt
    {
        get
        {
            var want = settings.Current.Voice.SttEngine;
            var engines = plugins.SttEngines;
            return engines.FirstOrDefault(e => e.EngineId.Equals(want, StringComparison.OrdinalIgnoreCase))
                   ?? engines.FirstOrDefault(e => e.IsReady);
        }
    }

    public ITextToSpeech? Tts
    {
        get
        {
            var want = settings.Current.Voice.TtsEngine;
            if (want.Equals("none", StringComparison.OrdinalIgnoreCase)) return null;
            var engines = plugins.TtsEngines;
            return engines.FirstOrDefault(e => e.EngineId.Equals(want, StringComparison.OrdinalIgnoreCase) && e.IsReady)
                   ?? engines.FirstOrDefault(e => e.IsReady && e.EngineId != "windows")   // a configured cloud voice
                   ?? engines.FirstOrDefault(e => e.IsReady);
        }
    }

    public async Task<string> TranscribeAsync(short[] samples, CancellationToken cancel)
    {
        var engine = Stt ?? throw new PluginException("No speech recognition engine is set up. Choose one on the hub's Voice page.");
        if (!engine.IsReady) throw new PluginException($"{engine.EngineName} isn't set up yet (see the Plugins page).");
        var lang = settings.Current.Voice.Language;
        var text = await engine.TranscribeAsync(new AudioClip(samples, Rate), string.IsNullOrWhiteSpace(lang) ? null : lang.Trim(), cancel).ConfigureAwait(false);
        return CleanTranscript(text);
    }

    /// <summary>Speech for <paramref name="text"/> as 16 kHz PCM, streamed. Empty when no voice is set up.</summary>
    public async IAsyncEnumerable<short[]> SynthesizeAsync(string text, [EnumeratorCancellation] CancellationToken cancel)
    {
        var engine = Tts;
        if (engine is null) yield break;
        var spoken = SpeakableText(text);
        if (spoken.Length == 0) yield break;
        Resampler? resampler = null;
        await foreach (var chunk in engine.SynthesizeAsync(spoken, cancel).ConfigureAwait(false))
        {
            if (chunk.Samples.Length == 0) continue;
            if (resampler is null || resampler.InputRate != chunk.SampleRate) resampler = new Resampler(chunk.SampleRate, Rate);
            var output = resampler.Process(chunk.Samples);
            if (output.Length > 0) yield return output;
        }
        if (resampler is not null)
        {
            var tail = resampler.Flush();
            if (tail.Length > 0) yield return tail;
        }
    }

    /// <summary>A whole utterance as a WAV file (for announcements on other speakers).</summary>
    public async Task<byte[]> SynthesizeWavAsync(string text, CancellationToken cancel)
    {
        var all = new List<short>();
        await foreach (var c in SynthesizeAsync(text, cancel).ConfigureAwait(false)) all.AddRange(c);
        if (all.Count == 0) throw new PluginException("No voice is set up on the hub (Voice page), so I can't make speech.");
        return Wav.Encode(all.ToArray(), Rate);
    }

    /// <summary>Whisper and friends sometimes "hear" these in silence.</summary>
    static readonly string[] Hallucinations =
    [
        "thank you.", "thanks for watching!", "thanks for watching.", "you", "bye.", ".", "thank you for watching.",
        "[blank_audio]", "(silence)", "[music]", "subtitles by the amara.org community",
    ];

    static string CleanTranscript(string text)
    {
        var t = (text ?? "").Trim();
        t = BracketNoise().Replace(t, "").Trim();
        if (Hallucinations.Contains(t.ToLowerInvariant())) return "";
        return t;
    }

    /// <summary>Text as it should be spoken: no markdown, no URLs, no emoji.</summary>
    public static string SpeakableText(string text)
    {
        var t = text;
        t = MarkdownLink().Replace(t, "$1");
        t = Url().Replace(t, "the link");
        t = t.Replace("**", "").Replace("__", "").Replace("`", "").Replace("#", "");
        t = Bullet().Replace(t, "");
        t = Emoji().Replace(t, "");
        t = t.Replace("°C", " degrees").Replace("°", " degrees");
        t = Spaces().Replace(t, " ");
        return t.Trim();
    }

    [GeneratedRegex(@"\[(?:BLANK_AUDIO|MUSIC|NOISE|INAUDIBLE|SOUND)\]|\((?:inaudible|silence|music)\)", RegexOptions.IgnoreCase)]
    private static partial Regex BracketNoise();
    [GeneratedRegex(@"\[([^\]]+)\]\([^)]+\)")]
    private static partial Regex MarkdownLink();
    [GeneratedRegex(@"https?://\S+")]
    private static partial Regex Url();
    [GeneratedRegex(@"(?m)^\s*(?:[-*•]|\d+\.)\s+")]
    private static partial Regex Bullet();
    [GeneratedRegex(@"[☀-➿]|[\uD83C-\uDBFF][\uDC00-\uDFFF]")]
    private static partial Regex Emoji();
    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    internal ILogger Log => log;
}
