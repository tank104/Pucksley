using System.Globalization;
using System.Speech.AudioFormat;
using System.Speech.Recognition;
using Pucksley.Sdk;

namespace Pucksley.Plugins.Windows.Speech;

/// <summary>
/// Offline speech to text with Windows' desktop (SAPI) dictation engine via System.Speech. No API
/// key or network, but accuracy is well below the cloud engines: a fallback.
/// </summary>
/// <remarks>Only reference this type from methods marked NoInlining, so a missing System.Speech is catchable.</remarks>
internal sealed class SapiDictation : IDisposable
{
    static readonly TimeSpan MaxRecognition = TimeSpan.FromSeconds(60);
    static readonly int[] SupportedRates = [8000, 11025, 16000, 22050, 44100, 48000];

    readonly SemaphoreSlim _gate = new(1, 1); // one recognition at a time per engine
    SpeechRecognitionEngine? _engine;
    string? _engineId;

    /// <summary>Installed recognizers' cultures ("en-US"); empty when none (or System.Speech can't run).</summary>
    public static IReadOnlyList<string> InstalledCultures()
    {
        try
        {
            return SpeechRecognitionEngine.InstalledRecognizers().Select(r => r.Culture.Name).ToList();
        }
        catch (Exception)
        {
            return [];
        }
    }

    public async Task<string> TranscribeAsync(short[] samples, int sampleRate, string? language, CancellationToken cancel)
    {
        await _gate.WaitAsync(cancel).ConfigureAwait(false);
        try
        {
            // Recognition blocks: give it its own thread rather than a pool thread
            return await Task.Factory.StartNew(() => Recognize(samples, sampleRate, language, cancel),
                cancel, TaskCreationOptions.LongRunning, TaskScheduler.Default).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    string Recognize(short[] samples, int sampleRate, string? language, CancellationToken cancel)
    {
        if (!SupportedRates.Contains(sampleRate))
        {
            samples = Resampler.Linear(samples, sampleRate, 16000);
            sampleRate = 16000;
        }
        var engine = EngineFor(language ?? CultureInfo.CurrentCulture.Name);

        // Raw PCM plus half a second of silence, which helps the engine decide the last phrase has ended
        var pcm = new byte[(samples.Length + sampleRate / 2) * sizeof(short)];
        Buffer.BlockCopy(samples, 0, pcm, 0, samples.Length * sizeof(short));
        using var audio = new MemoryStream(pcm, writable: false);

        var phrases = new List<string>();
        using var finished = new ManualResetEventSlim();
        void OnRecognized(object? sender, SpeechRecognizedEventArgs e)
        {
            if (e.Result?.Text is { Length: > 0 } text)
                lock (phrases) phrases.Add(text);
        }
        void OnCompleted(object? sender, RecognizeCompletedEventArgs e) => finished.Set();

        engine.SpeechRecognized += OnRecognized;
        engine.RecognizeCompleted += OnCompleted;
        try
        {
            engine.SetInputToAudioStream(audio, new SpeechAudioFormatInfo(sampleRate, AudioBitsPerSample.Sixteen, AudioChannel.Mono));
            // Multiple: keep going phrase after phrase until the stream ends
            engine.RecognizeAsync(RecognizeMode.Multiple);
            using (cancel.Register(() => CancelQuietly(engine)))
            {
                if (!finished.Wait(MaxRecognition))
                {
                    CancelQuietly(engine);
                    finished.Wait(TimeSpan.FromSeconds(2));
                }
            }
        }
        finally
        {
            engine.SpeechRecognized -= OnRecognized;
            engine.RecognizeCompleted -= OnCompleted;
            if (finished.IsSet)
            {
                try { engine.SetInputToNull(); }
                catch (InvalidOperationException) { /* already detached */ }
            }
            else
            {
                DisposeEngine(); // still busy: start the next recognition with a fresh engine
            }
        }
        cancel.ThrowIfCancellationRequested();
        lock (phrases) return string.Join(" ", phrases).Trim();
    }

    static void CancelQuietly(SpeechRecognitionEngine engine)
    {
        try { engine.RecognizeAsyncCancel(); }
        catch (Exception) { /* not running */ }
    }

    /// <summary>A cached engine with dictation loaded (that takes a second or so the first time).</summary>
    SpeechRecognitionEngine EngineFor(string language)
    {
        var installed = SpeechRecognitionEngine.InstalledRecognizers();
        int index = VoicePicker.PickRecognizer(installed.Select(r => r.Culture.Name).ToList(), language);
        if (index < 0) throw new PluginException("No Windows speech recognizer is installed. Add a speech language in Windows Settings > Time & language > Speech.");
        var info = installed[index];
        if (_engine is not null && _engineId == info.Id) return _engine;

        DisposeEngine();
        var engine = new SpeechRecognitionEngine(info);
        try
        {
            engine.LoadGrammar(new DictationGrammar());
        }
        catch
        {
            engine.Dispose();
            throw;
        }
        _engine = engine;
        _engineId = info.Id;
        return engine;
    }

    void DisposeEngine()
    {
        _engine?.Dispose();
        _engine = null;
        _engineId = null;
    }

    public void Dispose()
    {
        _gate.Wait(TimeSpan.FromSeconds(5));
        DisposeEngine();
    }
}
