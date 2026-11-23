using System.Text.RegularExpressions;
using Pucksley.Plugins.Pc.Common;
using Pucksley.Sdk;

namespace Pucksley.Plugins.Pc.Desktop;

/// <summary>A folder people name by voice ("Downloads", "the recycle bin"). Path may be a location only the system's file manager understands.</summary>
internal sealed record KnownFolder(string Name, string Path);

/// <summary>
/// The parts of working out what "open …" means that are the same on every operating system:
/// which links are safe to hand to the system, what looks like a web address, and which folder a
/// name means. Paths are the operating system's own business.
/// </summary>
internal static partial class OpenRules
{
    // "notes.txt" looks like a domain; these endings aren't top-level domains people visit
    static readonly HashSet<string> FileLikeEndings = new(StringComparer.OrdinalIgnoreCase)
    {
        "txt", "pdf", "doc", "docx", "xls", "xlsx", "ppt", "pptx", "csv", "jpg", "jpeg", "png", "gif", "mp3", "mp4", "wav",
        "zip", "exe", "msi", "json", "xml", "log", "md", "ini", "cfg", "bat", "ps1", "lnk", "html", "htm",
        "desktop", "conf", "toml", "yaml", "yml", "tar", "gz", "deb", "rpm", "svg", "webp", "mkv", "flac", "ogg",
    };

    [GeneratedRegex(@"^(?:[a-z0-9-]+\.)+([a-z]{2,24})(?::\d+)?(?:/\S*)?$", RegexOptions.IgnoreCase)]
    private static partial Regex BareDomainPattern();

    // "https:", "ms-settings:"; a single letter would be a Windows drive ("C:\…")
    [GeneratedRegex(@"^([a-z][a-z0-9+.\-]{1,31}):", RegexOptions.IgnoreCase)]
    private static partial Regex SchemePrefix();

    /// <summary>What was asked for, without the quotes and spaces speech and Claude wrap around it.</summary>
    public static string Clean(string target)
    {
        string t = target.Trim().Trim('"', '\'').Trim();
        if (t.Length == 0) throw new PluginException("Open what? Give me a web address, a folder or a file.");
        return t;
    }

    /// <summary>
    /// The link <paramref name="target"/> is, or null when it isn't one (a path, a name, a file: URL).
    /// Judged by its scheme even when the rest doesn't parse, so nothing slips past the list: link
    /// types outside <paramref name="allowedSchemes"/> have been used in attacks, and text Claude
    /// reads (web pages, emails, the screen) could ask for them.
    /// </summary>
    /// <param name="canOpen">For the refusal: what can be opened, e.g. "web pages, mail links, files and folders".</param>
    public static Uri? Link(string target, IReadOnlySet<string> allowedSchemes, string canOpen)
    {
        if (SchemePrefix().Match(target) is not { Success: true } scheme || scheme.Groups[1].Value.Equals("file", StringComparison.OrdinalIgnoreCase))
            return null;
        if (!allowedSchemes.Contains(scheme.Groups[1].Value))
            throw new PluginException($"I won't open '{scheme.Groups[1].Value}:' links. I can open {canOpen}.");
        if (!Uri.TryCreate(target, UriKind.Absolute, out var link)) throw new PluginException($"'{target}' doesn't look like a valid link.");
        return link;
    }

    /// <summary>What to say was opened: the site, or the whole link when it has none (mailto:, tel:).</summary>
    public static string Describe(Uri link, string target) => link.Host.Length > 0 ? link.Host : target;

    /// <summary>A web address said without the https:// ("youtube.com", "bbc.co.uk/news"), or null.</summary>
    public static string? WebAddress(string target) =>
        !target.Contains(' ') && BareDomainPattern().Match(target) is { Success: true } domain && !FileLikeEndings.Contains(domain.Groups[1].Value)
            ? "https://" + target
            : null;

    /// <summary>A folder people name: Downloads, Documents, the recycle bin…</summary>
    public static KnownFolder? NamedFolder(string target, IReadOnlyList<KnownFolder> folders) =>
        NameMatcher.Best(target, folders, f => f.Name, threshold: 0.7);

    public static PluginException NotFound(string target, IReadOnlyList<KnownFolder> folders) =>
        new($"I couldn't find '{target}'. I can open web addresses, full file or folder paths, and these folders: {Text.List(folders.Select(f => f.Name).Distinct(), 14)}.");

    public static PluginException IsAProgram() =>
        new("That file is a program or script, so I won't run it this way. To start an app, ask me to open it by name.");
}
