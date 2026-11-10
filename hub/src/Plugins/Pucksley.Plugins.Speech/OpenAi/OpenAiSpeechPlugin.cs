using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;
using Pucksley.Sdk;

namespace Pucksley.Plugins.Speech;

public sealed class OpenAiSpeechSettings
{
    [Setting("API key", Secret = true, Order = 1,
        Help = "From platform.openai.com → API keys, or your compatible server's key. A server on this computer or network may not need one.")]
    public string ApiKey { get; set; } = "";

    [Setting("Base URL", Order = 2, Placeholder = OpenAiSpeechPlugin.DefaultBaseUrl,
        Help = "Also works with OpenAI-compatible servers, e.g. Groq https://api.groq.com/openai/v1 for very fast transcription, or local Speaches / Kokoro-FastAPI servers.")]
    public string BaseUrl { get; set; } = OpenAiSpeechPlugin.DefaultBaseUrl;

    [Setting("Transcription model", Order = 3,
        Help = "gpt-4o-mini-transcribe (fast), gpt-4o-transcribe (most accurate) or whisper-1; on Groq, whisper-large-v3-turbo.")]
    public string SttModel { get; set; } = OpenAiSpeechPlugin.DefaultSttModel;

    [Setting("Transcription base URL", Order = 4, Advanced = true, Placeholder = "same as Base URL",
        Help = "A different server just for transcription, e.g. Groq https://api.groq.com/openai/v1 (model whisper-large-v3-turbo) while OpenAI does the voice.")]
    public string SttBaseUrl { get; set; } = "";

    [Setting("Transcription API key", Secret = true, Order = 5, Advanced = true,
        Help = "The transcription server's key, if it's different. The main key is only ever sent to the main server.")]
    public string SttApiKey { get; set; } = "";

    [Setting("Voice model", Order = 6, Help = "gpt-4o-mini-tts (follows the voice instructions), or tts-1 / tts-1-hd.")]
    public string TtsModel { get; set; } = OpenAiSpeechPlugin.DefaultTtsModel;

    [Setting("Voice", Order = 7, Options = ["alloy", "ash", "ballad", "coral", "echo", "fable", "nova", "onyx", "sage", "shimmer", "verse"])]
    public string Voice { get; set; } = OpenAiSpeechPlugin.DefaultVoice;

    [Setting("Other voice", Order = 8, Advanced = true, Placeholder = "e.g. af_heart",
        Help = "Overrides Voice: a newer OpenAI voice, or one your compatible server offers (Kokoro, Speaches).")]
    public string CustomVoice { get; set; } = "";

    [Setting("Voice instructions", Order = 9, Multiline = true,
        Help = "How to speak (gpt-4o-mini-tts only): tone, pace, accent, e.g. add \"with a light New Zealand accent\".")]
    public string TtsInstructions { get; set; } = OpenAiSpeechPlugin.DefaultInstructions;

    [Setting("Speed", Order = 10, Advanced = true, Help = "0.25 to 4; 1 is normal.")]
    public double Speed { get; set; } = 1.0;
}

/// <summary>
/// OpenAI's transcription and voices, or any server with the same API (Groq, Speaches, Kokoro-FastAPI). Transcription
/// can go to a different server than speech, e.g. Groq for speed while OpenAI does the voice.
/// </summary>
public sealed class OpenAiSpeechPlugin : SpeechPlugin<OpenAiSpeechSettings>, ISpeechToText, ITextToSpeech, IDirectSpeechToText, IDirectTextToSpeech
{
    public const string DefaultBaseUrl = "https://api.openai.com/v1";
    public const string DefaultSttModel = "gpt-4o-mini-transcribe";
    public const string DefaultTtsModel = "gpt-4o-mini-tts";
    public const string DefaultVoice = "coral";
    public const string DefaultInstructions = "Speak naturally and warmly, at a brisk conversational pace.";

    /// <summary>The "pcm" response format: 24 kHz, 16-bit signed little-endian, mono, no header.</summary>
    internal const int PcmSampleRate = 24000;

    /// <summary>The speech endpoint's input limit is 4096 characters.</summary>
    internal const int MaxInputLength = 4000;

    public override string Id => "openai-speech";
    public override string Name => "OpenAI speech";

    public override string Description =>
        "Transcription and natural voices from OpenAI, or any OpenAI-compatible server: Groq for very fast transcription, or Speaches / Kokoro at home.";

