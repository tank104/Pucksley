using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Pucksley.Sdk;
using Pucksley.TestSupport;

namespace Pucksley.Plugins.Tests;

/// <summary>
/// Wraps <see cref="FakePluginContext"/> (which records texts only) to also record which puck each
/// banner, speech and timer went to, with what kind and chime.
/// </summary>
internal sealed class PuckRecordingContext(FakePluginContext inner) : IPluginContext
{
    public FakePluginContext Inner => inner;
    public ConcurrentQueue<(string Text, NotifyKind Kind, bool Chime, string? PuckId)> Notified { get; } = new();
    public ConcurrentQueue<(string Text, string? PuckId)> Said { get; } = new();
    public ConcurrentQueue<(string Id, DateTimeOffset? EndsAt, string? Label, string? PuckId)> Shown { get; } = new();

    public ILogger Log => inner.Log;
    public HttpClient Http => inner.Http;
    public string DataDirectory => inner.DataDirectory;
    public IHubInfo Hub => inner.Hub;
    public TimeZoneInfo TimeZone => inner.TimeZone;
    public DateTimeOffset Now => inner.Now;

    public void SaveSettings(object settings) => inner.SaveSettings(settings);
    public void StatusChanged() => inner.StatusChanged();
    public void ToolsChanged() => inner.ToolsChanged();
    public void ControlsChanged() => inner.ControlsChanged();
    public void ControlValueChanged(string controlId, double? value = null, bool? on = null, string? text = null) => inner.ControlValueChanged(controlId, value, on, text);
    public void NowPlayingChanged(string sourceId) => inner.NowPlayingChanged(sourceId);

    public Task NotifyAsync(string text, NotifyKind kind = NotifyKind.Info, bool chime = false, string? puckId = null)
    {
        Notified.Enqueue((text, kind, chime, puckId));
        return inner.NotifyAsync(text, kind, chime, puckId);
    }

    public Task SayAsync(string text, string? puckId = null, CancellationToken cancel = default)
    {
        Said.Enqueue((text, puckId));
        return inner.SayAsync(text, puckId, cancel);
    }

    public void ShowTimer(string timerId, DateTimeOffset? endsAt, string? label, string? puckId = null)
    {
        Shown.Enqueue((timerId, endsAt, label, puckId));
        inner.ShowTimer(timerId, endsAt, label, puckId);
    }

    public Task<bool> ConfirmAsync(string question, ToolCaller caller, TimeSpan? timeout = null, CancellationToken cancel = default) =>
        inner.ConfirmAsync(question, caller, timeout, cancel);

    public Task<byte[]> SynthesizeWavAsync(string text, CancellationToken cancel = default) => inner.SynthesizeWavAsync(text, cancel);
    public Uri PublishClip(byte[] data, string contentType, TimeSpan lifetime) => inner.PublishClip(data, contentType, lifetime);
    public T? GetPlugin<T>() where T : class => inner.GetPlugin<T>();
}
