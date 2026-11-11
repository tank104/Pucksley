using System.Net;
using System.Text.Json.Nodes;
using Pucksley.Sdk;
using static Pucksley.Plugins.Speech.Tests.TestPlugins;

namespace Pucksley.Plugins.Speech.Tests;

public class DeepgramTests
{
    static readonly AudioClip Clip = new(Audio.Signal(16000), 16000);

    const string Answer = """
        {"metadata":{"request_id":"r1","duration":1.0,"channels":1,"models":["m"]},
         "results":{"channels":[{"alternatives":[{"transcript":"Turn the lounge lights down to 30%.","confidence":0.99,
           "words":[{"word":"turn","start":0.1,"end":0.3,"confidence":0.99,"punctuated_word":"Turn"}]}]}]}}
        """;

    [Fact]
    public async Task Transcription_request_has_the_documented_shape()
    {
        var handler = new RecordingHandler(_ => Responses.Json(Answer));
        var plugin = await StartAsync<DeepgramPlugin>(new DeepgramSettings { ApiKey = "dg-key" }, handler);

        var text = await plugin.TranscribeAsync(Clip, "en-NZ", Ct);

        Assert.Equal("Turn the lounge lights down to 30%.", text);
        var r = handler.Single;
        Assert.Equal(HttpMethod.Post, r.Method);
        Assert.Equal("https://api.deepgram.com/v1/listen?model=nova-3&smart_format=true&language=en-NZ&mip_opt_out=true", r.Url.AbsoluteUri);
        Assert.Equal("Token dg-key", r.Header("Authorization"));
        Assert.Equal("audio/wav", r.Header("Content-Type"));
        Assert.Equal(Clip.ToWav(), r.Body);
    }

    [Fact]
    public async Task Names_to_listen_for_go_as_key_terms_on_nova_3_only()
    {
        var settings = new DeepgramSettings { ApiKey = "k", Language = "en-AU", KeyTerms = ["Tāmaki", "  the   Den ", "", "tāmaki", "Mitchell"], ImproveModels = true };
        Assert.Equal(
            "https://api.deepgram.com/v1/listen?model=nova-3&smart_format=true&language=en-AU&keyterm=T%C4%81maki&keyterm=the%20Den&keyterm=Mitchell",
            DeepgramPlugin.ListenUrl(settings, "en-NZ").AbsoluteUri);

        settings.Model = "nova-2";
        Assert.DoesNotContain("keyterm", DeepgramPlugin.ListenUrl(settings, null).AbsoluteUri);

        Assert.Equal(50, DeepgramPlugin.CleanKeyTerms(Enumerable.Range(0, 80).Select(i => $"name {i}")).Count);
    }

    [Fact]
    public void Live_recognition_streams_raw_audio_with_interim_words_and_the_pause_that_ends_it()
    {
        var settings = new DeepgramSettings { ApiKey = "k", KeyTerms = ["Mitchell"] };
        var url = DeepgramPlugin.LiveUrl(settings, new SpeechStreamOptions("en-NZ", 800)).AbsoluteUri;
        Assert.StartsWith("wss://api.deepgram.com/v1/listen?model=nova-3&smart_format=true&language=en-NZ&mip_opt_out=true&keyterm=Mitchell", url);
        Assert.EndsWith("&encoding=linear16&sample_rate=16000&channels=1&interim_results=true&vad_events=true&endpointing=800&utterance_end_ms=1100", url);

        // It can't detect the language live: Nova-3 listens for several instead
        var auto = DeepgramPlugin.LiveUrl(settings, new SpeechStreamOptions(null, 100)).AbsoluteUri;
        Assert.Contains("language=multi", auto);
        Assert.DoesNotContain("detect_language", auto);
        Assert.Contains("endpointing=300&utterance_end_ms=1000", auto);   // within what Deepgram accepts
    }

