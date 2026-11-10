using System.Net;
using System.Text.Json.Nodes;
using Pucksley.Sdk;
using static Pucksley.Plugins.Speech.Tests.TestPlugins;

namespace Pucksley.Plugins.Speech.Tests;

public class ElevenLabsTests
{
    static readonly AudioClip Clip = new(Audio.Signal(16000), 16000);

    [Fact]
    public async Task Speech_request_has_the_documented_shape_and_streams_16khz_pcm()
    {
        var samples = Audio.Signal(16000 * 2 + 1);
        var handler = new RecordingHandler(_ => Responses.Stream(new TrickleStream(Audio.ToBytes(samples), [1, 3, 4097])));
        var plugin = await StartAsync<ElevenLabsPlugin>(new ElevenLabsSettings { ApiKey = "xi-test" }, handler);

        var chunks = await Audio.CollectAsync(plugin.SynthesizeAsync("Kia ora, *Sam*.", Ct));

        Assert.Equal(samples, Audio.Join(chunks));
        Assert.All(chunks, c => Assert.Equal(16000, c.SampleRate));
        var r = handler.Single;
        Assert.Equal(HttpMethod.Post, r.Method);
        Assert.Equal("https://api.elevenlabs.io/v1/text-to-speech/21m00Tcm4TlvDq8ikWAM/stream?output_format=pcm_16000", r.Url.AbsoluteUri);
        Assert.Equal("xi-test", r.Header("xi-api-key"));
        Assert.False(r.Headers.ContainsKey("Authorization"));
        Assert.Equal("application/json; charset=utf-8", r.Header("Content-Type"));
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""{"text":"Kia ora, Sam.","model_id":"eleven_flash_v2_5"}"""), r.Json), r.Text);
    }

    [Fact]
    public async Task Voice_settings_are_sent_only_when_set()
    {
        var handler = new RecordingHandler(_ => Responses.Stream(new MemoryStream(new byte[320])));
        var settings = new ElevenLabsSettings { ApiKey = "xi-test", VoiceId = "pNInz6obpgDQGcFmaJgB", Model = "eleven_turbo_v2_5", Stability = "0.4", Similarity = "0,8" };
        var plugin = await StartAsync<ElevenLabsPlugin>(settings, handler);

        await Audio.CollectAsync(plugin.SynthesizeAsync("Hello.", Ct));

        var r = handler.Single;
        Assert.Equal("https://api.elevenlabs.io/v1/text-to-speech/pNInz6obpgDQGcFmaJgB/stream?output_format=pcm_16000", r.Url.AbsoluteUri);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""
            {"text":"Hello.","model_id":"eleven_turbo_v2_5","voice_settings":{"stability":0.4,"similarity_boost":0.8}}
            """), r.Json), r.Text);
    }

    [Theory]
    [InlineData("0.5", 0.5)]
    [InlineData(" 0,25 ", 0.25)]
    [InlineData("1.7", 1.0)]
    [InlineData("-1", 0.0)]
    [InlineData("", null)]
    [InlineData("abc", null)]
    [InlineData("NaN", null)]
    public void Fractions_are_parsed_leniently(string text, double? expected) => Assert.Equal(expected, ElevenLabsPlugin.Fraction(text));

    [Fact]
    public async Task Transcription_request_has_the_documented_shape()
    {
        var handler = new RecordingHandler(_ => Responses.Json("""{"language_code":"eng","language_probability":0.98,"text":"Turn the lounge lights down.","words":[]}"""));
        var plugin = await StartAsync<ElevenLabsPlugin>(new ElevenLabsSettings { ApiKey = "xi-test" }, handler);

        var text = await plugin.TranscribeAsync(Clip, "en-NZ", Ct);

        Assert.Equal("Turn the lounge lights down.", text);
        var r = handler.Single;
        Assert.Equal(HttpMethod.Post, r.Method);
        Assert.Equal("https://api.elevenlabs.io/v1/speech-to-text", r.Url.AbsoluteUri);
        Assert.Equal("xi-test", r.Header("xi-api-key"));
        var parts = r.Parts;
        var file = Assert.Single(parts, p => p.Name == "file");
        Assert.Equal("speech.wav", file.FileName);
        Assert.Equal("audio/wav", file.ContentType);
        Assert.Equal(Clip.ToWav(), file.Data);
        Assert.Equal("scribe_v1", parts.Field("model_id"));
        Assert.Equal("en", parts.Field("language_code"));
        Assert.Equal("false", parts.Field("tag_audio_events"));
    }

    [Fact]
    public async Task Transcription_passes_three_letter_languages_and_omits_unknown()
    {
        var handler = new RecordingHandler(_ => Responses.Json("""{"text":"Kumusta"}"""));
        var plugin = await StartAsync<ElevenLabsPlugin>(new ElevenLabsSettings { ApiKey = "xi-test" }, handler);

        await plugin.TranscribeAsync(Clip, "fil-PH", Ct);
        await plugin.TranscribeAsync(Clip, null, Ct);

        Assert.Equal("fil", handler.Requests[0].Parts.Field("language_code"));
        Assert.DoesNotContain(handler.Requests[1].Parts, p => p.Name == "language_code");
    }

    [Fact]
    public async Task List_voices_shows_names_and_ids()
    {
        var handler = new RecordingHandler(_ => Responses.Json("""
            {"voices":[
              {"voice_id":"21m00Tcm4TlvDq8ikWAM","name":"Rachel","category":"premade","labels":{"accent":"american","gender":"female"}},
              {"voice_id":"Xb7hH8MSUJpSbSDYk0k2","name":"Alice","category":"premade","labels":{"accent":"british","gender":"female"}},
              {"voice_id":"abc123","name":"Kiwi Dad","category":"cloned","labels":{}}
            ]}
            """));
        var plugin = await StartAsync<ElevenLabsPlugin>(new ElevenLabsSettings { ApiKey = "xi-test" }, handler);

        var action = Assert.Single(plugin.GetActions());
        var result = await plugin.RunActionAsync(action.Id, new Dictionary<string, string>(), Ct);

        Assert.True(result.Ok, result.Message);
        Assert.Equal(
            "3 voices (name: ID). Copy an ID into Voice ID.\n" +
            "Alice (british, female): Xb7hH8MSUJpSbSDYk0k2\n" +
            "Kiwi Dad: abc123\n" +
            "Rachel (american, female): 21m00Tcm4TlvDq8ikWAM  ← current",
            result.Message);
        var r = handler.Single;
        Assert.Equal(HttpMethod.Get, r.Method);
        Assert.Equal("https://api.elevenlabs.io/v1/voices", r.Url.AbsoluteUri);
        Assert.Equal("xi-test", r.Header("xi-api-key"));
    }

    [Fact]
    public async Task List_voices_without_a_key_says_so()
    {
        var plugin = await StartAsync<ElevenLabsPlugin>(new ElevenLabsSettings());
        var result = await plugin.RunActionAsync("list-voices", new Dictionary<string, string>(), Ct);
        Assert.False(result.Ok);
        Assert.Equal("Add an API key first.", result.Message);
    }

    [Fact]
    public async Task An_empty_account_is_not_reported_as_a_bad_key()
    {
        // ElevenLabs answers 401 for both; the status code alone would say "API key rejected".
        var handler = new RecordingHandler(_ => Responses.Json(
            """{"detail":{"status":"quota_exceeded","message":"This request exceeds your quota of 10000. You have 3 credits remaining, while 28 credits are required for this request."}}""",
            HttpStatusCode.Unauthorized));
        var plugin = await StartAsync<ElevenLabsPlugin>(new ElevenLabsSettings { ApiKey = "xi-test" }, handler);

        var e = await Assert.ThrowsAsync<PluginException>(() => Audio.CollectAsync(plugin.SynthesizeAsync("Hello.", Ct)));

        Assert.StartsWith("ElevenLabs: out of credit or over quota (This request exceeds your quota of 10000", e.Message);
        Assert.Equal(ServiceErrorKind.Quota, e.Kind());
        Assert.Equal(StatusLevel.Error, plugin.Status.Level);
    }

    [Fact]
    public async Task Readiness_and_status()
    {
        var unset = await StartAsync<ElevenLabsPlugin>(new ElevenLabsSettings());
        Assert.True(unset.EnabledByDefault);
        Assert.False(unset.IsReady);
        Assert.Equal(new PluginStatus(StatusLevel.Idle, "Add an API key"), unset.Status);
        var e = await Assert.ThrowsAsync<PluginException>(() => unset.TranscribeAsync(Clip, "en-NZ", Ct));
        Assert.Contains("add an API key", e.Message);

        var ready = await StartAsync<ElevenLabsPlugin>(new ElevenLabsSettings { ApiKey = "xi-test" });
        Assert.True(ready.IsReady);
        Assert.Equal(new PluginStatus(StatusLevel.Ok, "Ready: voice 21m00Tcm4TlvDq8ikWAM, eleven_flash_v2_5"), ready.Status);
    }
}
