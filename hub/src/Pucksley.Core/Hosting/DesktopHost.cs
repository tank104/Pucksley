using System.Diagnostics;
using System.Reflection;
using Pucksley.Core.Pucks;
using Pucksley.Core.Settings;

namespace Pucksley.Core.Hosting;

/// <summary>
/// What every host does the same way, whatever it's dressed in (the Windows tray app, the Linux
/// desktop app, the console): find the plugin assemblies it ships, open the hub's page, and say in
/// a line who's connected.
/// </summary>
public static class DesktopHost
{
    /// <summary>The plugins every host ships. A host adds the ones for its operating system.</summary>
    public static readonly IReadOnlyList<string> CommonPlugins =
        ["Pucksley.Plugins.Music", "Pucksley.Plugins.Home", "Pucksley.Plugins.Utility", "Pucksley.Plugins.Speech"];

    /// <summary>
    /// Loads the plugin assemblies by name. One that isn't there (a trimmed-down build) is left out;
    /// <paramref name="onMissing"/> hears about it.
    /// </summary>
    public static IEnumerable<Assembly> LoadPlugins(IEnumerable<string> names, Action<string, Exception>? onMissing = null)
    {
        foreach (var name in names)
        {
            Assembly? assembly = null;
            try
            {
                assembly = Assembly.Load(name);
            }
            catch (Exception e) when (e is FileNotFoundException or FileLoadException or BadImageFormatException)
            {
                onMissing?.Invoke(name, e);
            }
            if (assembly is not null) yield return assembly;
        }
    }

    /// <summary>The hub's address on this machine, for a second copy that only wants to show the page.</summary>
    public static string DefaultLocalUrl
    {
        get
        {
            int port = int.TryParse(Environment.GetEnvironmentVariable("PUCKSLEY_PORT"), out var p) ? p : 8765;
            return $"http://127.0.0.1:{port}/";
        }
    }

    /// <summary>Opens the hub's page in the person's browser (null: the default address).</summary>
    public static void OpenPage(string? url) => OpenWithSystem(url ?? DefaultLocalUrl);

    /// <summary>Opens a web address or a folder the way a double-click would (the shell on Windows, xdg-open on Linux).</summary>
    public static void OpenWithSystem(string target)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception)
        {
            // no browser or file manager to hand it to: nothing to do about that here
        }
    }

    /// <summary>True once: the very first time the hub runs on this machine.</summary>
    public static bool FirstRun()
    {
        var marker = Path.Combine(HubPaths.DataDir, ".opened");
        if (File.Exists(marker)) return false;
        try
        {
            Directory.CreateDirectory(HubPaths.DataDir);
            File.WriteAllText(marker, DateTime.Now.ToString("O"));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // can't remember it: the page opens again next time, which is harmless
        }
        return true;
    }

    /// <summary>
    /// Whether to open the page as the host starts: when asked to (<c>--open</c>), or on the very
    /// first run unless the host was started with the computer or told not to (PUCKSLEY_NO_BROWSER=1).
    /// </summary>
    public static bool ShouldOpenPage(string[] args, bool startsAtLogin) =>
        args.Contains("--open") || (!startsAtLogin && Environment.GetEnvironmentVariable("PUCKSLEY_NO_BROWSER") != "1" && FirstRun());

    /// <summary>"No pucks connected", "Connected: Kitchen", "3 pucks connected".</summary>
    public static string StatusText(IReadOnlyList<PuckSession> connected) => connected.Count switch
    {
        0 => "No pucks connected",
        1 => $"Connected: {connected[0].Name}",
        _ => $"{connected.Count} pucks connected",
    };

    /// <summary>The words for "a puck is waiting to be approved".</summary>
    public static (string Title, string Text) PendingNotice(PendingPuck puck) =>
        ("A new puck wants to join", $"{puck.Name} (code {puck.Code}). Click to approve it.");
}

/// <summary>
/// Notices when a puck starts waiting for approval, so a host can announce each one once. Call
/// <see cref="Check"/> every couple of seconds.
/// </summary>
public sealed class PendingPuckWatcher(PuckManager pucks)
{
    string _last = "";

    /// <summary>The puck to announce now, or null when nothing new is waiting.</summary>
    public PendingPuck? Check()
    {
        var pending = pucks.Pending;
        var ids = string.Join(",", pending.Select(p => p.Id));
        bool changed = ids.Length > 0 && ids != _last;
        _last = ids;
        return changed ? pending[0] : null;
    }
}
