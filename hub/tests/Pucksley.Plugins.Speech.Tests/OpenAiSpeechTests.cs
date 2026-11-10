using System.Diagnostics;
using System.Net;
using System.Text.Json.Nodes;
using Pucksley.Sdk;
using static Pucksley.Plugins.Speech.Tests.TestPlugins;

namespace Pucksley.Plugins.Speech.Tests;

public class OpenAiSpeechTests
{
    static readonly AudioClip Clip = new(Audio.Signal(16000), 16000);

    [Fact]
    public async Task Transcription_request_has_the_documented_shape()
    {
        var handler = new RecordingHandler(_ => Responses.Json("""{"text":" Turn on the kitchen lights. "}"""));
        var plugin = await StartAsync<OpenAiSpeechPlugin>(new OpenAiSpeechSettings { ApiKey = "sk-test" }, handler);

        var text = await ((ISpeechToText)plugin).TranscribeAsync(Clip, "en-NZ", Ct);

        Assert.Equal("Turn on the kitchen lights.", text);
        var r = handler.Single;
        Assert.Equal(HttpMethod.Post, r.Method);
        Assert.Equal("https://api.openai.com/v1/audio/transcriptions", r.Url.AbsoluteUri);
        Assert.Equal("Bearer sk-test", r.Header("Authorization"));
        Assert.StartsWith("multipart/form-data; boundary=", r.Header("Content-Type"));

        var parts = r.Parts;
        Assert.Equal(["file", "model", "language", "response_format"], parts.Select(p => p.Name));
        var file = parts[0];
        Assert.Equal("speech.wav", file.FileName);
        Assert.Equal("audio/wav", file.ContentType);
        Assert.Equal(Clip.ToWav(), file.Data);
        Assert.Contains("name=\"file\"", file.RawDisposition);  // quoted, for strict servers
        Assert.Equal("gpt-4o-mini-transcribe", parts.Field("model"));
        Assert.Equal("en", parts.Field("language"));
        Assert.Equal("json", parts.Field("response_format"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("fil-PH")]  // no two-letter code: let the service detect it
    public async Task Transcription_omits_a_language_it_cant_name(string? language)
    {
        var handler = new RecordingHandler(_ => Responses.Json("""{"text":"Kumusta"}"""));
        var plugin = await StartAsync<OpenAiSpeechPlugin>(new OpenAiSpeechSettings { ApiKey = "sk-test" }, handler);

        await ((ISpeechToText)plugin).TranscribeAsync(Clip, language, Ct);

        Assert.DoesNotContain(handler.Single.Parts, p => p.Name == "language");
    }

    [Fact]
    public async Task Silence_too_short_to_hold_words_makes_no_request()
    {
        var handler = new RecordingHandler(_ => throw new InvalidOperationException("no request expected"));
        var plugin = await StartAsync<OpenAiSpeechPlugin>(new OpenAiSpeechSettings { ApiKey = "sk-test" }, handler);

        Assert.Equal("", await ((ISpeechToText)plugin).TranscribeAsync(new AudioClip(new short[800], 16000), "en-NZ", Ct));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Speech_request_has_the_documented_shape_and_streams_24khz_pcm()
    {
        var samples = Audio.Signal(24000 * 2 + 7);
        var handler = new RecordingHandler(_ => Responses.Stream(new TrickleStream(Audio.ToBytes(samples), [1, 3, 4097])));
        var plugin = await StartAsync<OpenAiSpeechPlugin>(new OpenAiSpeechSettings { ApiKey = "sk-test" }, handler);

        var chunks = await Audio.CollectAsync(((ITextToSpeech)plugin).SynthesizeAsync("**Kia ora!** The heat pump is on.", Ct));

        Assert.Equal(samples, Audio.Join(chunks));
        Assert.All(chunks, c => Assert.Equal(24000, c.SampleRate));
        var r = handler.Single;
        Assert.Equal(HttpMethod.Post, r.Method);
        Assert.Equal("https://api.openai.com/v1/audio/speech", r.Url.AbsoluteUri);
        Assert.Equal("Bearer sk-test", r.Header("Authorization"));
        Assert.Equal("application/json; charset=utf-8", r.Header("Content-Type"));
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""
            {
              "model": "gpt-4o-mini-tts",
              "voice": "coral",
              "input": "Kia ora! The heat pump is on.",
              "response_format": "pcm",
              "instructions": "Speak naturally and warmly, at a brisk conversational pace."
            }
            """), r.Json), r.Text);
    }

    [Fact]
    public async Task Tts1_gets_no_instructions_and_a_changed_speed_and_voice_are_sent()
    {
        var handler = new RecordingHandler(_ => Responses.Stream(new MemoryStream(new byte[4800])));
        var settings = new OpenAiSpeechSettings { ApiKey = "sk-test", TtsModel = "tts-1-hd", Voice = "nova", CustomVoice = "marin", Speed = 1.25 };
        var plugin = await StartAsync<OpenAiSpeechPlugin>(settings, handler);

        await Audio.CollectAsync(((ITextToSpeech)plugin).SynthesizeAsync("Hello.", Ct));

        var json = handler.Single.Json;
        Assert.False(json.ContainsKey("instructions"));
        Assert.Equal(1.25, json["speed"]!.GetValue<double>());
        Assert.Equal("marin", json["voice"]!.GetValue<string>());
        Assert.Equal("tts-1-hd", json["model"]!.GetValue<string>());
    }

    [Fact]
    public async Task Text_with_nothing_to_say_makes_no_request()
    {
        var handler = new RecordingHandler(_ => throw new InvalidOperationException("no request expected"));
        var plugin = await StartAsync<OpenAiSpeechPlugin>(new OpenAiSpeechSettings { ApiKey = "sk-test" }, handler);

        Assert.Empty(await Audio.CollectAsync(((ITextToSpeech)plugin).SynthesizeAsync(" **  ** ", Ct)));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Text_over_the_input_limit_is_spoken_in_several_requests()
    {
        var handler = new RecordingHandler(_ => Responses.Stream(new MemoryStream(new byte[480])));
        var plugin = await StartAsync<OpenAiSpeechPlugin>(new OpenAiSpeechSettings { ApiKey = "sk-test" }, handler);
        var text = string.Join(" ", Enumerable.Repeat("The quick brown fox jumps over the lazy dog.", 200));

        await Audio.CollectAsync(((ITextToSpeech)plugin).SynthesizeAsync(text, Ct));

        var inputs = handler.Requests.Select(r => r.Json["input"]!.GetValue<string>()).ToList();
        Assert.Equal(3, inputs.Count);
        Assert.All(inputs, i => Assert.InRange(i.Length, 1, OpenAiSpeechPlugin.MaxInputLength));
        Assert.Equal(text, string.Join(" ", inputs));
    }

    [Fact]
    public async Task Transcription_can_go_to_another_server_with_its_own_key()
    {
        var handler = new RecordingHandler(r => r.Url.Host == "api.groq.com"
            ? Responses.Json("""{"text":"Set a timer for ten minutes."}""")
            : Responses.Stream(new MemoryStream(new byte[480])));
        var settings = new OpenAiSpeechSettings
        {
            ApiKey = "sk-openai",
            SttBaseUrl = "https://api.groq.com/openai/v1",
            SttApiKey = "gsk-groq",
            SttModel = "whisper-large-v3-turbo",
        };
        var plugin = await StartAsync<OpenAiSpeechPlugin>(settings, handler);

        Assert.Equal("Set a timer for ten minutes.", await ((ISpeechToText)plugin).TranscribeAsync(Clip, "en-NZ", Ct));
        await Audio.CollectAsync(((ITextToSpeech)plugin).SynthesizeAsync("Done.", Ct));

        var stt = handler.Requests[0];
        Assert.Equal("https://api.groq.com/openai/v1/audio/transcriptions", stt.Url.AbsoluteUri);
        Assert.Equal("Bearer gsk-groq", stt.Header("Authorization"));
        Assert.Equal("whisper-large-v3-turbo", stt.Parts.Field("model"));
        var tts = handler.Requests[1];
        Assert.Equal("https://api.openai.com/v1/audio/speech", tts.Url.AbsoluteUri);
        Assert.Equal("Bearer sk-openai", tts.Header("Authorization"));
        Assert.Equal("Groq (OpenAI-compatible)", ((ISpeechToText)plugin).EngineName);
        Assert.Equal("OpenAI", ((ITextToSpeech)plugin).EngineName);
    }

    [Fact]
    public async Task The_main_key_is_never_sent_to_a_different_transcription_server()
    {
        var handler = new RecordingHandler(_ => throw new InvalidOperationException("no request expected"));
        var settings = new OpenAiSpeechSettings { ApiKey = "sk-openai", SttBaseUrl = "https://api.groq.com/openai/v1" };
        var plugin = await StartAsync<OpenAiSpeechPlugin>(settings, handler);

        Assert.False(((ISpeechToText)plugin).IsReady);
        Assert.True(((ITextToSpeech)plugin).IsReady);
        await Assert.ThrowsAsync<PluginException>(() => ((ISpeechToText)plugin).TranscribeAsync(Clip, "en-NZ", Ct));
        Assert.Empty(handler.Requests);
        Assert.Equal(new PluginStatus(StatusLevel.Ok, "Voice ready; add a transcription API key for Groq"), plugin.Status);
    }

    [Fact]
    public async Task Readiness_and_status_say_what_is_missing()
    {
        var unset = await StartAsync<OpenAiSpeechPlugin>(new OpenAiSpeechSettings());
        Assert.True(unset.EnabledByDefault);
        Assert.False(((ISpeechToText)unset).IsReady);
        Assert.False(((ITextToSpeech)unset).IsReady);
        Assert.Equal(new PluginStatus(StatusLevel.Idle, "Add an API key"), unset.Status);
        await Assert.ThrowsAsync<PluginException>(() => Audio.CollectAsync(((ITextToSpeech)unset).SynthesizeAsync("Hi", Ct)));

        var ready = await StartAsync<OpenAiSpeechPlugin>(new OpenAiSpeechSettings { ApiKey = "sk-test" });
        Assert.True(((ISpeechToText)ready).IsReady);
        Assert.True(((ITextToSpeech)ready).IsReady);
        Assert.Equal(new PluginStatus(StatusLevel.Ok, "Ready: gpt-4o-mini-transcribe and gpt-4o-mini-tts (coral)"), ready.Status);

        var broken = await StartAsync<OpenAiSpeechPlugin>(new OpenAiSpeechSettings { ApiKey = "sk-test", BaseUrl = "ftp://example.com" });
        Assert.False(((ITextToSpeech)broken).IsReady);
        Assert.Equal(StatusLevel.Error, broken.Status.Level);
    }

    [Fact]
    public async Task A_rejected_key_shows_in_the_status_until_a_call_works()
    {
        bool reject = true;
        var handler = new RecordingHandler(_ => reject
            ? Responses.Json("""{"error":{"message":"Incorrect API key provided: sk-wro**ng.","type":"invalid_request_error","code":"invalid_api_key"}}""", HttpStatusCode.Unauthorized)
            : Responses.Json("""{"text":"Hello"}"""));
        var plugin = await StartAsync<OpenAiSpeechPlugin>(new OpenAiSpeechSettings { ApiKey = "sk-wrong" }, handler);

        var e = await Assert.ThrowsAsync<PluginException>(() => ((ISpeechToText)plugin).TranscribeAsync(Clip, "en-NZ", Ct));
        Assert.Equal("OpenAI: API key rejected. Check it on the hub's Plugins page.", e.Message);
        Assert.Equal(new PluginStatus(StatusLevel.Error, e.Message), plugin.Status);

        reject = false;
        await ((ISpeechToText)plugin).TranscribeAsync(Clip, "en-NZ", Ct);
        Assert.Equal(StatusLevel.Ok, plugin.Status.Level);
    }

    [Fact]
    public async Task Rate_limits_and_empty_accounts_are_told_apart()
    {
        var quota = new RecordingHandler(_ => Responses.Json(
            """{"error":{"message":"You exceeded your current quota, please check your plan and billing details.","type":"insufficient_quota","code":"insufficient_quota"}}""",
            HttpStatusCode.TooManyRequests));
        var broke = await StartAsync<OpenAiSpeechPlugin>(new OpenAiSpeechSettings { ApiKey = "sk-test" }, quota);
        var e = await Assert.ThrowsAsync<PluginException>(() => Audio.CollectAsync(((ITextToSpeech)broke).SynthesizeAsync("Hi", Ct)));
        Assert.StartsWith("OpenAI: out of credit or over quota", e.Message);

        var busy = new RecordingHandler(_ => Responses.Json("""{"error":{"message":"Rate limit reached","code":"rate_limit_exceeded"}}""", HttpStatusCode.TooManyRequests));
        var limited = await StartAsync<OpenAiSpeechPlugin>(new OpenAiSpeechSettings { ApiKey = "sk-test" }, busy);
        e = await Assert.ThrowsAsync<PluginException>(() => Audio.CollectAsync(((ITextToSpeech)limited).SynthesizeAsync("Hi", Ct)));
        Assert.Equal("OpenAI: rate limited, too many requests. Wait a moment and try again.", e.Message);
        Assert.Equal(StatusLevel.Ok, limited.Status.Level);  // transient: not worth an error on the Plugins page
    }

    [Fact]
    public async Task Barge_in_cancels_synthesis_mid_stream_and_releases_the_response()
    {
        var body = new TrickleStream(Audio.ToBytes(Audio.Signal(24000 * 5)), [4800], async (read, cancel) =>
        {
            if (read >= 2) await Task.Delay(Timeout.Infinite, cancel);  // still synthesizing
        });
        var handler = new RecordingHandler(_ => Responses.Stream(body));
        var plugin = await StartAsync<OpenAiSpeechPlugin>(new OpenAiSpeechSettings { ApiKey = "sk-test" }, handler);
        using var cts = new CancellationTokenSource();

        var received = 0;
        var clock = new Stopwatch();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var chunk in ((ITextToSpeech)plugin).SynthesizeAsync("A long answer.", cts.Token))
            {
                received += chunk.Samples.Length;
                if (received >= 4800)
                {
                    clock.Start();
                    cts.Cancel();
                }
            }
        });

        Assert.InRange(clock.ElapsedMilliseconds, 0, 2000);
        Assert.Equal(4800, received);
        Assert.True(body.Disposed);
    }

    [Fact]
    public async Task Barge_in_over_a_real_connection_stops_promptly()
    {
        var bytes = Audio.ToBytes(Audio.Signal(24000));
        var release = new TaskCompletionSource();
        await using var server = new MiniHttpServer(async (_, response) =>
        {
            await response.StartChunkedAsync(200, "audio/pcm");
            await response.WriteChunkAsync(bytes.AsMemory(0, 4800));
            await release.Task.WaitAsync(TimeSpan.FromSeconds(10));  // keeps "synthesizing"
        });
        var plugin = await StartAsync<OpenAiSpeechPlugin>(new OpenAiSpeechSettings { BaseUrl = new Uri(server.BaseUrl, "v1").AbsoluteUri });
        using var cts = new CancellationTokenSource();

        var clock = new Stopwatch();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in ((ITextToSpeech)plugin).SynthesizeAsync("A long answer.", cts.Token))
            {
                clock.Start();
                cts.Cancel();
            }
        });
        release.SetResult();

        Assert.InRange(clock.ElapsedMilliseconds, 0, 2000);
    }

    [Fact]
    public async Task Streams_from_a_keyless_local_server_before_synthesis_finishes()
    {
        // A local OpenAI-compatible server (Kokoro-FastAPI, Speaches) over real sockets with chunked encoding. The server
        // holds back the rest of the audio until the client has the first chunk, so this only passes if audio is
        // yielded as it arrives.
        var samples = Audio.Signal(24000);
        var bytes = Audio.ToBytes(samples);
        var firstChunk = new TaskCompletionSource();
        MiniRequest? seen = null;
        await using var server = new MiniHttpServer(async (request, response) =>
        {
            seen = request;
            await response.StartChunkedAsync(200, "audio/pcm");
            await response.WriteChunkAsync(bytes.AsMemory(0, 9601));  // odd: splits a sample
            await firstChunk.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await response.WriteChunkAsync(bytes.AsMemory(9601));
            await response.EndChunkedAsync();
        });
        var settings = new OpenAiSpeechSettings { BaseUrl = new Uri(server.BaseUrl, "v1").AbsoluteUri, TtsModel = "kokoro", CustomVoice = "af_heart" };
        var plugin = await StartAsync<OpenAiSpeechPlugin>(settings);

        Assert.True(((ITextToSpeech)plugin).IsReady);  // no key needed on this machine
        var received = new List<AudioChunk>();
        await foreach (var chunk in ((ITextToSpeech)plugin).SynthesizeAsync("Hello from the lounge.", Ct))
        {
            received.Add(chunk);
            firstChunk.TrySetResult();
        }

        Assert.Null(server.Failure);
        Assert.Equal(samples, Audio.Join(received));
        Assert.Equal("POST", seen!.Method);
        Assert.Equal("/v1/audio/speech", seen.Path);
        Assert.False(seen.Headers.ContainsKey("Authorization"));
        Assert.Equal("af_heart", JsonNode.Parse(seen.Body)!["voice"]!.GetValue<string>());
        Assert.Equal($"OpenAI-compatible ({server.BaseUrl.Authority})", ((ITextToSpeech)plugin).EngineName);
    }
}
