using SkiaSharp;

namespace Pucksley.Linux;

/// <summary>
/// Pucksley's icon, drawn at whatever size is asked for: a pale ring, a dark face and a warm glow
/// (the same picture as the page's favicon and the Windows tray icon).
/// </summary>
internal static class PuckIcon
{
    static SKBitmap Draw(int size)
    {
        var bitmap = new SKBitmap(new SKImageInfo(size, size, SKColorType.Bgra8888, SKAlphaType.Unpremul));
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);
        float scale = size / 64f, centre = size / 2f;

        using var ring = new SKPaint { IsAntialias = true };
        ring.Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(size, size),
            [new SKColor(0xf1, 0xed, 0xe6), new SKColor(0xc3, 0xbc, 0xb0), new SKColor(0xeb, 0xe6, 0xde)], [0f, 0.5f, 1f], SKShaderTileMode.Clamp);
        canvas.DrawCircle(centre, centre, 31 * scale, ring);

        using var face = new SKPaint { IsAntialias = true, Color = new SKColor(0x16, 0x15, 0x14) };
        canvas.DrawCircle(centre, centre, 25.5f * scale, face);

        using var glow = new SKPaint { IsAntialias = true };
        glow.Shader = SKShader.CreateRadialGradient(new SKPoint(centre, centre), 17 * scale,
            [new SKColor(0xf6, 0xb9, 0x9c), new SKColor(0xd9, 0x77, 0x57), new SKColor(0xd9, 0x77, 0x57, 0x40), new SKColor(0xd9, 0x77, 0x57, 0x00)],
            [0f, 0.38f, 0.7f, 1f], SKShaderTileMode.Clamp);
        canvas.DrawCircle(centre, centre, 17 * scale, glow);
        return bitmap;
    }

    /// <summary>As a tray wants it: one pixel after another, each as alpha, red, green, blue.</summary>
    public static byte[] Argb(int size)
    {
        using var bitmap = Draw(size);
        var pixels = bitmap.Pixels;
        var bytes = new byte[pixels.Length * 4];
        for (int i = 0; i < pixels.Length; i++)
        {
            bytes[i * 4] = pixels[i].Alpha;
            bytes[i * 4 + 1] = pixels[i].Red;
            bytes[i * 4 + 2] = pixels[i].Green;
            bytes[i * 4 + 3] = pixels[i].Blue;
        }
        return bytes;
    }

    public static byte[] Png(int size)
    {
        using var bitmap = Draw(size);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
}
