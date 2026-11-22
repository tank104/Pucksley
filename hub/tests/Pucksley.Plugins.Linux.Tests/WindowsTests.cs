using Pucksley.Plugins.Linux.Common;
using Pucksley.Plugins.Linux.Pc;
using Pucksley.Plugins.Linux.Pc.Windows;
using Pucksley.Plugins.Pc.Desktop;
using Pucksley.Sdk;

namespace Pucksley.Plugins.Linux.Tests;

/// <summary>What <c>hyprctl -j</c> prints (the shapes of Hyprland 0.56, with made-up windows).</summary>
internal static class HyprSamples
{
    public const string Clients = """
        [
          {"address":"0x55d0a1b2c3d0","mapped":true,"hidden":false,"at":[3242,665],"size":[1100,760],"workspace":{"id":3,"name":"3"},"floating":true,
           "class":"chromium","title":"Recipes - Chromium","initialClass":"chromium","pid":4001,"focusHistoryID":2},
          {"address":"0x55d0a1b2c3e0","mapped":true,"hidden":false,"at":[1780,415],"size":[1652,1300],"workspace":{"id":3,"name":"3"},"floating":false,
           "class":"com.mitchellh.ghostty","title":"~/code","initialClass":"com.mitchellh.ghostty","pid":4002,"focusHistoryID":0},
          {"address":"0x55d0a1b2c3f0","mapped":true,"hidden":false,"at":[0,0],"size":[800,600],"workspace":{"id":-98,"name":"special:minimized"},"floating":true,
           "class":"org.gnome.Nautilus","title":"Downloads","initialClass":"org.gnome.Nautilus","pid":4003,"focusHistoryID":1},
          {"address":"0x55d0a1b2c400","mapped":false,"hidden":false,"at":[0,0],"size":[10,10],"workspace":{"id":3,"name":"3"},
           "class":"ghost","title":"Not on screen yet","pid":4004,"focusHistoryID":3},
          {"address":"0x55d0a1b2c410","mapped":true,"hidden":false,"at":[0,0],"size":[10,10],"workspace":{"id":3,"name":"3"},
           "class":"","title":"","initialClass":"","pid":4005,"focusHistoryID":4}
        ]
        """;

    public const string ActiveWindow = """
        {"address":"0x55d0a1b2c3e0","mapped":true,"at":[1780,415],"size":[1652,1300],"workspace":{"id":3,"name":"3"},
         "class":"com.mitchellh.ghostty","title":"~/code","pid":4002,"focusHistoryID":0}
        """;

    public const string Monitors = """
        [
          {"id":0,"name":"eDP-1","width":2880,"height":1800,"x":0,"y":0,"scale":2.0,"transform":0,"focused":false,"disabled":false,"solitaryBlockedBy":["WINDOWED"]},
          {"id":1,"name":"DP-1","width":3840,"height":2160,"x":1440,"y":0,"scale":1.5,"transform":1,"focused":true,"disabled":false,"solitaryBlockedBy":["WINDOWED","CANDIDATE"]},
          {"id":2,"name":"HDMI-A-1","width":1920,"height":1080,"x":0,"y":0,"scale":1,"transform":0,"focused":false,"disabled":true}
        ]
        """;
}

/// <summary>hyprctl in memory.</summary>
internal sealed class FakeHyprctl : IHyprctl
{
    public Dictionary<string, string> Answers { get; } = new()
    {
        ["-j clients"] = HyprSamples.Clients,
        ["-j activewindow"] = HyprSamples.ActiveWindow,
        ["-j monitors"] = HyprSamples.Monitors,
    };

    public List<string> Asked { get; } = [];

    /// <summary>What a dispatch answers, by its first word.</summary>
    public Func<string, string> Dispatch { get; set; } = _ => "ok";

    public Task<string> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancel)
    {
        string command = string.Join(' ', arguments);
        Asked.Add(command);
        if (arguments[0] == "dispatch") return Task.FromResult(Dispatch(arguments[1]));
        return Task.FromResult(Answers.GetValueOrDefault(command, "unknown request"));
    }
}

