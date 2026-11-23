using System.Runtime.InteropServices;
using Pucksley.Plugins.Windows.Common;
using Clipboard = System.Windows.Forms.Clipboard;

namespace Pucksley.Plugins.Windows.Pc;

/// <summary>The Windows clipboard. OLE clipboard calls need an STA thread, so each runs on its own.</summary>
internal static class ClipboardAccess
{
    static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    public static Task<ClipboardContents> ReadAsync(CancellationToken cancel) =>
        Sta.RunAsync(() => Retry(Read), Timeout, "The clipboard", cancel);

    public static Task WriteAsync(string text, CancellationToken cancel) =>
        Sta.RunAsync(() => Retry(() =>
        {
            Clipboard.SetText(text); // copies the data, so it outlives this thread
            return true;
        }), Timeout, "The clipboard", cancel);

    static ClipboardContents Read()
    {
        if (Clipboard.ContainsText()) return new ClipboardContents(Clipboard.GetText(), null, 0, 0, null);
        if (Clipboard.ContainsFileDropList())
            return new ClipboardContents(null, null, 0, 0, Clipboard.GetFileDropList().Cast<string>().ToList());
        if (Clipboard.ContainsImage())
        {
            using var image = Clipboard.GetImage();
            if (image is not null)
            {
                var (width, height) = ScreenshotMath.Fit(image.Width, image.Height);
                return new ClipboardContents(null, Jpeg.Encode(image, width, height), image.Width, image.Height, null);
            }
        }
        return new ClipboardContents(null, null, 0, 0, null);
    }

    // Another app can hold the clipboard open for a moment ("Requested Clipboard operation did not succeed")
    static T Retry<T>(Func<T> action)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                return action();
            }
            catch (ExternalException) when (attempt < 5)
            {
                Thread.Sleep(100);
            }
        }
    }
}
