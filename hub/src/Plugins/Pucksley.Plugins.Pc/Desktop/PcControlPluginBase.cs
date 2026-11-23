using System.Text.Json;
using Microsoft.Extensions.Logging;
using Pucksley.Plugins.Pc.Common;
using Pucksley.Plugins.Pc.Screen;
using Pucksley.Sdk;

namespace Pucksley.Plugins.Pc.Desktop;

/// <summary>
/// The PC the hub runs on: open apps, files and sites; see the screen; use the clipboard; type;
/// switch windows; lock, sleep, shut down; dark mode and brightness; system info.
/// </summary>
/// <remarks>
/// The same on every operating system: the tools Claude sees, their checks and the wording of
/// their answers are here. Each system derives a plugin from this and hands it its desktop
/// (<see cref="IPcDesktop"/>), which does the work.
/// </remarks>
public abstract class PcControlPluginBase : PuckPlugin<NoSettings>
{
    static readonly TimeSpan ContextMaxAge = TimeSpan.FromSeconds(2);

    readonly IPcDesktop _desktop;
    readonly AppIndex _apps;
    readonly object _gate = new();
    (string? Text, DateTime At)? _context;
    IReadOnlyList<ToolDefinition>? _tools;

    internal PcControlPluginBase(IPcDesktop desktop, AppIndex? apps = null)
    {
        _desktop = desktop;
        _apps = apps ?? new AppIndex(cancel => desktop.LoadAppsAsync(Log, cancel));
    }

    public override string Id => "pc";
    public override string Name => "This PC";
    public override string Description => $"Open apps, files and websites on this {_desktop.Os} PC, look at its screen, use the clipboard, type into apps, and lock, sleep or shut it down.";
    public override string Icon => "pc";
    public override bool EnabledByDefault => true;

    public override string? Instructions =>
        $"The pc_* tools act on the {_desktop.Os} PC the hub runs on. pc_type_text types into whichever window is in front. " +
        "When asked about what's on the screen, take a pc_screenshot and look.";

    /// <summary>The apps by name, for the desktop's own use (naming a window's app).</summary>
    internal AppIndex Apps => _apps;

    public override Task StartAsync(CancellationToken cancel)
    {
        SetStatus(StatusLevel.Ok, Ready(null));
        // Build the app list now, so the first "open …" doesn't wait for it
        _ = Task.Run(async () =>
        {
            try
            {
                var apps = await _apps.GetAsync(CancellationToken.None).ConfigureAwait(false);
                SetStatus(StatusLevel.Ok, Ready(apps.Count));
            }
            catch (Exception e)
            {
                Log.LogWarning(e, "Couldn't list the PC's apps");
                SetStatus(StatusLevel.Warning, "Couldn't list the PC's apps");
            }
        }, CancellationToken.None);
        return Task.CompletedTask;
    }

    /// <summary>"Ready", "Ready: 212 apps found", "Ready: 87 apps found (Hyprland)".</summary>
    string Ready(int? apps)
    {
        string text = apps is int n ? $"Ready: {n} apps found" : "Ready";
        return _desktop.StatusNote is { Length: > 0 } note ? $"{text} ({note})" : text;
    }

    /// <summary>The tools, with the words this operating system needs where they differ.</summary>
    public override IReadOnlyList<ToolDefinition> GetTools() => _tools ??= base.GetTools()
        .Select(t => _desktop.ToolDescriptions.TryGetValue(t.Name, out var description)
            ? new ToolDefinition { Name = t.Name, Description = description, InputSchema = t.InputSchema, Handler = t.Handler, Confirm = t.Confirm, Icon = t.Icon }
            : t)
        .ToList();

    // ───────────────────────── Opening things

    [Tool("pc_open_app", "Open (start) an app on this PC by name, e.g. Outlook, Spotify, Calculator, Settings, Chrome.")]
    public async Task<ToolResult> OpenApp([Param("The app's name")] string name, CancellationToken cancel = default)
    {
        var apps = await _apps.GetAsync(cancel).ConfigureAwait(false);
        var app = _desktop.FindApp(name, apps) ?? throw new PluginException(AppIndex.NotFoundMessage(name, apps));
        await _desktop.LaunchAsync(app, cancel).ConfigureAwait(false);
        return ToolResult.Ok($"Opening {app.Name}.", Text.Activity($"Opening {app.Name}"));
    }

