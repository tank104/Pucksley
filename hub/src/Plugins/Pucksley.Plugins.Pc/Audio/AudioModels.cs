using Pucksley.Sdk;

namespace Pucksley.Plugins.Pc.Audio;

/// <summary>The default output device's master level.</summary>
internal sealed record MasterVolume(string DeviceName, int Volume, bool Muted);

/// <summary>An output that can be played through right now ("Speakers (Realtek(R) Audio)").</summary>
internal sealed record AudioOutput(string Id, string Name, bool IsDefault);

/// <summary>
/// One app's audio sessions on the default device, grouped (Chrome has several). Volume is the
/// app's slider in the volume mixer (0–100, relative to the master volume).
/// </summary>
internal sealed record AppAudio(string Key, string Name, string ProcessName, int Volume, bool Muted, bool Playing);

/// <summary>What a media app reports to the system (Windows' media flyout, a Linux desktop's media controls).</summary>
internal sealed record MediaInfo(string? Title, string? Artist, string? Album, string App, bool Playing);

/// <summary>The PC's mixer: master volume, outputs and per-app sessions (Core Audio on Windows, PipeWire or PulseAudio on Linux).</summary>
internal interface IPcAudio : IDisposable
{
    /// <summary>The master volume or mute changed, or a different output became the default.</summary>
    event Action? Changed;

    void Start();

    /// <summary>Null when there's no output device.</summary>
    MasterVolume? GetMaster();

    /// <summary>Sets whichever of volume (0–100) and mute is given; returns the new state.</summary>
    MasterVolume SetMaster(int? volume, bool? muted);

    IReadOnlyList<AudioOutput> GetOutputs();

    /// <summary>Makes the output the default for everything (on Windows: every role — console, multimedia, communications).</summary>
    void SetDefaultOutput(string deviceId);

    IReadOnlyList<AppAudio> GetApps();

    /// <summary>Applies to every session of the app with this <see cref="AppAudio.Key"/>; returns the new state.</summary>
    AppAudio? SetApp(string key, int? volume, bool? muted);
}

/// <summary>The PC's media sessions (Windows' System Media Transport Controls, MPRIS players on Linux).</summary>
internal interface IPcMedia : IDisposable
{
    /// <summary>The track, playing state or playing app changed.</summary>
    event Action? Changed;

    Task StartAsync(CancellationToken cancel);

    /// <summary>The last state read (kept fresh by session events); null when nothing has a media session.</summary>
    MediaInfo? Current { get; }

    /// <summary>Reads the state now.</summary>
    Task<MediaInfo?> ReadAsync(CancellationToken cancel);

    /// <summary>
    /// Play/Pause/Toggle/Next/Previous. Asks the app's media session first and falls back to the
    /// media keys. False when there was nothing to do (pause with nothing playing).
    /// </summary>
    Task<bool> SendAsync(MediaAction action, CancellationToken cancel);
}
