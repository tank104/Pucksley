using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Security;
using System.Text;
using System.Text.Json.Nodes;
using Pucksley.Sdk;

namespace Pucksley.Plugins.Speech;

public sealed class AzureSpeechSettings
{
    [Setting("Key", Secret = true, Order = 1, Help = "Key 1 or 2 of an Azure AI Speech resource (Azure portal → the resource → Keys and Endpoint).")]
    public string Key { get; set; } = "";

    [Setting("Region", Order = 2, Placeholder = "australiaeast", Help = "The resource's region, shown next to its keys.")]
    public string Region { get; set; } = "";

    [Setting("Recognition language", Order = 3, Placeholder = AzureSpeechPlugin.DefaultLanguage,
        Help = "e.g. en-NZ, en-AU or en-GB. Empty uses the hub's language.")]
    public string RecognitionLanguage { get; set; } = AzureSpeechPlugin.DefaultLanguage;

    [Setting("Voice", Order = 4, Placeholder = AzureSpeechPlugin.DefaultVoice,
        Help = "e.g. en-AU-AnnetteNeural, en-NZ-MollyNeural, en-NZ-MitchellNeural, en-AU-NatashaNeural, en-GB-SoniaNeural, en-US-AvaMultilingualNeural, or any voice in Azure's voice gallery.")]
    public string Voice { get; set; } = AzureSpeechPlugin.DefaultVoice;

    [Setting("Speaking rate", Order = 5, Placeholder = "+5%", Help = "Faster or slower than normal, e.g. +5% or -10%. Empty for normal.")]
    public string Rate { get; set; } = "";
}

/// <summary>Azure AI Speech: neural voices (New Zealand English included) and short-utterance recognition, over REST.</summary>
public sealed class AzureSpeechPlugin : SpeechPlugin<AzureSpeechSettings>, ISpeechToText, ITextToSpeech, IDirectSpeechToText, IDirectTextToSpeech
{
    public const string DefaultLanguage = "en-NZ";
    public const string DefaultVoice = "en-AU-AnnetteNeural";
    internal const int PcmSampleRate = 16000;
    const string Service = "Azure Speech";

    // Keys only work in their resource's region, and a mismatch looks exactly like a bad key.
    const string KeyHint = "Check the key, and that the region matches the Speech resource.";

    public override string Id => "azure-speech";
    public override string Name => "Azure AI Speech";
    public override string Description => "Microsoft's neural voices and speech recognition, including Australian and New Zealand English (Annette, Molly, Mitchell).";

    public string EngineId => "azure";
    public string EngineName => "Azure AI Speech";
    public bool IsReady => NullIfBlank(Settings.Key) is not null && NormalizeRegion(Settings.Region) is not null;

    public async Task<string> TranscribeAsync(AudioClip audio, string? language, CancellationToken cancel)
    {
        var s = Settings;
        var (key, region) = CredentialsOrThrow(s);
        if (AudioConvert.TooShortToTranscribe(audio)) return "";

        // The short-audio REST API wants 16 kHz; the puck already sends that, so this is normally a no-op.
        var content = new ByteArrayContent(Wav.Encode(AudioConvert.Resample(audio.Samples, audio.SampleRate, 16000), 16000));
        // Sent verbatim: "codecs=audio/pcm" isn't a valid HTTP token, so the typed ContentType property would refuse it.
        content.Headers.TryAddWithoutValidation("Content-Type", "audio/wav; codecs=audio/pcm; samplerate=16000");
        using var request = new HttpRequestMessage(HttpMethod.Post, RecognitionUrl(region, RecognitionLanguage(s, language))) { Content = content };
        request.Headers.TryAddWithoutValidation("Ocp-Apim-Subscription-Key", key);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await SendAsync(request, Service, HttpCompletionOption.ResponseContentRead, cancel, KeyHint).ConfigureAwait(false);
        return ReadRecognition(await SpeechHttp.ReadJsonAsync(response, Service, cancel).ConfigureAwait(false));
    }

    public async IAsyncEnumerable<AudioChunk> SynthesizeAsync(string text, [EnumeratorCancellation] CancellationToken cancel)
    {
        var s = Settings;
        var speech = SpeechText.Clean(text);
        if (speech.Length == 0) yield break;
        var (key, region) = CredentialsOrThrow(s);

        var content = new StringContent(BuildSsml(speech, Or(s.Voice, DefaultVoice), s.Rate, s.RecognitionLanguage), Encoding.UTF8);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/ssml+xml");
        using var request = new HttpRequestMessage(HttpMethod.Post, SynthesisUrl(region)) { Content = content };
        request.Headers.TryAddWithoutValidation("Ocp-Apim-Subscription-Key", key);
        request.Headers.TryAddWithoutValidation("X-Microsoft-OutputFormat", "raw-16khz-16bit-mono-pcm");
        request.Headers.TryAddWithoutValidation("User-Agent", "Pucksley");

        using var response = await SendAsync(request, Service, HttpCompletionOption.ResponseHeadersRead, cancel, KeyHint).ConfigureAwait(false);
        await using var pcm = await response.Content.ReadAsStreamAsync(cancel).ConfigureAwait(false);
        await foreach (var chunk in PcmStream.ReadChunksAsync(pcm, PcmSampleRate, Service, cancel).ConfigureAwait(false))
            yield return chunk;
    }

    // ---- direct mode: the same requests, made by the puck itself when the hub is away

