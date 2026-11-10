using System.Net;
using Pucksley.Sdk;
using static Pucksley.Plugins.Speech.Tests.TestPlugins;

namespace Pucksley.Plugins.Speech.Tests;

public class AzureSpeechTests
{
    static readonly AudioClip Clip = new(Audio.Signal(16000), 16000);
    static AzureSpeechSettings Configured => new() { Key = "az-key", Region = "Australia East" };

    [Fact]
    public async Task Recognition_request_has_the_documented_shape()
    {
        var handler = new RecordingHandler(_ => Responses.Json("""{"RecognitionStatus":"Success","DisplayText":"Turn on the heat pump.","Offset":1800000,"Duration":15000000}"""));
        var plugin = await StartAsync<AzureSpeechPlugin>(Configured, handler);

        var text = await plugin.TranscribeAsync(Clip, "en-AU", Ct);

        Assert.Equal("Turn on the heat pump.", text);
        var r = handler.Single;
        Assert.Equal(HttpMethod.Post, r.Method);
        Assert.Equal(
            "https://australiaeast.stt.speech.microsoft.com/speech/recognition/conversation/cognitiveservices/v1?language=en-NZ&format=simple",
            r.Url.AbsoluteUri);  // the plugin's own language setting wins over the hub's
        Assert.Equal("az-key", r.Header("Ocp-Apim-Subscription-Key"));
        Assert.Equal("audio/wav; codecs=audio/pcm; samplerate=16000", r.Header("Content-Type"));
        Assert.Equal("application/json", r.Header("Accept"));
        Assert.Equal(Clip.ToWav(), r.Body);
    }

    [Theory]
    [InlineData("NoMatch")]
    [InlineData("InitialSilenceTimeout")]
    [InlineData("BabbleTimeout")]
    public async Task Nothing_recognised_is_an_empty_transcript(string status)
    {
        var handler = new RecordingHandler(_ => Responses.Json($$"""{"RecognitionStatus":"{{status}}","Offset":0,"Duration":0}"""));
        var plugin = await StartAsync<AzureSpeechPlugin>(Configured, handler);

        Assert.Equal("", await plugin.TranscribeAsync(Clip, "en-NZ", Ct));
    }

    [Fact]
    public async Task A_recognition_error_is_a_plugin_error()
    {
        var handler = new RecordingHandler(_ => Responses.Json("""{"RecognitionStatus":"Error"}"""));
        var plugin = await StartAsync<AzureSpeechPlugin>(Configured, handler);

        await Assert.ThrowsAsync<PluginException>(() => plugin.TranscribeAsync(Clip, "en-NZ", Ct));
    }

    [Fact]
    public async Task Other_sample_rates_are_sent_as_16khz()
    {
        var handler = new RecordingHandler(_ => Responses.Json("""{"RecognitionStatus":"Success","DisplayText":"Hi."}"""));
        var plugin = await StartAsync<AzureSpeechPlugin>(Configured, handler);

        await plugin.TranscribeAsync(new AudioClip(Audio.Signal(48000), 48000), "en-NZ", Ct);

        var sent = Wav.Decode(handler.Single.Body);
        Assert.Equal(16000, sent.SampleRate);
        Assert.Equal(16000, sent.Samples.Length);
    }

    [Theory]
    [InlineData("", "en-AU", "en-AU")]
    [InlineData("", "en_GB", "en-GB")]
    [InlineData("", "en", "en-NZ")]
    [InlineData("", null, "en-NZ")]
    [InlineData("en-GB", "en-AU", "en-GB")]
    public void Recognition_language_prefers_the_setting_then_the_hub(string setting, string? hub, string expected) =>
        Assert.Equal(expected, AzureSpeechPlugin.RecognitionLanguage(new AzureSpeechSettings { RecognitionLanguage = setting }, hub));

