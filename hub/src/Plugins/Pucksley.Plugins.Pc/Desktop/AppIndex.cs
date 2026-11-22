using Pucksley.Plugins.Pc.Common;
using Pucksley.Sdk;

namespace Pucksley.Plugins.Pc.Desktop;

/// <summary>Where an app entry came from. When two share a name, the earlier kind here wins.</summary>
internal enum AppKind
{
    /// <summary>Windows: a .lnk/.url/.appref-ms file in a Start Menu folder; <see cref="AppEntry.Target"/> is its path.</summary>
    Shortcut,

    /// <summary>Windows: an entry of shell:AppsFolder (Store apps and more); <see cref="AppEntry.Target"/> is its AUMID or parsing name.</summary>
    AppsFolder,

    /// <summary>Linux: a .desktop file, as app launchers list them; <see cref="AppEntry.Target"/> is its path.</summary>
    DesktopFile,
}

/// <summary>An app someone can open by name.</summary>
internal sealed record AppEntry(string Name, string Target, AppKind Kind)
{
    /// <summary>
    /// Other things people call it, where the system knows any: a generic name ("Web Browser"), the
    /// program ("google-chrome"). They count a little less than <see cref="Name"/> when matching.
    /// </summary>
    public IReadOnlyList<string> OtherNames { get; init; } = [];
}

/// <summary>
/// The apps on this PC by name (Windows: the Start Menu folders and shell:AppsFolder; Linux: the
/// .desktop files). Built on first use (the plugin warms it at start), then refreshed in the
/// background once older than 10 minutes while the previous list keeps answering.
/// </summary>
internal sealed class AppIndex
{
    public static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(10);

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

    /// <summary>The list without waiting for anything: null until the first load has finished.</summary>
    public IReadOnlyList<AppEntry>? Known
    {
        get { lock (_gate) return _apps; }
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
    /// Drops uninstallers, readmes and document links, and merges duplicates by name: the earlier
    /// <see cref="AppKind"/> wins (on Windows a desktop app is both a Start Menu shortcut and an
    /// AppsFolder entry, and the shortcut wins), then the one listed first. Sorted shortest name
    /// first, so on a tie "Microsoft Teams" beats "Microsoft Teams (work or school)".
    /// </summary>
    public static IReadOnlyList<AppEntry> Clean(IEnumerable<AppEntry> entries)
    {
        var byName = new Dictionary<string, AppEntry>();
        foreach (var entry in entries)
        {
            if (string.IsNullOrWhiteSpace(entry.Name) || string.IsNullOrWhiteSpace(entry.Target) || IsJunk(entry)) continue;
            var key = NameMatcher.Normalize(entry.Name);
            if (key.Length == 0) continue;
            if (!byName.TryGetValue(key, out var existing) || entry.Kind < existing.Kind)
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

    /// <summary>
    /// The app someone means ("Outlook", "the calculator", "Chrome"), or null.
    /// <paramref name="aliases"/> maps what people say to what the system calls it, where fuzzy
    /// matching can't get there ("vs code" → "Visual Studio Code"): the names it lists are looked
    /// for instead, in order.
    /// </summary>
    public static AppEntry? Match(string spoken, IReadOnlyList<AppEntry> apps, IReadOnlyDictionary<string, string[]>? aliases = null)
    {
        var said = Text.StripAppWords(spoken.Trim());
        if (aliases is not null && aliases.TryGetValue(NameMatcher.Normalize(said), out var canonical))
        {
            foreach (var name in canonical)
            {
                if (Best(name, apps) is { } aliased) return aliased;
            }
            return null;
        }
        return Best(said, apps);
    }

    static AppEntry? Best(string said, IReadOnlyList<AppEntry> apps)
    {
        if (string.IsNullOrWhiteSpace(said)) return null;
        AppEntry? best = null;
        double bestScore = 0;
        foreach (var app in apps) // shortest name first, so a tie goes to the plainest name
        {
            double score = Score(said, app);
            if (score > bestScore)
            {
                bestScore = score;
                best = app;
            }
        }
        return bestScore >= 0.6 ? best : null;
    }

    /// <summary>0..1: how well <paramref name="said"/> names the app, by its name or (a little less) one of its other names.</summary>
    internal static double Score(string said, AppEntry app)
    {
        double score = NameMatcher.Score(said, app.Name);
        foreach (var other in app.OtherNames) score = Math.Max(score, NameMatcher.Score(said, other) * 0.93);
        return score;
    }

    /// <summary>"I can't find 'Outluk'. Closest: Outlook (classic), …" so Claude can recover in one step.</summary>
    public static string NotFoundMessage(string spoken, IReadOnlyList<AppEntry> apps)
    {
        var said = Text.StripAppWords(spoken.Trim());
        var closest = apps
            .Select(a => (a.Name, Score: Score(said, a)))
            .Where(x => x.Score >= 0.3)
            .OrderByDescending(x => x.Score)
            .Take(5)
            .Select(x => x.Name)
            .ToList();
        return closest.Count > 0
            ? $"I can't find an app called '{spoken}' on the PC. Closest: {string.Join(", ", closest)}."
            : $"I can't find an app called '{spoken}' on the PC.";
    }
}
