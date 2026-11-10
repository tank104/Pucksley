namespace Pucksley.Core.Voice;

/// <summary>
/// Streaming windowed-sinc resampler for mono 16-bit audio (TTS engines speak at 16, 22.05, 24 or
/// 44.1 kHz; the puck plays 16 kHz). Low-passes when downsampling so the little speaker doesn't
/// get aliasing hiss.
/// </summary>
public sealed class Resampler
{
    const int HalfTaps = 12;
    readonly double _step;          // input samples per output sample
    readonly double _cutoff;        // relative to the input Nyquist
    readonly List<float> _buf = [];
    double _pos;                    // next output position, in input samples relative to _buf[0]

    public int InputRate { get; }
    public int OutputRate { get; }

    public Resampler(int inputRate, int outputRate)
    {
        InputRate = inputRate;
        OutputRate = outputRate;
        _step = (double)inputRate / outputRate;
        _cutoff = Math.Min(1.0, (double)outputRate / inputRate) * 0.95;
        // Prime with silence so the first samples have history
        for (int i = 0; i < HalfTaps; i++) _buf.Add(0);
        _pos = HalfTaps;
    }

    public bool IsPassThrough => InputRate == OutputRate;

    /// <summary>Feeds samples in; returns the output samples now available.</summary>
    public short[] Process(ReadOnlySpan<short> input)
    {
        if (IsPassThrough) return input.ToArray();
        foreach (var s in input) _buf.Add(s);
        var output = new List<short>((int)(input.Length / _step) + 2);
        while (_pos + HalfTaps < _buf.Count)
        {
            output.Add(Sample(_pos));
            _pos += _step;
        }
        // Drop consumed history, keeping enough for the filter
        int drop = (int)Math.Floor(_pos) - HalfTaps;
        if (drop > 0)
        {
            _buf.RemoveRange(0, drop);
            _pos -= drop;
        }
        return output.ToArray();
    }

    /// <summary>Pushes out what's left at the end of a stream.</summary>
    public short[] Flush() => IsPassThrough ? [] : Process(new short[HalfTaps + 1]);

    short Sample(double t)
    {
        int center = (int)Math.Floor(t);
        double sum = 0, norm = 0;
        for (int k = center - HalfTaps + 1; k <= center + HalfTaps; k++)
        {
            if (k < 0 || k >= _buf.Count) continue;
            double x = t - k;
            double w = Kernel(x);
            sum += _buf[k] * w;
            norm += w;
        }
        if (norm != 0) sum /= norm;
        return (short)Math.Clamp(Math.Round(sum), short.MinValue, short.MaxValue);
    }

    double Kernel(double x)
    {
        // sinc low-pass at _cutoff, Hann window over ±HalfTaps
        double a = Math.Abs(x);
        if (a >= HalfTaps) return 0;
        double sinc = a < 1e-9 ? 1.0 : Math.Sin(Math.PI * _cutoff * x) / (Math.PI * _cutoff * x);
        double window = 0.5 * (1 + Math.Cos(Math.PI * x / HalfTaps));
        return _cutoff * sinc * window;
    }

    /// <summary>One-shot conversion of a whole clip.</summary>
    public static short[] Convert(short[] samples, int fromRate, int toRate)
    {
        if (fromRate == toRate) return samples;
        var r = new Resampler(fromRate, toRate);
        var a = r.Process(samples);
        var b = r.Flush();
        int expected = (int)Math.Round(samples.Length * (double)toRate / fromRate);
        var all = new short[Math.Min(expected, a.Length + b.Length)];
        Array.Copy(a, all, Math.Min(a.Length, all.Length));
        if (all.Length > a.Length) Array.Copy(b, 0, all, a.Length, all.Length - a.Length);
        return all;
    }
}

/// <summary>
/// Notices when someone starts and stops talking, from the loudness of 20 ms frames against a
/// running estimate of the room's background noise.
/// </summary>
public sealed class VoiceActivityDetector
{
    const int FrameSamples = 320;            // 20 ms at 16 kHz
    const int StartFrames = 10;              // the first 200 ms measure the room
    readonly double _ratio;                  // speech must be this much louder than the noise floor
    readonly int _endSilenceFrames;
    readonly int _startFrames = 3;           // 60 ms of loud frames = speech
    readonly short[] _frame = new short[FrameSamples];
    int _frameFill;
    double _noise = 120;                     // RMS of background noise (adapts)
    int _loudRun, _quietRun, _frames;
    double _dc;

    public VoiceActivityDetector(double sensitivity = 3.0, int endSilenceMs = 900)
    {
        _ratio = Math.Clamp(sensitivity, 1.5, 6.0);
        _endSilenceFrames = Math.Max(10, endSilenceMs / 20);
    }