    [Theory]
    [InlineData("en-NZ", "en-NZ")]
    [InlineData("en_nz", "en-NZ")]
    [InlineData("en-GB", "en-GB")]
    [InlineData("en-IE", "en")]
    [InlineData("en", "en")]
    [InlineData("de-DE", "de")]
    [InlineData("auto", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Languages_map_to_what_nova_3_knows(string? tag, string? expected) => Assert.Equal(expected, DeepgramPlugin.LanguageFor(tag));

    [Fact]
    public async Task No_language_means_detect_it()
    {
        var handler = new RecordingHandler(_ => Responses.Json(Answer));
        var plugin = await StartAsync<DeepgramPlugin>(new DeepgramSettings { ApiKey = "k" }, handler);
        await plugin.TranscribeAsync(Clip, null, Ct);
        Assert.Contains("detect_language=true", handler.Single.Url.Query);
        Assert.DoesNotContain("language=en", handler.Single.Url.Query);
    }

    [Fact]
    public async Task Silence_is_an_empty_transcript_and_a_shapeless_answer_is_an_error()
    {
        var silent = new RecordingHandler(_ => Responses.Json("""{"results":{"channels":[{"alternatives":[{"transcript":"","confidence":0}]}]}}"""));
        Assert.Equal("", await (await StartAsync<DeepgramPlugin>(new DeepgramSettings { ApiKey = "k" }, silent)).TranscribeAsync(Clip, "en-NZ", Ct));

        var odd = new RecordingHandler(_ => Responses.Json("""{"results":{"channels":[]}}"""));
        var plugin = await StartAsync<DeepgramPlugin>(new DeepgramSettings { ApiKey = "k" }, odd);
        var e = await Assert.ThrowsAsync<PluginException>(() => plugin.TranscribeAsync(Clip, "en-NZ", Ct));
        Assert.Equal(ServiceErrorKind.BadResponse, e.Kind());
    }

    [Fact]
    public async Task A_rejected_key_says_so_and_shows_in_the_status()
    {
        var handler = new RecordingHandler(_ => Responses.Json(
            """{"err_code":"INVALID_AUTH","err_msg":"Invalid credentials.","request_id":"r2"}""", HttpStatusCode.Unauthorized));
        var plugin = await StartAsync<DeepgramPlugin>(new DeepgramSettings { ApiKey = "bad" }, handler);

        var e = await Assert.ThrowsAsync<PluginException>(() => plugin.TranscribeAsync(Clip, "en-NZ", Ct));
        Assert.Equal(ServiceErrorKind.Auth, e.Kind());
        Assert.StartsWith("Deepgram: API key rejected.", e.Message);
        Assert.Equal(StatusLevel.Error, plugin.Status.Level);

        var bad = new RecordingHandler(_ => Responses.Json(
            """{"err_code":"Bad Request","err_msg":"No such model/language/tier combination found.","request_id":"r3"}""", HttpStatusCode.BadRequest));
        var e2 = await Assert.ThrowsAsync<PluginException>(async () =>
            await (await StartAsync<DeepgramPlugin>(new DeepgramSettings { ApiKey = "k", Model = "nova-9" }, bad)).TranscribeAsync(Clip, "en-NZ", Ct));
        Assert.Contains("No such model/language/tier combination found", e2.Message);
    }

    [Fact]
    public async Task Status_says_what_is_set_up()
    {
        var unset = await StartAsync<DeepgramPlugin>(new DeepgramSettings());
        Assert.False(unset.IsReady);
        Assert.Equal(new PluginStatus(StatusLevel.Idle, "Add an API key"), unset.Status);
        await Assert.ThrowsAsync<PluginException>(() => unset.TranscribeAsync(Clip, "en-NZ", Ct));

        var ready = await StartAsync<DeepgramPlugin>(new DeepgramSettings { ApiKey = "k", KeyTerms = ["Puck"] });
        Assert.True(ready.IsReady);
        Assert.Equal(new PluginStatus(StatusLevel.Ok, "Ready: nova-3, recognising the hub's language, listening for 1 name"), ready.Status);
    }

    [Fact]
    public void The_puck_finds_the_transcript_at_the_path_it_is_given()
    {
        // what the firmware's jsonAt does with TextField
        JsonNode? node = JsonNode.Parse(Answer);
        foreach (var part in DeepgramPlugin.TranscriptPath.Split('.'))
            node = int.TryParse(part, out var i) ? node!.AsArray()[i] : node![part];
        Assert.Equal("Turn the lounge lights down to 30%.", node!.GetValue<string>());
    }
}
