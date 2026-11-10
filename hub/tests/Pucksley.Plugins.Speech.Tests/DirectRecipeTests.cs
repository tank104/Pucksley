using System.Text.Json.Nodes;
using Pucksley.Sdk;
using static Pucksley.Plugins.Speech.Tests.TestPlugins;

namespace Pucksley.Plugins.Speech.Tests;

/// <summary>
/// Direct mode: the requests the speech plugins hand a puck, so it can transcribe and speak on its
/// own when the hub is away. They must be the same requests the plugins make themselves.
/// </summary>
public class DirectRecipeTests
{
    // what the puck does with a body template: %TEXT% becomes the sentence as a JSON string
    static JsonObject Fill(DirectVoice v, string text) =>
        JsonNode.Parse(v.Body.Replace(DirectVoice.TextMark, System.Text.Json.JsonSerializer.Serialize(text), StringComparison.Ordinal))!.AsObject();

    [Fact]
    public async Task OpenAI_transcription_and_voice()
    {
        var plugin = await StartAsync<OpenAiSpeechPlugin>(new OpenAiSpeechSettings { ApiKey = "sk-oa", Voice = "nova", TtsInstructions = "Warm, with a light Kiwi accent." });
        var stt = plugin.GetDirectTranscription("en-NZ")!;
        Assert.Equal("https://api.openai.com/v1/audio/transcriptions", stt.Url);
        Assert.True(stt.Multipart);
        Assert.Contains("Authorization: Bearer sk-oa", stt.Headers);
        Assert.Contains(new KeyValuePair<string, string>("model", OpenAiSpeechPlugin.DefaultSttModel), stt.Fields);
        Assert.Contains(new KeyValuePair<string, string>("language", "en"), stt.Fields);
        Assert.Equal("text", stt.TextField);

        var voice = plugin.GetDirectVoice()!;
        Assert.Equal("https://api.openai.com/v1/audio/speech", voice.Url);
        Assert.Equal(24000, voice.SampleRate);
        Assert.False(voice.XmlBody);
        Assert.Contains("Content-Type: application/json", voice.Headers);
        var body = Fill(voice, "It's \"18\" degrees.");
        Assert.Equal("It's \"18\" degrees.", body["input"]!.GetValue<string>());
        Assert.Equal("nova", body["voice"]!.GetValue<string>());
        Assert.Equal("pcm", body["response_format"]!.GetValue<string>());
        Assert.Equal("Warm, with a light Kiwi accent.", body["instructions"]!.GetValue<string>());
    }

    [Fact]
    public async Task OpenAI_keeps_each_key_with_its_own_server_and_never_offers_a_local_one()
    {
        // Groq for transcription with its own key; OpenAI for the voice
        var split = await StartAsync<OpenAiSpeechPlugin>(new OpenAiSpeechSettings
        {
            ApiKey = "sk-oa", SttBaseUrl = "https://api.groq.com/openai/v1", SttApiKey = "gsk-groq", SttModel = "whisper-large-v3-turbo",
        });
        var stt = split.GetDirectTranscription(null)!;
        Assert.Equal("https://api.groq.com/openai/v1/audio/transcriptions", stt.Url);
        Assert.Contains("Authorization: Bearer gsk-groq", stt.Headers);
        Assert.DoesNotContain(stt.Fields, f => f.Key == "language");   // automatic
        Assert.Contains("Authorization: Bearer sk-oa", split.GetDirectVoice()!.Headers);

        // a speech server only the hub can reach (Speaches on the LAN, no key): not for the puck
        var local = await StartAsync<OpenAiSpeechPlugin>(new OpenAiSpeechSettings { BaseUrl = "http://192.168.1.20:8000/v1" });
        Assert.Null(local.GetDirectTranscription("en-NZ"));
        Assert.Null(local.GetDirectVoice());
        // no key at all
        var none = await StartAsync<OpenAiSpeechPlugin>(new OpenAiSpeechSettings());
        Assert.Null(none.GetDirectTranscription("en-NZ"));
    }

