using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Pucksley.Sdk;

namespace Pucksley.TestSupport;

/// <summary>
/// An <see cref="IPluginContext"/> for unit tests: records notifications, speech, timers and saved
/// settings instead of talking to pucks.
/// </summary>
public sealed class FakePluginContext : IPluginContext, IHubInfo
{
    public FakePluginContext(string? dataDirectory = null, HttpClient? http = null)
    {
        DataDirectory = dataDirectory ?? Path.Combine(Path.GetTempPath(), "pucksley-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(DataDirectory);
        Http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    }

    public ILogger Log { get; set; } = NullLogger.Instance;
    public HttpClient Http { get; }
    public string DataDirectory { get; }
    public IHubInfo Hub => this;

    public List<object> SavedSettings { get; } = [];
    public ConcurrentQueue<string> Notifications { get; } = new();
    public ConcurrentQueue<string> Spoken { get; } = new();
    public ConcurrentDictionary<string, (DateTimeOffset? EndsAt, string? Label)> Timers { get; } = new();
    public int StatusChanges, ToolChanges, ControlChanges;
    public bool ConfirmAnswer { get; set; } = true;
    public List<string> ConfirmQuestions { get; } = [];
    public Dictionary<Type, object> Plugins { get; } = [];
    public Dictionary<string, (byte[] Data, string ContentType)> Clips { get; } = [];

    public void SaveSettings(object settings) => SavedSettings.Add(settings);
    public void StatusChanged() => Interlocked.Increment(ref StatusChanges);
    public void ToolsChanged() => Interlocked.Increment(ref ToolChanges);
    public void ControlsChanged() => Interlocked.Increment(ref ControlChanges);
    public ConcurrentQueue<(string Id, double? Value, bool? On, string? Text)> ControlValues { get; } = new();
    public ConcurrentQueue<string> NowPlayingChanges { get; } = new();
    public void ControlValueChanged(string controlId, double? value = null, bool? on = null, string? text = null) => ControlValues.Enqueue((controlId, value, on, text));
    public void NowPlayingChanged(string sourceId) => NowPlayingChanges.Enqueue(sourceId);

    public Task NotifyAsync(string text, NotifyKind kind = NotifyKind.Info, bool chime = false, string? puckId = null)
    {
        Notifications.Enqueue(text);
        return Task.CompletedTask;
    }

    public Task SayAsync(string text, string? puckId = null, CancellationToken cancel = default)
    {
        Spoken.Enqueue(text);
        return Task.CompletedTask;
    }

    public Task<bool> ConfirmAsync(string question, ToolCaller caller, TimeSpan? timeout = null, CancellationToken cancel = default)
    {
        ConfirmQuestions.Add(question);
        return Task.FromResult(ConfirmAnswer);
    }

    public void ShowTimer(string timerId, DateTimeOffset? endsAt, string? label, string? puckId = null) => Timers[timerId] = (endsAt, label);

    public Task<byte[]> SynthesizeWavAsync(string text, CancellationToken cancel = default) =>
        Task.FromResult(Wav.Encode(new short[1600], 16000));

    public Uri PublishClip(byte[] data, string contentType, TimeSpan lifetime)
    {
        var id = Guid.NewGuid().ToString("n");
        Clips[id] = (data, contentType);
        return new Uri(LanUrl, $"clips/{id}");
    }

    public T? GetPlugin<T>() where T : class => Plugins.TryGetValue(typeof(T), out var p) ? (T)p : null;

    public TimeZoneInfo TimeZone { get; set; } = TimeZoneInfo.Local;
    public DateTimeOffset Now => TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TimeZone);

    // IHubInfo
    public string Name { get; set; } = "TEST-HUB";
    public Uri LanUrl { get; set; } = new("http://192.168.1.10:8765/");
    public Uri LoopbackUrl { get; set; } = new("http://127.0.0.1:8765/");
    public double? Latitude { get; set; } = -41.29;
    public double? Longitude { get; set; } = 174.78;
    public string? Location { get; set; } = "Wellington, NZ";
    public bool IsWindows { get; set; } = OperatingSystem.IsWindows();

    /// <summary>Runs a plugin's tool by name with JSON arguments, as the hub would.</summary>
    public static async Task<ToolResult> CallToolAsync(IPlugin plugin, string toolName, string jsonArgs = "{}", ToolCaller? caller = null, CancellationToken cancel = default)
    {
        var tool = plugin.GetTools().FirstOrDefault(t => t.Name == toolName)
                   ?? throw new InvalidOperationException($"No tool {toolName} on {plugin.Id}");
        var args = JsonNode.Parse(jsonArgs) as JsonObject ?? new JsonObject();
        return await tool.Handler(new ToolInvocation { Arguments = args, Caller = caller ?? ToolCaller.Web, Cancellation = cancel });
    }
}
