using Pucksley.Plugins.Linux.Common;
using Pucksley.Plugins.Linux.Pc;
using Pucksley.Plugins.Linux.Pc.Windows;
using Pucksley.Plugins.Pc.Desktop;
using Pucksley.Sdk;
using Pucksley.TestSupport;
using SkiaSharp;

namespace Pucksley.Plugins.Linux.Tests;

public class PowerTests
{
    static DesktopSession Session(Compositor compositor, bool wayland = true) => new(compositor, wayland, !wayland, compositor.ToString());

    static string Show(Power.LockAttempt a) => a.Program is not null ? string.Join(' ', [a.Program, .. a.Arguments]) : $"{a.Service} {a.Path}";

    [Fact]
    public void Omarchy_locks_its_own_way_first()
    {
        var plan = Power.LockPlan(Session(Compositor.Hyprland), p => p is "omarchy-system-lock" or "loginctl");
        Assert.Equal(new[] { "omarchy-system-lock", "org.freedesktop.ScreenSaver /org/freedesktop/ScreenSaver", "org.freedesktop.ScreenSaver /ScreenSaver", "loginctl lock-session" }, plan.Select(Show));
    }

    [Fact]
    public void Each_desktop_has_its_locker()
    {
        Assert.Equal("hyprlock", Show(Power.LockPlan(Session(Compositor.Hyprland), p => p == "hyprlock")[0]));
        Assert.Equal("swaylock -f", Show(Power.LockPlan(Session(Compositor.Sway), p => p is "swaylock" or "i3lock")[0]));
        Assert.Equal("i3lock", Show(Power.LockPlan(Session(Compositor.Sway, wayland: false), p => p is "swaylock" or "i3lock")[0]));
        Assert.Equal("org.gnome.ScreenSaver /org/gnome/ScreenSaver", Show(Power.LockPlan(Session(Compositor.Gnome), _ => false)[0]));
        Assert.Equal("org.freedesktop.ScreenSaver /org/freedesktop/ScreenSaver", Show(Power.LockPlan(Session(Compositor.Kde), _ => false)[0]));
    }

    [Fact]
    public void Whatever_the_desktop_logind_is_asked_last()
    {
        var plan = Power.LockPlan(Session(Compositor.OtherX11, wayland: false), p => p is "loginctl" or "xdg-screensaver");
        Assert.Equal(new[] { "loginctl lock-session", "xdg-screensaver lock" }, plan.TakeLast(2).Select(Show));
    }

    [Fact]
    public void Powering_off_goes_the_desktops_tidy_way_first()
    {
        string[] Plan(Compositor c, bool restart, params string[] installed) =>
            Power.PowerOffPlan(Session(c), restart, installed.Contains).Select(command => string.Join(' ', command)).ToArray();

        Assert.Equal(new[] { "omarchy-system shutdown", "systemctl poweroff", "loginctl poweroff" }, Plan(Compositor.Hyprland, false, "omarchy-system", "systemctl", "loginctl"));
        Assert.Equal(new[] { "omarchy-system reboot", "systemctl reboot" }, Plan(Compositor.Hyprland, true, "omarchy-system", "systemctl"));
        Assert.Equal(new[] { "gnome-session-quit --power-off --no-prompt", "systemctl poweroff" }, Plan(Compositor.Gnome, false, "gnome-session-quit", "systemctl"));
        Assert.Equal(new[] { "systemctl reboot" }, Plan(Compositor.Sway, true, "gnome-session-quit", "systemctl")); // GNOME's only on GNOME
        Assert.Empty(Plan(Compositor.Sway, false));
    }

    [Fact]
    public async Task A_countdown_runs_its_action_when_due()
    {
        var due = new TaskCompletionSource();
        var countdown = new ShutdownCountdown((_, _) => due.Task);
        int ran = 0;
        countdown.Start(TimeSpan.FromMinutes(1), restart: false, () =>
        {
            Interlocked.Increment(ref ran);
            return Task.CompletedTask;
        });
        Assert.True(countdown.Pending);
        Assert.Equal(0, ran);
        due.SetResult();
        await Wait.UntilAsync(() => Volatile.Read(ref ran) == 1);
        Assert.False(countdown.Pending);
        Assert.False(countdown.Cancel(out _)); // nothing left to cancel
    }

