using System.Text.Json.Nodes;
using Pucksley.Core.Pucks;
using Pucksley.Core.Settings;
using Pucksley.Core.Tests.Support;
using Pucksley.Sdk;

namespace Pucksley.Core.Tests;

/// <summary>Direct mode: the setup a puck gets so it can ask Claude itself when the hub is away.</summary>
public sealed class DirectSetupTests
{
    sealed class Stt(string id, DirectTranscription? recipe) : ISpeechToText, IDirectSpeechToText
    {
        public string EngineId => id;
        public string EngineName => id;
        public bool IsReady => true;
        public string? LastLanguage;
        public Task<string> TranscribeAsync(AudioClip audio, string? language, CancellationToken cancel) => Task.FromResult("");
        public DirectTranscription? GetDirectTranscription(string? language)
        {
            LastLanguage = language;
            return recipe;
        }
    }

    sealed class LocalStt : ISpeechToText   // like local Whisper: only the hub can use it
    {
        public string EngineId => "whisper-local";
        public string EngineName => "Whisper";
        public bool IsReady => true;
        public Task<string> TranscribeAsync(AudioClip audio, string? language, CancellationToken cancel) => Task.FromResult("");
    }

    sealed class Tts(string id, DirectVoice? recipe) : ITextToSpeech, IDirectTextToSpeech
    {
        public string EngineId => id;
        public string EngineName => id;
        public bool IsReady => true;
        public async IAsyncEnumerable<AudioChunk> SynthesizeAsync(string text, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancel)
        {
            await Task.CompletedTask;
            yield break;
        }
        public DirectVoice? GetDirectVoice() => recipe;
    }

    static DirectTranscription SttRecipe(string name) =>
        new(name, $"https://{name}.example.com/stt", ["Authorization: Bearer k"], true, [new("model", "m1")]);

    static DirectVoice VoiceRecipe(string name) =>
        new(name, $"https://{name}.example.com/tts", ["Authorization: Bearer k"], "{\"input\":%TEXT%}", false, 24000);

    static HubSettings Settings(Action<HubSettings>? change = null)
    {
        var s = new HubSettings();
        s.Assistant.AssistantName = "Kahu";
        s.Assistant.ExtraInstructions = "Use metric units.";
        s.Household.Location = "Christchurch, New Zealand";
        s.Household.Notes = "Our dog is called Mabel.";
        s.Voice.SttEngine = "whisper-local";
        s.Voice.TtsEngine = "";
        change?.Invoke(s);
        return s;
    }

    static readonly TimeZoneInfo Nz = TimeZoneInfo.FindSystemTimeZoneById("Pacific/Auckland");

    static string? S(JsonObject m, string k) => m[k] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    [Fact]
    public void Everything_a_puck_needs_comes_in_four_messages()
    {
        var stt = new Stt("openai", SttRecipe("OpenAI"));
        var messages = DirectSetup.Build(Settings(), "sk-ant-123", [new LocalStt(), stt], [new Tts("openai", VoiceRecipe("OpenAI"))], Nz);

        Assert.Equal(["claude", "stt", "tts", "listen"], messages.Select(m => S(m, "a")));
        Assert.All(messages, m => Assert.Equal("direct", S(m, "t")));

        var claude = messages[0];
        Assert.Equal("sk-ant-123", S(claude, "key"));
        Assert.Equal("claude-opus-5-5", S(claude, "model"));
        Assert.Equal("low", S(claude, "effort"));
        Assert.True(claude["search"]!.GetValue<bool>());
        Assert.Equal(5, claude["keep"]!.GetValue<int>());
        var sys = S(claude, "sys")!;
        Assert.Contains("You are Kahu", sys);
        Assert.Contains("Christchurch, New Zealand", sys);
        Assert.Contains("Mabel", sys);
        Assert.Contains("metric", sys);
        Assert.DoesNotContain("[Context]", sys);   // no tools, no context block: that's the hub's
        var loc = claude["loc"]!.AsObject();
        Assert.Equal("approximate", S(loc, "type"));
        Assert.Equal("Christchurch", S(loc, "city"));
        Assert.Equal("NZ", S(loc, "country"));
        Assert.Equal("Pacific/Auckland", S(loc, "timezone"));

        var sttMsg = messages[1];
        Assert.Equal("https://OpenAI.example.com/stt".ToLowerInvariant(), S(sttMsg, "url")!.ToLowerInvariant());
        Assert.Equal("multipart", S(sttMsg, "fmt"));
        Assert.Equal("model", sttMsg["fields"]![0]![0]!.GetValue<string>());
        Assert.Equal("en-NZ", stt.LastLanguage);   // the hub's language goes to the engine

        var tts = messages[2];
        Assert.Contains("%TEXT%", S(tts, "body"));
        Assert.Equal("json", S(tts, "esc"));
        Assert.Equal(24000, tts["rate"]!.GetValue<int>());

        var listen = messages[3];
        Assert.Equal(900, listen["end"]!.GetValue<int>());
        Assert.Equal(7, listen["nospeech"]!.GetValue<int>());
    }

