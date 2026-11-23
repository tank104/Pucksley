namespace Pucksley.Plugins.Linux.Pc.Windows;

/// <summary>A rectangle on the desktop, in the compositor's layout coordinates.</summary>
internal readonly record struct ScreenRect(int X, int Y, int Width, int Height)
{
    public bool IsEmpty => Width <= 0 || Height <= 0;

    public ScreenRect Intersect(ScreenRect other)
    {
        int left = Math.Max(X, other.X), top = Math.Max(Y, other.Y);
        int right = Math.Min(X + Width, other.X + other.Width), bottom = Math.Min(Y + Height, other.Y + other.Height);
        return new ScreenRect(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
    }

    public static ScreenRect Union(IEnumerable<ScreenRect> rects)
    {
        var all = rects.Where(r => !r.IsEmpty).ToList();
        if (all.Count == 0) return default;
        int left = all.Min(r => r.X), top = all.Min(r => r.Y);
        return new ScreenRect(left, top, all.Max(r => r.X + r.Width) - left, all.Max(r => r.Y + r.Height) - top);
    }
}

/// <summary>A monitor: its connector name ("DP-1") and where it sits on the desktop.</summary>
internal sealed record ScreenOutput(string Name, ScreenRect Bounds, bool Focused);

/// <summary>What a screenshot needs to know about the desktop's layout.</summary>
internal sealed record ScreenLayout(IReadOnlyList<ScreenOutput> Outputs, ScreenRect? ActiveWindow, string? ActiveWindowTitle);

/// <summary>
/// The windows of one kind of Linux desktop. Linux has no single way to list, focus or measure
/// windows: each compositor has its own, and some (GNOME and KDE on Wayland) offer other programs
/// none at all. An implementation throws <see cref="Pucksley.Sdk.PluginException"/>, saying so,
/// for what its desktop can't do.
/// </summary>
internal interface IWindowManager
{
    /// <summary>False when this desktop keeps its windows to itself: the tools then explain rather than fail oddly.</summary>
    bool CanSeeWindows { get; }

    /// <summary>Windowed apps, front-most first.</summary>
    Task<IReadOnlyList<AppWindow>> WindowsAsync(CancellationToken cancel);

    /// <summary>The window in front, or null for the bare desktop (or when the desktop won't say).</summary>
    Task<AppWindow?> ForegroundAsync(CancellationToken cancel);

    /// <summary>Brings a window to the front; false when the desktop wouldn't.</summary>
    Task<bool> FocusAsync(AppWindow window, CancellationToken cancel);

    Task ShowDesktopAsync(CancellationToken cancel);

    /// <summary>True, false, or null when this desktop can't tell (logind is asked instead).</summary>
    Task<bool?> IsLockedAsync(CancellationToken cancel);

    /// <summary>Monitors and the window in front, for screenshots; null when this desktop can't say.</summary>
    Task<ScreenLayout?> LayoutAsync(CancellationToken cancel);
}

/// <summary>How a window's app is named: the class it announces and its process, to the name people use.</summary>
internal delegate (string ProcessName, string AppName) AppNamer(string windowClass, int processId);
