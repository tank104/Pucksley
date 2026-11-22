using System.Diagnostics;
using System.Text.RegularExpressions;
using Pucksley.Plugins.Windows.Common;
using Pucksley.Sdk;
using static Pucksley.Plugins.Windows.Common.NativeMethods;

namespace Pucksley.Plugins.Windows.Pc;

/// <summary>What pc_open will start, and how to describe it.</summary>
internal sealed record OpenPlan(ProcessStartInfo StartInfo, string Description);

/// <summary>
/// Turns what someone asked to open (a URL, a path, "Downloads") into something to start. What
/// counts as a link, a web address or a named folder is <see cref="OpenRules"/> (shared with
/// Linux); paths, drives and how Windows starts things are here.
/// </summary>
internal static partial class OpenTarget
{
    // Link types that open a page or a known app. Others (ms-msdt:, search-ms:…) have been used in
    // attacks, and text Claude reads (web pages, emails, the screen) could ask for them.
    static readonly HashSet<string> AllowedSchemes = new(StringComparer.OrdinalIgnoreCase)
    {
        "http", "https", "mailto", "tel", "ms-settings", "ms-windows-store", "spotify", "msteams", "zoommtg", "steam",
    };

    // Opening these would run code: programs are pc_open_app's job, and only from the app list
    static readonly HashSet<string> RunnableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".com", ".bat", ".cmd", ".ps1", ".psm1", ".vbs", ".vbe", ".js", ".jse", ".wsf", ".wsh", ".msi", ".msp",
        ".msix", ".appx", ".appinstaller", ".scr", ".pif", ".cpl", ".hta", ".jar", ".reg", ".lnk", ".url", ".appref-ms",
        ".application", ".inf", ".sys", ".dll", ".settingcontent-ms", ".library-ms", ".search-ms", ".diagcab",
    };

    [GeneratedRegex(@"^(?:the\s+)?([a-z])(?:\s*:\\?|\s+drive)$", RegexOptions.IgnoreCase)]
    private static partial Regex DriveLetter();

    public static OpenPlan Resolve(string target, Func<string, bool> fileExists, Func<string, bool> directoryExists, IReadOnlyList<KnownFolder> folders)
    {
        string t = OpenRules.Clean(target);

        // 1. A link. Judged by its scheme even when the rest doesn't parse, so nothing slips past the list
        if (OpenRules.Link(t, AllowedSchemes, "web pages, mail links, Settings pages, files and folders") is { } link)
            return new OpenPlan(new ProcessStartInfo(link.AbsoluteUri) { UseShellExecute = true }, OpenRules.Describe(link, t));

        // 2. A full path (or file: URL)
        string path = Uri.TryCreate(t, UriKind.Absolute, out var uri) && uri.IsFile ? uri.LocalPath : Environment.ExpandEnvironmentVariables(t);
        if (path.StartsWith('~')) path = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + path[1..];
        if (Path.IsPathFullyQualified(path))
        {
            if (directoryExists(path)) return Folder(path, Path.GetFileName(path.TrimEnd('\\', '/')) is { Length: > 0 } n ? n : path);
            if (fileExists(path))
            {
                if (RunnableExtensions.Contains(Path.GetExtension(path))) throw OpenRules.IsAProgram();
                return new OpenPlan(new ProcessStartInfo(path) { UseShellExecute = true }, Path.GetFileName(path));
            }
            throw new PluginException($"There's no file or folder at {path}.");
        }

        // 3. A drive: "C drive", "D:"
        if (DriveLetter().Match(t) is { Success: true } drive)
        {
            string root = char.ToUpperInvariant(drive.Groups[1].Value[0]) + @":\";
            if (directoryExists(root)) return Folder(root, $"the {root[0]} drive");
            throw new PluginException($"This PC has no {root[0]} drive.");
        }

        // 4. A web address without the https:// ("youtube.com", "bbc.co.uk/news")
        if (OpenRules.WebAddress(t) is { } address) return new OpenPlan(new ProcessStartInfo(address) { UseShellExecute = true }, t);

        // 5. A folder people name: Downloads, Documents, the recycle bin…
        if (OpenRules.NamedFolder(t, folders) is { } folder) return Folder(folder.Path, folder.Name);

        throw OpenRules.NotFound(target, folders);
    }

    // Explorer opens folders and shell: locations alike. Quoted only when needed, and never as
    // "D:\" (a backslash before the closing quote escapes it)
    static OpenPlan Folder(string path, string name) =>
        new(new ProcessStartInfo("explorer.exe", path.Contains(' ') ? $"\"{path.TrimEnd('\\')}\"" : path) { UseShellExecute = true }, name);

    /// <summary>This PC's named folders (only ones that exist), with the names people use for them.</summary>
    public static IReadOnlyList<KnownFolder> KnownFolders()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        string videos = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
        string downloads = KnownFolderPath(FOLDERID_Downloads) ?? Path.Combine(home, "Downloads");
        var folders = new List<KnownFolder>
        {
            new("Desktop", Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)),
            new("Documents", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)),
            new("Downloads", downloads),
            new("Pictures", pictures),
            new("Photos", pictures),
            new("Music", Environment.GetFolderPath(Environment.SpecialFolder.MyMusic)),
            new("Videos", videos),
            new("Movies", videos),
            new("Home", home),
            new("User folder", home),
            new("Screenshots", Path.Combine(pictures, "Screenshots")),
            new("OneDrive", Environment.GetEnvironmentVariable("OneDrive") ?? ""),
        };
        folders.RemoveAll(f => f.Path.Length == 0 || !Directory.Exists(f.Path));
        folders.Add(new KnownFolder("Recycle Bin", "shell:RecycleBinFolder"));
        folders.Add(new KnownFolder("Trash", "shell:RecycleBinFolder"));
        folders.Add(new KnownFolder("This PC", "shell:MyComputerFolder"));
        folders.Add(new KnownFolder("My Computer", "shell:MyComputerFolder"));
        return folders;
    }
}
