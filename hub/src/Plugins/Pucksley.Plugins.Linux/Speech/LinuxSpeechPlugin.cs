using System.Globalization;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Pucksley.Plugins.Linux.Common;
using Pucksley.Sdk;

namespace Pucksley.Plugins.Linux.Speech;

public sealed class LinuxSpeechSettings
{
    [Setting("Voice", Placeholder = "e.g. Scotland, or en-us", Help = "Part of a voice's name, or its language code. Blank picks an English voice to suit this PC's language. \"List voices\" shows what's installed.")]
    public string Voice { get; set; } = "";

    [Setting("Speaking rate", Help = "0.5 (slow) to 2 (fast); 1 is normal.")]
    public double Rate { get; set; } = 1.0;

    [Setting("Pitch", Help = "0.5 (low) to 1.5 (high); 1 is normal.", Advanced = true)]
    public double Pitch { get; set; } = 1.0;
}

/// <summary>One of eSpeak NG's voices: the name people read and the code it's asked for by.</summary>
internal sealed record EspeakVoice(string Name, string Language);

/// <summary>
/// A voice that needs no account and no download: eSpeak NG, which most Linux desktops already
/// have (it's what their screen readers speak with). It sounds like a robot, so it's the voice of
/// last resort, as Windows' own voices are on Windows; Piper and the cloud voices sound far better.
/// </summary>
public sealed class LinuxSpeechPlugin : PuckPlugin<LinuxSpeechSettings>, ITextToSpeech
{
    const string ListVoicesAction = "list-voices";
    static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);
    const int NormalWordsPerMinute = 175, NormalPitch = 50;

    readonly object _gate = new();
    IReadOnlyList<EspeakVoice>? _voices;
    EspeakVoice? _voice;
    bool _voiceChosen;

    public override string Id => "linux-speech";
    public override string Name => "Linux voices";
    public override string Description => "Speaks with eSpeak NG, the voice most Linux desktops already have: robotic, but it needs no API key and no download.";
    public override string Icon => "pc";
    public override bool EnabledByDefault => true;

    public string EngineId => "espeak";
    public string EngineName => "eSpeak NG (basic)";
    public bool IsReady => Program is not null;

    /// <summary>espeak-ng, or the older espeak it grew out of (same options).</summary>
    static string? Program => Programs.FirstOf("espeak-ng", "espeak");

    protected override void OnSettingsApplied()
    {
        lock (_gate) _voiceChosen = false;
    }

    public override async Task StartAsync(CancellationToken cancel)
    {
        lock (_gate)
        {
            _voices = null;
            _voiceChosen = false;
        }
        if (Program is null)
        {
            SetStatus(StatusLevel.Idle, "eSpeak NG isn't installed (the espeak-ng package)");
            return;
        }
        var voices = await VoicesAsync(cancel).ConfigureAwait(false);
        var voice = await VoiceAsync(cancel).ConfigureAwait(false);
        string wanted = Settings.Voice.Trim();
        if (voices.Count == 0)
            SetStatus(StatusLevel.Warning, "eSpeak NG has no voices installed");
        else if (wanted.Length > 0 && voice is not null && !Matches(voice, wanted))
            SetStatus(StatusLevel.Warning, $"No voice like '{wanted}', using {voice.Name}");
        else
            SetStatus(StatusLevel.Ok, $"Voice: {voice?.Name} ({voices.Count} installed)");
    }

    // ───────────────────────── Voices

    async Task<IReadOnlyList<EspeakVoice>> VoicesAsync(CancellationToken cancel)
    {
        lock (_gate)
        {
            if (_voices is not null) return _voices;
        }
        IReadOnlyList<EspeakVoice> voices = [];
        if (Program is { } program)
        {
            try
            {
                voices = ParseVoices(await Programs.OutputAsync(program, ["--voices"], Timeout, cancel, environment: Programs.PlainLocale).ConfigureAwait(false));
            }
            catch (PluginException e)
            {
                Log.LogDebug("Couldn't list eSpeak's voices: {Message}", e.Message);
            }
        }
        lock (_gate) return _voices = voices;
    }

    /// <summary>
    /// <c>espeak-ng --voices</c>: a header, then lines of "Pty Language Age/Gender VoiceName File
    /// Other-languages", e.g. " 2  en-gb  --/M  English_(Great_Britain)  gmw/en  (en 2)".
    /// </summary>
    internal static List<EspeakVoice> ParseVoices(string listing)
    {
        var voices = new List<EspeakVoice>();
        foreach (var line in listing.Split('\n').Skip(1))
        {
            var columns = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (columns.Length < 4 || !int.TryParse(columns[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out _)) continue;
            voices.Add(new EspeakVoice(columns[3].Replace('_', ' '), columns[1]));
        }
        return voices;
    }

    static bool Matches(EspeakVoice voice, string wanted) =>
        voice.Language.Equals(wanted, StringComparison.OrdinalIgnoreCase) || voice.Name.Contains(wanted, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The voice to speak with: the one asked for (by language code, or part of its name), else
    /// the best English one for this PC's language (see <see cref="VoicePicker.Pick"/>).
    /// </summary>
    internal static EspeakVoice? Choose(IReadOnlyList<EspeakVoice> voices, string? wanted, IReadOnlyList<string> systemLanguages)
    {
        if (voices.Count == 0) return null;
        wanted = wanted?.Trim();
        if (!string.IsNullOrEmpty(wanted) && voices.FirstOrDefault(v => v.Language.Equals(wanted, StringComparison.OrdinalIgnoreCase)) is { } byCode) return byCode;
        int index = VoicePicker.Pick(voices.Select(v => (v.Name, v.Language)).ToList(), wanted, systemLanguages, null);
        return index >= 0 ? voices[index] : null;
    }

    async Task<EspeakVoice?> VoiceAsync(CancellationToken cancel)
    {
        lock (_gate)
        {
            if (_voiceChosen) return _voice;
        }
        var voices = await VoicesAsync(cancel).ConfigureAwait(false);
        var voice = Choose(voices, Settings.Voice, [CultureInfo.CurrentUICulture.Name, CultureInfo.CurrentCulture.Name]);
        lock (_gate)
        {
            _voice = voice;
            _voiceChosen = true;
        }
        return voice;
    }

    // ───────────────────────── Text to speech

    /// <summary>The options for a voice at a rate (0.5–2) and a pitch (0.5–1.5): words a minute and eSpeak's 0–99 pitch.</summary>
    internal static string[] Arguments(EspeakVoice? voice, double rate, double pitch)
    {
        int words = (int)Math.Round(NormalWordsPerMinute * Math.Clamp(double.IsFinite(rate) && rate > 0 ? rate : 1, 0.5, 2));
        int tone = (int)Math.Clamp(Math.Round(NormalPitch * (double.IsFinite(pitch) && pitch > 0 ? pitch : 1)), 0, 99);
        var arguments = new List<string> { "--stdin", "--stdout", "-s", words.ToString(CultureInfo.InvariantCulture), "-p", tone.ToString(CultureInfo.InvariantCulture) };
        if (voice is not null) arguments.AddRange(["-v", voice.Language]);
        return [.. arguments];
    }

    public async IAsyncEnumerable<AudioChunk> SynthesizeAsync(string text, [EnumeratorCancellation] CancellationToken cancel)
    {
        string program = Program ?? throw new PluginException("eSpeak NG isn't installed on this PC. Install the espeak-ng package, or choose another voice on the hub's Voice page.");
        var arguments = Arguments(await VoiceAsync(cancel).ConfigureAwait(false), Settings.Rate, Settings.Pitch);
        // A sentence at a time, so the first can play while the rest are made
        foreach (var sentence in SentenceSplitter.Split(text))
        {
            cancel.ThrowIfCancellationRequested();
            var result = await Programs.RunBinaryAsync(program, arguments, Timeout, cancel, input: sentence).ConfigureAwait(false);
            if (result.ExitCode != 0) throw Programs.Failed(Path.GetFileName(program), result.ExitCode, result.Error);
            if (result.Output.Length <= 44) continue; // a header and nothing said
            AudioClip clip;
            try
            {
                clip = Wav.Decode(result.Output);
            }
            catch (FormatException e)
            {
                throw new PluginException($"eSpeak NG gave something that isn't sound ({e.Message}).", e);
            }
            if (clip.Samples.Length > 0) yield return new AudioChunk(clip.Samples, clip.SampleRate);
        }
    }

    // ───────────────────────── Web page

    public override IReadOnlyList<PluginAction> GetActions() =>
        [new PluginAction(ListVoicesAction, "List voices", "Show the English voices eSpeak NG has.")];

    public override async Task<ActionResult> RunActionAsync(string actionId, IReadOnlyDictionary<string, string> input, CancellationToken cancel)
    {
        if (actionId != ListVoicesAction) return await base.RunActionAsync(actionId, input, cancel).ConfigureAwait(false);
        var voices = await VoicesAsync(cancel).ConfigureAwait(false);
        if (voices.Count == 0) return new ActionResult("No voices: eSpeak NG isn't installed (the espeak-ng package).", false);
        var english = voices.Where(v => v.Language.StartsWith("en", StringComparison.OrdinalIgnoreCase)).ToList();
        var shown = english.Count > 0 ? english : voices.Take(40).ToList();
        var inUse = await VoiceAsync(cancel).ConfigureAwait(false);
        return new ActionResult($"{voices.Count} voices in all. English: {string.Join(", ", shown.Select(v => $"{v.Name} ({v.Language})"))}. In use: {inUse?.Name ?? "eSpeak's default"}.");
    }
}