    [Tool("pc_open", "Open a web address, file or folder on this PC: \"youtube.com\", \"Downloads\", \"Documents\", \"the recycle bin\" or a full path. Use pc_open_app for apps.")]
    public async Task<ToolResult> Open([Param("A web address, a folder name like Downloads, or a full path")] string target, CancellationToken cancel = default)
    {
        string opened = await _desktop.OpenAsync(target, cancel).ConfigureAwait(false);
        return ToolResult.Ok($"Opened {opened}.", Text.Activity($"Opening {opened}"));
    }

    // ───────────────────────── Lock, sleep, shut down

    [Tool("pc_lock", "Lock this PC (shows the sign-in screen; nothing is closed).")]
    public async Task<ToolResult> Lock(CancellationToken cancel = default)
    {
        if (await _desktop.IsLockedAsync(cancel).ConfigureAwait(false)) return ToolResult.Ok("The PC is already locked.", "PC locked");
        await _desktop.LockAsync(cancel).ConfigureAwait(false);
        return ToolResult.Ok("Locked the PC.", "PC locked");
    }

    [Tool("pc_sleep", "Put this PC to sleep (in a few seconds, after you reply).", Confirm = true)]
    public ToolResult Sleep()
    {
        _desktop.SleepAfter(TimeSpan.FromSeconds(5), Log);
        return ToolResult.Ok("The PC will go to sleep in a few seconds.", "PC sleeping");
    }

    [Tool("pc_shutdown", "Shut down or restart this PC after a countdown (default 1 minute) that pc_cancel_shutdown can stop. " +
                         "Apps with unsaved work still get to ask before closing.", Confirm = true)]
    public async Task<ToolResult> Shutdown(
        [Param("true to restart instead of shutting down")] bool? restart = null,
        [Param("Minutes to wait first (default 1)", Min = 1, Max = 1440)] int? minutes = null,
        CancellationToken cancel = default)
    {
        bool reboot = restart ?? false;
        int wait = Math.Clamp(minutes ?? 1, 1, 1440);
        await _desktop.ScheduleShutdownAsync(reboot, wait, cancel).ConfigureAwait(false);
        string when = wait == 1 ? "1 minute" : $"{wait} minutes";
        string what = reboot ? "restart" : "shut down";
        return ToolResult.Ok($"The PC will {what} in {when}. Say \"cancel the shutdown\" to stop it.",
            Text.Activity($"{(reboot ? "Restart" : "Shutdown")} in {(wait == 1 ? "1 min" : $"{wait} min")}"));
    }

    [Tool("pc_cancel_shutdown", "Cancel a shutdown or restart that's counting down on this PC.")]
    public async Task<ToolResult> CancelShutdown(CancellationToken cancel = default) =>
        await _desktop.CancelShutdownAsync(cancel).ConfigureAwait(false)
            ? ToolResult.Ok("Cancelled the shutdown.", "Shutdown cancelled")
            : ToolResult.Ok("No shutdown or restart was counting down.", "No shutdown pending");

    // ───────────────────────── Screen, clipboard, typing

    [Tool("pc_screenshot", "Take a screenshot of this PC to see what's on screen (\"what's on my screen?\", \"read me that error\"). " +
                           "Default: the window in front, the sharpest view; all or primary show whole screens, scaled down so small text may be unreadable.")]
    public async Task<ToolResult> Screenshot(
        [Param("Which part to capture", Values = ["all", "primary", "active_window"])] string? screen = null,
        CancellationToken cancel = default)
    {
        var area = ParseArea(screen);
        if (await _desktop.IsLockedAsync(cancel).ConfigureAwait(false)) throw new PluginException("The PC is locked, so I can't see its screen.");
        var shot = await _desktop.CaptureAsync(area, cancel).ConfigureAwait(false);
        string sent = shot.Width != shot.SourceWidth ? $" (sent at {shot.Width}×{shot.Height})" : "";
        string window = shot.WindowTitle is { } title ? $" Active window: \"{Text.Clip(title, 120)}\"." : "";
        return new ToolResult
        {
            Text = $"Screenshot of {shot.Area}, {shot.SourceWidth}×{shot.SourceHeight}{sent}.{window}",
            Image = shot.Jpeg,
            ImageMediaType = "image/jpeg",
            Activity = "Screenshot",
        };
    }

    internal static ScreenArea ParseArea(string? screen) => ScreenAreas.Parse(screen);

