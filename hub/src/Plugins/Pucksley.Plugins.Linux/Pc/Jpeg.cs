using Pucksley.Sdk;
using SkiaSharp;

namespace Pucksley.Plugins.Linux.Pc;

/// <summary>An image ready for Claude: JPEG bytes, the size they were sent at, and the size of the original.</summary>
internal sealed record JpegImage(byte[] Jpeg, int Width, int Height, int SourceWidth, int SourceHeight);

/// <summary>JPEG encoding for images sent to Claude (screenshots, pictures on the clipboard).</summary>
internal static class Jpeg
{
    /// <summary>
    /// Decodes a PNG, JPEG, WebP, GIF or BMP, scales it down to what Claude reads
    /// (<see cref="ScreenshotMath.Fit"/>) and encodes it; transparency becomes white.
    /// </summary>
    public static JpegImage FromEncoded(byte[] encoded, int quality = 80)
    {
        using var source = SKImage.FromEncodedData(encoded) ?? throw new PluginException("That picture is in a format I can't read.");
        var (width, height) = ScreenshotMath.Fit(source.Width, source.Height);
        using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque))
            ?? throw new PluginException("Couldn't make room for the picture.");
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.White);
        // Shrinking a lot (a 7680-px desktop to 1568) needs every source pixel averaged in, or small
        // text turns to speckle; a small change is sharper with a cubic filter.
        bool shrinksALot = width * 2 <= source.Width;
        var sampling = shrinksALot ? new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear) : new SKSamplingOptions(SKCubicResampler.Mitchell);
        canvas.DrawImage(source, new SKRect(0, 0, width, height), sampling);
        using var scaled = surface.Snapshot();
        using var data = scaled.Encode(SKEncodedImageFormat.Jpeg, quality);
        return new JpegImage(data.ToArray(), width, height, source.Width, source.Height);
    }
}