    public string EngineId => "openai";

    string ISpeechToText.EngineName => EngineNameFor(TranscriptionServer(Settings));
    string ITextToSpeech.EngineName => EngineNameFor(SpeechServer(Settings));
    bool ISpeechToText.IsReady => TranscriptionServer(Settings) is { CanCall: true };
    bool ITextToSpeech.IsReady => SpeechServer(Settings) is { CanCall: true };

    public async Task<string> TranscribeAsync(AudioClip audio, string? language, CancellationToken cancel)
    {
        var s = Settings;
        var server = TranscriptionServer(s) is { CanCall: true } ready
            ? ready
            : throw new PluginException("OpenAI transcription isn't set up: add an API key on the hub's Plugins page.");
        if (AudioConvert.TooShortToTranscribe(audio)) return "";

        using var form = new MultipartFormDataContent();
        form.AddFile("file", audio.ToWav(), "speech.wav", "audio/wav");
        form.AddField("model", Or(s.SttModel, DefaultSttModel));
        if (Languages.Iso6391(language) is { } lang) form.AddField("language", lang);
        form.AddField("response_format", "json");
        using var request = new HttpRequestMessage(HttpMethod.Post, ServerUrl.Combine(server.BaseUrl, "audio/transcriptions")) { Content = form };
        Authorize(request, server);

        using var response = await SendAsync(request, server.Service, HttpCompletionOption.ResponseContentRead, cancel).ConfigureAwait(false);
        var json = await SpeechHttp.ReadJsonAsync(response, server.Service, cancel).ConfigureAwait(false);
        return SpeechHttp.GetString(json, "text")?.Trim()
               ?? throw SpeechHttp.Fail(ServiceErrorKind.BadResponse, null, null, $"{server.Service} sent a transcription without any text.");
    }

