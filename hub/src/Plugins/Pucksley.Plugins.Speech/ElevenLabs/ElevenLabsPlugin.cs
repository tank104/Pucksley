using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;
using Pucksley.Sdk;

namespace Pucksley.Plugins.Speech;

public sealed class ElevenLabsSettings
{
    [Setting("API key", Secret = true, Order = 1,
        Help = "From elevenlabs.io → Developers → API keys. Allow Text to Speech, Speech to Text and (for List voices) Voices read.")]
    public string ApiKey { get; set; } = "";

    [Setting("Voice ID", Order = 2, Placeholder = ElevenLabsPlugin.DefaultVoiceId, Help = "Click List voices to see the voices in your account and their IDs.")]
    public string VoiceId { get; set; } = ElevenLabsPlugin.DefaultVoiceId;

    [Setting("Model", Order = 3,
        Help = "eleven_flash_v2_5 starts speaking fastest; eleven_turbo_v2_5 and eleven_multilingual_v2 sound richer but take longer.")]
    public string Model { get; set; } = ElevenLabsPlugin.DefaultModel;

    [Setting("Stability", Order = 4, Advanced = true, Placeholder = "voice default",
        Help = "0 to 1: lower is more expressive, higher is steadier. Empty uses the voice's own setting.")]
    public string Stability { get; set; } = "";

    [Setting("Similarity", Order = 5, Advanced = true, Placeholder = "voice default",
        Help = "0 to 1: how closely to match the original voice. Empty uses the voice's own setting.")]
    public string Similarity { get; set; } = "";

    [Setting("Transcription model", Order = 6, Advanced = true)]
    public string SttModel { get; set; } = ElevenLabsPlugin.DefaultSttModel;
}

/// <summary>ElevenLabs voices (streamed as 16 kHz PCM) and Scribe transcription.</summary>
public sealed class ElevenLabsPlugin : SpeechPlugin<ElevenLabsSettings>, ISpeechToText, ITextToSpeech, IDirectSpeechToText, IDirectTextToSpeech
{
    /// <summary>"Rachel", one of the stock voices every account has.</summary>
    public const string DefaultVoiceId = "21m00Tcm4TlvDq8ikWAM";
    public const string DefaultModel = "eleven_flash_v2_5";
    public const string DefaultSttModel = "scribe_v1";
    internal const string ApiBase = "https://api.elevenlabs.io";
    internal const int PcmSampleRate = 16000;
    const string Service = "ElevenLabs";
    const string ListVoicesAction = "list-voices";

    public override string Id => "elevenlabs";
    public override string Name => "ElevenLabs";
    public override string Description => "Very natural voices, and transcription, from ElevenLabs. Flash voices start speaking quickly.";

    public string EngineId => "elevenlabs";
    public string EngineName => "ElevenLabs";
    public bool IsReady => NullIfBlank(Settings.ApiKey) is not null;

    public async Task<string> TranscribeAsync(AudioClip audio, string? language, CancellationToken cancel)
    {
        var s = Settings;
        var key = KeyOrThrow(s);
        if (AudioConvert.TooShortToTranscribe(audio)) return "";

        using var form = new MultipartFormDataContent();
        form.AddFile("file", audio.ToWav(), "speech.wav", "audio/wav");
        form.AddField("model_id", Or(s.SttModel, DefaultSttModel));
        if (Languages.Primary(language) is { } lang) form.AddField("language_code", lang);
        form.AddField("tag_audio_events", "false");  // no "(laughter)" in what Claude reads
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{ApiBase}/v1/speech-to-text") { Content = form };
        request.Headers.TryAddWithoutValidation("xi-api-key", key);

        using var response = await SendAsync(request, Service, HttpCompletionOption.ResponseContentRead, cancel).ConfigureAwait(false);
        var json = await SpeechHttp.ReadJsonAsync(response, Service, cancel).ConfigureAwait(false);
        return SpeechHttp.GetString(json, "text")?.Trim()
               ?? throw SpeechHttp.Fail(ServiceErrorKind.BadResponse, null, null, "ElevenLabs sent a transcription without any text.");
    }

