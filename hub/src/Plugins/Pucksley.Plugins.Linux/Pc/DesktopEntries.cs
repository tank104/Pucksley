using System.Text;

namespace Pucksley.Plugins.Linux.Pc;

/// <summary>
/// An app from a <c>.desktop</c> file: what a launcher shows. <see cref="Id"/> is its desktop-file
/// id ("firefox.desktop"); <see cref="Exec"/> is the command line with its field codes still in.
/// </summary>
internal sealed record DesktopEntry(string Id, string Path, string Name, string? GenericName, string? Exec, bool Terminal, string? StartupWmClass)
{
    /// <summary>
    /// Other things people call it: the generic name ("Web Browser"), the program ("google-chrome"),
    /// the id ("org.gnome.Nautilus" → "Nautilus").
    /// </summary>
    public IReadOnlyList<string> OtherNames { get; init; } = [];
}

/// <summary>Reads <c>.desktop</c> files and finds them where the desktop keeps them.</summary>
internal static class DesktopEntries
{
    /// <summary>
    /// The folders launchers read, most important first: the person's own, then the system's
    /// (XDG_DATA_DIRS), then Flatpak's and Snap's in case the session didn't list them.
    /// </summary>
    public static IReadOnlyList<string> Folders(Func<string, string?> env, string home)
    {
        var roots = new List<string>
        {
            env("XDG_DATA_HOME") is { Length: > 0 } dataHome ? dataHome : System.IO.Path.Combine(home, ".local/share"),
        };
        roots.AddRange((env("XDG_DATA_DIRS") is { Length: > 0 } dirs ? dirs : "/usr/local/share:/usr/share").Split(':', StringSplitOptions.RemoveEmptyEntries));
        roots.Add(System.IO.Path.Combine(home, ".local/share/flatpak/exports/share"));
        roots.Add("/var/lib/flatpak/exports/share");
        roots.Add("/var/lib/snapd/desktop");
        return roots.Select(r => System.IO.Path.Combine(r.TrimEnd('/'), "applications")).Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// Every app a launcher would show. A file in an earlier folder replaces the one with the same
    /// id further down (that's how people override or hide a system app).
    /// </summary>
    public static List<DesktopEntry> Load(IReadOnlyList<string> folders, IReadOnlyList<string> currentDesktops)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var apps = new List<DesktopEntry>();
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, MaxRecursionDepth = 4 };
        foreach (var folder in folders)
        {
            if (!Directory.Exists(folder)) continue;
            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(folder, "*.desktop", options).ToList();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            foreach (var file in files)
            {
                // "kde/konsole.desktop" in a subfolder has the id "kde-konsole.desktop"
                string id = System.IO.Path.GetRelativePath(folder, file).Replace('/', '-');
                if (!seen.Add(id)) continue;
                string text;
                try
                {
                    text = File.ReadAllText(file);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    continue;
                }
                if (Parse(text, id, file, currentDesktops) is { } entry) apps.Add(entry);
            }
        }
        return apps;
    }

    /// <summary>
    /// One <c>.desktop</c> file, or null when it isn't an app to show: another type, hidden, or meant
    /// for a different desktop (OnlyShowIn/NotShowIn).
    /// </summary>
    public static DesktopEntry? Parse(string text, string id, string path, IReadOnlyList<string> currentDesktops)
    {
        var keys = new Dictionary<string, string>(StringComparer.Ordinal);
        bool inEntry = false;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            if (line[0] == '[')
            {
                // Actions ("[Desktop Action new-window]") follow the main group
                if (inEntry) break;
                inEntry = line == "[Desktop Entry]";
                continue;
            }
            if (!inEntry) continue;
            int equals = line.IndexOf('=');
            if (equals <= 0) continue;
            string key = line[..equals].TrimEnd();
            if (!keys.ContainsKey(key)) keys[key] = line[(equals + 1)..].TrimStart();
        }

        string Value(string key) => keys.TryGetValue(key, out var v) ? Unescape(v) : "";
        bool Flag(string key) => Value(key).Equals("true", StringComparison.OrdinalIgnoreCase);
        string[] List(string key) => Value(key).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (Value("Type") != "Application" || Flag("NoDisplay") || Flag("Hidden")) return null;
        string name = Value("Name");
        if (name.Length == 0) return null;
        var only = List("OnlyShowIn");
        if (only.Length > 0 && !only.Any(d => currentDesktops.Contains(d, StringComparer.OrdinalIgnoreCase))) return null;
        if (List("NotShowIn").Any(d => currentDesktops.Contains(d, StringComparer.OrdinalIgnoreCase))) return null;

        string exec = Value("Exec");
        string generic = Value("GenericName");
        string wmClass = Value("StartupWMClass");

        var others = new List<string>();
        void Add(string? other)
        {
            if (!string.IsNullOrWhiteSpace(other) && !other.Equals(name, StringComparison.OrdinalIgnoreCase) && !others.Contains(other, StringComparer.OrdinalIgnoreCase))
                others.Add(other);
        }
        Add(generic);
        Add(ProgramName(exec));
        Add(NameFromId(id));
        return new DesktopEntry(id, path, name, generic.Length > 0 ? generic : null, exec.Length > 0 ? exec : null, Flag("Terminal"), wmClass.Length > 0 ? wmClass : null)
        {
            OtherNames = others,
        };
    }

    /// <summary>The escapes every value may hold: \s \n \t \r \\.</summary>
    static string Unescape(string value)
    {
        if (!value.Contains('\\')) return value;
        var sb = new StringBuilder(value.Length);
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if (c != '\\' || i + 1 >= value.Length)
            {
                sb.Append(c);
                continue;
            }
            char next = value[++i];
            sb.Append(next switch
            {
                's' => ' ',
                'n' => '\n',
                't' => '\t',
                'r' => '\r',
                '\\' => '\\',
                _ => "\\" + next, // not an escape of this layer: the Exec quoting reads it
            });
        }
        return sb.ToString();
    }

    /// <summary>"/usr/bin/google-chrome-stable %U" → "google-chrome-stable"; wrappers like env and flatpak are looked through.</summary>
    internal static string? ProgramName(string exec)
    {
        var words = ExecLine.Split(exec);
        for (int i = 0; i < words.Count; i++)
        {
            string word = words[i];
            if (word is "env" or "/usr/bin/env" || (word.Contains('=') && !word.StartsWith('-'))) continue; // env FOO=bar program
            string program = System.IO.Path.GetFileName(word);
            if (program is "flatpak" or "snap" or "sh" or "bash" or "gtk-launch" or "uwsm" or "uwsm-app" or "xdg-open") return null; // says nothing about the app
            return program.Length > 0 ? program : null;
        }
        return null;
    }

    /// <summary>"org.gnome.Nautilus.desktop" → "Nautilus"; "com.spotify.Client.desktop" → "spotify".</summary>
    internal static string? NameFromId(string id)
    {
        string stem = id.EndsWith(".desktop", StringComparison.Ordinal) ? id[..^8] : id;
        var parts = stem.Split('.', StringSplitOptions.RemoveEmptyEntries);
        // Reverse-domain ids only: "firefox" and "code-oss" are already the program's name
        if (parts.Length < 3) return null;
        string last = parts[^1];
        if (last.Equals("Client", StringComparison.OrdinalIgnoreCase) || last.Equals("desktop", StringComparison.OrdinalIgnoreCase) || last.Equals("App", StringComparison.OrdinalIgnoreCase))
            last = parts[^2];
        return last;
    }
}

