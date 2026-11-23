using Pucksley.Plugins.Linux.Common;
using Pucksley.Sdk;
using Tmds.DBus.Protocol;

namespace Pucksley.Plugins.Linux.Pc.Windows;

/// <summary>
/// A desktop that keeps its windows to itself: GNOME and KDE Plasma on Wayland give other programs
/// no way to list or switch windows (by design, for privacy), and there may be no desktop at all.
/// The window tools say so; KDE can still be asked to show the desktop.
/// </summary>
internal sealed class ClosedWindows(DesktopSession session) : IWindowManager
{
    public bool CanSeeWindows => false;

    PluginException Closed() => new(session.Graphical
        ? $"{session.Name} doesn't let other programs see or switch its windows. I can still open apps, files and websites."
        : "The hub isn't running inside a desktop session on this PC, so there are no windows for it to see.");

    public Task<IReadOnlyList<AppWindow>> WindowsAsync(CancellationToken cancel) => throw Closed();

    public Task<AppWindow?> ForegroundAsync(CancellationToken cancel) => Task.FromResult<AppWindow?>(null);

    public Task<bool> FocusAsync(AppWindow window, CancellationToken cancel) => throw Closed();

    public async Task ShowDesktopAsync(CancellationToken cancel)
    {
        if (session.Compositor != Compositor.Kde) throw Closed();
        try
        {
            // The same as pressing Plasma's "Show Desktop" shortcut
            await Bus.Session.CallAsync("org.kde.kglobalaccel", "/component/kwin", "org.kde.kglobalaccel.Component", "invokeShortcut",
                "s", (ref MessageWriter w) => w.WriteString("Show Desktop"), cancel: cancel).ConfigureAwait(false);
        }
        catch (DBusExceptionBase e)
        {
            throw new PluginException($"Plasma wouldn't show the desktop ({e.Message}).", e);
        }
    }

    public Task<bool?> IsLockedAsync(CancellationToken cancel) => Task.FromResult<bool?>(null);

    public Task<ScreenLayout?> LayoutAsync(CancellationToken cancel) => Task.FromResult<ScreenLayout?>(null);
}