    [Fact]
    public async Task Synthesis_request_has_the_documented_shape_and_streams_16khz_pcm()
    {
        var samples = Audio.Signal(16000 + 5);
        var handler = new RecordingHandler(_ => Responses.Stream(new TrickleStream(Audio.ToBytes(samples), [1, 3, 4097])));
        var settings = Configured;
        settings.Rate = "+5%";
        var plugin = await StartAsync<AzureSpeechPlugin>(settings, handler);

        var chunks = await Audio.CollectAsync(plugin.SynthesizeAsync("Fish & chips <3 for **tea**", Ct));

        Assert.Equal(samples, Audio.Join(chunks));
        Assert.All(chunks, c => Assert.Equal(16000, c.SampleRate));
        var r = handler.Single;
        Assert.Equal(HttpMethod.Post, r.Method);
        Assert.Equal("https://australiaeast.tts.speech.microsoft.com/cognitiveservices/v1", r.Url.AbsoluteUri);
        Assert.Equal("az-key", r.Header("Ocp-Apim-Subscription-Key"));
        Assert.Equal("application/ssml+xml", r.Header("Content-Type"));
        Assert.Equal("raw-16khz-16bit-mono-pcm", r.Header("X-Microsoft-OutputFormat"));
        Assert.Equal("Pucksley", r.Header("User-Agent"));
        Assert.Equal(
            "<speak version='1.0' xml:lang='en-AU'><voice name='en-AU-AnnetteNeural'><prosody rate='+5%'>Fish &amp; chips &lt;3 for tea</prosody></voice></speak>",
            r.Text);
    }

    [Fact]
    public void Ssml_without_a_rate_has_no_prosody_and_takes_the_voice_locale()
    {
        Assert.Equal(
            "<speak version='1.0' xml:lang='en-AU'><voice name='en-AU-NatashaNeural'>G&apos;day</voice></speak>",
            AzureSpeechPlugin.BuildSsml("G'day", "en-AU-NatashaNeural", "  "));
        Assert.Equal(
            "<speak version='1.0' xml:lang='en-GB'><voice name='MyCustomVoice'>Hi</voice></speak>",
            AzureSpeechPlugin.BuildSsml("Hi", "MyCustomVoice", null, "en-GB"));
    }

    [Theory]
    [InlineData("en-NZ-MitchellNeural", "en-NZ")]
    [InlineData("zh-CN-henan-YundengNeural", "zh-CN")]
    [InlineData("fil-PH-BlessicaNeural", "fil-PH")]
    [InlineData("MyCustomVoice", null)]
    public void Voice_locale_comes_from_the_voice_name(string voice, string? locale) => Assert.Equal(locale, AzureSpeechPlugin.VoiceLocale(voice));

    [Theory]
    [InlineData("australiaeast", "australiaeast")]
    [InlineData("Australia East", "australiaeast")]
    [InlineData(" WestUS2 ", "westus2")]
    [InlineData("https://australiaeast.api.cognitive.microsoft.com/", "australiaeast")]
    [InlineData("evil.com/x?", null)]
    [InlineData("", null)]
    public void Region_is_normalised_safely(string text, string? region) => Assert.Equal(region, AzureSpeechPlugin.NormalizeRegion(text));

    [Fact]
    public async Task Readiness_and_status_say_what_is_missing()
    {
        var unset = await StartAsync<AzureSpeechPlugin>(new AzureSpeechSettings());
        Assert.True(unset.EnabledByDefault);
        Assert.False(unset.IsReady);
        Assert.Equal(new PluginStatus(StatusLevel.Idle, "Add a key and region"), unset.Status);

        var noRegion = await StartAsync<AzureSpeechPlugin>(new AzureSpeechSettings { Key = "k" });
        Assert.Equal(new PluginStatus(StatusLevel.Idle, "Add the region (e.g. australiaeast)"), noRegion.Status);

        var badRegion = await StartAsync<AzureSpeechPlugin>(new AzureSpeechSettings { Key = "k", Region = "aus/east" });
        Assert.False(badRegion.IsReady);
        Assert.Equal(StatusLevel.Error, badRegion.Status.Level);

        var ready = await StartAsync<AzureSpeechPlugin>(Configured);
        Assert.True(ready.IsReady);
        Assert.Equal(new PluginStatus(StatusLevel.Ok, "Ready: en-AU-AnnetteNeural, recognising en-NZ"), ready.Status);
    }

    [Fact]
    public async Task A_rejected_key_is_reported_clearly()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        var plugin = await StartAsync<AzureSpeechPlugin>(Configured, handler);

        var e = await Assert.ThrowsAsync<PluginException>(() => Audio.CollectAsync(plugin.SynthesizeAsync("Hello.", Ct)));

        Assert.Equal("Azure Speech: API key rejected. Check the key, and that the region matches the Speech resource.", e.Message);
        Assert.Equal(StatusLevel.Error, plugin.Status.Level);
    }
}
