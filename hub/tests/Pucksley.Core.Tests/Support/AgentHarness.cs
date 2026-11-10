using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using Pucksley.Core.Agent;
using Pucksley.Core.Plugins;
using Pucksley.Core.Settings;
using Pucksley.Core.Web;
using Pucksley.Sdk;

namespace Pucksley.Core.Tests.Support;

/// <summary>What plugins see of the hub, for tests without a hub.</summary>
public sealed class TestRuntime : IHubRuntime, IHubInfo
{
    public IHubInfo HubInfo => this;
    public TimeZoneInfo TimeZone { get; set; } = TimeZoneInfo.Utc;
    public HttpClient Http { get; } = new();
    public ConcurrentQueue<string> Notifications { get; } = new();
    public Task NotifyAsync(string text, NotifyKind kind, bool chime, string? puckId) { Notifications.Enqueue(text); return Task.CompletedTask; }
    public Task SayAsync(string text, string? puckId, CancellationToken cancel) => Task.CompletedTask;
    public Task<bool> ConfirmAsync(string question, ToolCaller caller, TimeSpan timeout, CancellationToken cancel) => Task.FromResult(true);
    public void ShowTimer(string timerId, DateTimeOffset? endsAt, string? label, string? puckId) { }
    public Task<byte[]> SynthesizeWavAsync(string text, CancellationToken cancel) => Task.FromResult(Array.Empty<byte>());
    public Uri PublishClip(byte[] data, string contentType, TimeSpan lifetime) => new("http://127.0.0.1:1/clips/x");

    public string Name => "TEST-HUB";
    public Uri LanUrl => new("http://127.0.0.1:1/");
    public Uri LoopbackUrl => new("http://127.0.0.1:1/");
    public double? Latitude => null;
    public double? Longitude => null;
    public string? Location => null;
    public bool IsWindows => OperatingSystem.IsWindows();
}

/// <summary>Everything an agent turn reports back.</summary>
public sealed class RecordingCallbacks
{
    readonly List<string> _text = [];
    public ConcurrentQueue<(string Icon, string Text, string State)> Activities { get; } = new();
    public ConcurrentQueue<string> ConfirmQuestions { get; } = new();
    public bool ConfirmAnswer { get; set; } = true;

    public string Streamed { get { lock (_text) return string.Concat(_text); } }
    public int DeltaCount { get { lock (_text) return _text.Count; } }

    public AgentCallbacks Build(bool withConfirm = true) => new()
    {
        OnText = d => { lock (_text) _text.Add(d); },
        OnActivity = (icon, text, state) => Activities.Enqueue((icon, text, state)),
        Confirm = withConfirm ? q => { ConfirmQuestions.Enqueue(q); return Task.FromResult(ConfirmAnswer); } : null,
    };
}

/// <summary>
/// The agent without a hub: settings in a temp file, the plugin manager with this assembly's test
/// plugins (only agent-tools on), the real AnthropicChatModel pointed at the fake API, and the
/// real AgentService.
/// </summary>
public sealed class AgentHarness
{
    public required SettingsStore Settings { get; init; }
    public required PluginManager Plugins { get; init; }
    public required AnthropicChatModel Model { get; init; }
    public required AgentService Agent { get; init; }
    public required HubEvents Events { get; init; }
    public required ClaudeScript Claude { get; init; }
    public required TestRuntime Runtime { get; init; }

    public AgentToolsPlugin Tools => (AgentToolsPlugin)Plugins.Find("agent-tools")!.Plugin;

    public static readonly ToolCaller Kitchen = new("a1b2c3d4e5f60708", "Kitchen puck", "Kitchen");

    public static async Task<AgentHarness> CreateAsync(Action<HubSettings>? configure = null, bool tools = true)
    {
        var fake = FakeAnthropic.Shared;
        var claude = fake.Script();
        var settings = new SettingsStore(Path.Combine(TestEnvironment.NewTempDir("agent"), "settings.json"));
        settings.Update(s =>
        {
            s.Assistant.AnthropicApiKey = claude.ApiKey;
            s.Assistant.Model = "claude-opus-5-5";
            s.Assistant.WebSearch = false;
            s.Plugins["agent-tools"] = new PluginState { Enabled = tools };
            configure?.Invoke(s);
        });
        var runtime = new TestRuntime();
        var plugins = new PluginManager(settings, NullLoggerFactory.Instance, () => runtime);
        plugins.Discover([typeof(AgentHarness).Assembly]);
        await plugins.StartAllAsync();
        var model = new AnthropicChatModel(NullLogger<AnthropicChatModel>.Instance) { BaseUrl = fake.Url };
        var events = new HubEvents();
        var agent = new AgentService(settings, plugins, model, events, NullLogger<AgentService>.Instance, () => runtime);
        return new AgentHarness { Settings = settings, Plugins = plugins, Model = model, Agent = agent, Events = events, Claude = claude, Runtime = runtime };
    }

    public Task<TurnResult> AskAsync(string text, RecordingCallbacks? cb = null, string session = "puck:test", ToolCaller? caller = null, CancellationToken cancel = default) =>
        Agent.AskAsync(session, text, caller ?? Kitchen, (cb ?? new RecordingCallbacks()).Build(), cancel);
}
