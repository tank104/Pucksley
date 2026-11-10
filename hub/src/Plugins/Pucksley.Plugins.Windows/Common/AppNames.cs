using System.Collections.Concurrent;
using System.Diagnostics;
using static Pucksley.Plugins.Windows.Common.NativeMethods;

namespace Pucksley.Plugins.Windows.Common;

/// <summary>Names people use for apps: "Google Chrome" for chrome.exe, "WhatsApp" for a Store app, "Spotify" for a media session.</summary>
internal static class AppNames
{
    static readonly ConcurrentDictionary<string, string> ByExe = new(StringComparer.OrdinalIgnoreCase);
    static readonly ConcurrentDictionary<string, string?> ByAumid = new(StringComparer.OrdinalIgnoreCase);

    // Version-info descriptions that say nothing about the app
    static readonly HashSet<string> Generic = new(StringComparer.OrdinalIgnoreCase)
    {
        "Electron", "Application", "Chromium", "Java(TM) Platform SE binary", "COM Surrogate", "Host Process for Windows Services",
    };

    /// <summary>("chrome", "Google Chrome") for a process id.</summary>
    public static unsafe (string ProcessName, string Name) ForProcess(int processId)
    {
        var handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)processId);
        if (handle != 0)
        {
            try
            {
                var buffer = stackalloc char[1024];
                uint size = 1024;
                if (QueryFullProcessImageName(handle, 0, buffer, ref size))
                {
                    var path = new string(buffer, 0, (int)size);
                    // A Store/MSIX app's exe often has no useful description ("WhatsApp.Root", "Notepad.exe"):
                    // its package knows the real name
                    string? packaged = AppUserModelId(handle) is { } aumid ? RegisteredName(aumid) : null;
                    return (Path.GetFileNameWithoutExtension(path), packaged ?? FromExe(path));
                }
            }
            finally
            {
                CloseHandle(handle);
            }
        }
        try
        {
            using var p = Process.GetProcessById(processId);
            return (p.ProcessName, Capitalize(p.ProcessName));
        }
        catch (Exception)
        {
            return ($"pid{processId}", $"Process {processId}");
        }
    }

    static unsafe string? AppUserModelId(nint process)
    {
        var buffer = stackalloc char[256];
        uint length = 256;
        return GetApplicationUserModelId(process, ref length, buffer) == 0 && length > 1 ? new string(buffer, 0, (int)length - 1) : null;
    }

    /// <summary>From the exe's version info ("Google Chrome", "Spotify"), else its file name.</summary>
    public static string FromExe(string path) => ByExe.GetOrAdd(path, static p =>
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(p);
            foreach (var candidate in new[] { info.FileDescription, info.ProductName })
            {
                var c = candidate?.Trim();
                if (c is not null && c.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) c = c[..^4];
                if (!string.IsNullOrEmpty(c) && c.Length <= 40 && !Generic.Contains(c)) return c;
            }
        }
        catch (Exception)
        {
            // no version resource, or no access
        }
        return Capitalize(Path.GetFileNameWithoutExtension(p));
    });

    /// <summary>
    /// A media session's app id ("Spotify.exe", "SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify",
    /// "MSEdge", "Chrome") → a name to say.
    /// </summary>
    public static string FromAumid(string? aumid) =>
        string.IsNullOrWhiteSpace(aumid) ? "" : RegisteredName(aumid) ?? GuessFromAumid(aumid);

    /// <summary>The display name Windows has for an AUMID (packaged apps, and Win32 apps registered with one), or null.</summary>
    static string? RegisteredName(string aumid) => ByAumid.GetOrAdd(aumid, static id =>
    {
        try
        {
            var info = global::Windows.ApplicationModel.AppInfo.GetFromAppUserModelId(id);
            return info?.DisplayInfo?.DisplayName is { Length: > 0 } name ? name : null;
        }
        catch (Exception)
        {
            return null; // not a registered AUMID
        }
    });

    internal static string GuessFromAumid(string aumid)
    {
        var id = aumid.ToLowerInvariant();
        if (id.Contains("spotify")) return "Spotify";
        if (id.Contains("msedge")) return "Edge";
        if (id.Contains("chrome")) return "Chrome";
        if (id.Contains("firefox") || id == "308046b0af4a39cb") return "Firefox";
        if (id.Contains("zunemusic") || id.Contains("zunevideo")) return "Media Player";
        if (id.Contains("vlc")) return "VLC";
        if (id.Contains("teams")) return "Teams";
        if (id.Contains("brave")) return "Brave";
        if (id.Contains("opera")) return "Opera";
        if (id.Contains("applemusic")) return "Apple Music";
        if (id.Contains("tidal")) return "Tidal";

        var s = aumid;
        int bang = s.IndexOf('!');
        if (bang >= 0) s = s[(bang + 1)..];
        if (s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) s = s[..^4];
        s = s.Split('\\', '/').Last();
        int dot = s.LastIndexOf('.');
        if (dot >= 0 && dot < s.Length - 1) s = s[(dot + 1)..];
        return Capitalize(s);
    }

    static string Capitalize(string s) => s.Length > 0 ? char.ToUpperInvariant(s[0]) + s[1..] : s;
}