    [Fact]
    public void Off_when_disabled_without_a_key_or_without_a_cloud_transcriber()
    {
        Tts voice = new("openai", VoiceRecipe("OpenAI"));
        Stt stt = new("openai", SttRecipe("OpenAI"));
        Assert.Equal("off", S(DirectSetup.Build(Settings(s => s.Direct.Enabled = false), "sk", [stt], [voice], Nz).Single(), "a"));
        Assert.Equal("off", S(DirectSetup.Build(Settings(), "", [stt], [voice], Nz).Single(), "a"));
        Assert.Equal("off", S(DirectSetup.Build(Settings(), null, [stt], [voice], Nz).Single(), "a"));
        // local Whisper only: the puck couldn't hear
        Assert.Equal("off", S(DirectSetup.Build(Settings(), "sk", [new LocalStt()], [voice], Nz).Single(), "a"));
        // an engine that can't be called directly right now (no key)
        Assert.Equal("off", S(DirectSetup.Build(Settings(), "sk", [new Stt("openai", null)], [voice], Nz).Single(), "a"));
    }

    [Fact]
    public void The_hubs_own_engine_wins_then_the_first_that_can_and_a_choice_is_a_choice()
    {
        var a = new Stt("elevenlabs", SttRecipe("ElevenLabs"));
        var b = new Stt("azure", SttRecipe("Azure"));
        string? SttName(HubSettings s) => S(DirectSetup.Build(s, "sk", [a, b], [], Nz)[1], "name");

        Assert.Equal("ElevenLabs", SttName(Settings()));                                 // first that can
        Assert.Equal("Azure", SttName(Settings(s => s.Voice.SttEngine = "azure")));       // the hub's own
        Assert.Equal("ElevenLabs", SttName(Settings(s =>
        {
            s.Voice.SttEngine = "azure";
            s.Direct.SttEngine = "elevenlabs";                                            // chosen for direct mode
        })));
        // a chosen engine that can't: off, rather than quietly sending the audio somewhere else
        Assert.Equal("off", S(DirectSetup.Build(Settings(s => s.Direct.SttEngine = "openai"), "sk", [a, b], [], Nz).Single(), "a"));

        // no voice that can: text only
        var noVoice = DirectSetup.Build(Settings(), "sk", [a], [new Tts("piper", null)], Nz);
        Assert.True(noVoice[2]["off"]!.GetValue<bool>());
        // "none" means none, even with a voice available
        var none = DirectSetup.Build(Settings(s => s.Direct.TtsEngine = "none"), "sk", [a], [new Tts("openai", VoiceRecipe("OpenAI"))], Nz);
        Assert.True(none[2]["off"]!.GetValue<bool>());
        Assert.Equal("Claude · ElevenLabs hearing · text only", DirectSetup.Describe(none));
    }

    [Fact]
    public void Model_and_web_search_follow_the_direct_settings_and_the_fingerprint_notices()
    {
        var stt = new Stt("openai", SttRecipe("OpenAI"));
        var one = DirectSetup.Build(Settings(s =>
        {
            s.Direct.Model = "claude-sonnet-5-5";
            s.Direct.WebSearch = false;
        }), "sk-1", [stt], [], Nz);
        Assert.Equal("claude-sonnet-5-5", S(one[0], "model"));
        Assert.False(one[0]["search"]!.GetValue<bool>());

        var same = DirectSetup.Build(Settings(s =>
        {
            s.Direct.Model = "claude-sonnet-5-5";
            s.Direct.WebSearch = false;
        }), "sk-1", [stt], [], Nz);
        var newKey = DirectSetup.Build(Settings(s =>
        {
            s.Direct.Model = "claude-sonnet-5-5";
            s.Direct.WebSearch = false;
        }), "sk-2", [stt], [], Nz);
        Assert.Equal(DirectSetup.Fingerprint(one), DirectSetup.Fingerprint(same));
        Assert.NotEqual(DirectSetup.Fingerprint(one), DirectSetup.Fingerprint(newKey));
    }

    [Fact]
    public void The_speech_plugins_build_the_requests_a_puck_can_make()
    {
        // OpenAI's voice: the request body with the text left as %TEXT%, quotes and all
        var body = DirectVoice.JsonTemplate("{\"model\":\"m\",\"input\":\"\\u0001x\\u0001\",\"voice\":\"coral\"}", "\u0001x\u0001");
        Assert.Equal("{\"model\":\"m\",\"input\":%TEXT%,\"voice\":\"coral\"}", body);
    }
}