    [Fact]
    public async Task ElevenLabs_transcription_and_voice()
    {
        var plugin = await StartAsync<ElevenLabsPlugin>(new ElevenLabsSettings { ApiKey = "xi-test", VoiceId = "abc123", Stability = "0.4" });
        var stt = plugin.GetDirectTranscription("en-NZ")!;
        Assert.Equal("https://api.elevenlabs.io/v1/speech-to-text", stt.Url);
        Assert.Contains("xi-api-key: xi-test", stt.Headers);
        Assert.Contains(new KeyValuePair<string, string>("language_code", "en"), stt.Fields);
        Assert.Contains(new KeyValuePair<string, string>("tag_audio_events", "false"), stt.Fields);

        var voice = plugin.GetDirectVoice()!;
        Assert.Equal("https://api.elevenlabs.io/v1/text-to-speech/abc123/stream?output_format=pcm_16000", voice.Url);
        Assert.Equal(16000, voice.SampleRate);
        var body = Fill(voice, "Kia ora!");
        Assert.Equal("Kia ora!", body["text"]!.GetValue<string>());
        Assert.Equal(0.4, body["voice_settings"]!["stability"]!.GetValue<double>(), 3);

        var unset = await StartAsync<ElevenLabsPlugin>(new ElevenLabsSettings());
        Assert.Null(unset.GetDirectTranscription(null));
        Assert.Null(unset.GetDirectVoice());
    }

    [Fact]
    public async Task Deepgram_sends_the_wav_as_the_body_and_points_at_the_nested_transcript()
    {
        var plugin = await StartAsync<DeepgramPlugin>(new DeepgramSettings { ApiKey = "dg-key", KeyTerms = ["Tāmaki"] });
        var stt = plugin.GetDirectTranscription("en-NZ")!;
        Assert.False(stt.Multipart);
        Assert.Equal("https://api.deepgram.com/v1/listen?model=nova-3&smart_format=true&language=en-NZ&mip_opt_out=true&keyterm=T%C4%81maki", stt.Url);
        Assert.Contains("Authorization: Token dg-key", stt.Headers);
        Assert.Contains("Content-Type: audio/wav", stt.Headers);
        Assert.Empty(stt.Fields);
        Assert.Equal("results.channels.0.alternatives.0.transcript", stt.TextField);

        Assert.Null((await StartAsync<DeepgramPlugin>(new DeepgramSettings())).GetDirectTranscription("en-NZ"));
    }

    [Fact]
    public async Task Azure_sends_the_wav_as_the_body_and_ssml_for_the_voice()
    {
        var plugin = await StartAsync<AzureSpeechPlugin>(new AzureSpeechSettings { Key = "az-key", Region = "Australia East", Voice = "en-NZ-MollyNeural", Rate = "+5%" });
        var stt = plugin.GetDirectTranscription("en-NZ")!;
        Assert.False(stt.Multipart);
        Assert.StartsWith("https://australiaeast.stt.speech.microsoft.com/", stt.Url);
        Assert.Contains("language=en-NZ", stt.Url);
        Assert.Contains("Ocp-Apim-Subscription-Key: az-key", stt.Headers);
        Assert.Contains("Content-Type: audio/wav; codecs=audio/pcm; samplerate=16000", stt.Headers);
        Assert.Equal("DisplayText", stt.TextField);

        var voice = plugin.GetDirectVoice()!;
        Assert.True(voice.XmlBody);
        Assert.Equal("https://australiaeast.tts.speech.microsoft.com/cognitiveservices/v1", voice.Url);
        Assert.Contains("X-Microsoft-OutputFormat: raw-16khz-16bit-mono-pcm", voice.Headers);
        Assert.Contains("<voice name='en-NZ-MollyNeural'><prosody rate='+5%'>%TEXT%</prosody></voice>", voice.Body);
        Assert.Equal(16000, voice.SampleRate);

        Assert.Null((await StartAsync<AzureSpeechPlugin>(new AzureSpeechSettings { Key = "k" })).GetDirectVoice());
    }
}
