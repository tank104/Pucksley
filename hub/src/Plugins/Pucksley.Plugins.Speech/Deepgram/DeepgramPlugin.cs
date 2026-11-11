using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using Pucksley.Sdk;

namespace Pucksley.Plugins.Speech;

public sealed class DeepgramSettings
{
    [Setting("API key", Secret = true, Order = 1,
        Help = "From console.deepgram.com → API Keys → Create a New API Key (the Member role is enough).")]
    public string ApiKey { get; set; } = "";

    [Setting("Language", Order = 2, Placeholder = "the hub's language",
        Help = "e.g. en-NZ, en-AU, en-GB or en. Empty uses the hub's language.")]
    public string Language { get; set; } = "";

    [Setting("Names to listen for", Order = 3,
        Help = "Words it might otherwise mishear, one per line: people, rooms, places, the assistant's name (e.g. Tāmaki, Mitchell, the Den). Nova-3 only.")]
    public List<string> KeyTerms { get; set; } = [];

    [Setting("Model", Order = 4, Advanced = true, Placeholder = DeepgramPlugin.DefaultModel,
        Help = "nova-3 is the most accurate. nova-2 also works (without the names list).")]
    public string Model { get; set; } = DeepgramPlugin.DefaultModel;

    [Setting("Let Deepgram use recordings to improve its models", Order = 5, Advanced = true,
        Help = "Off (the default) asks Deepgram not to keep or train on what the pucks hear.")]
    public bool ImproveModels { get; set; }
}

/// <summary>Deepgram Nova-3 transcription over REST: fast and accurate on short commands, New Zealand English included.</summary>
public sealed class DeepgramPlugin : SpeechPlugin<DeepgramSettings>, IStreamingSpeechToText, IDirectSpeechToText
{
    public const string DefaultModel = "nova-3";
    internal const string ApiBase = "https://api.deepgram.com";
    internal const string TranscriptPath = "results.channels.0.alternatives.0.transcript";
    const string Service = "Deepgram";
    const int MaxKeyTerms = 50;

    // Nova-3's regional English; any other English tag goes as plain "en", other languages as their primary subtag.
    static readonly HashSet<string> EnglishRegions = new(StringComparer.OrdinalIgnoreCase) { "US", "AU", "GB", "IN", "NZ" };

    public override string Id => "deepgram";
    public override string Name => "Deepgram";
    public override string Description => "Fast, accurate speech recognition (Nova-3), with New Zealand English. Hearing only: pair it with a voice such as Azure.";

    public string EngineId => "deepgram";
    public string EngineName => "Deepgram";
    public bool IsReady => NullIfBlank(Settings.ApiKey) is not null;

    public async Task<string> TranscribeAsync(AudioClip audio, string? language, CancellationToken cancel)
    {
        var s = Settings;
        var key = NullIfBlank(s.ApiKey) ?? throw new PluginException("Deepgram isn't set up: add an API key on the hub's Plugins page.");
        if (AudioConvert.TooShortToTranscribe(audio)) return "";

        var content = new ByteArrayContent(audio.ToWav());
        content.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        using var request = new HttpRequestMessage(HttpMethod.Post, ListenUrl(s, language)) { Content = content };
        request.Headers.TryAddWithoutValidation("Authorization", $"Token {key}");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await SendAsync(request, Service, HttpCompletionOption.ResponseContentRead, cancel).ConfigureAwait(false);
        return ReadTranscript(await SpeechHttp.ReadJsonAsync(response, Service, cancel).ConfigureAwait(false));
    }

    // ---- live: words while the person is still talking, and Deepgram telling when they've finished

    public async Task<ISpeechStream?> StartStreamAsync(SpeechStreamOptions options, CancellationToken cancel)
    {
        var s = Settings;
        if (NullIfBlank(s.ApiKey) is not { } key) return null;
        return await DeepgramStream.OpenAsync(LiveUrl(s, options), key, Log, cancel).ConfigureAwait(false);
    }

