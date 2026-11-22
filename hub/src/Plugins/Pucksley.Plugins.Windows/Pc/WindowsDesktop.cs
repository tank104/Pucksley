using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Pucksley.Plugins.Windows.Common;
using Pucksley.Sdk;

namespace Pucksley.Plugins.Windows.Pc;

/// <summary>
/// Windows behind the "This PC" tools: each call goes to the class that knows that corner of
/// Windows (the Start Menu, the shell, Core windows, power, the clipboard), off the caller's thread
/// and with a timeout where Windows could keep it waiting.
/// </summary>
internal sealed class WindowsDesktop : IPcDesktop
{
    static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(10);

    public string Os => "Windows";

    public IReadOnlyDictionary<string, string> ToolDescriptions { get; } = new Dictionary<string, string>
    {
        ["pc_system_info"] = "This PC's vital signs: CPU and memory use, free disk space, battery, uptime, IP address and Windows version.",
        ["pc_dark_mode"] = "Turn Windows dark mode on or off (apps and taskbar).",
    };

    public string? StatusNote => null;

    // ───────────────────────── Apps, files, sites

    public Task<IReadOnlyList<AppEntry>> LoadAppsAsync(ILogger log, CancellationToken cancel) => AppSources.LoadAsync(log, cancel);

    public AppEntry? FindApp(string spoken, IReadOnlyList<AppEntry> apps) => AppSources.Match(spoken, apps);

    public Task LaunchAsync(AppEntry app, CancellationToken cancel) =>
        Blocking.RunAsync(() => Start(AppSources.LaunchInfo(app), app.Name), CallTimeout, $"Opening {app.Name}", cancel);

    public async Task<string> OpenAsync(string target, CancellationToken cancel)
    {
        // Off the caller's thread: checking a path on an unreachable network share can take a long time
        var plan = await Blocking.RunAsync(() =>
        {
            var p = OpenTarget.Resolve(target, File.Exists, Directory.Exists, OpenTarget.KnownFolders());
            Start(p.StartInfo, p.Description);
            return p;
        }, CallTimeout, $"Opening {Text.Clip(target, 40)}", cancel).ConfigureAwait(false);
        return plan.Description;
    }

    static void Start(ProcessStartInfo info, string what)
    {
        try
        {
            using var process = Process.Start(info);
        }
        catch (Win32Exception e)
        {
            throw new PluginException($"Windows couldn't open {what} ({e.Message}).");
        }
    }

    // ───────────────────────── Lock, sleep, shut down

    public Task<bool> IsLockedAsync(CancellationToken cancel) => Task.FromResult(SessionState.IsLocked());

    public Task LockAsync(CancellationToken cancel)
    {
        Power.Lock();
        return Task.CompletedTask;
    }

    public void SleepAfter(TimeSpan delay, ILogger log) => Power.SleepAfter(delay, log);

    public Task ScheduleShutdownAsync(bool restart, int minutes, CancellationToken cancel)
    {
        Power.ScheduleShutdown(restart, minutes);
        return Task.CompletedTask;
    }

    public Task<bool> CancelShutdownAsync(CancellationToken cancel) => Task.FromResult(Power.CancelShutdown());

    // ───────────────────────── Screen, clipboard, typing

    public Task<Screenshot> CaptureAsync(ScreenArea area, CancellationToken cancel) =>
        Blocking.RunAsync(() => ScreenCapture.Capture(area), CallTimeout, "The screen capture", cancel);

    public Task<ClipboardContents> ReadClipboardAsync(CancellationToken cancel) => ClipboardAccess.ReadAsync(cancel);

    public Task WriteClipboardAsync(string text, CancellationToken cancel) => ClipboardAccess.WriteAsync(text, cancel);

    public Task TypeAsync(string text, CancellationToken cancel) =>
        Blocking.RunAsync(() => TextTyper.Type(text, cancel), TimeSpan.FromSeconds(60), "Typing", cancel);

    // ───────────────────────── Windows

    public Task<AppWindow?> ForegroundAsync(CancellationToken cancel) => Task.FromResult(WindowList.Foreground());

    public async Task<IReadOnlyList<AppWindow>> WindowsAsync(CancellationToken cancel) =>
        await Blocking.RunAsync(WindowList.AppWindows, CallTimeout, "Windows", cancel).ConfigureAwait(false);

    public Task<bool> FocusAsync(AppWindow window, CancellationToken cancel) =>
        Blocking.RunAsync(() => WindowList.BringToFront(window.Handle), CallTimeout, "Windows", cancel);

    public Task ShowDesktopAsync(CancellationToken cancel)
    {
        WindowList.MinimizeAll();
        return Task.CompletedTask;
    }

    // ───────────────────────── System

    public Task<string> DescribeSystemAsync(CancellationToken cancel) => SystemInfo.DescribeAsync(cancel);

    public Task<bool?> IsDarkModeAsync(CancellationToken cancel) => Task.FromResult(Display.IsDarkMode());

    public Task SetDarkModeAsync(bool on, CancellationToken cancel) =>
        Blocking.RunAsync(() => Display.SetDarkMode(on), CallTimeout, "Windows", cancel);

    public Task<int> BrightnessAsync(int? level, int? change, CancellationToken cancel) => Display.BrightnessAsync(level, change, cancel);
}
