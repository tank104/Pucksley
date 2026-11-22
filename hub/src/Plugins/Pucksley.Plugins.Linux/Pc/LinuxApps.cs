using Pucksley.Plugins.Linux.Common;
using Pucksley.Sdk;

namespace Pucksley.Plugins.Linux.Pc;

/// <summary>
/// What's Linux about the app list: it comes from the <c>.desktop</c> files (what an app launcher
/// shows), people call some apps by other names, and an app is started the way a launcher would.
/// <see cref="AppIndex"/> (shared with Windows) does the cleaning, caching and matching.
/// </summary>
internal sealed class LinuxApps
{
    static readonly TimeSpan Watch = TimeSpan.FromMilliseconds(700);

    // What people say → what launchers call it (where fuzzy matching can't get there). The names
    // are looked for in order; what was said stays in where it may itself be an app's name.
    internal static readonly IReadOnlyDictionary<string, string[]> Aliases = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
    {
        ["vs code"] = ["Visual Studio Code", "Code", "VSCodium"],
        ["vscode"] = ["Visual Studio Code", "Code", "VSCodium"],
        // GNOME's is called "Files"; KDE's, Xfce's and Cinnamon's say "File Manager" as what they are
        ["explorer"] = ["Files", "File Manager"],
        ["file explorer"] = ["Files", "File Manager"],
        ["file manager"] = ["Files", "File Manager"],
        ["finder"] = ["Files", "File Manager"],
        ["chrome"] = ["Google Chrome", "Chromium"],
        ["google chrome"] = ["Google Chrome", "Chromium"],
        ["command prompt"] = ["Terminal"],
        ["cmd"] = ["Terminal"],
        ["console"] = ["Terminal", "Console"],
        ["task manager"] = ["System Monitor", "Activity", "btop", "htop"],
        ["control panel"] = ["Settings", "System Settings"],
        ["notepad"] = ["Text Editor"],
        ["store"] = ["Software", "Discover"],
        ["app store"] = ["Software", "Discover"],
    };

    static readonly string[] BrowserWords = ["browser", "web browser", "internet browser", "internet"];

    readonly object _gate = new();
    Dictionary<string, DesktopEntry> _byPath = new(StringComparer.Ordinal);
    string? _defaultBrowserPath;