    public async IAsyncEnumerable<AudioChunk> SynthesizeAsync(string text, [EnumeratorCancellation] CancellationToken cancel)
    {
        var s = Settings;
        var speech = SpeechText.Clean(text);
        if (speech.Length == 0) yield break;
        var server = SpeechServer(s) is { CanCall: true } ready
            ? ready
            : throw new PluginException("The OpenAI voice isn't set up: add an API key on the hub's Plugins page.");

        foreach (var piece in SpeechText.SplitForLimit(speech, MaxInputLength))
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, ServerUrl.Combine(server.BaseUrl, "audio/speech"))
            {
                Content = new StringContent(BuildSpeechRequest(s, piece).ToJsonString(), Encoding.UTF8, "application/json"),
            };
            Authorize(request, server);
            using var response = await SendAsync(request, server.Service, HttpCompletionOption.ResponseHeadersRead, cancel).ConfigureAwait(false);
            await using var pcm = await response.Content.ReadAsStreamAsync(cancel).ConfigureAwait(false);
            await foreach (var chunk in PcmStream.ReadChunksAsync(pcm, PcmSampleRate, server.Service, cancel).ConfigureAwait(false))
                yield return chunk;
        }
    }

    // ---- direct mode: the puck calls the service itself when the hub is away. Only a cloud server
    // with a key qualifies (the puck can't reach a server that's only local to the hub).

    public DirectTranscription? GetDirectTranscription(string? language)
    {
        var s = Settings;
        if (TranscriptionServer(s) is not { } server || !ForPuck(server)) return null;
        var fields = new List<KeyValuePair<string, string>> { new("model", Or(s.SttModel, DefaultSttModel)) };
        if (Languages.Iso6391(language) is { } lang) fields.Add(new("language", lang));
        fields.Add(new("response_format", "json"));
        return new DirectTranscription(server.Service, ServerUrl.Combine(server.BaseUrl, "audio/transcriptions").ToString(),
            [$"Authorization: Bearer {server.ApiKey}"], Multipart: true, fields);
    }

    public DirectVoice? GetDirectVoice()
    {
        var s = Settings;
        if (SpeechServer(s) is not { } server || !ForPuck(server)) return null;
        const string mark = "\u0001puck-text\u0001";
        var body = DirectVoice.JsonTemplate(BuildSpeechRequest(s, mark).ToJsonString(), mark);
        return new DirectVoice(server.Service, ServerUrl.Combine(server.BaseUrl, "audio/speech").ToString(),
            [$"Authorization: Bearer {server.ApiKey}", "Content-Type: application/json"], body, XmlBody: false, PcmSampleRate);
    }

    static bool ForPuck(Server server) =>
        server.ApiKey is not null && server.BaseUrl.Scheme == Uri.UriSchemeHttps && !ServerUrl.IsLocal(server.BaseUrl);

    protected override PluginStatus DescribeSetup()
    {
        var s = Settings;
        if (SpeechServer(s) is not { } tts) return new(StatusLevel.Error, "The base URL isn't a valid web address");
        if (TranscriptionServer(s) is not { } stt) return new(StatusLevel.Error, "The transcription base URL isn't a valid web address");
        string sttModel = Or(s.SttModel, DefaultSttModel), voice = $"{Or(s.TtsModel, DefaultTtsModel)} ({VoiceOf(s)})";
        return (stt.CanCall, tts.CanCall) switch
        {
            (false, false) => new(StatusLevel.Idle, "Add an API key"),
            (true, true) when stt.Service == tts.Service => new(StatusLevel.Ok, $"Ready: {sttModel} and {voice}"),
            (true, true) => new(StatusLevel.Ok, $"Ready: {sttModel} on {stt.Service}, {voice} on {tts.Service}"),
            (true, false) => new(StatusLevel.Ok, $"Transcription ready on {stt.Service}; add an API key for the voice"),
            (false, true) => new(StatusLevel.Ok, $"Voice ready; add a transcription API key for {stt.Service}"),
        };
    }

    /// <summary>Where requests go and with which key (null for none).</summary>
    internal sealed record Server(Uri BaseUrl, string? ApiKey, string Service)
    {
        /// <summary>Has a key, or doesn't need one (a server on this machine or the local network).</summary>
        public bool CanCall => ApiKey is not null || ServerUrl.IsLocal(BaseUrl);
    }

    internal static Server? SpeechServer(OpenAiSpeechSettings s) =>
        ServerUrl.ParseBase(Or(s.BaseUrl, DefaultBaseUrl)) is { } url ? new Server(url, NullIfBlank(s.ApiKey), ServiceName(url)) : null;

    internal static Server? TranscriptionServer(OpenAiSpeechSettings s)
    {
        var main = SpeechServer(s);
        var sttKey = NullIfBlank(s.SttApiKey);
        if (string.IsNullOrWhiteSpace(s.SttBaseUrl)) return main is null ? null : main with { ApiKey = sttKey ?? main.ApiKey };
        if (ServerUrl.ParseBase(s.SttBaseUrl) is not { } url) return null;
        // The main key belongs to the main server: reuse it only if the transcription server is that same server.
        var key = sttKey ?? (main is not null && ServerUrl.SameServer(main.BaseUrl, url) ? main.ApiKey : null);
        return new Server(url, key, ServiceName(url));
    }

    internal static JsonObject BuildSpeechRequest(OpenAiSpeechSettings s, string text)
    {
        var model = Or(s.TtsModel, DefaultTtsModel);
        var body = new JsonObject
        {
            ["model"] = model,
            ["voice"] = VoiceOf(s),
            ["input"] = text,
            ["response_format"] = "pcm",
        };
        // tts-1 and tts-1-hd don't take instructions.
        if (NullIfBlank(s.TtsInstructions) is { } instructions && !model.StartsWith("tts-1", StringComparison.OrdinalIgnoreCase))
            body["instructions"] = instructions;
        if (s.Speed > 0 && Math.Abs(s.Speed - 1.0) > 0.001) body["speed"] = Math.Clamp(s.Speed, 0.25, 4.0);
        return body;
    }

    internal static string ServiceName(Uri url) =>
        url.Host.Equals("api.openai.com", StringComparison.OrdinalIgnoreCase) ? "OpenAI"
        : url.Host.EndsWith("groq.com", StringComparison.OrdinalIgnoreCase) ? "Groq"
        : url.Authority;

    static string VoiceOf(OpenAiSpeechSettings s) => Or(s.CustomVoice, Or(s.Voice, DefaultVoice));

    static string EngineNameFor(Server? server) => server?.Service switch
    {
        null or "OpenAI" => "OpenAI",
        "Groq" => "Groq (OpenAI-compatible)",
        _ => $"OpenAI-compatible ({server.BaseUrl.Authority})",
    };

    static void Authorize(HttpRequestMessage request, Server server)
    {
        if (server.ApiKey is { } key) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
    }
}
