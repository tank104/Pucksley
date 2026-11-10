using System.Diagnostics;
using Pucksley.Plugins.Windows.Common;
using Pucksley.Sdk;

namespace Pucksley.Plugins.Windows.Pc;

internal enum AppKind
{
    /// <summary>A .lnk/.url/.appref-ms file in a Start Menu folder; <see cref="AppEntry.Target"/> is its path.</summary>
    Shortcut,

    /// <summary>An entry of shell:AppsFolder (Store apps and more); <see cref="AppEntry.Target"/> is its AUMID or parsing name.</summary>
    AppsFolder,
}

/// <summary>An app someone can open by name.</summary>
internal sealed record AppEntry(string Name, string Target, AppKind Kind);

/// <summary>
/// The apps on this PC by name, from the Start Menu folders and shell:AppsFolder. Built on first
/// use (the plugin warms it at start), then refreshed in the background once older than 10 minutes
/// while the previous list keeps answering.
/// </summary>
internal sealed class AppIndex
{
    public static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(10);

    // What people say → what the Start Menu calls it (where fuzzy matching can't get there)
    static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["vs code"] = "Visual Studio Code",
        ["vscode"] = "Visual Studio Code",
        ["cmd"] = "Command Prompt",
        ["explorer"] = "File Explorer",
        ["windows explorer"] = "File Explorer",
        ["control panel"] = "Control Panel",
        ["store"] = "Microsoft Store",
        ["app store"] = "Microsoft Store",
    };

    // Start Menu clutter that installers add next to the real app
    static readonly string[] JunkWords = ["readme", "read me", "release notes", "license", "licence", "documentation", "changelog", "website", "web site", "user guide", "manual", "help file"];
    static readonly HashSet<string> DocumentExtensions = new(StringComparer.OrdinalIgnoreCase) { ".txt", ".pdf", ".chm", ".htm", ".html", ".rtf", ".md", ".hlp" };

    readonly Func<CancellationToken, Task<IReadOnlyList<AppEntry>>> _load;
    readonly Func<DateTime> _clock;
    readonly SemaphoreSlim _loading = new(1, 1);
    readonly object _gate = new();
    IReadOnlyList<AppEntry>? _apps;
    DateTime _loadedAt;
    bool _refreshing;

    public AppIndex(Func<CancellationToken, Task<IReadOnlyList<AppEntry>>> load, Func<DateTime>? clock = null)
    {
        _load = load;
        _clock = clock ?? (() => DateTime.UtcNow);
    }

    /// <summary>The cleaned list, sorted shortest name first.</summary>
    public async Task<IReadOnlyList<AppEntry>> GetAsync(CancellationToken cancel)
    {
        lock (_gate)
        {
            if (_apps is not null)
            {
                if (_clock() - _loadedAt > MaxAge && !_refreshing)
                {
                    _refreshing = true;
                    _ = Task.Run(RefreshAsync, CancellationToken.None);
                }
                return _apps;
            }
        }
        await _loading.WaitAsync(cancel).ConfigureAwait(false);
        try
        {
            lock (_gate)
            {
                if (_apps is not null) return _apps;
            }
            return await LoadAsync(cancel).ConfigureAwait(false);
        }
        finally
        {
            _loading.Release();
        }
    }

    async Task RefreshAsync()
    {
        await _loading.WaitAsync().ConfigureAwait(false);
        try
        {
            await LoadAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // keep serving the old list; the next call retries
        }
        finally
        {
            lock (_gate) _refreshing = false;
            _loading.Release();
        }
    }

    async Task<IReadOnlyList<AppEntry>> LoadAsync(CancellationToken cancel)
    {
        var apps = Clean(await _load(cancel).ConfigureAwait(false));
        lock (_gate)
        {
            _apps = apps;
            _loadedAt = _clock();
        }
        return apps;
    }

    /// <summary>
    /// Drops uninstallers, readmes and document links, and merges duplicates by name (a desktop app
    /// is both a Start Menu shortcut and an AppsFolder entry; the shortcut wins). Sorted shortest
    /// name first, so on a tie "Microsoft Teams" beats "Microsoft Teams (work or school)".
    /// </summary>
    public static IReadOnlyList<AppEntry> Clean(IEnumerable<AppEntry> entries)
    {
        var byName = new Dictionary<string, AppEntry>();
        foreach (var entry in entries)
        {
            if (string.IsNullOrWhiteSpace(entry.Name) || string.IsNullOrWhiteSpace(entry.Target) || IsJunk(entry)) continue;
            var key = NameMatcher.Normalize(entry.Name);
            if (key.Length == 0) continue;
            if (!byName.TryGetValue(key, out var existing) || (existing.Kind != AppKind.Shortcut && entry.Kind == AppKind.Shortcut))
                byName[key] = entry;
        }
        return byName.Values
            .OrderBy(e => e.Name.Length)
            .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    static bool IsJunk(AppEntry entry)
    {
        var name = NameMatcher.Normalize(entry.Name);
        if (name.Contains("uninstall", StringComparison.Ordinal)) return true;
        var padded = " " + name + " ";
        if (JunkWords.Any(w => padded.Contains(" " + w + " ", StringComparison.Ordinal))) return true;
        if (entry.Kind == AppKind.AppsFolder && entry.Target.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return true;
        return DocumentExtensions.Contains(Path.GetExtension(entry.Target));
    }

    /// <summary>The app someone means ("Outlook", "the calculator", "Chrome"), or null.</summary>
    public static AppEntry? Match(string spoken, IReadOnlyList<AppEntry> apps)
    {
        var said = Text.StripAppWords(spoken.Trim());
        if (Aliases.TryGetValue(NameMatcher.Normalize(said), out var canonical)) said = canonical;
        return NameMatcher.Best(said, apps, a => a.Name, threshold: 0.6);
    }

    /// <summary>"I can't find 'Outluk'. Closest: Outlook (classic), …" so Claude can recover in one step.</summary>
    public static string NotFoundMessage(string spoken, IReadOnlyList<AppEntry> apps)
    {
        var said = Text.StripAppWords(spoken.Trim());
        var closest = apps
            .Select(a => (a.Name, Score: NameMatcher.Score(said, a.Name)))
            .Where(x => x.Score >= 0.3)
            .OrderByDescending(x => x.Score)
            .Take(5)
            .Select(x => x.Name)
            .ToList();
        return closest.Count > 0
            ? $"I can't find an app called '{spoken}' on the PC. Closest: {string.Join(", ", closest)}."
            : $"I can't find an app called '{spoken}' on the PC.";
    }

    /// <summary>How to start it: the shortcut itself, or Explorer for an AppsFolder entry (how Start launches Store apps).</summary>
    public static ProcessStartInfo LaunchInfo(AppEntry app) => app.Kind == AppKind.Shortcut
        ? new ProcessStartInfo(app.Target) { UseShellExecute = true }
        : new ProcessStartInfo("explorer.exe", @"shell:AppsFolder\" + app.Target) { UseShellExecute = true };
}