    public async IAsyncEnumerable<AudioChunk> SynthesizeAsync(string text, [EnumeratorCancellation] CancellationToken cancel)
    {
        var s = Settings;
        var speech = SpeechText.Clean(text);
        if (speech.Length == 0) yield break;
        var key = KeyOrThrow(s);

        using var request = new HttpRequestMessage(HttpMethod.Post, SpeechUrl(s))
        {
            Content = new StringContent(BuildSpeechRequest(s, speech).ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("xi-api-key", key);
        using var response = await SendAsync(request, Service, HttpCompletionOption.ResponseHeadersRead, cancel).ConfigureAwait(false);
        await using var pcm = await response.Content.ReadAsStreamAsync(cancel).ConfigureAwait(false);
        await foreach (var chunk in PcmStream.ReadChunksAsync(pcm, PcmSampleRate, Service, cancel).ConfigureAwait(false))
            yield return chunk;
    }

    // ---- direct mode: the same requests, made by the puck itself when the hub is away

    public DirectTranscription? GetDirectTranscription(string? language)
    {
        var s = Settings;
        if (NullIfBlank(s.ApiKey) is not { } key) return null;
        var fields = new List<KeyValuePair<string, string>> { new("model_id", Or(s.SttModel, DefaultSttModel)) };
        if (Languages.Primary(language) is { } lang) fields.Add(new("language_code", lang));
        fields.Add(new("tag_audio_events", "false"));
        return new DirectTranscription(Service, $"{ApiBase}/v1/speech-to-text", [$"xi-api-key: {key}"], Multipart: true, fields);
    }

    public DirectVoice? GetDirectVoice()
    {
        var s = Settings;
        if (NullIfBlank(s.ApiKey) is not { } key) return null;
        const string mark = "\u0001puck-text\u0001";
        var body = DirectVoice.JsonTemplate(BuildSpeechRequest(s, mark).ToJsonString(), mark);
        return new DirectVoice(Service, SpeechUrl(s).ToString(), [$"xi-api-key: {key}", "Content-Type: application/json"], body, XmlBody: false, PcmSampleRate);
    }

    public override IReadOnlyList<PluginAction> GetActions() =>
        [new(ListVoicesAction, "List voices", "Show the voices in your ElevenLabs account and their IDs.")];

    public override async Task<ActionResult> RunActionAsync(string actionId, IReadOnlyDictionary<string, string> input, CancellationToken cancel)
    {
        if (actionId != ListVoicesAction) return await base.RunActionAsync(actionId, input, cancel).ConfigureAwait(false);
        if (!IsReady) return new ActionResult("Add an API key first.", false);
        try
        {
            var voices = await ListVoicesAsync(cancel).ConfigureAwait(false);
            return new ActionResult(FormatVoices(voices, Or(Settings.VoiceId, DefaultVoiceId)));
        }
        catch (PluginException e)
        {
            return new ActionResult(e.Message, false);
        }
    }

    internal sealed record Voice(string Id, string Name, string? Accent, string? Gender, string? Category);

    internal async Task<IReadOnlyList<Voice>> ListVoicesAsync(CancellationToken cancel)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{ApiBase}/v1/voices");
        request.Headers.TryAddWithoutValidation("xi-api-key", KeyOrThrow(Settings));
        using var response = await SendAsync(request, Service, HttpCompletionOption.ResponseContentRead, cancel).ConfigureAwait(false);
        var json = await SpeechHttp.ReadJsonAsync(response, Service, cancel).ConfigureAwait(false);

        var voices = new List<Voice>();
        if (json["voices"] is JsonArray list)
        {
            foreach (var v in list)
            {
                if (SpeechHttp.GetString(v, "voice_id") is not { } id || SpeechHttp.GetString(v, "name") is not { } name) continue;
                var labels = v?["labels"];
                voices.Add(new Voice(id, name, SpeechHttp.GetString(labels, "accent"), SpeechHttp.GetString(labels, "gender"), SpeechHttp.GetString(v, "category")));
            }
        }
        return voices.OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    internal static string FormatVoices(IReadOnlyList<Voice> voices, string currentId)
    {
        if (voices.Count == 0) return "No voices found in this ElevenLabs account.";
        var sb = new StringBuilder($"{voices.Count} voices (name: ID). Copy an ID into Voice ID.");
        foreach (var v in voices.Take(100))
        {
            var about = string.Join(", ", new[] { v.Accent, v.Gender }.Where(x => !string.IsNullOrWhiteSpace(x)));
            sb.Append('\n').Append(v.Name);
            if (about.Length > 0) sb.Append(" (").Append(about).Append(')');
            sb.Append(": ").Append(v.Id);
            if (v.Id == currentId) sb.Append("  ← current");
        }
        return sb.ToString();
    }

    protected override PluginStatus DescribeSetup() => IsReady
        ? new(StatusLevel.Ok, $"Ready: voice {Or(Settings.VoiceId, DefaultVoiceId)}, {Or(Settings.Model, DefaultModel)}")
        : new(StatusLevel.Idle, "Add an API key");

    internal static Uri SpeechUrl(ElevenLabsSettings s) =>
        new($"{ApiBase}/v1/text-to-speech/{Uri.EscapeDataString(Or(s.VoiceId, DefaultVoiceId))}/stream?output_format=pcm_16000");

    internal static JsonObject BuildSpeechRequest(ElevenLabsSettings s, string text)
    {
        var body = new JsonObject { ["text"] = text, ["model_id"] = Or(s.Model, DefaultModel) };
        var voiceSettings = new JsonObject();
        if (Fraction(s.Stability) is { } stability) voiceSettings["stability"] = stability;
        if (Fraction(s.Similarity) is { } similarity) voiceSettings["similarity_boost"] = similarity;
        if (voiceSettings.Count > 0) body["voice_settings"] = voiceSettings;
        return body;
    }

    /// <summary>A 0–1 setting typed as text ("0.4" or "0,4"); null when empty or not a number.</summary>
    internal static double? Fraction(string? text) =>
        double.TryParse(text?.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && double.IsFinite(v)
            ? Math.Clamp(v, 0, 1)
            : null;

    static string KeyOrThrow(ElevenLabsSettings s) =>
        NullIfBlank(s.ApiKey) ?? throw new PluginException("ElevenLabs isn't set up: add an API key on the hub's Plugins page.");
}
