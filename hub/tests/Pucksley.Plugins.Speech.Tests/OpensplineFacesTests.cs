using Pucksley.Plugins.Speech.Openspline;
using Pucksley.Sdk;
using SkiaSharp;

namespace Pucksley.Plugins.Speech.Tests;

/// <summary>The talking face's ready-made faces: all there, and shown on the settings page.</summary>
public class OpensplineFacesTests
{
    [Fact]
    public void Every_face_on_offer_has_its_picture_and_a_thumbnail()
    {
        Assert.Equal(20, OpensplinePlugin.FaceNames.Count);
        Assert.Contains(new OpensplineSettings().Face, OpensplinePlugin.FaceNames);
        foreach (var name in OpensplinePlugin.FaceNames)
        {
            using var picture = SKBitmap.Decode(OpensplinePlugin.FacePicture(name) ?? throw new Xunit.Sdk.XunitException($"{name} is missing"));
            using var thumb = SKBitmap.Decode(OpensplinePlugin.FacePicture(name, thumbnail: true) ?? throw new Xunit.Sdk.XunitException($"{name}'s thumbnail is missing"));
            Assert.Equal(picture.Width, picture.Height);
            Assert.True(picture.Width >= 512, $"{name} is only {picture.Width} px");
            Assert.True(thumb.Width <= 256, $"{name}'s thumbnail is {thumb.Width} px");
            // On black, like the puck: the corners are dark
            var corner = picture.GetPixel(4, 4);
            Assert.True(corner.Red + corner.Green + corner.Blue < 40, $"{name}'s background isn't black");
        }
    }

    [Fact]
    public void Only_the_faces_on_offer_are_looked_up()
    {
        Assert.Null(OpensplinePlugin.FacePicture("face-99"));
        Assert.Null(OpensplinePlugin.FacePicture("../Openspline.Faces.face-01"));
        Assert.Null(OpensplinePlugin.FacePicture(null));
    }

    [Fact]
    public async Task The_settings_page_gets_each_faces_thumbnail()
    {
        var plugin = new OpensplinePlugin();
        var ok = await plugin.HandleHttpAsync(Get("pictures/face/face-07"), CancellationToken.None);
        Assert.NotNull(ok);
        Assert.Equal("image/jpeg", ok!.ContentType);
        Assert.Equal(OpensplinePlugin.FacePicture("face-07", thumbnail: true), ok.Data);

        Assert.Null(await plugin.HandleHttpAsync(Get("pictures/face/face-99"), CancellationToken.None));
        Assert.Null(await plugin.HandleHttpAsync(Get("pictures/other/face-07"), CancellationToken.None));
    }

    static PluginHttpRequest Get(string path) => new("GET", path, new Dictionary<string, string>(), new Dictionary<string, string>(), null);
}
