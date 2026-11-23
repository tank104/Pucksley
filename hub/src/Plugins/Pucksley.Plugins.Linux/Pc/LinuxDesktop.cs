using Microsoft.Extensions.Logging;
using Pucksley.Plugins.Linux.Audio;
using Pucksley.Plugins.Linux.Common;
using Pucksley.Plugins.Linux.Pc.Windows;
using Pucksley.Sdk;

namespace Pucksley.Plugins.Linux.Pc;

/// <summary>
/// Linux behind the "This PC" tools. Linux isn't one desktop, so this first works out which one
/// the hub is in (<see cref="DesktopSession"/>) and picks the way to its windows; the rest leans on
/// what nearly every desktop has (xdg-open, .desktop files, logind, the session bus) and on a few
/// small programs, saying which one to install when it's missing.
/// </summary>
internal sealed class LinuxDesktop : IPcDesktop
{
    readonly DesktopSession _session;
    readonly LinuxApps _apps = new();
    readonly IWindowManager _windows;
    readonly Power _power;
    readonly ScreenCapture _screen;
    readonly ClipboardAccess _clipboard;
    readonly TextTyper _typer;
    readonly Display _display;
    ILogger? _log;

    public LinuxDesktop() : this(DesktopSession.Current) { }

    internal LinuxDesktop(DesktopSession session)
    {
        _session = session;
        _windows = WindowsFor(session, NameApp);
        _power = new Power(session, _windows);
        _screen = new ScreenCapture(session, _windows);
        _clipboard = new ClipboardAccess(session);
        _typer = new TextTyper(session);
        _display = new Display(session);
    }

    /// <summary>The way to this desktop's windows.</summary>
    internal static IWindowManager WindowsFor(DesktopSession session, AppNamer namer) => session.Compositor switch
    {
        Compositor.Hyprland => new HyprlandWindows(new Hyprctl(), namer),
        Compositor.Sway => new SwayWindows(session.Wayland ? "swaymsg" : "i3-msg", namer),
        // Every X11 window manager answers wmctrl, GNOME's and KDE's included
        _ when session.X11 => new X11Windows(namer),
        _ => new ClosedWindows(session),
    };

    public string Os => "Linux";

    public IReadOnlyDictionary<string, string> ToolDescriptions { get; } = new Dictionary<string, string>
    {
        ["pc_open"] = "Open a web address, file or folder on this PC: \"youtube.com\", \"Downloads\", \"Documents\", \"the trash\" or a full path. Use pc_open_app for apps.",
        ["pc_shutdown"] = "Shut down or restart this PC after a countdown (default 1 minute) that pc_cancel_shutdown can stop. " +
                          "Unsaved work in open apps may be lost.",
        ["pc_show_desktop"] = "Show this PC's bare desktop: every window is minimised, or on a tiling desktop (Hyprland) an empty workspace is shown.",
        ["pc_brightness"] = "Get or set the screen brightness (0-100), exactly or by a step. Works on laptop screens, and on desktop monitors that take DDC commands.",
    };

    public string? StatusNote => _session.Name;

    // ───────────────────────── Apps, files, sites

    public Task<IReadOnlyList<AppEntry>> LoadAppsAsync(ILogger log, CancellationToken cancel)
    {
        _log = log;
        return _apps.LoadAsync(cancel);
    }

    public AppEntry? FindApp(string spoken, IReadOnlyList<AppEntry> apps) => _apps.Find(spoken, apps);

    public Task LaunchAsync(AppEntry app, CancellationToken cancel) => _apps.LaunchAsync(app, cancel);