    /// <summary>Speech has been heard.</summary>
    public bool SpeechStarted { get; private set; }

    /// <summary>Speech was heard and has now stopped for the end-silence time.</summary>
    public bool SpeechEnded { get; private set; }

    /// <summary>Where speech began (in samples), so leading silence can be trimmed.</summary>
    public int SpeechStartSample { get; private set; }

    /// <summary>Where the last loud frame ended (in samples).</summary>
    public int LastSpeechSample { get; private set; }

    /// <summary>Recent loudness 0..1 (for a level meter).</summary>
    public double Level { get; private set; }

    public void Process(ReadOnlySpan<short> samples)
    {
        foreach (var s in samples)
        {
            _frame[_frameFill++] = s;
            if (_frameFill == FrameSamples)
            {
                Frame();
                _frameFill = 0;
            }
        }
    }

    void Frame()
    {
        double sum = 0;
        for (int i = 0; i < FrameSamples; i++)
        {
            // remove DC offset (MEMS mics often have one)
            _dc += (_frame[i] - _dc) * 0.005;
            double v = _frame[i] - _dc;
            sum += v * v;
        }
        double rms = Math.Sqrt(sum / FrameSamples);
        _frames++;
        Level = Math.Clamp(Math.Log10(1 + rms) / 4.0, 0, 1);

        // The first 200 ms set the noise floor from their quietest frame (minimum statistics): the
        // puck's own "listening" beep, which plays while its mic is already on and can fill most of
        // that time, or someone who starts talking at once, can't inflate it. After that it adapts
        // only when quiet: slowly up, quicker down (so a loud start is soon forgotten).
        if (_frames <= StartFrames) _noise = _frames == 1 ? rms : Math.Min(_noise, rms);
        bool loud = _frames > StartFrames && rms > Math.Max(_noise * _ratio, 250);
        if (!loud && _frames > StartFrames)
            _noise = rms < _noise ? _noise * 0.9 + rms * 0.1 : _noise * 0.98 + Math.Min(rms, _noise * 2) * 0.02;

        if (loud)
        {
            _loudRun++;
            _quietRun = 0;
            if (!SpeechStarted && _loudRun >= _startFrames)
            {
                SpeechStarted = true;
                SpeechStartSample = Math.Max(0, (_frames - _startFrames - 10) * FrameSamples); // keep 200 ms before
            }
            if (SpeechStarted) LastSpeechSample = _frames * FrameSamples;
        }
        else
        {
            _loudRun = 0;
            if (SpeechStarted && ++_quietRun >= _endSilenceFrames) SpeechEnded = true;
        }
    }
}

/// <summary>
/// Cuts streamed answer text into speakable pieces: whole sentences, but not tiny ones, so speech
/// can start after the first sentence instead of after the whole answer.
/// </summary>
public sealed class SentenceSplitter
{
    readonly System.Text.StringBuilder _pending = new();
    readonly int _minChars;
    bool _first = true;

    public SentenceSplitter(int minChars = 40) => _minChars = minChars;

    /// <summary>Adds text; returns any complete pieces.</summary>
    public IEnumerable<string> Add(string delta)
    {
        _pending.Append(delta);
        var result = new List<string>();
        while (true)
        {
            int cut = FindCut(_pending.ToString(), _first ? 12 : _minChars);
            if (cut < 0) break;
            var piece = _pending.ToString(0, cut).Trim();
            _pending.Remove(0, cut);
            if (piece.Length > 0)
            {
                result.Add(piece);
                _first = false;
            }
        }
        return result;
    }

    /// <summary>Whatever is left at the end.</summary>
    public string? Flush()
    {
        var rest = _pending.ToString().Trim();
        _pending.Clear();
        return rest.Length > 0 ? rest : null;
    }

    static int FindCut(string s, int min)
    {
        for (int i = min; i < s.Length; i++)
        {
            char c = s[i - 1];
            bool end = c is '.' or '!' or '?' or ';' or ':' or '\n';
            if (!end) continue;
            // the next char must be whitespace (not "3.5" or "e.g.x")
            if (!char.IsWhiteSpace(s[i])) continue;
            // don't cut after common abbreviations
            var before = s[..(i - 1)];
            int sp = before.LastIndexOf(' ');
            var word = before[(sp + 1)..];
            if (c == '.' && word is "Mr" or "Mrs" or "Ms" or "Dr" or "St" or "vs" or "e.g" or "i.e" or "approx") continue;
            return i;
        }
        // a very long run without punctuation: cut at a comma
        if (s.Length > 220)
        {
            int comma = s.LastIndexOf(", ", 200, StringComparison.Ordinal);
            if (comma > min) return comma + 1;
        }
        return -1;
    }
}