    [Fact]
    public async Task A_cancelled_countdown_never_runs()
    {
        int ran = 0;
        var countdown = new ShutdownCountdown();
        countdown.Start(TimeSpan.FromMilliseconds(150), restart: true, () =>
        {
            Interlocked.Increment(ref ran);
            return Task.CompletedTask;
        });
        Assert.True(countdown.Cancel(out bool restart));
        Assert.True(restart);
        await Task.Delay(400);
        Assert.Equal(0, ran);
        Assert.False(countdown.Pending);
    }

    [Fact]
    public void Only_one_countdown_at_a_time()
    {
        var countdown = new ShutdownCountdown((_, cancel) => Task.Delay(Timeout.Infinite, cancel));
        countdown.Start(TimeSpan.FromMinutes(5), false, () => Task.CompletedTask);
        var e = Assert.Throws<PluginException>(() => countdown.Start(TimeSpan.FromMinutes(1), true, () => Task.CompletedTask));
        Assert.Contains("already counting down", e.Message);
        Assert.True(countdown.Cancel(out bool restart));
        Assert.False(restart); // the first one's
        countdown.Start(TimeSpan.FromMinutes(1), true, () => Task.CompletedTask); // and now another can start
        countdown.Cancel(out _);
    }
}

public class ClipboardTests
{
    [Theory]
    [InlineData(new[] { "text/plain", "text/plain;charset=utf-8", "UTF8_STRING" }, "Text", null)]
    [InlineData(new[] { "text/uri-list", "text/plain", "x-special/gnome-copied-files" }, "Files", "text/uri-list")] // files copied in a file manager
    [InlineData(new[] { "image/png", "text/html" }, "Image", "image/png")]                                          // a picture copied from a web page
    [InlineData(new[] { "text/plain", "image/png", "text/html" }, "Text", null)]                                     // spreadsheet cells: the text is what's meant
    [InlineData(new[] { "image/jpeg", "image/bmp" }, "Image", "image/jpeg")]
    [InlineData(new[] { "TARGETS", "TIMESTAMP", "application/x-something" }, "Nothing", null)]
    [InlineData(new string[0], "Nothing", null)]
    public void What_the_clipboard_holds_is_judged_from_its_formats(string[] types, string holds, string? type) =>
        Assert.Equal((Enum.Parse<ClipboardAccess.Holds>(holds), type), ClipboardAccess.Classify(types));

    [Fact]
    public void Copied_files_become_paths() =>
        Assert.Equal(
            new[] { "/home/me/My Notes.txt", "/home/me/Pictures", "https://example.com/x" },
            ClipboardAccess.ParseUriList("file:///home/me/My%20Notes.txt\r\n# a comment\r\nfile:///home/me/Pictures\r\nhttps://example.com/x\r\n\r\n"));
}

public class TextTyperTests
{
    static string[] Show(string program, string text) =>
        TextTyper.Steps(program, text).Select(s => string.Join(' ', [s.Program, .. s.Arguments]) + (s.Input is null ? "" : $" <<{s.Input}>>")).ToArray();

    [Fact]
    public void Wtype_types_text_after_a_double_dash() =>
        Assert.Equal(new[] { "wtype -d 4 -- Hello there" }, Show("wtype", "Hello there"));

    [Fact]
    public void Wtype_presses_enter_and_tab_as_keys_before_the_text_that_follows()
    {
        Assert.Equal(
            new[] { "wtype -d 4 -- Dear Sam,", "wtype -d 4 -k Return -k Return -- Thanks", "wtype -d 4 -k Tab -- Andrew", "wtype -d 4 -k Return" },
            Show("wtype", "Dear Sam,\r\n\r\nThanks\tAndrew\n"));
    }

    [Fact]
    public void A_line_that_starts_with_a_dash_is_still_text() =>
        Assert.Equal(new[] { "wtype -d 4 -- --help me", "wtype -d 4 -k Return -- - a list item" }, Show("wtype", "--help me\n- a list item"));

    [Fact]
    public void Xdotool_and_ydotool_take_the_text_on_their_input_with_plain_line_breaks()
    {
        Assert.Equal(new[] { "xdotool type --clearmodifiers --delay 4 --file - <<a\nb\tc>>" }, Show("xdotool", "a\r\nb\tc"));
        Assert.Equal(new[] { "ydotool type --key-delay 4 --file - <<a\nb>>" }, Show("ydotool", "a\rb"));
    }

    [Fact]
    public void Nothing_to_type_is_nothing_to_run() => Assert.Empty(TextTyper.Steps("wtype", ""));
}

