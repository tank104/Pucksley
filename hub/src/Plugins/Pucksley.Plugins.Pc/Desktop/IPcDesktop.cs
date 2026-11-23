using Microsoft.Extensions.Logging;
using Pucksley.Plugins.Pc.Screen;

namespace Pucksley.Plugins.Pc.Desktop;

/// <summary>
/// The PC's desktop as the "This PC" plugin needs it: what an operating system has to be able to
/// do for the pc_* tools. <see cref="PcControlPluginBase"/> holds the tools (what Claude sees, the
/// checks, the wording of the answers); this is the part that differs between Windows and Linux.
/// </summary>
/// <remarks>
/// Throw <see cref="Pucksley.Sdk.PluginException"/> with a message for the person when something
/// can't be done ("Typing needs wtype installed"). Calls may arrive concurrently.
/// </remarks>
internal interface IPcDesktop
{
    /// <summary>The operating system's name, for messages and Claude's instructions: "Windows", "Linux".</summary>
    string Os { get; }

    /// <summary>
    /// Tool descriptions that need other words on this system than the plugin's own, by tool name
    /// (the recycle bin is "the trash" on Linux). Tools not listed keep theirs.
    /// </summary>
    IReadOnlyDictionary<string, string> ToolDescriptions { get; }

    /// <summary>Something worth adding to the plugin's status line (which desktop it found, what's missing), or null.</summary>
    string? StatusNote { get; }

    // ───────────────────────── Apps, files, sites

    /// <summary>Every app someone can open by name; <see cref="AppIndex"/> cleans, caches and refreshes it.</summary>
    Task<IReadOnlyList<AppEntry>> LoadAppsAsync(ILogger log, CancellationToken cancel);

    /// <summary>The app someone means, or null (usually <see cref="AppIndex.Match"/> with this system's aliases).</summary>
    AppEntry? FindApp(string spoken, IReadOnlyList<AppEntry> apps);

    Task LaunchAsync(AppEntry app, CancellationToken cancel);

    /// <summary>Opens a web address, file or folder; returns what to say was opened ("youtube.com", "Downloads").</summary>
    Task<string> OpenAsync(string target, CancellationToken cancel);

    // ───────────────────────── Lock, sleep, shut down

    /// <summary>True when the screen is locked: it can't be captured or typed into then.</summary>
    Task<bool> IsLockedAsync(CancellationToken cancel);

    Task LockAsync(CancellationToken cancel);

    /// <summary>Sleeps the PC after <paramref name="delay"/>, so the hub (which runs on it) can finish speaking the reply first.</summary>
    void SleepAfter(TimeSpan delay, ILogger log);

    /// <summary>Starts a countdown to shutting down or restarting, which <see cref="CancelShutdownAsync"/> can stop.</summary>
    Task ScheduleShutdownAsync(bool restart, int minutes, CancellationToken cancel);

    /// <summary>False when nothing was counting down.</summary>
    Task<bool> CancelShutdownAsync(CancellationToken cancel);

    // ───────────────────────── Screen, clipboard, typing

    Task<Screenshot> CaptureAsync(ScreenArea area, CancellationToken cancel);

    Task<ClipboardContents> ReadClipboardAsync(CancellationToken cancel);

    Task WriteClipboardAsync(string text, CancellationToken cancel);

    /// <summary>Types into the window in front, as if on the keyboard.</summary>
    Task TypeAsync(string text, CancellationToken cancel);

    // ───────────────────────── Windows

    /// <summary>The window in front, or null for the bare desktop (or when this desktop won't say).</summary>
    Task<AppWindow?> ForegroundAsync(CancellationToken cancel);

    /// <summary>Windowed apps, front-most first.</summary>
    Task<IReadOnlyList<AppWindow>> WindowsAsync(CancellationToken cancel);

    /// <summary>Brings a window to the front; false when the system wouldn't.</summary>
    Task<bool> FocusAsync(AppWindow window, CancellationToken cancel);

    Task ShowDesktopAsync(CancellationToken cancel);

    // ───────────────────────── System

    /// <summary>One short paragraph of vital signs (see <see cref="SystemFacts"/>).</summary>
    Task<string> DescribeSystemAsync(CancellationToken cancel);

    /// <summary>Null when the system has never stored a choice.</summary>
    Task<bool?> IsDarkModeAsync(CancellationToken cancel);

    Task SetDarkModeAsync(bool on, CancellationToken cancel);

    /// <summary>Reads (both null) or sets the screen's brightness, exactly or by a step; returns the new level, 0–100.</summary>
    Task<int> BrightnessAsync(int? level, int? change, CancellationToken cancel);
}