    [Tool("pc_clipboard_get", "Read this PC's clipboard: its text, or whether it holds an image (which you'll see) or files.")]
    public async Task<ToolResult> ClipboardGet(CancellationToken cancel = default)
    {
        const int MaxChars = 6000;
        var contents = await _desktop.ReadClipboardAsync(cancel).ConfigureAwait(false);
        if (contents.Text is { } text)
        {
            if (text.Length == 0) return ToolResult.Ok("The clipboard is empty.", "Clipboard empty");
            return ToolResult.Ok(text.Length <= MaxChars
                ? $"Clipboard text:\n{text}"
                : $"Clipboard text (first {MaxChars} of {text.Length} characters):\n{text[..MaxChars]}", "Read clipboard");
        }
        if (contents.Files is { Count: > 0 } files)
            return ToolResult.Ok($"The clipboard holds {files.Count} file{(files.Count == 1 ? "" : "s")}: {Text.List(files.Select(f => Path.GetFileName(f.TrimEnd('\\', '/'))), 10)}.", "Read clipboard");
        if (contents.ImageJpeg is { } jpeg)
            return new ToolResult
            {
                Text = $"The clipboard holds an image, {contents.ImageWidth}×{contents.ImageHeight}.",
                Image = jpeg,
                ImageMediaType = "image/jpeg",
                Activity = "Read clipboard",
            };
        return ToolResult.Ok("The clipboard is empty.", "Clipboard empty");
    }

    [Tool("pc_clipboard_set", "Put text on this PC's clipboard, ready to paste.")]
    public async Task<ToolResult> ClipboardSet([Param("The text to copy")] string text, CancellationToken cancel = default)
    {
        if (string.IsNullOrEmpty(text)) throw new PluginException("There's no text to copy.");
        await _desktop.WriteClipboardAsync(text, cancel).ConfigureAwait(false);
        string what = text.Length <= 60 ? $"\"{text}\"" : $"{text.Length} characters";
        return ToolResult.Ok($"Copied {what} to the clipboard.", "Copied to clipboard");
    }

    [Tool("pc_type_text", "Type text into whichever window is in front on this PC, as if on the keyboard (dictation into any app). Line breaks press Enter.")]
    public async Task<ToolResult> TypeText([Param("The text to type")] string text, CancellationToken cancel = default)
    {
        if (string.IsNullOrEmpty(text)) throw new PluginException("There's no text to type.");
        if (text.Length > TypedText.MaxLength)
            throw new PluginException($"That's too long to type ({text.Length} characters; the limit is {TypedText.MaxLength}). Put it on the clipboard instead.");
        if (await _desktop.IsLockedAsync(cancel).ConfigureAwait(false)) throw new PluginException("The PC is locked, so I can't type into it.");
        var target = await ForegroundOrNullAsync(cancel).ConfigureAwait(false);
        await _desktop.TypeAsync(text, cancel).ConfigureAwait(false);
        string into = target is null ? "" : $" into {target.AppName}";
        return ToolResult.Ok($"Typed {text.Length} character{(text.Length == 1 ? "" : "s")}{into}.", Text.Activity($"Typed into {target?.AppName ?? "the PC"}"));
    }

    // ───────────────────────── Windows

    [Tool("pc_show_desktop", "Minimise every window on this PC to show the desktop.")]
    public async Task<ToolResult> ShowDesktop(CancellationToken cancel = default)
    {
        await _desktop.ShowDesktopAsync(cancel).ConfigureAwait(false);
        return ToolResult.Ok("Showing the desktop.", "Desktop");
    }

    [Tool("pc_focus_app", "Bring an app that's already open on this PC to the front, e.g. Outlook, Chrome, or words from a window's title.")]
    public async Task<ToolResult> FocusApp([Param("The app, or words from its window title")] string name, CancellationToken cancel = default)
    {
        var windows = await _desktop.WindowsAsync(cancel).ConfigureAwait(false);
        var window = WindowMatcher.Match(name, windows)
            ?? throw new PluginException(windows.Count == 0
                ? $"'{name}' isn't open, and no app windows are. pc_open_app can start it."
                : $"'{name}' isn't open. Open apps: {Text.List(windows.Select(w => w.AppName))}. (pc_open_app can start it.)");
        bool inFront = await _desktop.FocusAsync(window, cancel).ConfigureAwait(false);
        if (!inFront)
        {
            // A locked screen keeps the focus: that's the reason worth saying
            throw new PluginException(await _desktop.IsLockedAsync(cancel).ConfigureAwait(false)
                ? "The PC is locked, so I can't switch its windows."
                : $"{_desktop.Os} wouldn't bring {window.AppName} to the front.");
        }
        return ToolResult.Ok($"{window.AppName} is in front: \"{Text.Clip(window.Title, 80)}\".", Text.Activity($"{window.AppName} in front"));
    }

