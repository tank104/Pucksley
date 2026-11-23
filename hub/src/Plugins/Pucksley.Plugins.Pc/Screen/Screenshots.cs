using Pucksley.Sdk;

namespace Pucksley.Plugins.Pc.Screen;

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

/// <summary>The words a screenshot tool takes for <see cref="ScreenArea"/>.</summary>
internal static class ScreenAreas
{
    public static ScreenArea Parse(string? screen) => screen?.Trim().ToLowerInvariant().Replace(' ', '_') switch
    {
        null or "" or "active_window" or "window" or "active" => ScreenArea.ActiveWindow,
        "active_screen" or "screen" or "current" => ScreenArea.ActiveScreen,
        "all" or "all_screens" or "everything" => ScreenArea.All,
        "primary" or "main" => ScreenArea.Primary,
        _ => throw new PluginException($"'{screen}' isn't a screen I know. Use all, primary or active_window."),
    };
}
