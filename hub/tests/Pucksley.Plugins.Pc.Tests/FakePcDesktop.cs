using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Pucksley.Plugins.Pc.Desktop;
using Pucksley.Plugins.Pc.Screen;
using Pucksley.Sdk;

namespace Pucksley.Plugins.Pc.Tests;

/// <summary>
/// A desktop in memory: Outlook, Chrome and a spreadsheet open, a few apps installed, a clipboard,
/// and a record of everything it was asked to do.
/// </summary>
internal sealed class FakePcDesktop : IPcDesktop
{
    public string Os { get; set; } = "Windows";
    public Dictionary<string, string> Descriptions { get; } = [];
    public IReadOnlyDictionary<string, string> ToolDescriptions => Descriptions;
    public string? StatusNote { get; set; }

    public ConcurrentQueue<string> Calls { get; } = new();

    public List<AppEntry> Installed { get; } =
    [
        new("Outlook", "outlook.lnk", AppKind.Shortcut),
        new("Google Chrome", "chrome.lnk", AppKind.Shortcut),
        new("Calculator", "calc!App", AppKind.AppsFolder),
        new("Visual Studio Code", "code.lnk", AppKind.Shortcut),
    ];

    public IReadOnlyDictionary<string, string[]> Aliases { get; } = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase) { ["vs code"] = ["Visual Studio Code"] };

    public List<AppWindow> Open { get; } =
    [
        new(1, "Inbox - me@example.com - Outlook", 10, "olk", "Outlook"),
        new(2, "YouTube - Google Chrome", 11, "chrome", "Google Chrome"),
        new(3, "Budget 2026.xlsx - Excel", 12, "EXCEL", "Excel"),
    ];

    public bool Locked { get; set; }
    public bool FocusWorks { get; set; } = true;
    public bool ShutdownPending { get; set; }
    public ClipboardContents Clipboard { get; set; } = new("hello", null, 0, 0, null);
    public bool? Dark { get; set; } = false;
    public int Level { get; set; } = 60;
    public Screenshot Shot { get; set; } = new([0xFF, 0xD8, 0xFF], 1568, 882, 3840, 2160, "the screen", "Inbox - Outlook");
    public Exception? Fails { get; set; }

    void Did(string what)
    {
        Calls.Enqueue(what);
        if (Fails is not null) throw Fails;
    }

    public Task<IReadOnlyList<AppEntry>> LoadAppsAsync(ILogger log, CancellationToken cancel) => Task.FromResult<IReadOnlyList<AppEntry>>(Installed.ToList());

    public AppEntry? FindApp(string spoken, IReadOnlyList<AppEntry> apps) => AppIndex.Match(spoken, apps, Aliases);

    public Task LaunchAsync(AppEntry app, CancellationToken cancel)
    {
        Did($"launch {app.Target}");
        return Task.CompletedTask;
    }

    public Task<string> OpenAsync(string target, CancellationToken cancel)
    {
        Did($"open {target}");
        return Task.FromResult(target.Contains('.') ? target : char.ToUpperInvariant(target[0]) + target[1..]);
    }

    public Task<bool> IsLockedAsync(CancellationToken cancel) => Task.FromResult(Locked);

    public Task LockAsync(CancellationToken cancel)
    {
        Did("lock");
        Locked = true;
        return Task.CompletedTask;
    }

    public void SleepAfter(TimeSpan delay, ILogger log) => Did($"sleep after {delay.TotalSeconds:0}s");

    public Task ScheduleShutdownAsync(bool restart, int minutes, CancellationToken cancel)
    {
        if (ShutdownPending) throw new PluginException("A shutdown or restart is already counting down. Cancel it first to change it.");
        Did($"{(restart ? "restart" : "shutdown")} in {minutes}");
        ShutdownPending = true;
        return Task.CompletedTask;
    }

    public Task<bool> CancelShutdownAsync(CancellationToken cancel)
    {
        bool was = ShutdownPending;
        ShutdownPending = false;
        return Task.FromResult(was);
    }

    public Task<Screenshot> CaptureAsync(ScreenArea area, CancellationToken cancel)
    {
        Did($"capture {area}");
        return Task.FromResult(Shot);
    }

    public Task<ClipboardContents> ReadClipboardAsync(CancellationToken cancel) => Task.FromResult(Clipboard);

    public Task WriteClipboardAsync(string text, CancellationToken cancel)
    {
        Did($"copy {text}");
        Clipboard = new ClipboardContents(text, null, 0, 0, null);
        return Task.CompletedTask;
    }

    public Task TypeAsync(string text, CancellationToken cancel)
    {
        Did($"type {text}");
        return Task.CompletedTask;
    }

    public Task<AppWindow?> ForegroundAsync(CancellationToken cancel) => Task.FromResult(Open.FirstOrDefault());

    public Task<IReadOnlyList<AppWindow>> WindowsAsync(CancellationToken cancel) => Task.FromResult<IReadOnlyList<AppWindow>>(Open.ToList());

    public Task<bool> FocusAsync(AppWindow window, CancellationToken cancel)
    {
        Did($"focus {window.Handle}");
        return Task.FromResult(FocusWorks);
    }

    public Task ShowDesktopAsync(CancellationToken cancel)
    {
        Did("show desktop");
        return Task.CompletedTask;
    }

    public Task<string> DescribeSystemAsync(CancellationToken cancel) => Task.FromResult("TEST-PC: Test OS 1.0. CPU 3%.");

    public Task<bool?> IsDarkModeAsync(CancellationToken cancel) => Task.FromResult(Dark);

    public Task SetDarkModeAsync(bool on, CancellationToken cancel)
    {
        Did($"dark {on}");
        Dark = on;
        return Task.CompletedTask;
    }

    public Task<int> BrightnessAsync(int? level, int? change, CancellationToken cancel)
    {
        if (level is int l) Level = Math.Clamp(l, 0, 100);
        else if (change is int c) Level = Math.Clamp(Level + c, 0, 100);
        return Task.FromResult(Level);
    }
}

/// <summary>The shared "This PC" plugin as an operating system would derive it, over the fake desktop.</summary>
internal sealed class TestPcControlPlugin(IPcDesktop desktop, AppIndex? apps = null) : PcControlPluginBase(desktop, apps);
