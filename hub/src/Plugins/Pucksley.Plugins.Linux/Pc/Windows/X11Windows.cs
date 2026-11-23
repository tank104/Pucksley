using System.Globalization;
using Pucksley.Plugins.Linux.Common;
using Pucksley.Sdk;

namespace Pucksley.Plugins.Linux.Pc.Windows;

/// <summary>
/// Windows on an X11 desktop (Xfce, Cinnamon, MATE, GNOME or KDE on X11, a bare window manager),
/// through <c>wmctrl</c>: every X11 window manager answers its questions the same way.
/// </summary>
internal sealed class X11Windows(AppNamer namer) : IWindowManager
{
    static readonly TimeSpan Timeout = TimeSpan.FromSeconds(4);

    // What a desktop draws for itself: panels, docks and the desktop's own window
    static readonly string[] ShellClasses = ["xfce4-panel", "xfdesktop", "plasmashell", "gnome-shell", "polybar", "conky", "desktop_window", "nemo-desktop", "caja-desktop", "lxpanel", "mate-panel", "cinnamon", "latte-dock", "plank", "tint2"];

    public bool CanSeeWindows => true;

    static PluginException NeedsWmctrl() => new("Seeing and switching windows on this desktop needs wmctrl. Install the wmctrl package.");

    public async Task<IReadOnlyList<AppWindow>> WindowsAsync(CancellationToken cancel)
    {
        if (!Programs.Has("wmctrl")) throw NeedsWmctrl();
        string list = await Programs.OutputAsync("wmctrl", ["-lpx"], Timeout, cancel, environment: Programs.PlainLocale).ConfigureAwait(false);
        var windows = ParseList(list, Environment.MachineName, namer).Where(w => w.ProcessId != Environment.ProcessId).ToList();
        // wmctrl lists them oldest first; the stacking order says which are in front
        var stacking = await StackingAsync(cancel).ConfigureAwait(false);
        if (stacking.Count > 0) windows = windows.OrderByDescending(w => stacking.IndexOf(w.Handle)).ToList();
        return windows;
    }

    /// <summary>
    /// <c>wmctrl -lpx</c>: "0x03c00003  0 12345  navigator.Firefox  myhost Page title". The class may
    /// hold spaces, so the machine's name (which follows it) marks where the title starts.
    /// </summary>
    internal static List<AppWindow> ParseList(string output, string host, AppNamer namer)
    {
        var windows = new List<AppWindow>();
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd();
            var head = line.Split(' ', 4, StringSplitOptions.RemoveEmptyEntries);
            if (head.Length < 4 || ParseId(head[0]) is not { } id || !int.TryParse(head[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int desktop)
                || !int.TryParse(head[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int pid)) continue;
            string rest = head[3].Trim();
            string windowClass, title;
            int hostAt = host.Length > 0 ? rest.IndexOf($" {host} ", StringComparison.Ordinal) : -1;
            if (hostAt >= 0)
            {
                windowClass = rest[..hostAt].Trim();
                title = rest[(hostAt + host.Length + 2)..].Trim();
            }
            else
            {
                var parts = rest.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 3) continue;
                (windowClass, title) = (parts[0], parts[2]);
            }
            if (title.Length == 0) continue;
            // "navigator.Firefox": the second half is the app's class
            string appClass = windowClass.Contains('.') ? windowClass[(windowClass.LastIndexOf('.') + 1)..] : windowClass;
            if (desktop == -1 && ShellClasses.Any(s => windowClass.Contains(s, StringComparison.OrdinalIgnoreCase))) continue;
            var (processName, appName) = namer(appClass, pid);
            windows.Add(new AppWindow(id, title, pid, processName, appName));
        }
        return windows;
    }

    internal static nint? ParseId(string text)
    {
        text = text.Trim().TrimEnd(',');
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return long.TryParse(text[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out long hex) ? (nint)hex : null;
        return long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long number) ? (nint)number : null;
    }

    /// <summary>Window ids from the bottom of the stack to the top, or none when xprop isn't there.</summary>
    static async Task<List<nint>> StackingAsync(CancellationToken cancel)
    {
        if (!Programs.Has("xprop")) return [];
        try
        {
            var result = await Programs.RunAsync("xprop", ["-root", "_NET_CLIENT_LIST_STACKING"], Timeout, cancel, environment: Programs.PlainLocale).ConfigureAwait(false);
            return result.ExitCode == 0 ? ParseIds(result.Output) : [];
        }
        catch (PluginException)
        {
            return [];
        }
    }

    /// <summary>"_NET_CLIENT_LIST_STACKING(WINDOW): window id # 0x1e00003, 0x3c00003" → the ids.</summary>
    internal static List<nint> ParseIds(string xprop)
    {
        int hash = xprop.IndexOf('#');
        if (hash < 0) return [];
        return xprop[(hash + 1)..].Split([',', ' ', '\n'], StringSplitOptions.RemoveEmptyEntries).Select(ParseId).Where(i => i is not null and not 0).Select(i => i!.Value).ToList();
    }

    public async Task<AppWindow?> ForegroundAsync(CancellationToken cancel)
    {
        if (!Programs.Has("wmctrl")) return null;
        nint? active = null;
        if (Programs.Has("xprop"))
        {
            var result = await Programs.RunAsync("xprop", ["-root", "_NET_ACTIVE_WINDOW"], Timeout, cancel, environment: Programs.PlainLocale).ConfigureAwait(false);
            if (result.ExitCode == 0) active = ParseIds(result.Output).FirstOrDefault() is var id && id != 0 ? id : null;
        }
        else if (Programs.Has("xdotool"))
        {
            var result = await Programs.RunAsync("xdotool", ["getactivewindow"], Timeout, cancel).ConfigureAwait(false);
            if (result.ExitCode == 0) active = ParseId(result.Output);
        }
        if (active is null) return null;
        return (await WindowsAsync(cancel).ConfigureAwait(false)).FirstOrDefault(w => w.Handle == active);
    }

    public async Task<bool> FocusAsync(AppWindow window, CancellationToken cancel)
    {
        if (!Programs.Has("wmctrl")) throw NeedsWmctrl();
        var result = await Programs.RunAsync("wmctrl", ["-i", "-a", "0x" + ((long)window.Handle).ToString("x8", CultureInfo.InvariantCulture)], Timeout, cancel).ConfigureAwait(false);
        return result.ExitCode == 0;
    }

    public async Task ShowDesktopAsync(CancellationToken cancel)
    {
        if (!Programs.Has("wmctrl")) throw NeedsWmctrl();
        await Programs.OutputAsync("wmctrl", ["-k", "on"], Timeout, cancel).ConfigureAwait(false);
    }

    public Task<bool?> IsLockedAsync(CancellationToken cancel) => Task.FromResult<bool?>(null);

    // X11 screenshot programs find the screen and the active window themselves
    public Task<ScreenLayout?> LayoutAsync(CancellationToken cancel) => Task.FromResult<ScreenLayout?>(null);
}