public class HyprlandTests
{
    static readonly AppNamer Namer = (windowClass, pid) => (windowClass.Split('.')[^1], windowClass switch
    {
        "com.mitchellh.ghostty" => "Ghostty",
        "org.gnome.Nautilus" => "Files",
        "chromium" => "Chromium",
        _ => windowClass,
    });

    [Fact]
    public void Windows_are_listed_most_recently_used_first()
    {
        var windows = HyprlandWindows.ParseClients(HyprSamples.Clients, Namer, selfPid: 1);
        Assert.Equal(new[] { "Ghostty", "Files", "Chromium" }, windows.Select(w => w.AppName)); // not the unmapped one, nor the one with no title
        Assert.Equal("~/code", windows[0].Title);
        Assert.Equal(4002, windows[0].ProcessId);
        Assert.Equal("ghostty", windows[0].ProcessName);
        Assert.Equal(unchecked((nint)0x55d0a1b2c3e0), windows[0].Handle);
    }

    [Fact]
    public void The_hubs_own_windows_are_left_out() =>
        Assert.DoesNotContain(HyprlandWindows.ParseClients(HyprSamples.Clients, Namer, selfPid: 4002), w => w.ProcessId == 4002);

    [Fact]
    public void The_window_in_front_is_read_and_an_empty_workspace_has_none()
    {
        Assert.Equal("Ghostty", HyprlandWindows.ParseActiveWindow(HyprSamples.ActiveWindow, Namer)?.AppName);
        Assert.Null(HyprlandWindows.ParseActiveWindow("{}", Namer));
    }

    [Theory]
    [InlineData("0x55d0a1b2c3e0", 0x55d0a1b2c3e0)]
    [InlineData("55d0a1b2c3e0", 0x55d0a1b2c3e0)]
    public void A_window_address_survives_the_round_trip(string address, long expected)
    {
        Assert.Equal((nint)expected, HyprlandWindows.ParseAddress(address));
        Assert.Equal("0x55d0a1b2c3e0", HyprlandWindows.FormatAddress((nint)expected));
        Assert.Null(HyprlandWindows.ParseAddress("not an address"));
    }

    [Fact]
    public void A_locked_session_shows_in_the_monitors_reasons()
    {
        Assert.False(HyprlandWindows.ParseLocked(HyprSamples.Monitors));
        Assert.True(HyprlandWindows.ParseLocked("""[{"name":"DP-1","solitaryBlockedBy":["LOCK","WINDOWED","CANDIDATE"]}]"""));
        // A monitor with no workspace stops at that reason; an older Hyprland lists none: neither can say
        Assert.Null(HyprlandWindows.ParseLocked("""[{"name":"DP-1","solitaryBlockedBy":["WORKSPACE"]}]"""));
        Assert.Null(HyprlandWindows.ParseLocked("""[{"name":"DP-1"}]"""));
    }

    [Fact]
    public void Monitors_are_measured_in_layout_units()
    {
        var outputs = HyprlandWindows.ParseMonitors(HyprSamples.Monitors);
        Assert.Equal(2, outputs.Count); // the disabled one isn't there
        Assert.Equal(new ScreenOutput("eDP-1", new ScreenRect(0, 0, 1440, 900), false), outputs[0]);      // 2880×1800 at 2×
        Assert.Equal(new ScreenOutput("DP-1", new ScreenRect(1440, 0, 1440, 2560), true), outputs[1]);    // 3840×2160 at 1.5×, turned on its side
        Assert.Equal((new ScreenRect(1780, 415, 1652, 1300), "~/code"), HyprlandWindows.ParseActiveRect(HyprSamples.ActiveWindow));
        Assert.Equal((null, null), HyprlandWindows.ParseActiveRect("{}"));
    }

    [Fact]
    public async Task Focus_uses_the_lua_command_on_a_new_hyprland()
    {
        var hyprctl = new FakeHyprctl();
        var windows = new HyprlandWindows(hyprctl, Namer);
        var ghostty = (await windows.WindowsAsync(default))[0];
        Assert.True(await windows.FocusAsync(ghostty, default));
        Assert.Contains("""dispatch hl.dsp.focus({ window = "address:0x55d0a1b2c3e0" })""", hyprctl.Asked);
        Assert.DoesNotContain(hyprctl.Asked, a => a.StartsWith("dispatch focuswindow"));
    }

