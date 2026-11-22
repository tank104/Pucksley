namespace Pucksley.Plugins.Pc.Speech;

/// <summary>Sample-rate conversion for speech (linear interpolation: fine for recognition, not for music).</summary>
internal static class Resampler
{
    public static short[] Linear(short[] input, int fromRate, int toRate)
    {
        if (fromRate <= 0 || toRate <= 0) throw new ArgumentOutOfRangeException(nameof(fromRate), "Sample rates must be positive.");
        if (fromRate == toRate || input.Length == 0) return input;
        long length = (long)input.Length * toRate / fromRate;
        var output = new short[length];
        double step = (double)fromRate / toRate;
        for (long i = 0; i < length; i++)
        {
            double position = i * step;
            int i0 = (int)position;
            int i1 = Math.Min(i0 + 1, input.Length - 1);
            double fraction = position - i0;
            output[i] = (short)Math.Round(input[i0] + (input[i1] - input[i0]) * fraction);
        }
        return output;
    }
}