    /// <summary>
    /// The streaming address: the same options as <see cref="ListenUrl"/>, plus raw 16 kHz audio,
    /// interim words, and the pause that ends what was said ("endpointing" after speech; an
    /// "utterance end" from the words' timing a little later, which noise can't hold off).
    /// </summary>
    internal static Uri LiveUrl(DeepgramSettings s, SpeechStreamOptions o)
    {
        var rest = ListenUrl(s, o.Language);
        // Live recognition can't detect the language; Nova-3 listens for several at once instead
        var query = rest.Query.TrimStart('?').Replace("detect_language=true", "language=multi");
        int pause = Math.Clamp(o.EndSilenceMs, 300, 3000);
        query += $"&encoding=linear16&sample_rate=16000&channels=1&interim_results=true&vad_events=true" +
                 $"&endpointing={pause}&utterance_end_ms={Math.Clamp(pause + 300, 1000, 5000)}";
        return new UriBuilder(rest) { Scheme = "wss", Port = -1, Query = query }.Uri;
    }

    // ---- direct mode: the same request, made by the puck itself when the hub is away

    public DirectTranscription? GetDirectTranscription(string? language)
    {
        var s = Settings;
        if (NullIfBlank(s.ApiKey) is not { } key) return null;
        return new DirectTranscription(Service, ListenUrl(s, language).AbsoluteUri,   // escaped: the puck sends it as is
            [$"Authorization: Token {key}", "Content-Type: audio/wav", "Accept: application/json"],
            Multipart: false, [], TextField: TranscriptPath);
    }

    protected override PluginStatus DescribeSetup()
    {
        var s = Settings;
        if (NullIfBlank(s.ApiKey) is null) return new(StatusLevel.Idle, "Add an API key");
        var terms = CleanKeyTerms(s.KeyTerms).Count;
        var listening = terms == 0 ? "" : $", listening for {terms} name{(terms == 1 ? "" : "s")}";
        return new(StatusLevel.Ok, $"Ready: {Or(s.Model, DefaultModel)}, recognising {Or(s.Language, "the hub's language")}{listening}");
    }

    /// <summary>The /v1/listen address with every option in the query, so the puck can use it as is.</summary>
    internal static Uri ListenUrl(DeepgramSettings s, string? hubLanguage)
    {
        var model = Or(s.Model, DefaultModel);
        var query = new List<string> { $"model={Uri.EscapeDataString(model)}", "smart_format=true" };
        if (LanguageFor(NullIfBlank(s.Language) ?? hubLanguage) is { } lang) query.Add($"language={Uri.EscapeDataString(lang)}");
        else query.Add("detect_language=true");
        if (!s.ImproveModels) query.Add("mip_opt_out=true");
        if (model.StartsWith("nova-3", StringComparison.OrdinalIgnoreCase))
            foreach (var term in CleanKeyTerms(s.KeyTerms)) query.Add($"keyterm={Uri.EscapeDataString(term)}");
        return new($"{ApiBase}/v1/listen?{string.Join('&', query)}");
    }

    /// <summary>
    /// "en-NZ" → "en-NZ" (Nova-3 has New Zealand, Australian, British, Indian and US English), "en-IE" → "en",
    /// "de-DE" → "de"; null (detect it) for an empty or automatic language.
    /// </summary>
    internal static string? LanguageFor(string? tag)
    {
        if (Languages.Primary(tag) is not { } primary) return null;
        var parts = tag!.Trim().Split('-', '_');
        if (primary == "en" && parts.Length >= 2 && EnglishRegions.Contains(parts[^1])) return $"en-{parts[^1].ToUpperInvariant()}";
        return primary;
    }

    /// <summary>Trimmed, de-duplicated, without blanks, at most 50 (each one makes recognition a little slower).</summary>
    internal static List<string> CleanKeyTerms(IEnumerable<string>? terms) =>
        (terms ?? [])
            .Select(t => string.Join(' ', (t ?? "").Split((char[])[' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries)))
            .Where(t => t.Length is > 0 and <= 100)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxKeyTerms)
            .ToList();

    /// <summary>The first channel's best transcript; "" when nothing was said.</summary>
    internal static string ReadTranscript(JsonNode json)
    {
        var alternative = json["results"]?["channels"] is JsonArray { Count: > 0 } channels
                          && channels[0]?["alternatives"] is JsonArray { Count: > 0 } alternatives
            ? alternatives[0]
            : null;
        return alternative is null
            ? throw SpeechHttp.Fail(ServiceErrorKind.BadResponse, null, null, "Deepgram sent an answer without a transcript.")
            : SpeechHttp.GetString(alternative, "transcript")?.Trim() ?? "";
    }
}