/// <summary>The <c>Exec</c> line of a <c>.desktop</c> file.</summary>
internal static class ExecLine
{
    /// <summary>
    /// Splits it into words the way the Desktop Entry specification says: spaces separate, double
    /// quotes group, and inside quotes a backslash escapes <c>" ` $ \</c>.
    /// </summary>
    public static List<string> Split(string exec)
    {
        var words = new List<string>();
        var word = new StringBuilder();
        bool quoted = false, any = false;
        for (int i = 0; i < exec.Length; i++)
        {
            char c = exec[i];
            if (quoted)
            {
                if (c == '\\' && i + 1 < exec.Length && exec[i + 1] is '"' or '`' or '$' or '\\') word.Append(exec[++i]);
                else if (c == '"') quoted = false;
                else word.Append(c);
            }
            else if (c == '"')
            {
                quoted = true;
                any = true;
            }
            else if (c is ' ' or '\t' or '\n')
            {
                if (any) words.Add(word.ToString());
                word.Clear();
                any = false;
            }
            else
            {
                word.Append(c);
                any = true;
            }
        }
        if (any) words.Add(word.ToString());
        return words;
    }

    /// <summary>
    /// The command to run with nothing to open: the field codes for files and addresses (%f %F %u %U)
    /// vanish, %c is the app's name, %k its file, and %% is a percent sign.
    /// </summary>
    public static List<string> Command(DesktopEntry entry)
    {
        var command = new List<string>();
        foreach (var word in Split(entry.Exec ?? ""))
        {
            // A word that is only a file code disappears; "%i" would be "--icon <name>", which nothing needs
            if (word is "%f" or "%F" or "%u" or "%U" or "%i" or "%d" or "%D" or "%n" or "%N" or "%v" or "%m") continue;
            if (!word.Contains('%'))
            {
                command.Add(word);
                continue;
            }
            var sb = new StringBuilder(word.Length);
            for (int i = 0; i < word.Length; i++)
            {
                if (word[i] != '%' || i + 1 >= word.Length)
                {
                    sb.Append(word[i]);
                    continue;
                }
                switch (word[++i])
                {
                    case '%': sb.Append('%'); break;
                    case 'c': sb.Append(entry.Name); break;
                    case 'k': sb.Append(entry.Path); break;
                    default: break; // a file code inside a word: nothing to put there
                }
            }
            if (sb.Length > 0) command.Add(sb.ToString());
        }
        return command;
    }
}
