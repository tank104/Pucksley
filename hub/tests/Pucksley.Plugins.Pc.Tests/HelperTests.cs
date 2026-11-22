using Pucksley.Plugins.Pc.Common;
using Pucksley.Plugins.Pc.Screen;
using Pucksley.Sdk;

namespace Pucksley.Plugins.Pc.Tests;

public class ScreenshotMathTests
{
    [Theory]
    [InlineData(3840, 2160, 1568, 882)]  // 4K
    [InlineData(2560, 1440, 1568, 882)]
    [InlineData(1920, 1080, 1568, 882)]
    [InlineData(1280, 720, 1280, 720)]   // already small enough: unchanged
    [InlineData(1568, 1000, 1568, 1000)] // exactly the limit
    [InlineData(1080, 1920, 882, 1568)]  // portrait
    [InlineData(5760, 1080, 1568, 294)]  // three monitors side by side
    [InlineData(3440, 1440, 1568, 656)]  // ultrawide
    [InlineData(10000, 1, 1568, 1)]      // never below one pixel
    public void Fit_scales_the_long_side_down_to_1568(int width, int height, int expectedWidth, int expectedHeight) =>
        Assert.Equal((expectedWidth, expectedHeight), ScreenshotMath.Fit(width, height));

    [Fact]
    public void Fit_keeps_the_aspect_ratio()
    {
        var (w, h) = ScreenshotMath.Fit(2880, 1800);
        Assert.Equal(1568, w);
        Assert.InRange(Math.Abs(w / (double)h - 2880 / 1800.0), 0, 0.005);
    }

    [Fact]
    public void Fit_rejects_empty_images() => Assert.Throws<ArgumentOutOfRangeException>(() => ScreenshotMath.Fit(0, 100));

    [Theory]
    [InlineData(null, nameof(ScreenArea.ActiveWindow))]
    [InlineData("", nameof(ScreenArea.ActiveWindow))]
    [InlineData("screen", nameof(ScreenArea.ActiveScreen))]
    [InlineData("all", nameof(ScreenArea.All))]
    [InlineData("All Screens", nameof(ScreenArea.All))]
    [InlineData("primary", nameof(ScreenArea.Primary))]
    [InlineData("active_window", nameof(ScreenArea.ActiveWindow))]
    [InlineData("Active Window", nameof(ScreenArea.ActiveWindow))]
    public void Screen_words_map_to_an_area(string? screen, string expected) =>
        Assert.Equal(Enum.Parse<ScreenArea>(expected), ScreenAreas.Parse(screen));

    [Fact]
    public void An_unknown_screen_word_is_explained() =>
        Assert.Contains("all, primary or active_window", Assert.Throws<PluginException>(() => ScreenAreas.Parse("left one")).Message);
}

public class TextTests
{
    [Theory]
    [InlineData("A short line", 24, "A short line")]
    [InlineData("Output: Headphones (WH-1000XM4 Stereo)", 24, "Output: Headphones (WH-…")]
    public void Clip_trims_to_the_limit(string text, int max, string expected)
    {
        Assert.Equal(expected, Text.Clip(text, max));
        Assert.True(Text.Clip(text, max).Length <= max);
    }

    [Theory]
    [InlineData("Speakers (Realtek(R) Audio)", "Speakers")]
    [InlineData("DELL U2720Q (NVIDIA High Definition Audio)", "DELL U2720Q")]
    [InlineData("Plain", "Plain")]
    public void Short_device_name_is_the_part_people_say(string name, string expected) => Assert.Equal(expected, Text.ShortDeviceName(name));

    [Theory]
    [InlineData(3 * 1440 + 4 * 60, "3 days 4 hours")]
    [InlineData(61, "1 hour 1 minute")]
    [InlineData(5, "5 minutes")]
    [InlineData(0, "0 minutes")]
    public void Durations_read_naturally(int minutes, string expected) =>
        Assert.Equal(expected, Text.Duration(TimeSpan.FromMinutes(minutes)));

    [Theory]
    [InlineData("the Spotify app", "the Spotify")]
    [InlineData("Calculator program", "Calculator")]
    [InlineData("app", "app")] // nothing else was said: keep it
    public void App_words_are_dropped(string spoken, string expected) => Assert.Equal(expected, Text.StripAppWords(spoken));

    [Fact]
    public void Lists_are_deduplicated_and_cut_short()
    {
        Assert.Equal("a, b", Text.List(["a", "A", "b", " "]));
        Assert.Equal("1, 2 and 2 more", Text.List(["1", "2", "3", "4"], max: 2));
    }
}
