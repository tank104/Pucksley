using Pucksley.Plugins.Speech.Openspline;
using SkiaSharp;

namespace Pucksley.Plugins.Speech.Tests;

/// <summary>Avatar frames readied for the round AMOLED.</summary>
public class FaceFramesTests
{
    // A frame like Openspline's: 512 x 512, a not-quite-black background, a bright face in the middle
    static byte[] AvatarFrame()
    {
        using var surface = SKSurface.Create(new SKImageInfo(512, 512));
        surface.Canvas.Clear(new SKColor(12, 12, 12));
        using var paint = new SKPaint { Color = new SKColor(230, 190, 160) };
        surface.Canvas.DrawCircle(256, 256, 120, paint);
        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 80);
        return data.ToArray();
    }

    [Fact]
    public void Frames_are_scaled_to_464_with_true_black_outside_the_face_and_the_circle()
    {
        var output = FaceFrames.ForPuck(AvatarFrame());
        using var bitmap = SKBitmap.Decode(output);
        Assert.Equal((464, 464), (bitmap.Width, bitmap.Height));
        // Outside the circle, and the dark background inside it: black (off on the AMOLED), allowing for JPEG noise
        foreach (var (x, y) in new[] { (2, 2), (461, 461), (60, 232), (232, 30) })
        {
            var c = bitmap.GetPixel(x, y);
            Assert.True(c.Red <= 6 && c.Green <= 6 && c.Blue <= 6, $"({x},{y}) is {c}");
        }
        var face = bitmap.GetPixel(232, 232);
        Assert.True(face.Red > 200 && face.Green > 150, $"face is {face}");
    }

    [Fact]
    public void Frames_are_lighter_baseline_jpegs()
    {
        var input = AvatarFrame();
        var output = FaceFrames.ForPuck(input);
        Assert.Equal(new byte[] { 0xFF, 0xD8 }, output[..2]);
        // No progressive scan (SOF2): the puck's decoder only does baseline
        Assert.DoesNotContain(Enumerable.Range(0, output.Length - 1), i => output[i] == 0xFF && output[i + 1] == 0xC2);
    }

    [Fact]
    public void Something_that_is_not_an_image_passes_through()
    {
        var junk = new byte[] { 1, 2, 3, 4 };
        Assert.Same(junk, FaceFrames.ForPuck(junk));
    }
}
