using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using Pucksley.Plugins.Windows.Common;
using Pucksley.Sdk;

namespace Pucksley.Plugins.Windows.Audio;

/// <summary>
/// The PC's mixer through Core Audio (NAudio): the default output's master volume and mute, the
/// active outputs, and per-app sessions. Follows the default output when it changes (headphones
/// plugged in, a Bluetooth headset connecting).
/// </summary>
/// <remarks>Call from thread-pool (MTA) threads; see <see cref="Blocking.RunAsync{T}"/>.</remarks>
internal sealed class CoreAudio : IPcAudio
{
    readonly ILogger _log;
    readonly object _gate = new();
    readonly DeviceWatcher _watcher;
    MMDeviceEnumerator? _enumerator;
    MMDevice? _device;
    string _deviceName = "";
    bool _disposed;

    public event Action? Changed;

    public CoreAudio(ILogger log)
    {
        _log = log;
        _watcher = new DeviceWatcher(this);
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_disposed || _enumerator is not null) return;
            _enumerator = new MMDeviceEnumerator();
            _enumerator.RegisterEndpointNotificationCallback(_watcher);
        }
        Bind();
    }

    /// <summary>(Re)attaches to the current default output.</summary>
    void Bind()
    {
        lock (_gate)
        {
            if (_disposed || _enumerator is null) return;
            ReleaseDevice();
            try
            {
                if (_enumerator.HasDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia))
                {
                    _device = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                    _deviceName = _device.FriendlyName;
                    _device.AudioEndpointVolume.OnVolumeNotification += OnVolumeNotification;
                }
            }
            catch (Exception e)
            {
                _log.LogWarning(e, "Couldn't open the default audio output");
                ReleaseDevice();
            }
        }
        Changed?.Invoke();
    }

    void ReleaseDevice()
    {
        if (_device is null) return;
        try { _device.AudioEndpointVolume.OnVolumeNotification -= OnVolumeNotification; }
        catch (Exception) { /* device already gone */ }
        _device.Dispose();
        _device = null;
        _deviceName = "";
    }

    // Core Audio calls this for every step of a slider drag; the plugin coalesces them
    void OnVolumeNotification(AudioVolumeNotificationData data) => Changed?.Invoke();

    public MasterVolume? GetMaster() => WithDevice(device =>
    {
        var endpoint = device.AudioEndpointVolume;
        return new MasterVolume(_deviceName, VolumeMath.ToPercent(endpoint.MasterVolumeLevelScalar), endpoint.Mute);
    });

    public MasterVolume SetMaster(int? volume, bool? muted) => WithDevice(device =>
    {
        var endpoint = device.AudioEndpointVolume;
        if (volume is int v) endpoint.MasterVolumeLevelScalar = VolumeMath.ToScalar(v);
        if (muted is bool m) endpoint.Mute = m;
        return new MasterVolume(_deviceName, VolumeMath.ToPercent(endpoint.MasterVolumeLevelScalar), endpoint.Mute);
    }) ?? throw new PluginException("This PC has no audio output.");

    /// <summary>
    /// Runs against the default device. If the device vanished mid-call (unplugged before the
    /// change notification arrived), re-binds and tries once more.
    /// </summary>
    MasterVolume? WithDevice(Func<MMDevice, MasterVolume> action)
    {
        try
        {
            lock (_gate) return _device is null ? null : action(_device);
        }
        catch (COMException e)
        {
            _log.LogDebug(e, "Audio device call failed; re-binding");
        }
        Bind();
        lock (_gate) return _device is null ? null : action(_device);
    }

    public IReadOnlyList<AudioOutput> GetOutputs()
    {
        lock (_gate)
        {
            var enumerator = _enumerator ?? throw new PluginException("PC audio hasn't started.");
            string? defaultId = null;
            if (enumerator.HasDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia))
            {
                using var current = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                defaultId = current.ID;
            }
            var outputs = new List<AudioOutput>();
            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                using (device) outputs.Add(new AudioOutput(device.ID, device.FriendlyName, device.ID == defaultId));
            }
            return outputs;
        }
    }

    public void SetDefaultOutput(string deviceId) => PolicyConfig.SetDefaultEndpoint(deviceId);

    public IReadOnlyList<AppAudio> GetApps() => Group(VisitSessions(null, null));

    public AppAudio? SetApp(string key, int? volume, bool? muted)
    {
        var sessions = VisitSessions(s => s.Key == key, v =>
        {
            if (volume is int x) v.Volume = VolumeMath.ToScalar(x);
            if (muted is bool m) v.Mute = m;
        });
        return Group(sessions.Where(s => s.Key == key)).FirstOrDefault();
    }

    sealed record SessionInfo(string Key, string Name, string ProcessName, int Volume, bool Muted, bool Active);

    static List<AppAudio> Group(IEnumerable<SessionInfo> sessions) =>
        sessions.GroupBy(s => s.Key)
            .Select(g => new AppAudio(g.Key, g.First().Name, g.First().ProcessName,
                g.Max(s => s.Volume), g.All(s => s.Muted), g.Any(s => s.Active)))
            .OrderByDescending(a => a.Playing)
            .ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>Reads every live session on the default device, applying <paramref name="apply"/> to the selected ones.</summary>
    List<SessionInfo> VisitSessions(Func<SessionInfo, bool>? select, Action<SimpleAudioVolume>? apply)
    {
        var result = new List<SessionInfo>();
        lock (_gate)
        {
            if (_device is null) return result;
            var manager = _device.AudioSessionManager;
            manager.RefreshSessions();
            var sessions = manager.Sessions;
            for (int i = 0; i < sessions.Count; i++)
            {
                using var session = sessions[i];
                try
                {
                    var state = session.State;
                    if (state == AudioSessionState.AudioSessionStateExpired) continue;
                    var info = Describe(session, state);
                    if (apply is not null && select is not null && select(info))
                    {
                        apply(session.SimpleAudioVolume);
                        info = Describe(session, state);
                    }
                    result.Add(info);
                }
                catch (COMException e)
                {
                    // the session ended while we looked at it
                    _log.LogDebug(e, "Skipped an audio session");
                }
            }
        }
        return result;
    }

    static SessionInfo Describe(AudioSessionControl session, AudioSessionState state)
    {
        var volume = session.SimpleAudioVolume;
        int level = VolumeMath.ToPercent(volume.Volume);
        bool muted = volume.Mute;
        bool active = state == AudioSessionState.AudioSessionStateActive;
        if (session.IsSystemSoundsSession) return new SessionInfo("system", "System sounds", "system", level, muted, active);
        var (processName, name) = AppNames.ForProcess((int)session.GetProcessID);
        return new SessionInfo(processName.ToLowerInvariant(), name, processName, level, muted, active);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            ReleaseDevice();
            if (_enumerator is not null)
            {
                try { _enumerator.UnregisterEndpointNotificationCallback(_watcher); }
                catch (Exception) { /* audio service gone */ }
                _enumerator.Dispose();
                _enumerator = null;
            }
        }
    }

    /// <summary>Core Audio's device notifications; only the default output changing matters.</summary>
    sealed class DeviceWatcher(CoreAudio owner) : IMMNotificationClient
    {
        // Never block in these callbacks (Core Audio's thread): hop to the pool
        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
        {
            if (flow == DataFlow.Render && role == Role.Multimedia) Task.Run(owner.Bind);
        }

        public void OnDeviceStateChanged(string deviceId, DeviceState newState) { }
        public void OnDeviceAdded(string pwstrDeviceId) { }
        public void OnDeviceRemoved(string deviceId) { }
        public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }
    }
}
