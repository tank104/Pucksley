using System.Text;
using Pucksley.Plugins.Linux.Common;
using Pucksley.Sdk;

namespace Pucksley.Plugins.Linux.Pc;

/// <summary>
/// The clipboard, through the desktop's own command-line clients: wl-clipboard on Wayland, xclip
/// (or xsel, for text) on X11.
/// </summary>
internal sealed class ClipboardAccess(DesktopSession session)
{
    static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    static readonly string[] ImageTypes = ["image/png", "image/jpeg", "image/webp", "image/bmp", "image/gif"];

    /// <summary>What kind of thing the clipboard holds, judged from the formats it's offered in.</summary>
    internal enum Holds { Nothing, Text, Files, Image }

    PluginException Missing() => new(session.Wayland
        ? "Using the clipboard on this desktop needs wl-clipboard installed (wl-copy and wl-paste)."
        : "Using the clipboard on this desktop needs xclip installed.");

    public async Task<ClipboardContents> ReadAsync(CancellationToken cancel)
    {
        bool wayland = session.Wayland && Programs.Has("wl-paste");
        if (!wayland && !Programs.Has("xclip"))
        {
            // xsel knows text only
            if (!session.Wayland && Programs.Has("xsel")) return TextContents(await PasteAsync("xsel", ["--clipboard", "--output"], cancel).ConfigureAwait(false));
            throw Missing();
        }
        string paste = wayland ? "wl-paste" : "xclip";
        string[] As(string type) => wayland ? ["--no-newline", "--type", type] : ["-selection", "clipboard", "-o", "-t", type];

        var listing = await Programs.RunAsync(paste, wayland ? ["--list-types"] : ["-selection", "clipboard", "-o", "-t", "TARGETS"], Timeout, cancel).ConfigureAwait(false);
        if (listing.ExitCode != 0) return ClipboardContents.Empty; // "Nothing is copied"
        var types = listing.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var (holds, type) = Classify(types);
        switch (holds)
        {
            case Holds.Files:
                var files = ParseUriList(Encoding.UTF8.GetString(await PasteAsync(paste, As(type!), cancel).ConfigureAwait(false) ?? []));
                return files.Count > 0 ? new ClipboardContents(null, null, 0, 0, files) : ClipboardContents.Empty;
            case Holds.Image:
                var picture = await PasteAsync(paste, As(type!), cancel).ConfigureAwait(false);
                if (picture is not { Length: > 0 }) return ClipboardContents.Empty;
                var image = Jpeg.FromEncoded(picture);
                return new ClipboardContents(null, image.Jpeg, image.SourceWidth, image.SourceHeight, null);
            case Holds.Text:
                return TextContents(await PasteAsync(paste, wayland ? ["--no-newline"] : ["-selection", "clipboard", "-o"], cancel).ConfigureAwait(false));
            default:
                return ClipboardContents.Empty;
        }
    }

    static ClipboardContents TextContents(byte[]? bytes) => new(bytes is null ? "" : Encoding.UTF8.GetString(bytes), null, 0, 0, null);

    /// <summary>
    /// Files copied in a file manager come as a list of addresses (and as text too, which is why
    /// they're looked for first). A picture counts when there's no text with it: spreadsheet cells
    /// are offered as both, and the text is what was meant.
    /// </summary>
    internal static (Holds Holds, string? Type) Classify(IReadOnlyList<string> types)
    {
        bool Has(string type) => types.Contains(type, StringComparer.OrdinalIgnoreCase);
        if (Has("text/uri-list")) return (Holds.Files, "text/uri-list");
        bool text = types.Any(t => t.StartsWith("text/plain", StringComparison.OrdinalIgnoreCase) || t is "UTF8_STRING" or "STRING" or "TEXT");
        if (!text && ImageTypes.FirstOrDefault(Has) is { } image) return (Holds.Image, image);
        if (text) return (Holds.Text, null);
        return (Holds.Nothing, null);
    }

    /// <summary>"file:///home/me/My%20Notes.txt" lines → paths. Other addresses (a web link) are kept as they are.</summary>
    internal static List<string> ParseUriList(string list)
    {
        var files = new List<string>();
        foreach (var raw in list.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            files.Add(Uri.TryCreate(line, UriKind.Absolute, out var uri) && uri.IsFile ? uri.LocalPath : line);
        }
        return files;
    }

    /// <summary>The clipboard's bytes, exactly (text keeps its spaces and line breaks); null when there's nothing.</summary>
    static async Task<byte[]?> PasteAsync(string program, string[] arguments, CancellationToken cancel)
    {
        var result = await Programs.RunBinaryAsync(program, arguments, Timeout, cancel).ConfigureAwait(false);
        return result.ExitCode == 0 ? result.Output : null;
    }

    public async Task WriteAsync(string text, CancellationToken cancel)
    {
        string[] copy = session.Wayland && Programs.Has("wl-copy") ? [Programs.Find("wl-copy")!]
            : Programs.Has("xclip") ? [Programs.Find("xclip")!, "-selection", "clipboard", "-in"]
            : !session.Wayland && Programs.Has("xsel") ? [Programs.Find("xsel")!, "--clipboard", "--input"]
            : throw Missing();
        // These stay running in the background to hand the text to whoever pastes it, and would keep
        // our pipes open for as long: the shell points their output elsewhere first.
        var result = await ProcessRunner.RunAsync("/bin/sh", ["-c", "exec \"$@\" >/dev/null 2>&1", "sh", .. copy], Timeout, cancel, input: text).ConfigureAwait(false);
        if (result.ExitCode != 0) throw new PluginException($"The clipboard didn't take the text ({Path.GetFileName(copy[0])} gave exit code {result.ExitCode}).");
    }
}
