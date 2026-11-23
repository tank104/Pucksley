using System.Diagnostics;
using System.Text;
using Pucksley.Core.Hosting;

namespace Pucksley.Linux;

/// <summary>
/// Pucksley's place on the desktop, as the freedesktop conventions have it: an entry in the app
/// launcher (a .desktop file and an icon under ~/.local/share) and, when wanted, a start at login
/// (the same kind of file under ~/.config/autostart, which GNOME, KDE, Xfce and systemd-run
/// sessions such as Omarchy's all honour). Where the session starts those through systemd, a small
/// addition to the unit it makes has systemd start the hub again if it ever crashes.
/// </summary>
internal static class DesktopIntegration
{
    const string FileName = "pucksley.desktop";
    static readonly int[] IconSizes = [48, 64, 128, 256];

    static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    static string ConfigHome => Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } dir ? dir : Path.Combine(Home, ".config");

    static string DataHome => Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } dir ? dir : Path.Combine(Home, ".local/share");

    public static string AutostartFile => Path.Combine(ConfigHome, "autostart", FileName);

    /// <summary>
    /// The name systemd's autostart generator gives the unit it makes from the autostart file (the
    /// file's name, escaped, which "pucksley" doesn't need).
    /// </summary>
    public const string AutostartUnit = "app-pucksley@autostart.service";

    /// <summary>An addition (a drop-in) to that unit: start the hub again after a crash.</summary>
    public static string KeepRunningFile => Path.Combine(ConfigHome, "systemd/user", AutostartUnit + ".d", "keep-running.conf");

    internal const string KeepRunning = """
        # Written by Pucksley with its "Start at login": when the desktop starts it through systemd,
        # start it again if it crashes. Quitting from the tray menu ends it for good.
        [Service]
        Restart=on-failure
        RestartSec=3
        # Judge the hub by its own process, not by whatever it started (a face service, say), so a
        # crash is noticed; what it started goes with it and starts again with the new hub
        ExitType=main
        # The session's own default (5 s) can cut a shutdown short
        TimeoutStopSec=15

        """;

    public static string LauncherFile => Path.Combine(DataHome, "applications", FileName);

    static string IconFile(int size) => Path.Combine(DataHome, "icons/hicolor", $"{size}x{size}", "apps/pucksley.png");

    static string ScalableIconFile => Path.Combine(DataHome, "icons/hicolor/scalable/apps/pucksley.svg");

    // ───────────────────────── Start at login

    public static bool StartsAtLogin
    {
        get
        {
            try
            {
                // "Hidden=true" is how a desktop's own settings switch an entry off
                return File.Exists(AutostartFile) && !File.ReadLines(AutostartFile).Any(l => l.Trim().Equals("Hidden=true", StringComparison.OrdinalIgnoreCase));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }
    }

    public static void SetStartAtLogin(bool on)
    {
        if (on)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(AutostartFile)!);
            File.WriteAllText(AutostartFile, DesktopFile(SelfCommand(), autostart: true));
            Write(KeepRunningFile, Encoding.UTF8.GetBytes(KeepRunning));
        }
        else
        {
            if (File.Exists(AutostartFile)) File.Delete(AutostartFile);
            RemoveKeepRunning();
        }
        ReloadSystemd();
    }

    static bool RemoveKeepRunning()
    {
        if (!File.Exists(KeepRunningFile)) return false;
        File.Delete(KeepRunningFile);
        var dir = Path.GetDirectoryName(KeepRunningFile)!;
        if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
        return true;
    }

    /// <summary>Off in tests, which write to a scratch folder the session doesn't read.</summary>
    internal static bool TellSystemd { get; set; } = true;

    /// <summary>
    /// Has a systemd user session (where there is one) read the autostart file and its drop-in again
    /// now, rather than at the next login. Best effort: without systemd there's nothing to tell.
    /// </summary>
    static void ReloadSystemd()
    {
        if (!TellSystemd) return;
        try
        {
            using var reload = Process.Start(new ProcessStartInfo("systemctl", ["--user", "daemon-reload"])
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (reload is not null && !reload.WaitForExit(5000)) reload.Kill();
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // no systemctl here
        }
    }

    // ───────────────────────── The app launcher

    /// <summary>Puts Pucksley in the app launcher. Returns the files written.</summary>
    public static IReadOnlyList<string> Install()
    {
        var written = new List<string>();
        foreach (int size in IconSizes)
        {
            Write(IconFile(size), PuckIcon.Png(size));
            written.Add(IconFile(size));
        }
        // The page's own icon is the same picture as a vector
        using (var svg = typeof(HubHost).Assembly.GetManifestResourceStream("Pucksley.Core.wwwroot.favicon.svg"))
        {
            if (svg is not null)
            {
                using var buffer = new MemoryStream();
                svg.CopyTo(buffer);
                Write(ScalableIconFile, buffer.ToArray());
                written.Add(ScalableIconFile);
            }
        }
        Write(LauncherFile, Encoding.UTF8.GetBytes(DesktopFile([.. SelfCommand(), "--open"], autostart: false)));
        written.Add(LauncherFile);
        // A start at login written by an older copy elsewhere should point here too
        if (File.Exists(AutostartFile)) SetStartAtLogin(true);
        return written;
    }

    /// <summary>Takes Pucksley out of the launcher and out of the login. Returns the files removed. Settings stay.</summary>
    public static IReadOnlyList<string> Uninstall()
    {
        var removed = new List<string>();
        foreach (var file in new[] { LauncherFile, AutostartFile, ScalableIconFile }.Concat(IconSizes.Select(IconFile)))
        {
            if (!File.Exists(file)) continue;
            File.Delete(file);
            removed.Add(file);
        }
        string keepRunning = KeepRunningFile;
        if (RemoveKeepRunning()) removed.Add(keepRunning);
        ReloadSystemd();
        return removed;
    }

    static void Write(string file, byte[] content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllBytes(file, content);
    }

    // ───────────────────────── The files

    /// <summary>How this copy of the hub is started: the program itself, or dotnet with the hub's DLL when run that way.</summary>
    internal static IReadOnlyList<string> SelfCommand()
    {
        string program = Environment.ProcessPath ?? "pucksley";
        if (Path.GetFileNameWithoutExtension(program).Equals("dotnet", StringComparison.OrdinalIgnoreCase) && typeof(DesktopIntegration).Assembly.Location is { Length: > 0 } dll)
            return [program, dll];
        return [program];
    }

    internal static string DesktopFile(IReadOnlyList<string> command, bool autostart)
    {
        var sb = new StringBuilder();
        sb.Append("[Desktop Entry]\n");
        sb.Append("Type=Application\n");
        sb.Append("Name=Pucksley\n");
        sb.Append("GenericName=Voice assistant hub\n");
        sb.Append("Comment=The hub behind the Pucksley voice assistant puck\n");
        sb.Append("Exec=").Append(ExecLine(command)).Append('\n');
        sb.Append("Icon=pucksley\n");
        sb.Append("Terminal=false\n");
        sb.Append("Categories=Utility;\n");
        sb.Append("StartupNotify=false\n");
        if (autostart) sb.Append("X-GNOME-Autostart-enabled=true\n");
        return sb.ToString();
    }

    /// <summary>
    /// A command as an Exec line: words with anything special in them are put in double quotes, with
    /// the characters the format reserves escaped (and a literal percent sign doubled).
    /// </summary>
    internal static string ExecLine(IReadOnlyList<string> command) => string.Join(' ', command.Select(Quote));

    static string Quote(string word)
    {
        word = word.Replace("%", "%%");
        if (word.Length > 0 && word.All(c => char.IsAsciiLetterOrDigit(c) || c is '/' or '.' or '-' or '_' or '+' or ':' or '@' or '%' or '=' or ',')) return word;
        var sb = new StringBuilder("\"");
        foreach (char c in word)
        {
            // Inside quotes a backslash escapes " ` $ and \; and every backslash in a value is itself written twice
            if (c is '"' or '`' or '$') sb.Append("\\\\").Append(c);
            else if (c == '\\') sb.Append("\\\\\\\\");
            else sb.Append(c);
        }
        return sb.Append('"').ToString();
    }
}
