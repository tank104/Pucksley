using Microsoft.Extensions.Logging;

namespace Pucksley.Sdk;

/// <summary>What the hub offers a plugin.</summary>
public interface IPluginContext
{
    ILogger Log { get; }

    /// <summary>A shared client (30 s timeout, follows redirects). Don't dispose it. Create your own
    /// <see cref="HttpClient"/> if you need cookies, client certificates or self-signed TLS.</summary>
    HttpClient Http { get; }

    /// <summary>A private folder for this plugin (tokens, caches). Created on first use.</summary>
    string DataDirectory { get; }

    IHubInfo Hub { get; }

    /// <summary>Persist the plugin's settings object (after the plugin changed it, e.g. a new OAuth token).</summary>
    void SaveSettings(object settings);

    /// <summary>The plugin's status changed (re-read <see cref="IPlugin.Status"/>).</summary>
    void StatusChanged();

    /// <summary>The plugin's tool list changed (re-read <see cref="IPlugin.GetTools"/>).</summary>
    void ToolsChanged();

    /// <summary>The plugin's controls changed (re-read <see cref="IPlugin.GetControls"/> and send them to the pucks).</summary>
    void ControlsChanged();

    /// <summary>One control's value changed; cheaper than <see cref="ControlsChanged"/>.</summary>
    void ControlValueChanged(string controlId, double? value = null, bool? on = null, string? text = null);

    /// <summary>A media source's now-playing state changed (pushes it to pucks showing that source).</summary>
    void NowPlayingChanged(string sourceId);

    /// <summary>A banner on the puck(s). <paramref name="puckId"/> null = every puck.</summary>
    Task NotifyAsync(string text, NotifyKind kind = NotifyKind.Info, bool chime = false, string? puckId = null);

    /// <summary>Speak on the puck(s) (reminders, alerts). <paramref name="puckId"/> null = every puck.</summary>
    Task SayAsync(string text, string? puckId = null, CancellationToken cancel = default);

    /// <summary>
    /// Ask the person on the puck to confirm (turn for yes/no, tap). False when they say no, nobody
    /// answers within <paramref name="timeout"/> (default 30 s), or no puck is connected.
    /// </summary>
    Task<bool> ConfirmAsync(string question, ToolCaller caller, TimeSpan? timeout = null, CancellationToken cancel = default);

    /// <summary>
    /// Show a running timer on the puck(s) (countdown on the Timer face), or remove it when
    /// <paramref name="endsAt"/> is null. <paramref name="puckId"/> null = every puck.
    /// </summary>
    void ShowTimer(string timerId, DateTimeOffset? endsAt, string? label, string? puckId = null);

    /// <summary>Text to speech with the hub's voice, as a 16-bit mono WAV file (for announcements on speakers).</summary>
    Task<byte[]> SynthesizeWavAsync(string text, CancellationToken cancel = default);

    /// <summary>
    /// Serve bytes at a URL other devices on the LAN can fetch (e.g. a Sonos speaker playing an
    /// announcement). Expires after <paramref name="lifetime"/>.
    /// </summary>
    Uri PublishClip(byte[] data, string contentType, TimeSpan lifetime);

    /// <summary>Another enabled plugin by type or interface, e.g. Spotify asking for Sonos. Null if not loaded/enabled.</summary>
    T? GetPlugin<T>() where T : class;

    /// <summary>The household's time zone (for "at 5 pm").</summary>
    TimeZoneInfo TimeZone { get; }

    /// <summary>Now, in the household's time zone.</summary>
    DateTimeOffset Now { get; }
}

/// <summary>Facts about the hub and the household.</summary>
public interface IHubInfo
{
    /// <summary>The hub's name (the machine's name unless changed).</summary>
    string Name { get; }

    /// <summary>The hub as other LAN devices reach it, e.g. <c>http://192.168.1.10:8765/</c>.</summary>
    Uri LanUrl { get; }

    /// <summary><c>http://127.0.0.1:8765/</c> — for OAuth redirect URIs (Spotify requires a loopback IP).</summary>
    Uri LoopbackUrl { get; }

    /// <summary>Where the household is, if set (for weather, sunrise…).</summary>
    double? Latitude { get; }

    double? Longitude { get; }

    /// <summary>A place name, e.g. "Wellington, NZ".</summary>
    string? Location { get; }

    /// <summary>True when the hub is running on Windows (and so can control this PC).</summary>
    bool IsWindows { get; }
}

public enum NotifyKind { Info, Ai, Alert }
