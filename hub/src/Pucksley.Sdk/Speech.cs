namespace Pucksley.Sdk;

/// <summary>
/// A speech-to-text engine, offered by a plugin (OpenAI, Azure, local Whisper…). The hub's Voice
/// settings choose which one to use.
/// </summary>
public interface ISpeechToText
{
    /// <summary>Unique id, e.g. <c>openai</c>.</summary>
    string EngineId { get; }

    string EngineName { get; }

    /// <summary>Whether it's configured well enough to try (e.g. has an API key).</summary>
    bool IsReady { get; }

    /// <summary>Transcribe one utterance. <paramref name="language"/> is a BCP-47 tag like "en-NZ" or null for auto.</summary>
    Task<string> TranscribeAsync(AudioClip audio, string? language, CancellationToken cancel);
}

/// <summary>A text-to-speech engine, offered by a plugin.</summary>
public interface ITextToSpeech
{
    string EngineId { get; }

    string EngineName { get; }

    bool IsReady { get; }

    /// <summary>
    /// Speak <paramref name="text"/> (one or a few sentences). Yield audio as it's made so playback
    /// can start early; any sample rate (the hub resamples to 16 kHz).
    /// </summary>
    IAsyncEnumerable<AudioChunk> SynthesizeAsync(string text, CancellationToken cancel);
}

/// <summary>Mono 16-bit PCM.</summary>
public sealed record AudioClip(short[] Samples, int SampleRate)
{
    public TimeSpan Duration => TimeSpan.FromSeconds((double)Samples.Length / SampleRate);

    /// <summary>As a WAV file (RIFF, 16-bit mono).</summary>
    public byte[] ToWav() => Wav.Encode(Samples, SampleRate);
}

/// <summary>A piece of synthesized speech: mono 16-bit PCM at <see cref="SampleRate"/>.</summary>
public sealed record AudioChunk(short[] Samples, int SampleRate);

/// <summary>
/// A speech-to-text engine the puck can call by itself, straight over the internet, when it can't
/// reach the hub ("direct mode"). Only an engine that is one plain HTTPS request can: the hub hands
/// the puck the request to make (key included) and the puck makes it.
/// </summary>
public interface IDirectSpeechToText
{
    /// <summary>The request for the puck, or null if this engine can't be called that way right now (no key, a server only the hub can reach).</summary>
    DirectTranscription? GetDirectTranscription(string? language);
}

/// <summary>A voice the puck can call by itself in direct mode (see <see cref="IDirectSpeechToText"/>).</summary>
public interface IDirectTextToSpeech
{
    /// <summary>The request for the puck, or null if this voice can't be called that way right now.</summary>
    DirectVoice? GetDirectVoice();
}

/// <summary>
/// How the puck transcribes on its own: POST the recording (16 kHz mono 16-bit WAV) to
/// <see cref="Url"/>, as multipart form data (the <see cref="Fields"/>, then the file as
/// <see cref="FileField"/>) or, when <see cref="Multipart"/> is false, as the whole body (put its
/// Content-Type in <see cref="Headers"/>). The transcript is the string at <see cref="TextField"/> in
/// the JSON answer: a property name, or a dotted path where numbers index arrays
/// ("results.channels.0.alternatives.0.transcript"). Headers are "Name: value" lines.
/// </summary>
public sealed record DirectTranscription(
    string Name, string Url, IReadOnlyList<string> Headers, bool Multipart,
    IReadOnlyList<KeyValuePair<string, string>> Fields, string FileField = "file", string TextField = "text");

/// <summary>
/// How the puck speaks a sentence on its own: POST <see cref="Body"/> to <see cref="Url"/> with every
/// <see cref="TextMark"/> replaced by the sentence (as a JSON string literal, quotes included, or
/// XML-escaped when <see cref="XmlBody"/>). The answer must be raw 16-bit mono PCM at
/// <see cref="SampleRate"/>. Headers are "Name: value" lines.
/// </summary>
public sealed record DirectVoice(string Name, string Url, IReadOnlyList<string> Headers, string Body, bool XmlBody, int SampleRate)
{
    public const string TextMark = "%TEXT%";

