using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Pucksley.Sdk;

namespace Pucksley.Core.Tests.Support;

// The hub discovers every IPlugin with a public parameterless constructor in the assemblies it's
// given, so these all load wherever this assembly is passed in. They're off by default: each test
// (or the hub fixture) turns on the ones it wants through settings.

/// <summary>Tools for the agent tests (unit level, no hub).</summary>
public sealed class AgentToolsPlugin : PuckPlugin<NoSettings>
{
    public override string Id => "agent-tools";
    public override string Name => "Lights";
    public override string Description => "Test tools for the agent tests.";
    public override string Icon => "light";
    public override string? Instructions => "Rooms are Kitchen and Lounge.";

    public ConcurrentQueue<string> Calls { get; } = new();
    public ConcurrentQueue<ToolCaller> Callers { get; } = new();

    /// <summary>A tool whose description the test can change (dynamic tools, like Custom actions or MCP).</summary>
    public string NoteDescription { get; set; } = "Write a note.";

    /// <summary>Delays agent_tools_set_level (to test cancellation mid-tool).</summary>
    public TimeSpan SetLevelDelay { get; set; }

    public string? Context_ { get; set; } = "Kitchen lights: off";

    /// <summary>Makes GetContextAsync slow (the hub gives each plugin ~0.9 s).</summary>
    public TimeSpan ContextDelay { get; set; }

    public static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4];

    public override async Task<string?> GetContextAsync(ToolCaller caller, CancellationToken cancel)
    {
        if (ContextDelay > TimeSpan.Zero) await Task.Delay(ContextDelay, cancel);
        return Context_;
    }

    [Tool("agent_tools_set_level", "Set a room's light level.")]
    public async Task<ToolResult> SetLevel([Param("Room name")] string room, [Param("0-100", Min = 0, Max = 100)] int level, ToolCaller caller, CancellationToken cancel)
    {
        if (SetLevelDelay > TimeSpan.Zero) await Task.Delay(SetLevelDelay, cancel);
        Calls.Enqueue($"set_level {room} {level}");
        Callers.Enqueue(caller);
        return ToolResult.Ok($"{room} is at {level}%.", activity: $"{room} {level}%");
    }

    [Tool("agent_tools_fail", "A tool that always fails.")]
    public string Fail()
    {
        Calls.Enqueue("fail");
        throw new PluginException("The lights are unplugged.");
    }

    [Tool("agent_tools_wipe", "Wipe every light scene. Drastic.", Confirm = true)]
    public string Wipe()
    {
        Calls.Enqueue("wipe");
        return "Wiped.";
    }

    public override IReadOnlyList<ToolDefinition> GetTools()
    {
        var list = base.GetTools().ToList();
        list.Add(new ToolDefinition
        {
            Name = "agent_tools_note",
            Description = NoteDescription,
            InputSchema = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject { ["text"] = new JsonObject { ["type"] = "string" } } },
            Handler = inv => Task.FromResult(ToolResult.Ok("Noted.")),
        });
        // Like an MCP tool generated from a Pydantic model: $defs, a $ref and additionalProperties
        list.Add(new ToolDefinition
        {
            Name = "agent_tools_scene",
            Description = "Apply a scene.",
            InputSchema = JsonNode.Parse("""
                {"type":"object","properties":{"scene":{"$ref":"#/$defs/Scene"}},"required":["scene"],
                 "additionalProperties":false,"$defs":{"Scene":{"type":"object","properties":{"name":{"type":"string"}}}}}
                """)!.AsObject(),
            Handler = inv => Task.FromResult(ToolResult.Ok("Scene on.")),
        });
        list.Add(new ToolDefinition
        {
            Name = "agent_tools_snap",
            Description = "Take a picture.",
            InputSchema = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() },
            Handler = inv => Task.FromResult(new ToolResult { Text = "A picture of the kitchen.", Image = Png, ImageMediaType = "image/png" }),
        });
        return list;
    }

    /// <summary>Tells the hub the tool list changed (after <see cref="NoteDescription"/> was edited).</summary>
    public void RaiseToolsChanged() => Context.ToolsChanged();
}

/// <summary>Speech for the end-to-end tests: a fixed transcript, and a short 24 kHz tone per sentence.</summary>
public sealed class FakeSpeechPlugin : PuckPlugin<NoSettings>, ISpeechToText, ITextToSpeech, IDirectSpeechToText, IDirectTextToSpeech
{
    /// <summary>When set, the engines say a puck could call them itself (direct mode).</summary>
    public volatile bool DirectCapable;