    [Tool("pc_running_apps", "List the apps with open windows on this PC, with their window titles (front-most first).")]
    public async Task<ToolResult> RunningApps(CancellationToken cancel = default)
    {
        var windows = await _desktop.WindowsAsync(cancel).ConfigureAwait(false);
        if (windows.Count == 0) return ToolResult.Ok("No app windows are open on the PC.", "No apps open");
        var apps = windows
            .GroupBy(w => w.AppName, StringComparer.OrdinalIgnoreCase)
            .Select(g => new { app = g.Key, windows = g.Select(w => Text.Clip(w.Title, 80)).Distinct().Take(5).ToArray() })
            .ToList();
        return ToolResult.Ok(JsonSerializer.Serialize(apps), Text.Activity($"{apps.Count} apps open"));
    }

    // ───────────────────────── System

    [Tool("pc_system_info", "This PC's vital signs: CPU and memory use, free disk space, battery, uptime, IP address and operating system version.")]
    public async Task<ToolResult> SystemInformation(CancellationToken cancel = default) =>
        ToolResult.Ok(await _desktop.DescribeSystemAsync(cancel).ConfigureAwait(false), "PC info");

    [Tool("pc_dark_mode", "Turn dark mode on or off on this PC (apps, and the websites that follow it).")]
    public async Task<ToolResult> DarkMode([Param("true for dark mode, false for light mode")] bool on, CancellationToken cancel = default)
    {
        string mode = on ? "Dark" : "Light";
        if (await _desktop.IsDarkModeAsync(cancel).ConfigureAwait(false) == on) return ToolResult.Ok($"{mode} mode is already on.", $"{mode} mode on");
        await _desktop.SetDarkModeAsync(on, cancel).ConfigureAwait(false);
        return ToolResult.Ok($"{mode} mode is on.", $"{mode} mode on");
    }

    [Tool("pc_brightness", "Get or set the screen brightness (0-100), exactly or by a step. Works on laptops and other built-in screens, not most desktop monitors.")]
    public async Task<ToolResult> Brightness(
        [Param("Brightness 0-100", Min = 0, Max = 100)] int? level = null,
        [Param("Relative change, e.g. -20")] int? change = null,
        CancellationToken cancel = default)
    {
        int now = await _desktop.BrightnessAsync(level, change, cancel).ConfigureAwait(false);
        return ToolResult.Ok(level is null && change is null ? $"Screen brightness is {now}%." : $"Screen brightness is now {now}%.", $"Brightness {now}%");
    }

    // ───────────────────────── Context

    /// <summary><c>PC: Outlook is in front.</c> or <c>PC: locked.</c> (cached briefly: it runs before every turn).</summary>
    public override async Task<string?> GetContextAsync(ToolCaller caller, CancellationToken cancel)
    {
        lock (_gate)
        {
            if (_context is { } cached && DateTime.UtcNow - cached.At < ContextMaxAge) return cached.Text;
        }
        string? text = null;
        try
        {
            if (await _desktop.IsLockedAsync(cancel).ConfigureAwait(false)) text = "PC: locked.";
            else if (await _desktop.ForegroundAsync(cancel).ConfigureAwait(false) is { } window) text = $"PC: {Text.Clip(window.AppName, 40)} is in front.";
        }
        catch (Exception e) when (e is not OperationCanceledException || !cancel.IsCancellationRequested)
        {
            Log.LogDebug(e, "Couldn't read the foreground window");
        }
        lock (_gate) _context = (text, DateTime.UtcNow);
        return text;
    }

    /// <summary>Who the typing goes to is only for the answer: not knowing mustn't stop the typing.</summary>
    async Task<AppWindow?> ForegroundOrNullAsync(CancellationToken cancel)
    {
        try
        {
            return await _desktop.ForegroundAsync(cancel).ConfigureAwait(false);
        }
        catch (PluginException)
        {
            return null;
        }
    }
}
