namespace Pucksley.Plugins.Pc.Desktop;

/// <summary>What's on the clipboard: text, an image (as a JPEG for Claude), files, or nothing.</summary>
internal sealed record ClipboardContents(string? Text, byte[]? ImageJpeg, int ImageWidth, int ImageHeight, IReadOnlyList<string>? Files)
{
    public static ClipboardContents Empty { get; } = new(null, null, 0, 0, null);
}
