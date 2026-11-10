using System.Globalization;
using System.Runtime.CompilerServices;
using Pucksley.Sdk;
using Windows.Media.SpeechSynthesis;

namespace Pucksley.Plugins.Windows.Speech;

/// <summary>Text to speech with Windows' OneCore voices (Windows.Media.SpeechSynthesis).</summary>
internal static class OneCoreVoices
{
    static readonly TimeSpan SentenceTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Installed voices; empty when the speech platform isn't available (e.g. some Server editions).</summary>
    public static IReadOnlyList<VoiceInformation> All()
    {
        try
        {
            return SpeechSynthesizer.AllVoices.ToList();
        }
        catch (Exception)
        {
            return [];
        }
    }

    /// <summary>See <see cref="VoicePicker.Pick"/>.</summary>
    public static VoiceInformation? Choose(string? wanted)
    {
        var voices = All();
        string? defaultName = null;
        try
        {
            defaultName = SpeechSynthesizer.DefaultVoice?.DisplayName;
        }
        catch (Exception)
        {
            // no default voice
        }
        var languages = new[] { CultureInfo.CurrentCulture.Name, CultureInfo.CurrentUICulture.Name };
        int index = VoicePicker.Pick(voices.Select(v => (v.DisplayName, v.Language)).ToList(), wanted, languages, defaultName);
        return index >= 0 ? voices[index] : null;
    }

    /// <summary>One chunk per sentence, each yielded as soon as it's made.</summary>
    public static async IAsyncEnumerable<AudioChunk> SynthesizeAsync(string text, VoiceInformation? voice, double rate, double pitch,
        [EnumeratorCancellation] CancellationToken cancel)
    {
        using var synthesizer = new SpeechSynthesizer();
        if (voice is not null) synthesizer.Voice = voice;
        synthesizer.Options.SpeakingRate = Math.Clamp(rate is > 0 ? rate : 1, 0.5, 2.0);
        synthesizer.Options.AudioPitch = Math.Clamp(pitch is > 0 ? pitch : 1, 0.5, 1.5);
        synthesizer.Options.AppendedSilence = SpeechAppendedSilence.Min; // less dead air between sentences

        foreach (var sentence in SentenceSplitter.Split(text))
        {
            cancel.ThrowIfCancellationRequested();
            byte[] wav;
            using (var stream = await synthesizer.SynthesizeTextToStreamAsync(sentence).AsTask(cancel).WaitAsync(SentenceTimeout, cancel).ConfigureAwait(false))
            {
                wav = await ReadAllAsync(stream, cancel).ConfigureAwait(false);
            }
            var clip = Wav.Decode(wav);
            if (clip.Samples.Length > 0) yield return new AudioChunk(clip.Samples, clip.SampleRate);
        }
    }

    /// <summary>The stream holds a complete WAV file.</summary>
    static async Task<byte[]> ReadAllAsync(SpeechSynthesisStream stream, CancellationToken cancel)
    {
        using var input = stream.AsStreamForRead();
        using var buffer = new MemoryStream((int)Math.Min(stream.Size, int.MaxValue));
        await input.CopyToAsync(buffer, cancel).ConfigureAwait(false);
        return buffer.ToArray();
    }
}
