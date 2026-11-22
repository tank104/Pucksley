using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Pucksley.Plugins.Pc.Audio;
using Pucksley.Sdk;
using Pucksley.TestSupport;

namespace Pucksley.Plugins.Pc.Tests;

/// <summary>The shared PC audio plugin as an operating system would derive it, over the fakes below.</summary>
internal sealed class TestPcAudioPlugin(IPcAudio audio, IPcMedia media, string os = "Windows", string mediaSessions = "Windows media sessions")
    : PcAudioPluginBase(os, mediaSessions, _ => audio, _ => media);

/// <summary>A mixer in memory: Realtek speakers (default), Sony headphones, a Dell monitor; Spotify, Chrome, Teams.</summary>
internal sealed class FakePcAudio : IPcAudio
{
    readonly object _gate = new();

    public event Action? Changed;

    public MasterVolume? Master { get; set; } = new("Speakers (Realtek(R) Audio)", 30, false);

    public List<AudioOutput> Outputs { get; } =
    [
        new("id-speakers", "Speakers (Realtek(R) Audio)", true),
        new("id-headphones", "Headphones (WH-1000XM4 Stereo)", false),
        new("id-dell", "DELL U2720Q (NVIDIA High Definition Audio)", false),
    ];

    public List<AppAudio> Apps { get; } =
    [
        new("spotify", "Spotify", "Spotify", 100, false, true),
        new("chrome", "Google Chrome", "chrome", 80, false, false),
        new("ms-teams", "Microsoft Teams", "ms-teams", 50, true, false),
    ];

    public ConcurrentQueue<string> Calls { get; } = new();
    public bool Started { get; private set; }
    public bool Disposed { get; private set; }

    public void Start() => Started = true;

    public MasterVolume? GetMaster()
    {
        lock (_gate) return Master;
    }

    public MasterVolume SetMaster(int? volume, bool? muted)
    {
        Calls.Enqueue($"master {volume?.ToString() ?? "-"} {muted?.ToString() ?? "-"}");
        lock (_gate)
        {
            Master = Master! with { Volume = volume ?? Master.Volume, Muted = muted ?? Master.Muted };
            return Master;
        }
    }

    public IReadOnlyList<AudioOutput> GetOutputs()
    {
        lock (_gate) return Outputs.ToList();
    }

    public void SetDefaultOutput(string deviceId)
    {
        Calls.Enqueue($"default {deviceId}");
        lock (_gate)
        {
            for (int i = 0; i < Outputs.Count; i++) Outputs[i] = Outputs[i] with { IsDefault = Outputs[i].Id == deviceId };
        }
    }

    public IReadOnlyList<AppAudio> GetApps()
    {
        lock (_gate) return Apps.ToList();
    }

    public AppAudio? SetApp(string key, int? volume, bool? muted)
    {
        Calls.Enqueue($"app {key} {volume?.ToString() ?? "-"} {muted?.ToString() ?? "-"}");
        lock (_gate)
        {
            int i = Apps.FindIndex(a => a.Key == key);
            if (i < 0) return null;
            Apps[i] = Apps[i] with { Volume = volume ?? Apps[i].Volume, Muted = muted ?? Apps[i].Muted };
            return Apps[i];
        }
    }

    /// <summary>As if someone pressed the keyboard's volume keys.</summary>
    public void ChangeOutsideTheHub(int volume, bool muted = false)
    {
        lock (_gate) Master = Master! with { Volume = volume, Muted = muted };
        Changed?.Invoke();
    }

    public void Dispose() => Disposed = true;
}

/// <summary>Media sessions in memory: Spotify playing "Teardrop".</summary>
internal sealed class FakePcMedia : IPcMedia
{
    public event Action? Changed;

    public MediaInfo? Current { get; set; } = new("Teardrop", "Massive Attack", "Mezzanine", "Spotify", true);

    /// <summary>What "next" moves to.</summary>
    public MediaInfo? NextTrack { get; set; } = new("Angel", "Massive Attack", "Mezzanine", "Spotify", true);

    public ConcurrentQueue<MediaAction> Sent { get; } = new();

    public Task StartAsync(CancellationToken cancel) => Task.CompletedTask;

    public Task<MediaInfo?> ReadAsync(CancellationToken cancel) => Task.FromResult(Current);

    public Task<bool> SendAsync(MediaAction action, CancellationToken cancel)
    {
        Sent.Enqueue(action);
        if (action == MediaAction.Pause && Current?.Playing != true) return Task.FromResult(false);
        if (Current is not null)
        {
            Current = action switch
            {
                MediaAction.Next => NextTrack ?? Current,
                MediaAction.Pause => Current with { Playing = false },
                MediaAction.Play => Current with { Playing = true },
                MediaAction.Toggle => Current with { Playing = !Current.Playing },
                _ => Current,
            };
        }
        return Task.FromResult(true);
    }

    public void RaiseChanged() => Changed?.Invoke();

    public void Dispose() { }
}

/// <summary>
/// <see cref="FakePluginContext"/> plus a record of control-value and now-playing pushes, which the
/// shared fake ignores.
/// </summary>
internal sealed class RecordingContext(FakePluginContext inner) : IPluginContext
{
    int _controlsChanged;

    public FakePluginContext Inner => inner;
    public ConcurrentQueue<(string Id, double? Value)> ControlValues { get; } = new();
    public ConcurrentQueue<string> NowPlayingChanges { get; } = new();
    public int ControlsChangedCount => Volatile.Read(ref _controlsChanged);

    public void ControlValueChanged(string controlId, double? value = null, bool? on = null, string? text = null) => ControlValues.Enqueue((controlId, value));
    public void NowPlayingChanged(string sourceId) => NowPlayingChanges.Enqueue(sourceId);
    public void ControlsChanged() => Interlocked.Increment(ref _controlsChanged);

    public ILogger Log => inner.Log;
    public HttpClient Http => inner.Http;
    public string DataDirectory => inner.DataDirectory;
    public IHubInfo Hub => inner.Hub;
    public TimeZoneInfo TimeZone => inner.TimeZone;
    public DateTimeOffset Now => inner.Now;
    public void SaveSettings(object settings) => inner.SaveSettings(settings);
    public void StatusChanged() => inner.StatusChanged();
    public void ToolsChanged() => inner.ToolsChanged();
    public Task NotifyAsync(string text, NotifyKind kind = NotifyKind.Info, bool chime = false, string? puckId = null) => inner.NotifyAsync(text, kind, chime, puckId);
    public Task SayAsync(string text, string? puckId = null, CancellationToken cancel = default) => inner.SayAsync(text, puckId, cancel);
    public Task<bool> ConfirmAsync(string question, ToolCaller caller, TimeSpan? timeout = null, CancellationToken cancel = default) => inner.ConfirmAsync(question, caller, timeout, cancel);
    public void ShowTimer(string timerId, DateTimeOffset? endsAt, string? label, string? puckId = null) => inner.ShowTimer(timerId, endsAt, label, puckId);
    public Task<byte[]> SynthesizeWavAsync(string text, CancellationToken cancel = default) => inner.SynthesizeWavAsync(text, cancel);
    public Uri PublishClip(byte[] data, string contentType, TimeSpan lifetime) => inner.PublishClip(data, contentType, lifetime);
    public T? GetPlugin<T>() where T : class => inner.GetPlugin<T>();
}
