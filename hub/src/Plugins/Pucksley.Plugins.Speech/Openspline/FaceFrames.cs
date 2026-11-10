using SkiaSharp;

namespace Pucksley.Plugins.Speech.Openspline;

/// <summary>
/// Readies an avatar frame for the round AMOLED: scaled to 464 x 464 (the most a puck decodes in
/// time for 25 frames a second: multiples of 8, the panel is 466), near-black made black (the panel
/// shows black as off, so the background vanishes), cut to the circle, and re-encoded lighter
/// (quality 70, 4:2:0 baseline). About 13 KB a frame; the puck decodes it in ~25 ms.
/// </summary>
public static class FaceFrames
{
    public const int Size = 464;
    public const int Quality = 70;

    static readonly byte[] Crush = Enumerable.Range(0, 256)
        .Select(v => (byte)(v < 20 ? 0 : (v - 20) * 255 / 235)).ToArray();

    static readonly SKSamplingOptions Sampling = new(SKCubicResampler.Mitchell);

    /// <summary>The puck's version of <paramref name="jpeg"/>; the input unchanged if it can't be read.</summary>
    public static byte[] ForPuck(byte[] jpeg)
    {
        using var source = SKImage.FromEncodedData(jpeg);
        if (source is null) return jpeg;
        using var surface = SKSurface.Create(new SKImageInfo(Size, Size, SKColorType.Rgba8888, SKAlphaType.Premul));
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Black);
        using (var circle = new SKPath())
        {
            circle.AddCircle(Size / 2f, Size / 2f, Size / 2f);
            canvas.ClipPath(circle, antialias: true);
        }
        using (var paint = new SKPaint { ColorFilter = SKColorFilter.CreateTable(Crush) })
            canvas.DrawImage(source, new SKRect(0, 0, Size, Size), Sampling, paint);
        using var frame = surface.Snapshot();
        using var data = frame.Encode(SKEncodedImageFormat.Jpeg, Quality);
        return data?.ToArray() ?? jpeg;
    }
}
