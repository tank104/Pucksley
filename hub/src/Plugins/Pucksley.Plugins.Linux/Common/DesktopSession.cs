namespace Pucksley.Plugins.Linux.Common;

/// <summary>Who manages the windows: it decides how they're listed, focused and photographed.</summary>
internal enum Compositor
{
    /// <summary>No graphical session was found (the hub was started from a console or over SSH).</summary>
    None,
    Hyprland,
    /// <summary>Sway, or i3 under X11: they speak the same protocol.</summary>
    Sway,
    Kde,
    Gnome,
    /// <summary>Another Wayland compositor (river, niri, labwc, COSMIC…).</summary>
    OtherWayland,
    /// <summary>Another X11 window manager (Xfce, Cinnamon, MATE, Openbox…).</summary>
    OtherX11,
}

/// <summary>
/// The graphical session the hub runs in, read from its environment. Linux desktops differ in how
/// their windows are reached, so the PC plugin asks this which way to go.
/// </summary>
internal sealed record DesktopSession(Compositor Compositor, bool Wayland, bool X11, string Name)
{
    /// <summary>The session this process was started in.</summary>
    public static DesktopSession Current { get; } = Detect(Environment.GetEnvironmentVariable);

    public bool Graphical => Compositor != Compositor.None;

    /// <summary>wlroots-style compositors, where grim and wtype work.</summary>
    public bool Wlroots => Compositor is Compositor.Hyprland or Compositor.OtherWayland || (Compositor == Compositor.Sway && Wayland);

    public static DesktopSession Detect(Func<string, string?> env)
    {
        static bool Set(string? v) => !string.IsNullOrWhiteSpace(v);

        // "ubuntu:GNOME", "KDE", "Hyprland", "sway", "X-Cinnamon", "XFCE"…
        var desktops = (env("XDG_CURRENT_DESKTOP") ?? env("XDG_SESSION_DESKTOP") ?? env("DESKTOP_SESSION") ?? "")
            .Split([':', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        bool Is(string name) => desktops.Any(d => d.Contains(name, StringComparison.OrdinalIgnoreCase));

        string type = env("XDG_SESSION_TYPE")?.Trim().ToLowerInvariant() ?? "";
        bool wayland = Set(env("WAYLAND_DISPLAY")) || type == "wayland";
        // Wayland sessions set DISPLAY too (XWayland): X11 means X11 only
        bool x11 = !wayland && (Set(env("DISPLAY")) || type == "x11");

        Compositor compositor;
        if (Set(env("HYPRLAND_INSTANCE_SIGNATURE")) || Is("hyprland")) compositor = Compositor.Hyprland;
        else if (Set(env("SWAYSOCK")) || Is("sway") || Set(env("I3SOCK")) || Is("i3")) compositor = Compositor.Sway;
        else if (Is("kde") || Is("plasma")) compositor = Compositor.Kde;
        else if (Is("gnome") || Is("unity") || Is("pantheon") || Is("budgie")) compositor = Compositor.Gnome;
        else if (wayland) compositor = Compositor.OtherWayland;
        else if (x11) compositor = Compositor.OtherX11;
        else compositor = Compositor.None;

        // A named compositor without a display (variables left over in a service's environment) is no session
        if (!wayland && !x11) compositor = Compositor.None;
        if (compositor == Compositor.Hyprland) wayland = true;

        string name = compositor switch
        {
            Compositor.None => "no desktop session",
            Compositor.Hyprland => "Hyprland",
            Compositor.Sway => wayland ? "Sway" : "i3",
            Compositor.Kde => wayland ? "KDE Plasma (Wayland)" : "KDE Plasma (X11)",
            Compositor.Gnome => wayland ? "GNOME (Wayland)" : "GNOME (X11)",
            Compositor.OtherWayland => desktops.Length > 0 ? $"{desktops[0]} (Wayland)" : "Wayland",
            _ => desktops.Length > 0 ? $"{desktops[0]} (X11)" : "X11",
        };
        return new DesktopSession(compositor, wayland, x11, name);
    }
}