public class SystemInfoTests
{
    [Fact]
    public void Cpu_use_is_the_busy_share_between_two_samples()
    {
        var first = SystemInfo.CpuTimes("cpu  1000 0 500 8000 500 0 0 0 0 0\ncpu0 1 2 3 4\n")!.Value;
        var second = SystemInfo.CpuTimes("cpu  1300 0 600 8500 600 0 0 0 0 0\n")!.Value;
        Assert.Equal((8500, 10000), first); // waiting on the disk counts as idle
        Assert.Equal(40, SystemInfo.CpuPercent(first, second)); // 400 busy of 1000
        Assert.Equal(0, SystemInfo.CpuPercent(first, first));
        Assert.Null(SystemInfo.CpuTimes("nothing here"));
        Assert.Null(SystemInfo.CpuTimes(null));
    }

    [Theory]
    [InlineData("NAME=\"Omarchy\"\nPRETTY_NAME=\"Omarchy\"\nID=omarchy\nBUILD_ID=\"4.5.0\"\n", "7.2.15-1-omarchy\n", "Omarchy 4.5.0 (Linux 7.2.15-1-omarchy)")]
    [InlineData("PRETTY_NAME=\"Ubuntu 24.04.1 LTS\"\nVERSION_ID=\"24.04\"\n", "6.8.0-45-generic", "Ubuntu 24.04.1 LTS (Linux 6.8.0-45-generic)")]
    [InlineData("NAME=\"Arch Linux\"\nPRETTY_NAME=\"Arch Linux\"\nBUILD_ID=rolling\n", "6.12.1-arch1-1", "Arch Linux (Linux 6.12.1-arch1-1)")]
    [InlineData("NAME=Fedora\nVERSION_ID=41\n", null, "Fedora 41")]
    [InlineData(null, "6.1.0", "Linux (Linux 6.1.0)")]
    public void The_system_is_named_with_its_release_and_kernel(string? osRelease, string? kernel, string expected) =>
        Assert.Equal(expected, SystemInfo.OsVersion(osRelease, kernel));

    [Fact]
    public void The_processor_is_named_from_cpuinfo()
    {
        Assert.Equal("Intel(R) Core(TM) i9-14900", SystemInfo.ProcessorName("processor\t: 0\nvendor_id\t: GenuineIntel\nmodel name\t: Intel(R)  Core(TM) i9-14900\n"));
        Assert.Equal("Raspberry Pi 5 Model B Rev 1.0", SystemInfo.ProcessorName("processor\t: 0\nBogoMIPS\t: 108.00\nModel\t\t: Raspberry Pi 5 Model B Rev 1.0\n"));
        Assert.Null(SystemInfo.ProcessorName("processor\t: 0\n"));
    }

    [Fact]
    public void Memory_is_what_is_not_available()
    {
        Assert.Equal("Memory 18.0 of 62.5 GB used (29%)", SystemInfo.Memory("MemTotal:       65536000 kB\nMemFree:         1000000 kB\nMemAvailable:   46661632 kB\n"));
        Assert.Null(SystemInfo.Memory("nothing"));
    }

    [Fact]
    public void Uptime_is_the_first_number() => Assert.Equal(TimeSpan.FromSeconds(60780.5), SystemInfo.Uptime("60780.50 950000.11\n"));