    public async Task<string> OpenAsync(string target, CancellationToken cancel)
    {
        var plan = OpenTarget.Resolve(target, File.Exists, Directory.Exists, OpenTarget.IsExecutable, OpenTarget.KnownFolders(),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        await OpenTarget.OpenAsync(plan, cancel).ConfigureAwait(false);
        return plan.Description;
    }

    // ───────────────────────── Lock, sleep, shut down

    public Task<bool> IsLockedAsync(CancellationToken cancel) => _power.IsLockedAsync(cancel);

    public Task LockAsync(CancellationToken cancel) => _power.LockAsync(cancel);

    public void SleepAfter(TimeSpan delay, ILogger log) => _power.SleepAfter(delay, log);

    public Task ScheduleShutdownAsync(bool restart, int minutes, CancellationToken cancel) =>
        _power.ScheduleShutdownAsync(restart, minutes, _log ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, cancel);

    public Task<bool> CancelShutdownAsync(CancellationToken cancel) => _power.CancelShutdownAsync(cancel);

    // ───────────────────────── Screen, clipboard, typing

    public Task<Screenshot> CaptureAsync(ScreenArea area, CancellationToken cancel) => _screen.CaptureAsync(area, cancel);

    public Task<ClipboardContents> ReadClipboardAsync(CancellationToken cancel) => _clipboard.ReadAsync(cancel);

    public Task WriteClipboardAsync(string text, CancellationToken cancel) => _clipboard.WriteAsync(text, cancel);

    public Task TypeAsync(string text, CancellationToken cancel) => _typer.TypeAsync(text, cancel);

    // ───────────────────────── Windows

    public Task<AppWindow?> ForegroundAsync(CancellationToken cancel) => _windows.ForegroundAsync(cancel);

    public Task<IReadOnlyList<AppWindow>> WindowsAsync(CancellationToken cancel) => _windows.WindowsAsync(cancel);

    public Task<bool> FocusAsync(AppWindow window, CancellationToken cancel) => _windows.FocusAsync(window, cancel);

    public Task ShowDesktopAsync(CancellationToken cancel) => _windows.ShowDesktopAsync(cancel);

    /// <summary>
    /// A window's app, from the class it announces: the name its launcher entry gives it ("Files"
    /// for org.gnome.Nautilus), else a tidied form of the class; and its program, from /proc.
    /// </summary>
    (string ProcessName, string AppName) NameApp(string windowClass, int processId)
    {
        string process = ProcessName(processId) ?? windowClass;
        string app = _apps.NameForWindowClass(windowClass) ?? FriendlyClass(windowClass, process);
        return (process.Length > 0 ? process : app, app);
    }

    static string? ProcessName(int processId)
    {
        if (processId <= 0) return null;
        try
        {
            return File.ReadAllText($"/proc/{processId}/comm").Trim() is { Length: > 0 } name ? name : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// "com.mitchellh.ghostty" → "Ghostty"; "google-chrome" → "Google Chrome"; a browser's web app
    /// ("chrome-web.whatsapp.com__-Default") → the site ("Whatsapp").
    /// </summary>
    internal static string FriendlyClass(string windowClass, string process)
    {
        string name = windowClass.Trim();
        if (name.Length == 0) return PulseNames.Friendly(process);
        if (WebAppSite(name) is { } site) return site;
        // A reverse-domain id: its last part is the app
        var parts = name.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 3 && !name.Contains(' ')) name = parts[^1];
        return PulseNames.Friendly(name);
    }

    /// <summary>
    /// Chromium-family browsers give a site installed as an app the class "browser-host__path-Profile".
    /// The site's own name is the part of the host that isn't "www" or the ending: "Teams" for
    /// teams.cloud.microsoft, "Whatsapp" for web.whatsapp.com.
    /// </summary>
    internal static string? WebAppSite(string windowClass)
    {
        int dash = windowClass.IndexOf('-'), split = windowClass.IndexOf("__", StringComparison.Ordinal);
        if (dash <= 0 || split <= dash || windowClass[..dash] is not ("chrome" or "chromium" or "msedge" or "brave" or "vivaldi" or "opera")) return null;
        var labels = windowClass[(dash + 1)..split].Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (labels.Length < 2) return null;
        // Drop the ending, then the fillers people don't say
        var words = labels[..^1].Where(l => l is not ("www" or "web" or "app" or "m" or "cloud" or "co" or "com")).ToArray();
        string site = words.Length > 0 ? words[0] : labels[0];
        return char.ToUpperInvariant(site[0]) + site[1..];
    }

    // ───────────────────────── System

    public Task<string> DescribeSystemAsync(CancellationToken cancel) => SystemInfo.DescribeAsync(cancel);

    public Task<bool?> IsDarkModeAsync(CancellationToken cancel) => _display.IsDarkModeAsync(cancel);

    public Task SetDarkModeAsync(bool on, CancellationToken cancel) => _display.SetDarkModeAsync(on, cancel);

    public Task<int> BrightnessAsync(int? level, int? change, CancellationToken cancel) => _display.BrightnessAsync(level, change, cancel);
}
