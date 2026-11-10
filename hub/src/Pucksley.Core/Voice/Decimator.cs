namespace Pucksley.Core.Voice;

/// <summary>
/// 48 kHz → 16 kHz for a stream arriving in pieces: a windowed-sinc low-pass (about 7 kHz) and
/// every third sample. It keeps its history between calls, so the joins between pieces are clean.
/// </summary>
public sealed class Decimator
{
    public const int Factor = 3;
    const int Taps = 48;
    static readonly float[] Kernel = MakeKernel();
    readonly float[] _history = new float[Taps];   // the last Taps inputs, oldest first
    int _phase;                                    // inputs since the last output

    static float[] MakeKernel()
    {
        var k = new float[Taps];
        double cutoff = 7200.0 / 48000.0, sum = 0;
        for (int i = 0; i < Taps; i++)
        {
            double m = i - (Taps - 1) / 2.0;
            double sinc = m == 0 ? 2 * cutoff : Math.Sin(2 * Math.PI * cutoff * m) / (Math.PI * m);
            double blackman = 0.42 - 0.5 * Math.Cos(2 * Math.PI * i / (Taps - 1)) + 0.08 * Math.Cos(4 * Math.PI * i / (Taps - 1));
            k[i] = (float)(sinc * blackman);
            sum += k[i];
        }
        for (int i = 0; i < Taps; i++) k[i] = (float)(k[i] / sum);
        return k;
    }

    public short[] Process(ReadOnlySpan<short> input)
    {
        var output = new List<short>(input.Length / Factor + 1);
        foreach (var sample in input)
        {
            Array.Copy(_history, 1, _history, 0, Taps - 1);
            _history[Taps - 1] = sample;
            if (++_phase < Factor) continue;
            _phase = 0;
            float acc = 0;
            for (int i = 0; i < Taps; i++) acc += _history[i] * Kernel[i];
            output.Add((short)Math.Clamp(MathF.Round(acc), short.MinValue, short.MaxValue));
        }
        return [.. output];
    }
}