    [Fact]
    public async Task Focus_falls_back_to_the_old_command_on_an_old_hyprland()
    {
        var hyprctl = new FakeHyprctl { Dispatch = first => first == "focuswindow" ? "ok" : "Invalid dispatcher" };
        var windows = new HyprlandWindows(hyprctl, Namer);
        Assert.True(await windows.FocusAsync((await windows.WindowsAsync(default))[0], default));
        Assert.Contains("dispatch focuswindow address:0x55d0a1b2c3e0", hyprctl.Asked);
    }

    [Fact]
    public async Task Focus_that_did_not_take_is_reported()
    {
        var hyprctl = new FakeHyprctl();
        var windows = new HyprlandWindows(hyprctl, Namer);
        var chromium = (await windows.WindowsAsync(default))[2]; // the fake keeps Ghostty in front
        Assert.False(await windows.FocusAsync(chromium, default));
    }

    [Fact]
    public async Task A_hyprland_that_refuses_both_commands_says_why()
    {
        var hyprctl = new FakeHyprctl { Dispatch = _ => "error: no such thing\n" };
        var windows = new HyprlandWindows(hyprctl, Namer);
        var e = await Assert.ThrowsAsync<PluginException>(() => windows.ShowDesktopAsync(default));
        Assert.Contains("no such thing", e.Message);
    }

    [Fact]
    public async Task Showing_the_desktop_goes_to_an_empty_workspace()
    {
        var hyprctl = new FakeHyprctl();
        await new HyprlandWindows(hyprctl, Namer).ShowDesktopAsync(default);
        Assert.Contains("""dispatch hl.dsp.focus({ workspace = "empty" })""", hyprctl.Asked);
    }

    [Fact]
    public async Task The_layout_for_screenshots_has_the_monitors_and_the_window_in_front()
    {
        var layout = await new HyprlandWindows(new FakeHyprctl(), Namer).LayoutAsync(default);
        Assert.Equal(2, layout!.Outputs.Count);
        Assert.Equal(new ScreenRect(1780, 415, 1652, 1300), layout.ActiveWindow);
        Assert.Equal("~/code", layout.ActiveWindowTitle);
    }
}

public class SwayTests
{
    // A trimmed get_tree: one output, two workspaces, a floating window, the scratchpad
    const string Tree = """
        {"id":1,"type":"root","name":"root","focus":[3,2],"nodes":[
          {"id":2,"type":"output","name":"__i3","rect":{"x":0,"y":0,"width":1920,"height":1080},"nodes":[
            {"id":20,"type":"workspace","name":"__i3_scratch","floating_nodes":[
              {"id":21,"type":"floating_con","name":"Hidden terminal","app_id":"foot","pid":900,"focused":false,"rect":{"x":0,"y":0,"width":10,"height":10}}]}]},
          {"id":3,"type":"output","name":"DP-1","rect":{"x":0,"y":0,"width":2560,"height":1440},"focus":[5,4],"nodes":[
            {"id":4,"type":"workspace","name":"1","focus":[41,40],"nodes":[
              {"id":40,"type":"con","name":"Inbox - Thunderbird","app_id":"thunderbird","pid":910,"focused":false,"rect":{"x":0,"y":0,"width":1280,"height":1440}},
              {"id":41,"type":"con","name":"Budget.ods - LibreOffice Calc","app_id":null,"window_properties":{"class":"libreoffice-calc"},"pid":911,"focused":false,"rect":{"x":1280,"y":0,"width":1280,"height":1440}}]},
            {"id":5,"type":"workspace","name":"2","focus":[51,50],"nodes":[
              {"id":50,"type":"con","name":"~/code","app_id":"foot","pid":920,"focused":false,"rect":{"x":0,"y":0,"width":2560,"height":1440}}],
             "floating_nodes":[
              {"id":51,"type":"floating_con","name":"Calculator","app_id":"org.gnome.Calculator","pid":921,"focused":true,"rect":{"x":800,"y":400,"width":400,"height":600}}]}]}]}
        """;

    static readonly AppNamer Namer = (windowClass, pid) => (windowClass, windowClass);