    public DirectTranscription? GetDirectTranscription(string? language) => DirectCapable
        ? new DirectTranscription("Fake STT", "https://stt.example.com/v1/audio/transcriptions", ["Authorization: Bearer stt-key"], true,
            [new("model", "fake-1"), new("language", language ?? "")])
        : null;

    public DirectVoice? GetDirectVoice() => DirectCapable
        ? new DirectVoice("Fake TTS", "https://tts.example.com/v1/audio/speech", ["Authorization: Bearer tts-key", "Content-Type: application/json"],
            "{\"input\":%TEXT%,\"voice\":\"coral\"}", false, TtsRate)
        : null;

    public override string Id => "fake-speech";
    public override string Name => "Fake speech";
    public override string Description => "Test speech engines.";

    string ISpeechToText.EngineId => "fake-stt";
    string ISpeechToText.EngineName => "Fake STT";
    bool ISpeechToText.IsReady => true;
    string ITextToSpeech.EngineId => "fake-tts";
    string ITextToSpeech.EngineName => "Fake TTS";
    bool ITextToSpeech.IsReady => true;

    public const int TtsRate = 24000;

    public volatile string Transcript = "turn the kitchen lights on";
    public ConcurrentQueue<AudioClip> Heard { get; } = new();
    public ConcurrentQueue<string> Spoken { get; } = new();

    /// <summary>Seconds of audio per sentence (the hub paces it out a little ahead of real time).</summary>
    public double SecondsPerSentence { get; set; } = 0.25;

    /// <summary>Delay before the transcript comes back.</summary>
    public TimeSpan TranscribeDelay { get; set; }

    /// <summary>When set, transcription fails with this message (as an engine would: PluginException).</summary>
    public string? TranscribeError { get; set; }

    /// <summary>Picks the transcript from the audio (lets concurrent pucks say different things).</summary>
    public Func<AudioClip, string>? TranscriptFor { get; set; }

    public async Task<string> TranscribeAsync(AudioClip audio, string? language, CancellationToken cancel)
    {
        Heard.Enqueue(audio);
        if (TranscribeDelay > TimeSpan.Zero) await Task.Delay(TranscribeDelay, cancel);
        if (TranscribeError is { } error) throw new PluginException(error);
        return TranscriptFor?.Invoke(audio) ?? Transcript;
    }

    public async IAsyncEnumerable<AudioChunk> SynthesizeAsync(string text, [EnumeratorCancellation] CancellationToken cancel)
    {
        Spoken.Enqueue(text);
        int total = (int)(TtsRate * SecondsPerSentence);
        var all = Signals.Sine(440, TtsRate, total, 8000);
        int chunk = Math.Max(1, total / 3 + 7);   // a few uneven chunks
        for (int off = 0; off < total; off += chunk)
        {
            await Task.Yield();
            cancel.ThrowIfCancellationRequested();
            yield return new AudioChunk(all[off..Math.Min(total, off + chunk)], TtsRate);
        }
    }

    public void Reset()
    {
        Transcript = "turn the kitchen lights on";
        Heard.Clear();
        Spoken.Clear();
        SecondsPerSentence = 0.25;
        TranscribeDelay = TimeSpan.Zero;
        TranscribeError = null;
        TranscriptFor = null;
        DirectCapable = false;
    }
}

/// <summary>Tools, controls, notifications and a media source for the end-to-end tests.</summary>
public sealed class HubToolsPlugin : PuckPlugin<NoSettings>, IMediaSourceProvider
{
    public override string Id => "hub-tools";
    public override string Name => "Hub tools";
    public override string Description => "Test tools for the hub tests.";
    public override string Icon => "light";

    public ConcurrentQueue<string> Calls { get; } = new();
    public ConcurrentQueue<(string Id, ControlInput Input)> ControlInputs { get; } = new();
    public ConcurrentQueue<(string Source, MediaCommand Command)> MediaCommands { get; } = new();

    /// <summary>What's playing (curly apostrophe and macron on purpose: the puck gets them folded).</summary>
    public NowPlaying Playing { get; set; } = new() { Title = "Don’t Stop", Artist = "Tāmaki Five", Playing = true, Volume = 25, Source = "Kitchen" };