    [Fact]
    public void The_battery_is_the_pcs_own_not_a_mouses()
    {
        string root = Path.Combine(Path.GetTempPath(), "pucksley-tests", Guid.NewGuid().ToString("n"));
        void Supply(string name, params (string File, string Value)[] files)
        {
            Directory.CreateDirectory(Path.Combine(root, name));
            foreach (var (file, value) in files) File.WriteAllText(Path.Combine(root, name, file), value + "\n");
        }
        try
        {
            Assert.Equal("No battery", SystemInfo.Battery(Path.Combine(root, "missing")));
            Supply("AC", ("type", "Mains"), ("online", "0"));
            Supply("hidpp_battery_0", ("type", "Battery"), ("scope", "Device"), ("capacity", "12"), ("status", "Discharging")); // a wireless mouse
            Assert.Equal("No battery", SystemInfo.Battery(root));

            Supply("BAT0", ("type", "Battery"), ("capacity", "45"), ("status", "Discharging"), ("energy_now", "26000000"), ("power_now", "12000000"));
            Assert.Equal("Battery 45%, about 2 hours 10 minutes left", SystemInfo.Battery(root));

            File.WriteAllText(Path.Combine(root, "BAT0", "status"), "Charging\n");
            File.WriteAllText(Path.Combine(root, "AC", "online"), "1\n");
            Assert.Equal("Battery 45%, charging", SystemInfo.Battery(root));

            File.WriteAllText(Path.Combine(root, "BAT0", "status"), "Full\n");
            Assert.Equal("Battery 45%, plugged in", SystemInfo.Battery(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

public class DisplayTests
{
    [Theory]
    [InlineData("60", 60)]
    [InlineData("60%\n", 60)]
    [InlineData("current value = 46, max value = 100", 46)]
    [InlineData("", null)]
    [InlineData("no display", null)]
    public void A_brightness_is_found_in_what_a_command_printed(string output, int? expected) => Assert.Equal(expected, Display.ParsePercent(output));

    [Theory]
    [InlineData("VCP 10 C 60 100", 60)]
    [InlineData("VCP 10 C 128 255\n", 50)]
    [InlineData("VCP 10 ERR", null)]
    [InlineData("No displays found", null)]
    public void A_monitors_answer_is_a_share_of_its_most(string output, int? expected) => Assert.Equal(expected, Display.ParseVcp(output));

    [Fact]
    public async Task A_laptops_backlight_is_read_from_its_own_driver()
    {
        string root = Path.Combine(Path.GetTempPath(), "pucksley-tests", Guid.NewGuid().ToString("n"));
        try
        {
            Assert.Null(Display.Backlight.Find(root));
            foreach (var (name, now, max) in new[] { ("acpi_video0", "5", "10"), ("intel_backlight", "24000", "96000") })
            {
                Directory.CreateDirectory(Path.Combine(root, name));
                File.WriteAllText(Path.Combine(root, name, "brightness"), now + "\n");
                File.WriteAllText(Path.Combine(root, name, "max_brightness"), max + "\n");
            }
            var panel = Display.Backlight.Find(root)!;
            Assert.Equal(25, await panel.GetAsync(default)); // intel_backlight, not the generic one beside it
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}

public class ScreenCaptureTests
{
    static readonly ScreenOutput Laptop = new("eDP-1", new ScreenRect(0, 0, 1440, 900), false);
    static readonly ScreenOutput Monitor = new("DP-1", new ScreenRect(1440, 0, 2560, 1440), true);

    static (string Arguments, string Description) Region(ScreenArea area, ScreenLayout? layout)
    {
        var (arguments, description) = ScreenCapture.GrimRegion(area, layout);
        return (string.Join(' ', arguments), description);
    }

    [Fact]
    public void The_window_in_front_is_captured_by_its_place_on_the_desktop() =>
        Assert.Equal(("-g 1500,100 800x600", "the active window"), Region(ScreenArea.ActiveWindow, new ScreenLayout([Laptop, Monitor], new ScreenRect(1500, 100, 800, 600), "Notes")));

    [Fact]
    public void A_window_hanging_off_the_desktop_is_cut_to_what_shows() =>
        Assert.Equal(("-g 3600,1000 400x440", "the active window"), Region(ScreenArea.ActiveWindow, new ScreenLayout([Laptop, Monitor], new ScreenRect(3600, 1000, 800, 600), "Notes")));

    [Fact]
    public void With_no_window_in_front_it_is_the_screen_in_use()
    {
        Assert.Equal(("-o DP-1", "the screen with the active window"), Region(ScreenArea.ActiveWindow, new ScreenLayout([Laptop, Monitor], null, null)));
        Assert.Equal(("-o DP-1", "the screen with the active window"), Region(ScreenArea.ActiveWindow, new ScreenLayout([Laptop, Monitor], new ScreenRect(9000, 9000, 5, 5), null)));
        Assert.Equal(("-o DP-1", "the screen"), Region(ScreenArea.ActiveScreen, new ScreenLayout([Monitor], null, null)));
    }

    [Fact]
    public void All_and_primary_are_whole_screens()
    {
        var both = new ScreenLayout([Laptop, Monitor], null, null);
        Assert.Equal(("", "all screens"), Region(ScreenArea.All, both));
        Assert.Equal(("-o eDP-1", "the primary screen"), Region(ScreenArea.Primary, both)); // the one at the desktop's origin
        Assert.Equal(("", "the screen"), Region(ScreenArea.All, new ScreenLayout([Monitor], null, null)));
    }

    [Fact]
    public void Without_a_layout_it_is_everything() => Assert.Equal(("", "the screen"), Region(ScreenArea.ActiveWindow, null));

    [Fact]
    public void Rectangles_intersect_and_join()
    {
        Assert.Equal(new ScreenRect(5, 5, 5, 5), new ScreenRect(0, 0, 10, 10).Intersect(new ScreenRect(5, 5, 10, 10)));
        Assert.True(new ScreenRect(0, 0, 10, 10).Intersect(new ScreenRect(20, 20, 5, 5)).IsEmpty);
        Assert.Equal(new ScreenRect(0, 0, 4000, 1440), ScreenRect.Union([Laptop.Bounds, Monitor.Bounds]));
        Assert.True(ScreenRect.Union([]).IsEmpty);
    }

    static byte[] Png(int width, int height, SKColor colour)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        bitmap.Erase(colour);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    [Fact]
    public void A_big_capture_is_scaled_to_what_claude_reads()
    {
        var image = Jpeg.FromEncoded(Png(3840, 2160, new SKColor(200, 30, 30)));
        Assert.Equal((1568, 882, 3840, 2160), (image.Width, image.Height, image.SourceWidth, image.SourceHeight));
        using var decoded = SKBitmap.Decode(image.Jpeg);
        Assert.Equal((1568, 882), (decoded.Width, decoded.Height));
        var pixel = decoded.GetPixel(700, 400);
        Assert.InRange(pixel.Red, 190, 210); // the colours arrive the right way round
        Assert.InRange(pixel.Blue, 20, 45);
    }

    [Fact]
    public void A_small_capture_keeps_its_size_and_transparency_becomes_white()
    {
        var image = Jpeg.FromEncoded(Png(320, 200, SKColors.Transparent));
        Assert.Equal((320, 200), (image.Width, image.Height));
        using var decoded = SKBitmap.Decode(image.Jpeg);
        var pixel = decoded.GetPixel(10, 10);
        Assert.True(pixel is { Red: > 250, Green: > 250, Blue: > 250 });
    }

    [Fact]
    public void Something_that_is_not_a_picture_is_refused() =>
        Assert.Contains("can't read", Assert.Throws<PluginException>(() => Jpeg.FromEncoded([1, 2, 3, 4])).Message);
}

public class ProgramsTests
{
    [Fact]
    public void A_program_is_found_along_the_folders_if_it_can_run()
    {
        string root = Path.Combine(Path.GetTempPath(), "pucksley-tests", Guid.NewGuid().ToString("n"));
        string first = Path.Combine(root, "a"), second = Path.Combine(root, "b");
        Directory.CreateDirectory(first);
        Directory.CreateDirectory(second);
        try
        {
            File.WriteAllText(Path.Combine(first, "tool"), "#!/bin/sh\n"); // there, but not marked as a program
            File.WriteAllText(Path.Combine(second, "tool"), "#!/bin/sh\n");
            File.SetUnixFileMode(Path.Combine(second, "tool"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Assert.Equal(Path.Combine(second, "tool"), Programs.Search("tool", [first, "", second]));
            Assert.Null(Programs.Search("tool", [first]));
            Assert.Null(Programs.Search("other", [first, second]));
            Assert.Equal(Path.Combine(second, "tool"), Programs.Search(Path.Combine(second, "tool"), []));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task A_missing_program_is_named_to_the_person()
    {
        var e = await Assert.ThrowsAsync<PluginException>(() => Programs.RunAsync("pucksley-no-such-program", [], TimeSpan.FromSeconds(2), default));
        Assert.Equal("pucksley-no-such-program isn't installed on this PC.", e.Message);
    }

    [Fact]
    public async Task Output_input_and_exit_codes_come_through()
    {
        var echoed = await Programs.RunAsync("cat", [], TimeSpan.FromSeconds(5), default, input: "héllo\nworld\n");
        Assert.Equal((0, "héllo\nworld"), (echoed.ExitCode, echoed.Output));
        var failed = await Programs.RunAsync("sh", ["-c", "echo oops >&2; exit 3"], TimeSpan.FromSeconds(5), default);
        Assert.Equal((3, "oops"), (failed.ExitCode, failed.Error));
        var e = await Assert.ThrowsAsync<PluginException>(() => Programs.OutputAsync("sh", ["-c", "echo 'first line' >&2; echo second >&2; exit 1"], TimeSpan.FromSeconds(5), default));
        Assert.Equal("sh failed: first line", e.Message);
    }

    [Fact]
    public async Task Bytes_come_through_untouched_and_the_environment_is_passed()
    {
        var bytes = await Programs.RunBinaryAsync("sh", ["-c", "printf '\\211PNG\\r\\n\\032\\n  '"], TimeSpan.FromSeconds(5), default);
        Assert.Equal(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A, 0x20, 0x20 }, bytes.Output);
        var env = await Programs.RunAsync("sh", ["-c", "echo $LC_ALL"], TimeSpan.FromSeconds(5), default, environment: Programs.PlainLocale);
        Assert.Equal("C.UTF-8", env.Output);
    }

    [Fact]
    public async Task A_program_that_hangs_is_stopped()
    {
        var e = await Assert.ThrowsAsync<PluginException>(() => Programs.RunAsync("sleep", ["30"], TimeSpan.FromMilliseconds(300), default));
        Assert.Equal("sleep took too long.", e.Message);
    }

    [Fact]
    public async Task A_detached_program_reports_an_early_exit_and_is_left_alone_otherwise()
    {
        Assert.Equal(0, await Detached.StartAsync(["true"], TimeSpan.FromSeconds(5), default));
        Assert.Equal(7, await Detached.StartAsync(["sh", "-c", "exit 7"], TimeSpan.FromSeconds(5), default));
        Assert.Null(await Detached.StartAsync(["sleep", "2"], TimeSpan.FromMilliseconds(200), default)); // still running: it started
        await Assert.ThrowsAsync<PluginException>(() => Detached.StartAsync(["pucksley-no-such-program"], TimeSpan.FromSeconds(1), default));
    }
}

public class ToolSchemaTests
{
    [Fact]
    public void The_linux_plugins_are_discoverable_with_the_same_identity_as_on_windows()
    {
        foreach (var type in new[] { typeof(Audio.PcAudioPlugin), typeof(PcControlPlugin), typeof(Speech.LinuxSpeechPlugin) })
        {
            Assert.True(type.IsPublic && !type.IsAbstract);
            Assert.NotNull(type.GetConstructor(Type.EmptyTypes)); // the hub creates plugins by reflection
        }
        IPlugin[] plugins = [new Audio.PcAudioPlugin(), new PcControlPlugin(), new Speech.LinuxSpeechPlugin()];
        Assert.Equal(new[] { "pc-audio", "pc", "linux-speech" }, plugins.Select(p => p.Id));
        Assert.Equal(new[] { "volume", "pc", "pc" }, plugins.Select(p => p.Icon));
        Assert.All(plugins, p => Assert.True(p.EnabledByDefault));
    }

    [Fact]
    public void The_tools_are_the_ones_windows_has()
    {
        Assert.Equal(
            new[] { "pc_volume", "pc_mute", "pc_media", "pc_now_playing", "pc_audio_outputs", "pc_set_audio_output", "pc_app_volume", "pc_apps_playing_audio" },
            new Audio.PcAudioPlugin().GetTools().Select(t => t.Name));
        var tools = new PcControlPlugin().GetTools();
        Assert.Equal(
            new[]
            {
                "pc_open_app", "pc_open", "pc_lock", "pc_sleep", "pc_shutdown", "pc_cancel_shutdown", "pc_screenshot", "pc_clipboard_get",
                "pc_clipboard_set", "pc_type_text", "pc_show_desktop", "pc_focus_app", "pc_running_apps", "pc_system_info", "pc_dark_mode", "pc_brightness",
            },
            tools.Select(t => t.Name));
        Assert.All(tools, t => Assert.InRange(t.Description.Length, 20, 300));
        Assert.Equal(new[] { "pc_sleep", "pc_shutdown" }, tools.Where(t => t.Confirm).Select(t => t.Name));
    }

    [Fact]
    public void Linux_words_its_own_tools_where_windows_words_do_not_fit()
    {
        var tools = new PcControlPlugin().GetTools().ToDictionary(t => t.Name);
        Assert.Contains("the trash", tools["pc_open"].Description);
        Assert.DoesNotContain("recycle bin", tools["pc_open"].Description);
        Assert.Contains("Unsaved work", tools["pc_shutdown"].Description);
        Assert.Contains("empty workspace", tools["pc_show_desktop"].Description);
        Assert.Contains("DDC", tools["pc_brightness"].Description);
        Assert.Contains("Linux PC", new PcControlPlugin().Description);
    }
}