    [Fact]
    public void Windows_are_walked_from_the_front_to_the_back()
    {
        var windows = SwayWindows.ParseTree(Tree, Namer);
        Assert.Equal(new[] { "Calculator", "~/code", "Budget.ods - LibreOffice Calc", "Inbox - Thunderbird" }, windows.Select(w => w.Window.Title));
        Assert.True(windows[0].Focused);
        Assert.Equal((nint)51, windows[0].Window.Handle);
        Assert.Equal("DP-1", windows[0].Output);
        Assert.Equal(new ScreenRect(800, 400, 400, 600), windows[0].Rect);
    }

    [Fact]
    public void An_x11_windows_class_is_used_where_there_is_no_app_id() =>
        Assert.Equal("libreoffice-calc", SwayWindows.ParseTree(Tree, Namer).Single(w => w.Window.ProcessId == 911).Window.AppName);

    [Fact]
    public void The_scratchpad_is_not_on_any_screen()
    {
        Assert.DoesNotContain(SwayWindows.ParseTree(Tree, Namer), w => w.Window.Title == "Hidden terminal");
        var output = Assert.Single(SwayWindows.ParseOutputs(Tree, "DP-1"));
        Assert.Equal(new ScreenOutput("DP-1", new ScreenRect(0, 0, 2560, 1440), true), output);
    }
}

public class X11Tests
{
    const string List = """
        0x01e00003 -1 1500   xfce4-panel.Xfce4-panel  studypc xfce4-panel
        0x03c00003  0 12345  navigator.Firefox     studypc Mozilla Firefox
        0x04a00007  1 12400  VirtualBox Manager.VirtualBox Manager  studypc Oracle VM VirtualBox Manager
        0x05200004  0 0      N/A                   N/A A window with no owner
        0x06000001  0 12500  xterm.XTerm           studypc
        """;

    static readonly AppNamer Namer = (windowClass, pid) => (windowClass.ToLowerInvariant(), windowClass);

    [Fact]
    public void Windows_are_read_from_wmctrl()
    {
        var windows = X11Windows.ParseList(List, "studypc", Namer);
        Assert.Equal(new[] { "Mozilla Firefox", "Oracle VM VirtualBox Manager", "A window with no owner" }, windows.Select(w => w.Title)); // not the panel, nor the untitled one
        Assert.Equal(("Firefox", 12345, (nint)0x03c00003), (windows[0].AppName, windows[0].ProcessId, windows[0].Handle));
        Assert.Equal("VirtualBox Manager", windows[1].AppName); // a class with a space in it
    }

    [Fact]
    public void Window_ids_are_read_from_xprop()
    {
        Assert.Equal(new nint[] { 0x1e00003, 0x3c00003, 0x4a00007 }, X11Windows.ParseIds("_NET_CLIENT_LIST_STACKING(WINDOW): window id # 0x1e00003, 0x3c00003, 0x4a00007"));
        Assert.Equal(new nint[] { 0x3c00003 }, X11Windows.ParseIds("_NET_ACTIVE_WINDOW(WINDOW): window id # 0x3c00003"));
        Assert.Empty(X11Windows.ParseIds("_NET_ACTIVE_WINDOW(WINDOW): window id # 0x0")); // nothing is active
        Assert.Empty(X11Windows.ParseIds("_NET_ACTIVE_WINDOW:  not found."));
        Assert.Equal((nint)62914563, X11Windows.ParseId("62914563")); // xdotool gives it in decimal
    }
}

public class DesktopSessionTests
{
    static DesktopSession Detect(params (string Key, string Value)[] env)
    {
        var map = env.ToDictionary(e => e.Key, e => e.Value);
        return DesktopSession.Detect(k => map.GetValueOrDefault(k));
    }

    [Fact]
    public void Omarchy_is_hyprland_on_wayland()
    {
        var s = Detect(("XDG_CURRENT_DESKTOP", "Hyprland"), ("XDG_SESSION_TYPE", "wayland"), ("WAYLAND_DISPLAY", "wayland-1"), ("DISPLAY", ":1"), ("HYPRLAND_INSTANCE_SIGNATURE", "abc"));
        Assert.Equal((Compositor.Hyprland, true, false, "Hyprland"), (s.Compositor, s.Wayland, s.X11, s.Name));
        Assert.True(s.Wlroots);
        Assert.True(s.Graphical);
    }

