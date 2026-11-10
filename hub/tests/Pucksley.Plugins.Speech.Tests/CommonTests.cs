using System.Net;
using Pucksley.Sdk;
using static Pucksley.Plugins.Speech.Tests.TestPlugins;

namespace Pucksley.Plugins.Speech.Tests;

public class LanguageTests
{
    [Theory]
    [InlineData("en-NZ", "en")]
    [InlineData("en", "en")]
    [InlineData("EN-nz", "en")]
    [InlineData(" mi-NZ ", "mi")]
    [InlineData("en_NZ", "en")]
    [InlineData("zh-Hant-TW", "zh")]
    [InlineData("es-419", "es")]
    [InlineData("fil-PH", null)]
    [InlineData("yue", null)]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("auto", null)]
    [InlineData("und", null)]
    [InlineData("x-klingon", null)]
    [InlineData("i-default", null)]
    public void Bcp47_to_iso639_1(string? tag, string? expected) => Assert.Equal(expected, Languages.Iso6391(tag));

    [Theory]
    [InlineData("fil-PH", "fil")]
    [InlineData("yue-HK", "yue")]
    [InlineData("en-NZ", "en")]
    [InlineData("e1-NZ", null)]
    [InlineData("toolong-NZ", null)]
    public void Primary_subtag_keeps_three_letter_codes(string tag, string? expected) => Assert.Equal(expected, Languages.Primary(tag));
}

public class ServerUrlTests
{
    [Theory]
    [InlineData("https://api.openai.com/v1", "https://api.openai.com/v1")]
    [InlineData("https://api.openai.com/v1/", "https://api.openai.com/v1")]
    [InlineData("  api.groq.com/openai/v1 ", "https://api.groq.com/openai/v1")]
    [InlineData("localhost:8880/v1", "http://localhost:8880/v1")]
    [InlineData("192.168.1.20:8000/v1", "http://192.168.1.20:8000/v1")]
    [InlineData("http://speaches:8000/v1", "http://speaches:8000/v1")]
    [InlineData("ftp://example.com", null)]
    [InlineData("", null)]
    [InlineData("not a url", null)]
    public void Parses_base_urls(string text, string? expected) => Assert.Equal(expected, ServerUrl.ParseBase(text)?.AbsoluteUri);

    [Theory]
    [InlineData("https://api.groq.com/openai/v1", "audio/transcriptions", "https://api.groq.com/openai/v1/audio/transcriptions")]
    [InlineData("http://localhost:8880/v1/", "/audio/speech", "http://localhost:8880/v1/audio/speech")]
    [InlineData("https://example.com", "v1/audio/speech", "https://example.com/v1/audio/speech")]
    public void Combines_paths(string baseUrl, string path, string expected) => Assert.Equal(expected, ServerUrl.Combine(new Uri(baseUrl), path).AbsoluteUri);

    [Theory]
    [InlineData("http://localhost:8880/v1", true)]
    [InlineData("http://127.0.0.1:8000", true)]
    [InlineData("http://192.168.1.20:8000", true)]
    [InlineData("http://10.0.0.5", true)]
    [InlineData("http://172.20.1.1", true)]
    [InlineData("http://172.32.0.1", false)]
    [InlineData("http://100.101.102.103", true)]
    [InlineData("http://speaches:8000/v1", true)]
    [InlineData("http://kokoro.local:8880", true)]
    [InlineData("http://tts.home.arpa", true)]
    [InlineData("http://[::1]:8000", true)]
    [InlineData("http://[fd00::5]", true)]
    [InlineData("https://api.openai.com/v1", false)]
    [InlineData("https://api.groq.com/openai/v1", false)]
    [InlineData("http://8.8.8.8", false)]
    public void Knows_local_servers(string url, bool local) => Assert.Equal(local, ServerUrl.IsLocal(new Uri(url)));
}

public class AudioConvertTests
{
    [Fact]
    public void Same_rate_is_untouched()
    {
        var samples = Audio.Signal(100);
        Assert.Same(samples, AudioConvert.Resample(samples, 16000, 16000));
    }

    [Fact]
    public void Upsampling_interpolates() => Assert.Equal([0, 50, 100, 100], AudioConvert.Resample([0, 100], 8000, 16000));

    [Fact]
    public void Downsampling_keeps_duration_and_shape()
    {
        var sine = Enumerable.Range(0, 48000).Select(i => (short)(10000 * Math.Sin(2 * Math.PI * 440 * i / 48000.0))).ToArray();
        var result = AudioConvert.Resample(sine, 48000, 16000);

        Assert.Equal(16000, result.Length);
        for (int i = 0; i < result.Length; i += 97)
            Assert.InRange(result[i], 10000 * Math.Sin(2 * Math.PI * 440 * i / 16000.0) - 40, 10000 * Math.Sin(2 * Math.PI * 440 * i / 16000.0) + 40);
    }

    [Fact]
    public void Floats_are_scaled_and_padded()
    {
        var floats = AudioConvert.ToFloats([16384, -32768, 32767], minLength: 5);
        Assert.Equal([0.5f, -1f, 32767 / 32768f, 0f, 0f], floats);
    }

    [Theory]
    [InlineData(1599, true)]
    [InlineData(1600, false)]
    [InlineData(0, true)]
    public void Under_100ms_is_too_short(int samples, bool tooShort) =>
        Assert.Equal(tooShort, AudioConvert.TooShortToTranscribe(new AudioClip(new short[samples], 16000)));
}

