using Pucksley.Plugins.Linux.Common;
using Pucksley.Sdk;

namespace Pucksley.Plugins.Linux.Pc;

/// <summary>What pc_open will hand to the desktop (a link, a file or a folder), and how to describe it.</summary>
internal sealed record OpenPlan(string Target, string Description);

/// <summary>
/// Turns what someone asked to open (a URL, a path, "Downloads") into something for <c>xdg-open</c>.
/// What counts as a link, a web address or a named folder is <see cref="OpenRules"/> (shared with
/// Windows); paths, and what would run as a program, are Linux's.
/// </summary>
internal static class OpenTarget
{
    // Link types that open a page or a known app. A desktop hands any other scheme to whichever
    // program registered for it, and text Claude reads (web pages, emails, the screen) could ask for them.
    static readonly HashSet<string> AllowedSchemes = new(StringComparer.OrdinalIgnoreCase)
    {
        "http", "https", "mailto", "tel", "spotify", "msteams", "zoommtg", "steam",
    };

    // Opening these could run code (or install some): programs are pc_open_app's job, and only from the app list
    static readonly HashSet<string> RunnableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".desktop", ".appimage", ".run", ".bin", ".sh", ".bash", ".zsh", ".fish", ".csh", ".ksh", ".py", ".pyw", ".pl", ".rb", ".php", ".lua",
        ".js", ".mjs", ".jar", ".exe", ".msi", ".bat", ".cmd", ".com", ".scr", ".ps1", ".vbs", ".lnk", ".url",
        ".deb", ".rpm", ".flatpak", ".flatpakref", ".flatpakrepo", ".snap", ".service",
    };

    /// <param name="isExecutable">Whether a file is marked as a program (its execute permission).</param>
    public static OpenPlan Resolve(string target, Func<string, bool> fileExists, Func<string, bool> directoryExists, Func<string, bool> isExecutable,
        IReadOnlyList<KnownFolder> folders, string home)
    {
        string t = OpenRules.Clean(target);

        // 1. A link. Judged by its scheme even when the rest doesn't parse, so nothing slips past the list
        if (OpenRules.Link(t, AllowedSchemes, "web pages, mail links, files and folders") is { } link)
            return new OpenPlan(link.AbsoluteUri, OpenRules.Describe(link, t));

        // 2. A full path (or file: URL); "~" is the home folder
        string path = Uri.TryCreate(t, UriKind.Absolute, out var uri) && uri.IsFile ? uri.LocalPath : t;
        if (path == "~") path = home;
        else if (path.StartsWith("~/", StringComparison.Ordinal)) path = Path.Combine(home, path[2..]);
        if (path.StartsWith('/'))
        {
            if (directoryExists(path)) return new OpenPlan(path, Path.GetFileName(path.TrimEnd('/')) is { Length: > 0 } name ? name : path);
            if (fileExists(path))
            {
                if (RunnableExtensions.Contains(Path.GetExtension(path)) || isExecutable(path)) throw OpenRules.IsAProgram();
                return new OpenPlan(path, Path.GetFileName(path));
            }
            throw new PluginException($"There's no file or folder at {path}.");
        }

        // 3. A web address without the https:// ("youtube.com", "bbc.co.uk/news")
        if (OpenRules.WebAddress(t) is { } address) return new OpenPlan(address, t);

        // 4. A folder people name: Downloads, Documents, the trash…
        if (OpenRules.NamedFolder(t, folders) is { } folder) return new OpenPlan(folder.Path, folder.Name);

        throw OpenRules.NotFound(target, folders);
    }

    /// <summary>This PC's named folders (only ones that exist), with the names people use for them.</summary>
    public static IReadOnlyList<KnownFolder> KnownFolders()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var user = UserDirs(home, Environment.GetEnvironmentVariable("XDG_CONFIG_HOME"));
        string Dir(string key, string fallback) => user.GetValueOrDefault(key) ?? Path.Combine(home, fallback);

        string pictures = Dir("XDG_PICTURES_DIR", "Pictures");
        string videos = Dir("XDG_VIDEOS_DIR", "Videos");
        string trash = Path.Combine(Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } data ? data : Path.Combine(home, ".local/share"), "Trash/files");
        var folders = new List<KnownFolder>
        {
            new("Desktop", Dir("XDG_DESKTOP_DIR", "Desktop")),
            new("Documents", Dir("XDG_DOCUMENTS_DIR", "Documents")),
            new("Downloads", Dir("XDG_DOWNLOAD_DIR", "Downloads")),
            new("Pictures", pictures),
            new("Photos", pictures),
            new("Music", Dir("XDG_MUSIC_DIR", "Music")),
            new("Videos", videos),
            new("Movies", videos),
            new("Home", home),
            new("User folder", home),
            new("Screenshots", Path.Combine(pictures, "Screenshots")),
            new("Trash", trash),
            new("Recycle Bin", trash),
            new("Rubbish bin", trash),
        };
        folders.RemoveAll(f => f.Path.Length == 0 || !Directory.Exists(f.Path));
        return folders;
    }

    /// <summary>
    /// The folders from <c>~/.config/user-dirs.dirs</c> (a desktop in another language keeps
    /// Downloads under another name): lines like <c>XDG_DOWNLOAD_DIR="$HOME/Downloads"</c>.
    /// </summary>
    internal static Dictionary<string, string> UserDirs(string home, string? configHome)
    {
        var file = Path.Combine(string.IsNullOrEmpty(configHome) ? Path.Combine(home, ".config") : configHome, "user-dirs.dirs");
        try
        {
            return File.Exists(file) ? ParseUserDirs(File.ReadAllText(file), home) : [];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    internal static Dictionary<string, string> ParseUserDirs(string text, string home)
    {
        var dirs = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            int equals = line.IndexOf('=');
            if (equals <= 0) continue;
            string value = line[(equals + 1)..].Trim().Trim('"');
            if (value.StartsWith("$HOME", StringComparison.Ordinal)) value = home + value[5..];
            // A folder set to the home folder itself means "this desktop has none"
            if (value.Length > 0 && value.TrimEnd('/') != home.TrimEnd('/')) dirs[line[..equals].Trim()] = value;
        }
        return dirs;
    }

    /// <summary>Whether a file carries an execute permission.</summary>
    public static bool IsExecutable(string path)
    {
        try
        {
            return (File.GetUnixFileMode(path) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Hands <paramref name="plan"/> to the desktop, which picks the app for it.</summary>
    public static async Task OpenAsync(OpenPlan plan, CancellationToken cancel)
    {
        IReadOnlyList<string> command = Programs.Has("xdg-open") ? ["xdg-open", plan.Target]
            : Programs.Has("gio") ? ["gio", "open", plan.Target]
            : throw new PluginException("xdg-open isn't installed, so I can't open things on this PC (it comes with xdg-utils).");
        // xdg-open usually leaves at once; on some desktops it stays until the viewer closes
        int? exit = await Detached.StartAsync(command, TimeSpan.FromSeconds(2), cancel, ownScope: true, scopeName: "open").ConfigureAwait(false);
        if (exit is null or 0) return;
        throw new PluginException(exit switch
        {
            2 => $"There's nothing at {plan.Target}.",
            3 => $"This desktop has no app set up to open {plan.Description}.",
            _ => $"The desktop couldn't open {plan.Description} (xdg-open gave exit code {exit}).",
        });
    }
}