    [Theory]
    [InlineData("ubuntu:GNOME", "wayland", Compositor.Gnome, "GNOME (Wayland)", false)]
    [InlineData("KDE", "wayland", Compositor.Kde, "KDE Plasma (Wayland)", false)]
    [InlineData("sway", "wayland", Compositor.Sway, "Sway", true)]
    [InlineData("niri", "wayland", Compositor.OtherWayland, "niri (Wayland)", true)]
    public void Wayland_desktops_are_told_apart(string desktop, string type, object compositor, string name, bool wlroots)
    {
        var s = Detect(("XDG_CURRENT_DESKTOP", desktop), ("XDG_SESSION_TYPE", type), ("WAYLAND_DISPLAY", "wayland-0"));
        Assert.Equal(((Compositor)compositor, name, wlroots), (s.Compositor, s.Name, s.Wlroots));
    }

    [Theory]
    [InlineData("XFCE", Compositor.OtherX11, "XFCE (X11)")]
    [InlineData("KDE", Compositor.Kde, "KDE Plasma (X11)")]
    [InlineData("i3", Compositor.Sway, "i3")]
    [InlineData("X-Cinnamon", Compositor.OtherX11, "X-Cinnamon (X11)")]
    public void X11_desktops_are_told_apart(string desktop, object compositor, string name)
    {
        var s = Detect(("XDG_CURRENT_DESKTOP", desktop), ("XDG_SESSION_TYPE", "x11"), ("DISPLAY", ":0"));
        Assert.Equal(((Compositor)compositor, name, true, false), (s.Compositor, s.Name, s.X11, s.Wayland));
        Assert.False(s.Wlroots);
    }

    [Fact]
    public void A_console_or_a_service_has_no_desktop()
    {
        Assert.Equal(Compositor.None, Detect().Compositor);
        // Variables left behind in a service's environment, with no display to go with them
        var leftover = Detect(("XDG_CURRENT_DESKTOP", "GNOME"));
        Assert.Equal(Compositor.None, leftover.Compositor);
        Assert.False(leftover.Graphical);
    }

    [Fact]
    public void Each_desktop_gets_the_way_to_its_windows()
    {
        AppNamer namer = (c, _) => (c, c);
        Assert.IsType<HyprlandWindows>(LinuxDesktop.WindowsFor(Detect(("WAYLAND_DISPLAY", "w"), ("HYPRLAND_INSTANCE_SIGNATURE", "x")), namer));
        Assert.IsType<SwayWindows>(LinuxDesktop.WindowsFor(Detect(("WAYLAND_DISPLAY", "w"), ("SWAYSOCK", "/run/sway")), namer));
        Assert.IsType<X11Windows>(LinuxDesktop.WindowsFor(Detect(("DISPLAY", ":0"), ("XDG_CURRENT_DESKTOP", "GNOME")), namer));
        Assert.IsType<ClosedWindows>(LinuxDesktop.WindowsFor(Detect(("WAYLAND_DISPLAY", "w"), ("XDG_CURRENT_DESKTOP", "GNOME")), namer));
        Assert.IsType<ClosedWindows>(LinuxDesktop.WindowsFor(Detect(), namer));
    }

    [Fact]
    public async Task A_desktop_that_keeps_its_windows_to_itself_says_so()
    {
        var gnome = new ClosedWindows(Detect(("WAYLAND_DISPLAY", "w"), ("XDG_CURRENT_DESKTOP", "GNOME")));
        Assert.False(gnome.CanSeeWindows);
        Assert.Null(await gnome.ForegroundAsync(default)); // quietly: the context line just says nothing
        var e = await Assert.ThrowsAsync<PluginException>(() => gnome.WindowsAsync(default));
        Assert.Equal("GNOME (Wayland) doesn't let other programs see or switch its windows. I can still open apps, files and websites.", e.Message);
        var none = await Assert.ThrowsAsync<PluginException>(() => new ClosedWindows(Detect()).ShowDesktopAsync(default));
        Assert.Contains("isn't running inside a desktop session", none.Message);
    }
}