    public DirectTranscription? GetDirectTranscription(string? language)
    {
        var s = Settings;
        if (NullIfBlank(s.Key) is not { } key || NormalizeRegion(s.Region) is not { } region) return null;
        return new DirectTranscription(Service, RecognitionUrl(region, RecognitionLanguage(s, language)).ToString(),
            [$"Ocp-Apim-Subscription-Key: {key}", "Content-Type: audio/wav; codecs=audio/pcm; samplerate=16000", "Accept: application/json"],
            Multipart: false, [], TextField: "DisplayText");
    }

    public DirectVoice? GetDirectVoice()
    {
        var s = Settings;
        if (NullIfBlank(s.Key) is not { } key || NormalizeRegion(s.Region) is not { } region) return null;
        var body = BuildSsml(DirectVoice.TextMark, Or(s.Voice, DefaultVoice), s.Rate, s.RecognitionLanguage);
        return new DirectVoice(Service, SynthesisUrl(region).ToString(),
            [$"Ocp-Apim-Subscription-Key: {key}", "Content-Type: application/ssml+xml", "X-Microsoft-OutputFormat: raw-16khz-16bit-mono-pcm"],
            body, XmlBody: true, PcmSampleRate);
    }

    protected override PluginStatus DescribeSetup()
    {
        var s = Settings;
        bool hasKey = NullIfBlank(s.Key) is not null, hasRegion = NullIfBlank(s.Region) is not null;
        if (!hasKey) return new(StatusLevel.Idle, hasRegion ? "Add a key" : "Add a key and region");
        if (!hasRegion) return new(StatusLevel.Idle, "Add the region (e.g. australiaeast)");
        if (NormalizeRegion(s.Region) is null) return new(StatusLevel.Error, $"'{s.Region.Trim()}' isn't an Azure region name (e.g. australiaeast)");
        return new(StatusLevel.Ok, $"Ready: {Or(s.Voice, DefaultVoice)}, recognising {Or(s.RecognitionLanguage, "the hub's language")}");
    }

    /// <summary>
    /// "australiaeast" from "australiaeast", "Australia East" (as the portal shows it) or a pasted endpoint URL;
    /// null when it can't be a region (it goes into a host name, so only letters and digits pass).
    /// </summary>
    internal static string? NormalizeRegion(string? text)
    {
        var t = text?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(t)) return null;
        if (t.Contains("://", StringComparison.Ordinal) && Uri.TryCreate(t, UriKind.Absolute, out var url)) t = url.Host.Split('.')[0];
        t = t.Replace(" ", "");
        if (t.Length == 0) return null;
        foreach (char c in t)
            if (!char.IsAsciiLetterLower(c) && !char.IsAsciiDigit(c)) return null;
        return t;
    }

    /// <summary>The plugin's own setting if set, else the hub's language when it names a region ("en-NZ"), else en-NZ.</summary>
    internal static string RecognitionLanguage(AzureSpeechSettings s, string? hubLanguage)
    {
        if (NullIfBlank(s.RecognitionLanguage) is { } own) return own;
        var hub = hubLanguage?.Trim().Replace('_', '-');
        return hub is not null && hub.Contains('-') ? hub : DefaultLanguage;
    }

    internal static Uri RecognitionUrl(string region, string language) =>
        new($"https://{region}.stt.speech.microsoft.com/speech/recognition/conversation/cognitiveservices/v1?language={Uri.EscapeDataString(language)}&format=simple");

    internal static Uri SynthesisUrl(string region) => new($"https://{region}.tts.speech.microsoft.com/cognitiveservices/v1");

    /// <summary>The text of a "simple" recognition result; "" when nothing intelligible was said.</summary>
    internal static string ReadRecognition(JsonNode json) => SpeechHttp.GetString(json, "RecognitionStatus") switch
    {
        "Success" => SpeechHttp.GetString(json, "DisplayText")?.Trim() ?? "",
        "NoMatch" or "InitialSilenceTimeout" or "BabbleTimeout" => "",
        "Error" => throw SpeechHttp.Fail(ServiceErrorKind.Server, null, null, "Azure Speech couldn't recognise the audio (it reported an error). Try again."),
        null => throw SpeechHttp.Fail(ServiceErrorKind.BadResponse, null, null, "Azure Speech sent an answer without a recognition status."),
        _ => SpeechHttp.GetString(json, "DisplayText")?.Trim() ?? "",
    };

    /// <summary>SSML for one utterance. Everything that goes into it is XML-escaped.</summary>
    internal static string BuildSsml(string text, string voice, string? rate, string? fallbackLanguage = null)
    {
        var lang = VoiceLocale(voice) ?? NullIfBlank(fallbackLanguage) ?? "en-US";
        var body = SecurityElement.Escape(text);
        if (NullIfBlank(rate) is { } r) body = $"<prosody rate='{SecurityElement.Escape(r)}'>{body}</prosody>";
        return $"<speak version='1.0' xml:lang='{SecurityElement.Escape(lang)}'><voice name='{SecurityElement.Escape(voice)}'>{body}</voice></speak>";
    }

    /// <summary>"en-NZ-MollyNeural" → "en-NZ"; null when the name doesn't start with a locale.</summary>
    internal static string? VoiceLocale(string voice)
    {
        var parts = voice.Split('-');
        return parts.Length >= 3 && parts[0].Length is 2 or 3 && parts[1].Length is >= 2 and <= 4 ? $"{parts[0]}-{parts[1]}" : null;
    }

    static (string Key, string Region) CredentialsOrThrow(AzureSpeechSettings s) =>
        NullIfBlank(s.Key) is { } key && NormalizeRegion(s.Region) is { } region
            ? (key, region)
            : throw new PluginException("Azure Speech isn't set up: add a key and region on the hub's Plugins page.");
}
