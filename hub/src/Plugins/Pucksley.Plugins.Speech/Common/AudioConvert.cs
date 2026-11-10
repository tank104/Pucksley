using Pucksley.Sdk;

namespace Pucksley.Plugins.Speech;

/// <summary>Small sample-format helpers.</summary>
internal static class AudioConvert
{
    /// <summary>Under 100 ms there are no words to find (and OpenAI rejects clips that short), so engines skip the call.</summary>
    public static bool TooShortToTranscribe(AudioClip clip)
    {
        if (clip.SampleRate <= 0) throw new ArgumentException("The clip's sample rate must be positive.", nameof(clip));
        return clip.Samples.Length < clip.SampleRate / 10;
    }

    /// <summary>
    /// Linear-interpolation resampling. Fine for speech going to a recogniser (the puck already sends 16 kHz,
    /// so this is a fallback); returns <paramref name="samples"/> itself when the rates match.
    /// </summary>
    public static short[] Resample(short[] samples, int fromRate, int toRate)
    {
        if (fromRate <= 0 || toRate <= 0) throw new ArgumentOutOfRangeException(nameof(fromRate), "Sample rates must be positive.");
        if (fromRate == toRate || samples.Length == 0) return samples;
        long outLength = Math.Max(1, (long)samples.Length * toRate / fromRate);
        var output = new short[outLength];
        double step = (double)fromRate / toRate;
        int last = samples.Length - 1;
        for (long i = 0; i < outLength; i++)
        {
            double pos = i * step;
            int index = (int)pos;
            if (index >= last)
            {
                output[i] = samples[last];
                continue;
            }
            double frac = pos - index;
            output[i] = (short)Math.Round(samples[index] + (samples[index + 1] - samples[index]) * frac);
        }
        return output;
    }

    /// <summary>
    /// 16-bit samples → floats in [-1, 1), padded with trailing silence to at least <paramref name="minLength"/>
    /// samples.
    /// </summary>
    public static float[] ToFloats(short[] samples, int minLength = 0)
    {
        var output = new float[Math.Max(samples.Length, minLength)];
        for (int i = 0; i < samples.Length; i++) output[i] = samples[i] / 32768f;
        return output;
    }
}