    public int NowPlayingCalls;

    [Tool("hub_tools_lights", "Set a room's lights (0-100).")]
    public ToolResult Lights([Param("Room name")] string room, [Param("Level 0-100")] int level, ToolCaller caller)
    {
        Calls.Enqueue($"lights {room} {level} {caller.PuckId}");
        return ToolResult.Ok($"{room} lights are at {level}%.", activity: $"{room} {level}%");
    }

    [Tool("hub_tools_shutdown", "Shut down the test computer. Only when asked.", Confirm = true, Icon = "pc")]
    public string Shutdown(ToolCaller caller)
    {
        Calls.Enqueue($"shutdown {caller.PuckId}");
        return "Shutting down.";
    }

    public override IReadOnlyList<PuckControl> GetControls() =>
    [
        new() { Id = "kitchen.vol", Name = "Kitchen", Kind = ControlKind.Number, Value = 25, Min = 0, Max = 100, Step = 1, Unit = "%" },
        new() { Id = "lamp", Name = "Lamp", Kind = ControlKind.Toggle, On = true },
        new() { Id = "tv.off", Name = "TV off", Kind = ControlKind.Action },
    ];

    public override Task OnControlAsync(string controlId, ControlInput input, CancellationToken cancel)
    {
        ControlInputs.Enqueue((controlId, input));
        return Task.CompletedTask;
    }

    public Task NotifyAsync(string text, string? puckId = null) => Context.NotifyAsync(text, NotifyKind.Alert, chime: true, puckId);

    public void SetControlValue(string controlId, double value) => Context.ControlValueChanged(controlId, value);

    public void SetStatusForTest(string message) => SetStatus(StatusLevel.Ok, message);

    /// <summary>Tells the hub what's playing may have changed (it re-reads and pushes it to pucks showing it).</summary>
    public void RaiseNowPlaying() => Context.NowPlayingChanged("kitchen");

    public IReadOnlyList<MediaSourceInfo> GetMediaSources() => [new("kitchen", "Kitchen")];

    public Task<NowPlaying?> GetNowPlayingAsync(string sourceId, CancellationToken cancel)
    {
        Interlocked.Increment(ref NowPlayingCalls);
        return Task.FromResult<NowPlaying?>(Playing);
    }

    public Task MediaCommandAsync(string sourceId, MediaCommand command, CancellationToken cancel)
    {
        MediaCommands.Enqueue((sourceId, command));
        return Task.CompletedTask;
    }

    public void Reset()
    {
        Calls.Clear();
        ControlInputs.Clear();
        MediaCommands.Clear();
        Playing = new() { Title = "Don’t Stop", Artist = "Tāmaki Five", Playing = true, Volume = 25, Source = "Kitchen" };
    }
}

public static class Signals
{
    public static short[] Sine(double hz, int rate, int count, double amplitude, double phase = 0)
    {
        var s = new short[count];
        for (int i = 0; i < count; i++) s[i] = (short)Math.Round(amplitude * Math.Sin(phase + 2 * Math.PI * hz * i / rate));
        return s;
    }

    public static short[] Silence(int count) => new short[count];

    /// <summary>Quiet deterministic noise (well under the VAD's floor of 250 RMS).</summary>
    public static short[] Hiss(int count, int amplitude = 40, int seed = 1)
    {
        var r = new Random(seed);
        var s = new short[count];
        for (int i = 0; i < count; i++) s[i] = (short)r.Next(-amplitude, amplitude + 1);
        return s;
    }

    public static short[] Concat(params short[][] parts) => parts.SelectMany(p => p).ToArray();

    public static double Rms(ReadOnlySpan<short> s)
    {
        if (s.Length == 0) return 0;
        double sum = 0;
        foreach (var v in s) sum += (double)v * v;
        return Math.Sqrt(sum / s.Length);
    }

    /// <summary>Frequency from rising zero crossings (good enough for one clean tone).</summary>
    public static double Frequency(ReadOnlySpan<short> s, int rate)
    {
        int first = -1, last = -1, crossings = 0;
        for (int i = 1; i < s.Length; i++)
        {
            if (s[i - 1] < 0 && s[i] >= 0)
            {
                if (first < 0) first = i;
                last = i;
                crossings++;
            }
        }
        if (crossings < 2) return 0;
        return (crossings - 1) * (double)rate / (last - first);
    }
}
