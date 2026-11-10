using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Pucksley.Sdk;
using Windows.Media.SpeechSynthesis;

namespace Pucksley.Plugins.Windows.Speech;

public sealed class WindowsSpeechSettings
{
    [Setting("Voice", Placeholder = "e.g. Zira", Help = "Part of a voice's name. Blank picks an English voice to suit Windows' language. \"List voices\" shows what's installed.")]
    public string Voice { get; set; } = "";

    [Setting("Speaking rate", Help = "0.5 (slow) to 2 (fast); 1 is normal.")]
    public double Rate { get; set; } = 1.0;

    [Setting("Pitch", Help = "0.5 (low) to 1.5 (high); 1 is normal.", Advanced = true)]
    public double Pitch { get; set; } = 1.0;
}

/// <summary>
/// Speech engines that need no account: Windows' installed voices for text to speech, and Windows'
/// built-in offline dictation for speech to text (basic accuracy; a fallback).
/// </summary>
public sealed class WindowsSpeechPlugin : PuckPlugin<WindowsSpeechSettings>, ITextToSpeech, ISpeechToText
{
    const string ListVoicesAction = "list-voices";

    readonly object _gate = new();
    VoiceInformation? _voice;
    bool _voiceChosen;
    bool? _ttsReady, _sttReady;
    SapiDictation? _dictation;

    public override string Id => "windows-speech";
    public override string Name => "Windows voices";
    public override string Description => "Speaks with the voices installed in Windows, and offers Windows' built-in offline speech recognition (basic accuracy, but no API key needed).";
    public override string Icon => "pc";
    public override bool EnabledByDefault => true;

    // Both interfaces name their engine "windows"; they're listed separately in Voice settings
    string ITextToSpeech.EngineId => "windows";
    string ITextToSpeech.EngineName => "Windows voices";
    bool ITextToSpeech.IsReady => TtsReady;

    string ISpeechToText.EngineId => "windows";
    string ISpeechToText.EngineName => "Windows offline recognition (basic)";
    bool ISpeechToText.IsReady => SttReady;

    bool TtsReady => _ttsReady ??= OneCoreVoices.All().Count > 0;

    bool SttReady
    {
        get
        {
            if (_sttReady is bool known) return known;
            bool ready;
            try
            {
                ready = DictationCultures().Count > 0;
            }
            catch (Exception)
            {
                ready = false; // System.Speech couldn't load
            }
            return (_sttReady = ready).Value;
        }
    }

    // NoInlining: if System.Speech can't load, the failure surfaces at this call, inside the caller's try
    [MethodImpl(MethodImplOptions.NoInlining)]
    static IReadOnlyList<string> DictationCultures() => SapiDictation.InstalledCultures();

    VoiceInformation? Voice
    {
        get
        {
            lock (_gate)
            {
                if (!_voiceChosen)
                {
                    _voice = OneCoreVoices.Choose(Settings.Voice);
                    _voiceChosen = true;
                }
                return _voice;
            }
        }
    }

    protected override void OnSettingsApplied()
    {
        lock (_gate) _voiceChosen = false;
    }

    public override Task StartAsync(CancellationToken cancel)
    {
        _ttsReady = null;
        _sttReady = null;
        int voices = OneCoreVoices.All().Count;
        var voice = Voice;
        string recognition = SttReady ? $"offline recognition: {string.Join(", ", DictationCultures())}" : "offline recognition unavailable";

        if (voices == 0)
            SetStatus(StatusLevel.Warning, $"No Windows voices installed; {recognition}");
        else if (!string.IsNullOrWhiteSpace(Settings.Voice) && voice?.DisplayName.Contains(Settings.Voice.Trim(), StringComparison.OrdinalIgnoreCase) != true)
            SetStatus(StatusLevel.Warning, $"No voice like '{Settings.Voice.Trim()}', using {voice?.DisplayName}; {recognition}");
        else
            SetStatus(StatusLevel.Ok, $"Voice: {voice?.DisplayName} ({voices} installed); {recognition}");
        return Task.CompletedTask;
    }

    public override Task StopAsync()
    {
        SapiDictation? dictation;
        lock (_gate)
        {
            dictation = _dictation;
            _dictation = null;
        }
        if (dictation is not null) DisposeDictation(dictation);
        return Task.CompletedTask;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void DisposeDictation(SapiDictation dictation) => dictation.Dispose();

    // ───────────────────────── Text to speech

    IAsyncEnumerable<AudioChunk> ITextToSpeech.SynthesizeAsync(string text, CancellationToken cancel)
    {
        if (!TtsReady) throw new PluginException("No Windows voices are installed. Add one in Windows Settings > Time & language > Speech.");
        return OneCoreVoices.SynthesizeAsync(text, Voice, Settings.Rate, Settings.Pitch, cancel);
    }

    // ───────────────────────── Speech to text

    Task<string> ISpeechToText.TranscribeAsync(AudioClip audio, string? language, CancellationToken cancel)
    {
        if (!SttReady) throw new PluginException("Windows speech recognition isn't available on this PC.");
        if (audio.Samples.Length == 0) return Task.FromResult("");
        return Transcribe(audio, language, cancel);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    Task<string> Transcribe(AudioClip audio, string? language, CancellationToken cancel)
    {
        SapiDictation dictation;
        lock (_gate) dictation = _dictation ??= new SapiDictation();
        return dictation.TranscribeAsync(audio.Samples, audio.SampleRate, language, cancel);
    }

    // ───────────────────────── Web page

    public override IReadOnlyList<PluginAction> GetActions() =>
        [new PluginAction(ListVoicesAction, "List voices", "Show the voices installed in Windows.")];

    public override Task<ActionResult> RunActionAsync(string actionId, IReadOnlyDictionary<string, string> input, CancellationToken cancel)
    {
        if (actionId != ListVoicesAction) return base.RunActionAsync(actionId, input, cancel);
        var voices = OneCoreVoices.All();
        if (voices.Count == 0)
            return Task.FromResult(new ActionResult("No voices are installed. Add some in Windows Settings > Time & language > Speech.", false));
        var list = string.Join(", ", voices.Select(v => $"{v.DisplayName} ({v.Language})"));
        Log.LogDebug("Windows voices: {Voices}", list);
        return Task.FromResult(new ActionResult($"{voices.Count} voices: {list}. In use: {Voice?.DisplayName ?? "Windows' default"}."));
    }
}
