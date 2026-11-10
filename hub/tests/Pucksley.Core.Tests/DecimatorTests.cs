using Pucksley.Core.Voice;

namespace Pucksley.Core.Tests;

/// <summary>48 kHz → 16 kHz for the talking face's audio.</summary>
public class DecimatorTests
{
    static short[] Sine(double hz, int samples, double amplitude = 10000) =>
        Enumerable.Range(0, samples).Select(i => (short)Math.Round(amplitude * Math.Sin(2 * Math.PI * hz * i / 48000))).ToArray();

    static double Rms(ReadOnlySpan<short> s, int skip)
    {
        double sum = 0;
        for (int i = skip; i < s.Length; i++) sum += (double)s[i] * s[i];
        return Math.Sqrt(sum / (s.Length - skip));
    }

    [Fact]
    public void Speech_frequencies_pass_at_a_third_of_the_rate()
    {
        var output = new Decimator().Process(Sine(1000, 48000));
        Assert.Equal(16000, output.Length);
        Assert.InRange(Rms(output, 100), 10000 / Math.Sqrt(2) * 0.97, 10000 / Math.Sqrt(2) * 1.03);
    }

    [Fact]
    public void Frequencies_the_puck_can_not_play_are_filtered_out_not_folded_back()
    {
        // 12 kHz would alias to 4 kHz at 16 kHz without the low-pass
        var output = new Decimator().Process(Sine(12000, 48000));
        Assert.True(Rms(output, 100) < 10000 / Math.Sqrt(2) * 0.01, $"rms {Rms(output, 100):0}");
    }

    [Fact]
    public void Pieces_of_any_size_join_seamlessly()
    {
        var input = Sine(700, 9600);
        var whole = new Decimator().Process(input);
        var pieces = new Decimator();
        var joined = new List<short>();
        int offset = 0;
        foreach (var size in new[] { 1920, 1, 2, 1917, 3000, 2760 })
        {
            joined.AddRange(pieces.Process(input.AsSpan(offset, size)));
            offset += size;
        }
        Assert.Equal(input.Length, offset);
        Assert.Equal(whole, joined.ToArray());
    }
}