/// <summary>Time zones as POSIX rules, for the puck to keep local time on its own.</summary>
public sealed class PosixTzTests
{
    [Fact]
    public void New_Zealand_with_daylight_saving()
    {
        var nz = TimeZoneInfo.FindSystemTimeZoneById("Pacific/Auckland");
        Assert.Equal("STD-12DST-13,M9.5.0/2,M4.1.0/3", PosixTz.FromRules(nz, new DateTimeOffset(2026, 11, 9, 9, 0, 0, TimeSpan.Zero)));
    }

    [Fact]
    public void A_zone_without_daylight_saving_and_one_west_of_Greenwich()
    {
        var sg = TimeZoneInfo.FindSystemTimeZoneById("Asia/Singapore");
        Assert.Equal("STD-8", PosixTz.FromRules(sg, DateTimeOffset.UtcNow));
        var ny = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        Assert.Equal("STD5DST4,M3.2.0/2,M11.1.0/2", PosixTz.FromRules(ny, new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero)));
        var india = TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata");
        Assert.Equal("STD-5:30", PosixTz.FromRules(india, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void A_zone_file_gives_its_rule_exactly()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pucksley-tz-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "Test"));
        try
        {
            var file = new List<byte>();
            file.AddRange("TZif2"u8.ToArray());
            file.AddRange(new byte[40]);
            file.AddRange("\nNZST-12NZDT,M9.5.0,M4.1.0/3\n"u8.ToArray());
            File.WriteAllBytes(Path.Combine(dir, "Test", "Zone"), file.ToArray());
            var before = Environment.GetEnvironmentVariable("TZDIR");
            Environment.SetEnvironmentVariable("TZDIR", dir);
            try
            {
                Assert.Equal("NZST-12NZDT,M9.5.0,M4.1.0/3", PosixTz.FromZoneFile("Test/Zone"));
                Assert.Null(PosixTz.FromZoneFile("Test/Missing"));
                Assert.Null(PosixTz.FromZoneFile("../../etc/passwd"));
                Assert.Null(PosixTz.FromZoneFile("New Zealand Standard Time"));   // a Windows id: no file to look for
            }
            finally
            {
                Environment.SetEnvironmentVariable("TZDIR", before);
            }
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}

/// <summary>Direct mode end to end: what a puck on Wi-Fi is sent, and that changes follow.</summary>
public sealed class DirectModeHubTests(HubFixture hub) : HubTestBase(hub)
{
    [Fact]
    public async Task A_wifi_puck_gets_its_direct_setup_and_changes_follow()
    {
        var interval = PuckManager.DirectCheckInterval;
        PuckManager.DirectCheckInterval = TimeSpan.FromMilliseconds(200);
        Hub.Speech.DirectCapable = true;
        Hub.Settings.Update(s =>
        {
            s.Assistant.AnthropicApiKey = "sk-ant-direct-test";
            s.Direct = new DirectSettings();
        });
        try
        {
            await using var puck = await Hub.ConnectAsync();
            await puck.HelloAsync(name: "Puck-DIRECT");
            var first = await puck.WaitForAsync("welcome");
            if (first["ok"]?.GetValue<bool>() != true)
                (await Hub.PostJsonAsync($"api/pucks/{puck.Id}/approve")).EnsureSuccessStatusCode();
            var frames = await puck.CollectUntilAsync(f => f.Type == "state");
            var direct = frames.Where(f => f.Type == "direct").Select(f => f.Json!).ToList();
            Assert.Equal(["claude", "stt", "tts", "listen"], direct.Select(m => S(m, "a")));
            Assert.Equal("sk-ant-direct-test", S(direct[0], "key"));
            Assert.Equal("https://stt.example.com/v1/audio/transcriptions", S(direct[1], "url"));
            Assert.Equal("{\"input\":%TEXT%,\"voice\":\"coral\"}", S(direct[2], "body"));

            // the time comes with its zone's rule, for the puck to keep on its own
            var time = frames.First(f => f.Type == "time").Json!;
            Assert.False(string.IsNullOrEmpty(S(time, "tzp")));

            // the page says what the puck can do on its own
            var status = await Hub.GetJsonAsync("api/status");
            var listed = status["pucks"]!.AsArray().Select(p => p!.AsObject()).Single(p => S(p, "id") == puck.Id);
            Assert.Equal("Claude · Fake STT hearing · Fake TTS voice", S(listed, "directMode"));

            // turned off on the hub: the puck is told (and wipes its copy)
            Hub.Settings.Update(s => s.Direct.Enabled = false);
            var off = await puck.WaitForAsync("direct", m => S(m, "a") == "off", TimeSpan.FromSeconds(5));
            Assert.NotNull(off);
        }
        finally
        {
            PuckManager.DirectCheckInterval = interval;
            Hub.Settings.Update(s => s.Direct = new DirectSettings());
        }
    }
}