    /// <summary>
    /// A JSON body template from a request built with <paramref name="placeholder"/> as the text:
    /// the placeholder's JSON string (quotes and all) becomes <see cref="TextMark"/>.
    /// </summary>
    public static string JsonTemplate(string json, string placeholder) =>
        json.Replace(System.Text.Json.JsonSerializer.Serialize(placeholder), TextMark, StringComparison.Ordinal);
}

/// <summary>WAV helpers.</summary>
public static class Wav
{
    public static byte[] Encode(ReadOnlySpan<short> samples, int sampleRate)
    {
        int dataBytes = samples.Length * 2;
        var buf = new byte[44 + dataBytes];
        var s = buf.AsSpan();
        "RIFF"u8.CopyTo(s);
        BitConverter.TryWriteBytes(s[4..], 36 + dataBytes);
        "WAVE"u8.CopyTo(s[8..]);
        "fmt "u8.CopyTo(s[12..]);
        BitConverter.TryWriteBytes(s[16..], 16);
        BitConverter.TryWriteBytes(s[20..], (short)1);           // PCM
        BitConverter.TryWriteBytes(s[22..], (short)1);           // mono
        BitConverter.TryWriteBytes(s[24..], sampleRate);
        BitConverter.TryWriteBytes(s[28..], sampleRate * 2);     // byte rate
        BitConverter.TryWriteBytes(s[32..], (short)2);           // block align
        BitConverter.TryWriteBytes(s[34..], (short)16);          // bits
        "data"u8.CopyTo(s[36..]);
        BitConverter.TryWriteBytes(s[40..], dataBytes);
        System.Runtime.InteropServices.MemoryMarshal.AsBytes(samples).CopyTo(s[44..]);
        return buf;
    }

    /// <summary>
    /// Reads a PCM WAV (8/16/24/32-bit integer or 32-bit float, any channels) into mono 16-bit.
    /// </summary>
    public static AudioClip Decode(ReadOnlySpan<byte> wav)
    {
        if (wav.Length < 12 || !wav[..4].SequenceEqual("RIFF"u8) || !wav[8..12].SequenceEqual("WAVE"u8))
            throw new FormatException("Not a WAV file.");
        int pos = 12, channels = 1, rate = 16000, bits = 16, format = 1;
        while (pos + 8 <= wav.Length)
        {
            var id = wav.Slice(pos, 4);
            int size = BitConverter.ToInt32(wav.Slice(pos + 4, 4));
            int body = pos + 8;
            if (id.SequenceEqual("fmt "u8))
            {
                format = BitConverter.ToInt16(wav.Slice(body, 2));
                channels = BitConverter.ToInt16(wav.Slice(body + 2, 2));
                rate = BitConverter.ToInt32(wav.Slice(body + 4, 4));
                bits = BitConverter.ToInt16(wav.Slice(body + 14, 2));
                if (format == unchecked((short)0xFFFE) && size >= 26) format = BitConverter.ToInt16(wav.Slice(body + 24, 2));
            }
            else if (id.SequenceEqual("data"u8))
            {
                int len = Math.Min(size < 0 ? int.MaxValue : size, wav.Length - body);
                return new AudioClip(ToMono16(wav.Slice(body, len), channels, bits, format == 3), rate);
            }
            pos = body + size + (size & 1);
        }
        throw new FormatException("WAV file has no data.");
    }

    static short[] ToMono16(ReadOnlySpan<byte> data, int channels, int bits, bool isFloat)
    {
        int bytesPer = bits / 8;
        int frames = data.Length / (bytesPer * channels);
        var output = new short[frames];
        for (int f = 0; f < frames; f++)
        {
            double sum = 0;
            for (int c = 0; c < channels; c++)
            {
                var b = data.Slice((f * channels + c) * bytesPer, bytesPer);
                double v = bits switch
                {
                    8 => (b[0] - 128) / 128.0,
                    16 => BitConverter.ToInt16(b) / 32768.0,
                    24 => ((b[0] | (b[1] << 8) | ((sbyte)b[2] << 16))) / 8388608.0,
                    32 when isFloat => BitConverter.ToSingle(b),
                    32 => BitConverter.ToInt32(b) / 2147483648.0,
                    _ => 0,
                };
                sum += v;
            }
            output[f] = (short)Math.Clamp(sum / channels * 32767.0, -32768, 32767);
        }
        return output;
    }
}
