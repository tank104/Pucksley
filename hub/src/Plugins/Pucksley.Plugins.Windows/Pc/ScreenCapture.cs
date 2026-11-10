using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using Pucksley.Sdk;
using static Pucksley.Plugins.Windows.Common.NativeMethods;

namespace Pucksley.Plugins.Windows.Pc;

internal enum ScreenArea
{
    /// <summary>
    /// The window in front (the default): the sharpest view, since a whole screen is scaled down
    /// to 1568 px (a 7680-px ultrawide would lose all its text). Falls back to <see cref="ActiveScreen"/>
    /// when no app window is in front.
    /// </summary>
    ActiveWindow,

    /// <summary>The monitor showing the window in front.</summary>
    ActiveScreen,
    All,
    Primary,
}

/// <summary>A captured screen, ready for Claude.</summary>
internal sealed record Screenshot(byte[] Jpeg, int Width, int Height, int SourceWidth, int SourceHeight, string Area, string? WindowTitle);

/// <summary>The size an image is sent at.</summary>
internal static class ScreenshotMath
{
    /// <summary>Claude downsizes anything with a longer side, so sending more only costs time.</summary>
    public const int MaxLongSide = 1568;

    /// <summary>Scales <paramref name="width"/>×<paramref name="height"/> down (never up) so the long side fits, keeping the aspect ratio.</summary>
    public static (int Width, int Height) Fit(int width, int height, int maxLongSide = MaxLongSide)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width), "An image needs a positive size.");
        int longSide = Math.Max(width, height);
        if (longSide <= maxLongSide) return (width, height);
        double scale = (double)maxLongSide / longSide;
        return (Math.Clamp((int)Math.Round(width * scale), 1, maxLongSide), Math.Clamp((int)Math.Round(height * scale), 1, maxLongSide));
    }
}

/// <summary>Screenshots in physical pixels, whatever the display scaling.</summary>
internal static class ScreenCapture
{
    const int SM_CMONITORS = 80;

    /// <summary>Blocking; run it on a pool thread (it switches that thread's DPI awareness while it works).</summary>
    public static Screenshot Capture(ScreenArea area)
    {
        // Per-monitor aware, so bounds and the copy are in real pixels even when the hub process
        // isn't DPI aware (otherwise a 150 % display would come back cropped)
        var previous = SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        try
        {
            var foreground = GetForegroundWindow();
            var bounds = Bounds(area, foreground, out string description);
            using var bitmap = CopyFromScreen(bounds);
            var (width, height) = ScreenshotMath.Fit(bounds.Width, bounds.Height);
            var jpeg = Jpeg.Encode(bitmap, width, height);
            string? title = foreground == 0 ? null : WindowText(foreground);
            return new Screenshot(jpeg, width, height, bounds.Width, bounds.Height, description, string.IsNullOrWhiteSpace(title) ? null : title);
        }
        finally
        {
            if (previous != 0) SetThreadDpiAwarenessContext(previous);
        }
    }

    static Rectangle Bounds(ScreenArea area, nint foreground, out string description)
    {
        var all = new Rectangle(GetSystemMetrics(SM_XVIRTUALSCREEN), GetSystemMetrics(SM_YVIRTUALSCREEN),
            GetSystemMetrics(SM_CXVIRTUALSCREEN), GetSystemMetrics(SM_CYVIRTUALSCREEN));
        switch (area)
        {
            case ScreenArea.All:
                description = "all screens";
                return all;
            case ScreenArea.Primary:
                description = "the primary screen";
                return MonitorBounds(MonitorFromPoint(default, MONITOR_DEFAULTTOPRIMARY));
            case ScreenArea.ActiveWindow when foreground != 0 && !IsIconic(foreground) && !IsShellWindow(foreground):
                var window = WindowBounds(foreground);
                window.Intersect(all);
                if (window.Width >= 16 && window.Height >= 16)
                {
                    description = "the active window";
                    return window;
                }
                goto default;
            default:
                description = GetSystemMetrics(SM_CMONITORS) > 1 ? "the screen with the active window" : "the screen";
                var monitor = foreground != 0
                    ? MonitorFromWindow(foreground, MONITOR_DEFAULTTOPRIMARY)
                    : MonitorFromPoint(default, MONITOR_DEFAULTTOPRIMARY);
                return MonitorBounds(monitor);
        }
    }

    // The desktop and taskbar count as "no window in front"
    static bool IsShellWindow(nint hwnd) => ClassName(hwnd) is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd";

    static Rectangle MonitorBounds(nint monitor)
    {
        var info = new MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitor, ref info)) throw new InvalidOperationException("Couldn't read the monitor's size.");
        return Rectangle.FromLTRB(info.rcMonitor.Left, info.rcMonitor.Top, info.rcMonitor.Right, info.rcMonitor.Bottom);
    }

    /// <summary>The visible frame (without the invisible resize borders GetWindowRect includes).</summary>
    static Rectangle WindowBounds(nint hwnd)
    {
        if (DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out RECT r, System.Runtime.InteropServices.Marshal.SizeOf<RECT>()) != 0)
            GetWindowRect(hwnd, out r);
        return Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
    }

    static Bitmap CopyFromScreen(Rectangle bounds)
    {
        var bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format24bppRgb);
        try
        {
            using var g = Graphics.FromImage(bitmap);
            g.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size, CopyPixelOperation.SourceCopy);
            return bitmap;
        }
        catch (Win32Exception e)
        {
            bitmap.Dispose();
            // BitBlt fails while the secure desktop (lock screen, UAC prompt) is showing
            throw new PluginException("I couldn't capture the screen; the PC may be locked or asleep.", e);
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
    }
}

/// <summary>JPEG encoding for images sent to Claude.</summary>
internal static class Jpeg
{
    static readonly ImageCodecInfo Codec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);

    /// <summary>Scales (high-quality bicubic) and encodes; transparency becomes white.</summary>
    public static byte[] Encode(Image source, int width, int height, long quality = 80)
    {
        bool redraw = width != source.Width || height != source.Height || Image.IsAlphaPixelFormat(source.PixelFormat);
        using var redrawn = redraw ? Redraw(source, width, height) : null;
        using var parameters = new EncoderParameters(1);
        parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, quality);
        using var stream = new MemoryStream();
        (redrawn ?? source).Save(stream, Codec, parameters);
        return stream.ToArray();
    }

    static Bitmap Redraw(Image source, int width, int height)
    {
        var target = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        using var g = Graphics.FromImage(target);
        g.Clear(Color.White);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.CompositingQuality = CompositingQuality.HighQuality;
        using var attributes = new ImageAttributes();
        attributes.SetWrapMode(WrapMode.TileFlipXY); // no dark fringe along the edges
        g.DrawImage(source, new Rectangle(0, 0, width, height), 0, 0, source.Width, source.Height, GraphicsUnit.Pixel, attributes);
        return target;
    }
}
