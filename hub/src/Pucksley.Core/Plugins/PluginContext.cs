using Microsoft.Extensions.Logging;
using Pucksley.Core.Settings;
using Pucksley.Sdk;

namespace Pucksley.Core.Plugins;

/// <summary>
/// What the hub's plugins can reach: pucks, speech, clips, settings. Implemented by the hub; a
/// small seam so plugins don't depend on the hub's internals.
/// </summary>
public interface IHubRuntime
{
    IHubInfo HubInfo { get; }
    TimeZoneInfo TimeZone { get; }
    HttpClient Http { get; }
    Task NotifyAsync(string text, NotifyKind kind, bool chime, string? puckId);
    Task SayAsync(string text, string? puckId, CancellationToken cancel);
    Task<bool> ConfirmAsync(string question, ToolCaller caller, TimeSpan timeout, CancellationToken cancel);
    void ShowTimer(string timerId, DateTimeOffset? endsAt, string? label, string? puckId);
    Task<byte[]> SynthesizeWavAsync(string text, CancellationToken cancel);
    Uri PublishClip(byte[] data, string contentType, TimeSpan lifetime);
}

/// <summary>One plugin's view of the hub.</summary>
internal sealed class PluginContext : IPluginContext
{
    readonly PluginManager _manager;
    PluginHost _host;

    public PluginContext(PluginManager manager, PluginHost host, ILogger log)
    {
        _manager = manager;
        _host = host;
        Log = log;
    }

    internal void Bind(PluginHost host) => _host = host;

    public ILogger Log { get; }

    public HttpClient Http => _manager.Runtime.Http;

    public string DataDirectory
    {
        get
        {
            var dir = HubPaths.PluginDataDir(_host.Id);
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public IHubInfo Hub => _manager.Runtime.HubInfo;

    public void SaveSettings(object settings) => _manager.PersistFromPlugin(_host, settings);

    public void StatusChanged() => _manager.OnStatus(_host);

    public void ToolsChanged() => _manager.OnToolsChanged(_host);

    public void ControlsChanged() => _manager.OnControlsChanged(_host);

    public void ControlValueChanged(string controlId, double? value = null, bool? on = null, string? text = null) =>
        _manager.OnControlValue(_host, controlId, value, on, text);

    public void NowPlayingChanged(string sourceId) => _manager.OnNowPlaying(_host, sourceId);

    public Task NotifyAsync(string text, NotifyKind kind = NotifyKind.Info, bool chime = false, string? puckId = null) =>
        _manager.Runtime.NotifyAsync(text, kind, chime, puckId);

    public Task SayAsync(string text, string? puckId = null, CancellationToken cancel = default) =>
        _manager.Runtime.SayAsync(text, puckId, cancel);

    public Task<bool> ConfirmAsync(string question, ToolCaller caller, TimeSpan? timeout = null, CancellationToken cancel = default) =>
        _manager.Runtime.ConfirmAsync(question, caller, timeout ?? TimeSpan.FromSeconds(30), cancel);

    public void ShowTimer(string timerId, DateTimeOffset? endsAt, string? label, string? puckId = null) =>
        _manager.Runtime.ShowTimer(timerId, endsAt, label, puckId);

    public Task<byte[]> SynthesizeWavAsync(string text, CancellationToken cancel = default) =>
        _manager.Runtime.SynthesizeWavAsync(text, cancel);

    public Uri PublishClip(byte[] data, string contentType, TimeSpan lifetime) =>
        _manager.Runtime.PublishClip(data, contentType, lifetime);

    public T? GetPlugin<T>() where T : class => _manager.GetPlugin<T>();

    public TimeZoneInfo TimeZone => _manager.Runtime.TimeZone;

    public DateTimeOffset Now => TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TimeZone);
}