    /// <summary>Reads this PC's <c>.desktop</c> files, and which one is the default web browser.</summary>
    public async Task<IReadOnlyList<AppEntry>> LoadAsync(CancellationToken cancel)
    {
        var entries = await Task.Run(() =>
        {
            var desktops = (Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP") ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var folders = DesktopEntries.Folders(Environment.GetEnvironmentVariable, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
            return DesktopEntries.Load(folders, desktops);
        }, cancel).ConfigureAwait(false);
        string? browserId = await DefaultBrowserAsync(cancel).ConfigureAwait(false);
        Remember(entries, browserId);
        return entries.Select(ToApp).ToList();
    }

    /// <summary>Keeps the launch details of each app (and the default browser) for later.</summary>
    internal void Remember(IReadOnlyList<DesktopEntry> entries, string? defaultBrowserId)
    {
        var byPath = new Dictionary<string, DesktopEntry>(StringComparer.Ordinal);
        foreach (var entry in entries) byPath.TryAdd(entry.Path, entry);
        lock (_gate)
        {
            _byPath = byPath;
            _defaultBrowserPath = defaultBrowserId is null ? null : entries.FirstOrDefault(e => e.Id == defaultBrowserId)?.Path;
        }
    }

    internal static AppEntry ToApp(DesktopEntry entry) => new(entry.Name, entry.Path, AppKind.DesktopFile) { OtherNames = entry.OtherNames };

    /// <summary>The app someone means ("Spotify", "the calculator", "Chrome", "the browser"), or null.</summary>
    public AppEntry? Find(string spoken, IReadOnlyList<AppEntry> apps)
    {
        // "the browser" is whichever one this desktop opens links with
        string plain = string.Join(' ', NameMatcher.Normalize(Text.StripAppWords(spoken.Trim())).Split(' ').Where(w => w is not ("the" or "my" or "a")));
        string? browser;
        lock (_gate) browser = _defaultBrowserPath;
        if (browser is not null && BrowserWords.Contains(plain) && apps.FirstOrDefault(a => a.Target == browser) is { } app) return app;
        return AppIndex.Match(plain.Length > 0 && Aliases.ContainsKey(plain) ? plain : spoken, apps, Aliases);
    }

    DesktopEntry? Entry(AppEntry app)
    {
        lock (_gate) return _byPath.GetValueOrDefault(app.Target);
    }

    /// <summary>
    /// The name people use for a window's app, from the class the window announces
    /// ("org.gnome.Nautilus" is "Files"; "chrome-web.whatsapp.com__-Default" is the WhatsApp web
    /// app). Null when no installed app claims it.
    /// </summary>
    public string? NameForWindowClass(string windowClass)
    {
        if (string.IsNullOrWhiteSpace(windowClass)) return null;
        List<DesktopEntry> entries;
        lock (_gate) entries = _byPath.Values.ToList();
        return NameForWindowClass(windowClass, entries);
    }

    internal static string? NameForWindowClass(string windowClass, IReadOnlyList<DesktopEntry> entries)
    {
        bool Same(string? a) => a is not null && a.Equals(windowClass, StringComparison.OrdinalIgnoreCase);
        return (entries.FirstOrDefault(e => Same(e.StartupWmClass))
                ?? entries.FirstOrDefault(e => Same(e.Id.EndsWith(".desktop", StringComparison.Ordinal) ? e.Id[..^8] : e.Id))
                ?? entries.FirstOrDefault(e => e.Exec is not null && Same(DesktopEntries.ProgramName(e.Exec))))?.Name;
    }

    /// <summary>The desktop's default web browser, as a desktop-file id ("firefox.desktop"), or null.</summary>
    static async Task<string?> DefaultBrowserAsync(CancellationToken cancel)
    {
        if (!Programs.Has("xdg-settings")) return null;
        try
        {
            var result = await Programs.RunAsync("xdg-settings", ["get", "default-web-browser"], TimeSpan.FromSeconds(5), cancel).ConfigureAwait(false);
            var id = result.Output.Trim();
            return result.ExitCode == 0 && id.EndsWith(".desktop", StringComparison.Ordinal) ? id : null;
        }
        catch (PluginException)
        {
            return null;
        }
    }

    /// <summary>
    /// The ways to start an app, best first: the session's own launcher (uwsm, as Omarchy uses),
    /// GLib's, GTK's, and finally the command from the file itself.
    /// </summary>
    internal static List<(IReadOnlyList<string> Command, bool OwnScope)> Plans(DesktopEntry app, Func<string, bool> has)
    {
        var plans = new List<(IReadOnlyList<string>, bool)>();
        // An id with spaces or other surprises is left to the launchers that take the file's path
        bool plainId = app.Id.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_');
        if (plainId && has("uwsm-app")) plans.Add((["uwsm-app", "--", app.Id], false));
        else if (plainId && has("uwsm")) plans.Add((["uwsm", "app", "--", app.Id], false));
        if (has("gio")) plans.Add((["gio", "launch", app.Path], true));
        if (plainId && has("gtk-launch")) plans.Add((["gtk-launch", app.Id], true));

        var command = ExecLine.Command(app);
        if (command.Count > 0)
        {
            if (!app.Terminal) plans.Add((command, true));
            else if (has("xdg-terminal-exec")) plans.Add((["xdg-terminal-exec", .. command], true));
        }
        return plans;
    }

    public async Task LaunchAsync(AppEntry app, CancellationToken cancel)
    {
        var entry = Entry(app) ?? throw new PluginException($"{app.Name} isn't in the app list any more. Ask again in a moment.");
        var plans = Plans(entry, Programs.Has);
        if (plans.Count == 0) throw new PluginException($"I don't know how to start {app.Name} on this desktop (its launcher entry has no command).");
        PluginException? last = null;
        foreach (var (command, ownScope) in plans)
        {
            try
            {
                int? exit = await Detached.StartAsync(command, Watch, cancel, ownScope, app.Name).ConfigureAwait(false);
                if (exit is null or 0) return; // running, or a launcher that handed it over and left
                last = new PluginException($"{app.Name} didn't start ({command[0]} gave exit code {exit}).");
            }
            catch (PluginException e)
            {
                last = e;
            }
        }
        throw last!;
    }
}