public class SpeechHttpTests
{
    [Theory]
    [InlineData("""{"error":{"message":"Incorrect API key provided.","type":"invalid_request_error","code":"invalid_api_key"}}""", "Incorrect API key provided", "invalid_api_key")]
    [InlineData("""{"error":{"message":"You exceeded your current quota.","type":"insufficient_quota","code":"insufficient_quota"}}""", "You exceeded your current quota", "insufficient_quota")]
    [InlineData("""{"detail":{"status":"quota_exceeded","message":"This request exceeds your quota."}}""", "This request exceeds your quota", "quota_exceeded")]
    [InlineData("""{"detail":"Model 'kokoro-x' not found"}""", "Model 'kokoro-x' not found", null)]
    [InlineData("""{"detail":[{"loc":["body","voice"],"msg":"field required","type":"value_error.missing"}]}""", "field required", "value_error.missing")]
    [InlineData("""{"error":"bad voice"}""", "bad voice", null)]
    [InlineData("""{"message":"Too many requests","code":429}""", "Too many requests", "429")]
    [InlineData("Unsupported voice", "Unsupported voice", null)]
    [InlineData("<html><body>502 Bad Gateway</body></html>", null, null)]
    [InlineData("", null, null)]
    public void Finds_the_message_in_error_bodies(string body, string? message, string? code) =>
        Assert.Equal((message, code), SpeechHttp.ParseError(body));

    [Fact]
    public void Long_error_text_is_shortened()
    {
        var (detail, _) = SpeechHttp.ParseError(new string('x', 1000));
        Assert.Equal(200, detail!.Length);
    }

    [Theory]
    [InlineData(401, null, "Auth", "OpenAI: API key rejected")]
    [InlineData(401, "quota_exceeded", "Quota", "out of credit")]
    [InlineData(402, null, "Quota", "payment required")]
    [InlineData(403, null, "Auth", "access denied")]
    [InlineData(404, null, "NotFound", "not found")]
    [InlineData(400, null, "BadRequest", "rejected the request")]
    [InlineData(422, null, "BadRequest", "rejected the request")]
    [InlineData(413, null, "BadRequest", "too large")]
    [InlineData(429, null, "RateLimited", "rate limited")]
    [InlineData(429, "insufficient_quota", "Quota", "out of credit")]
    [InlineData(500, null, "Server", "having trouble (500)")]
    [InlineData(503, null, "Server", "having trouble (503)")]
    [InlineData(504, null, "Timeout", "took too long")]
    [InlineData(408, null, "Timeout", "took too long")]
    [InlineData(418, null, "Other", "returned an error (418)")]
    public void Maps_http_errors_to_clear_messages(int status, string? code, string kind, string phrase)
    {
        var e = SpeechHttp.ErrorFor("OpenAI", (HttpStatusCode)status, "detail", code);
        Assert.Contains(phrase, e.Message);
        Assert.Equal(kind, e.Kind().ToString());
        Assert.Equal(status, ((ServiceError)e.InnerException!).StatusCode);
    }

    [Fact]
    public async Task An_error_response_becomes_a_plugin_exception_with_the_service_detail()
    {
        var handler = new RecordingHandler(_ => Responses.Json("""{"error":{"message":"Invalid voice 'bob'.","code":null}}""", HttpStatusCode.BadRequest));
        using var http = new HttpClient(handler);
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/audio/speech");

        var e = await Assert.ThrowsAsync<PluginException>(() => SpeechHttp.SendAsync(http, request, "OpenAI", HttpCompletionOption.ResponseHeadersRead, Ct));

        Assert.Equal("OpenAI rejected the request (Invalid voice 'bob').", e.Message);
    }

    [Fact]
    public async Task Unreachable_servers_are_plugin_errors()
    {
        var handler = new RecordingHandler((_, _) => throw new HttpRequestException(HttpRequestError.ConnectionError, "No connection could be made"));
        using var http = new HttpClient(handler);
        using var request = new HttpRequestMessage(HttpMethod.Get, "http://192.168.1.5:8000/v1/audio/speech");

        var e = await Assert.ThrowsAsync<PluginException>(() => SpeechHttp.SendAsync(http, request, "192.168.1.5:8000", HttpCompletionOption.ResponseHeadersRead, Ct));

        Assert.Equal("Couldn't reach 192.168.1.5:8000 (the connection failed).", e.Message);
        Assert.Equal(ServiceErrorKind.Network, e.Kind());
    }

    [Fact]
    public async Task Client_timeouts_are_plugin_errors_but_cancellation_is_not()
    {
        var handler = new RecordingHandler(async (_, cancel) =>
        {
            await Task.Delay(Timeout.Infinite, cancel);
            return Responses.Json("{}");
        });
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(100) };

        using (var request = new HttpRequestMessage(HttpMethod.Get, "https://api.openai.com/v1/models"))
        {
            var e = await Assert.ThrowsAsync<PluginException>(() => SpeechHttp.SendAsync(http, request, "OpenAI", HttpCompletionOption.ResponseHeadersRead, Ct));
            Assert.Equal(ServiceErrorKind.Timeout, e.Kind());
        }

        using var patient = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        using var cts = new CancellationTokenSource(100);
        using var again = new HttpRequestMessage(HttpMethod.Get, "https://api.openai.com/v1/models");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SpeechHttp.SendAsync(patient, again, "OpenAI", HttpCompletionOption.ResponseHeadersRead, cts.Token));
    }
}
