using Pucksley.Linux.Tray;
using SkiaSharp;

namespace Pucksley.Linux.Tests;

public class TrayMenuTests
{
    [Fact]
    public void Items_are_numbered_from_one_in_the_order_added()
    {
        var menu = new TrayMenu();
        var open = menu.Add(new TrayMenuItem { Label = "Open Pucksley" });
        menu.AddSeparator();
        var quit = menu.Add(new TrayMenuItem { Label = "Quit" });
        Assert.Equal((1, 3), (open.Id, quit.Id));
        Assert.Same(quit, menu.Find(3));
        Assert.True(menu.Find(2)!.Separator);
        Assert.Null(menu.Find(0)); // the menu itself, not an item
        Assert.Null(menu.Find(4));
    }

    static Dictionary<string, object> Properties(TrayMenuItem item) => TrayMenu.Properties(item).ToDictionary(p => p.Name, p => p.Value);

    [Fact]
    public void A_plain_item_has_a_label_and_can_be_clicked()
    {
        var p = Properties(new TrayMenuItem { Label = "Open data folder" });
        Assert.Equal(new Dictionary<string, object> { ["label"] = "Open data folder", ["enabled"] = true, ["visible"] = true }, p);
    }

    [Fact]
    public void A_line_that_only_says_something_is_greyed()
    {
        var p = Properties(new TrayMenuItem { Label = "Connected: Kitchen_puck", Enabled = false });
        Assert.Equal(false, p["enabled"]);
        Assert.Equal("Connected: Kitchen__puck", p["label"]); // an underscore would otherwise mark a shortcut key
    }

    [Fact]
    public void A_tick_box_says_whether_it_is_ticked()
    {
        Assert.Equal(("checkmark", 1), (Properties(new TrayMenuItem { Label = "Start at login", Checked = true })["toggle-type"], Properties(new TrayMenuItem { Label = "Start at login", Checked = true })["toggle-state"]));
        Assert.Equal(0, Properties(new TrayMenuItem { Label = "Start at login", Checked = false })["toggle-state"]);
        Assert.False(Properties(new TrayMenuItem { Label = "Quit" }).ContainsKey("toggle-type"));
    }

    [Fact]
    public void A_separator_is_only_that() =>
        Assert.Equal(new Dictionary<string, object> { ["type"] = "separator" }, Properties(new TrayMenuItem { Separator = true }));
}

public class PuckIconTests
{
    [Theory]
    [InlineData(16)]
    [InlineData(22)]
    [InlineData(64)]
    public void The_tray_icon_is_four_bytes_a_pixel_round_and_see_through_at_the_corners(int size)
    {
        var argb = PuckIcon.Argb(size);
        Assert.Equal(size * size * 4, argb.Length);
        Assert.Equal(0, argb[0]); // the top-left corner: fully transparent (alpha comes first)
        int centre = (size / 2 * size + size / 2) * 4;
        Assert.Equal(255, argb[centre]);             // the middle: solid…
        Assert.True(argb[centre + 1] > argb[centre + 3]); // …and warm (more red than blue)
    }

    [Fact]
    public void The_launcher_icon_is_a_png_of_the_size_asked_for()
    {
        using var bitmap = SKBitmap.Decode(PuckIcon.Png(128));
        Assert.Equal((128, 128), (bitmap.Width, bitmap.Height));
        Assert.Equal(0, bitmap.GetPixel(0, 0).Alpha);
        Assert.True(bitmap.GetPixel(64, 2).Red > 180); // the pale ring at the top
    }
}

public class DesktopIntegrationTests
{
    [Fact]
    public void The_launcher_entry_names_the_program_and_its_icon()
    {
        string file = DesktopIntegration.DesktopFile(["/opt/pucksley/pucksley", "--open"], autostart: false);
        Assert.StartsWith("[Desktop Entry]\nType=Application\nName=Pucksley\n", file);
        Assert.Contains("\nExec=/opt/pucksley/pucksley --open\n", file);
        Assert.Contains("\nIcon=pucksley\n", file);
        Assert.Contains("\nTerminal=false\n", file);
        Assert.DoesNotContain("Autostart", file);
        Assert.Contains("\nX-GNOME-Autostart-enabled=true\n", DesktopIntegration.DesktopFile(["/opt/pucksley/pucksley"], autostart: true));
    }

    [Fact]
    public void The_crash_restart_drop_in_restarts_only_after_a_failure()
    {
        Assert.Equal("app-pucksley@autostart.service", DesktopIntegration.AutostartUnit);
        Assert.EndsWith("/systemd/user/app-pucksley@autostart.service.d/keep-running.conf", DesktopIntegration.KeepRunningFile);
        Assert.Contains("\n[Service]\nRestart=on-failure\nRestartSec=3\n", DesktopIntegration.KeepRunning);
        Assert.Contains("\nExitType=main\n", DesktopIntegration.KeepRunning);
    }

    [Theory]
    [InlineData(new[] { "/usr/bin/pucksley" }, "/usr/bin/pucksley")]
    [InlineData(new[] { "/home/me/My Apps/pucksley", "--open" }, "\"/home/me/My Apps/pucksley\" --open")]
    [InlineData(new[] { "/usr/bin/dotnet", "/opt/pucksley/pucksley.dll" }, "/usr/bin/dotnet /opt/pucksley/pucksley.dll")]
    [InlineData(new[] { "/odd/100%/pucksley" }, "/odd/100%%/pucksley")]
    [InlineData(new[] { "/odd/$HOME \"x\"/pucksley" }, "\"/odd/\\\\$HOME \\\\\"x\\\\\"/pucksley\"")]
    public void A_command_becomes_an_exec_line_a_launcher_reads_back(string[] command, string expected) =>
        Assert.Equal(expected, DesktopIntegration.ExecLine(command));

    [Fact]
    public void The_exec_line_reads_back_as_the_same_command()
    {
        // The plugins' own reader of .desktop files is the judge: what's written must come back unchanged
        string[] command = ["/home/me/My Apps/$odd `dir`/100% \"quoted\"/pucksley", "--open"];
        string file = DesktopIntegration.DesktopFile(command, autostart: false);
        var entry = Plugins.Linux.Pc.DesktopEntries.Parse(file, "pucksley.desktop", "/tmp/pucksley.desktop", [])!;
        Assert.Equal("Pucksley", entry.Name);
        Assert.Equal(command, Plugins.Linux.Pc.ExecLine.Command(entry));
    }

    [Fact]
    public void This_copy_is_started_the_way_it_was_started()
    {
        var command = DesktopIntegration.SelfCommand();
        Assert.InRange(command.Count, 1, 2);
        Assert.True(Path.IsPathRooted(command[0]));
    }

    [Fact]
    public void Installing_and_removing_touch_only_the_desktops_own_folders()
    {
        string root = Path.Combine(Path.GetTempPath(), "pucksley-tests", Guid.NewGuid().ToString("n"));
        string? config = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME"), data = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", Path.Combine(root, "config"));
        Environment.SetEnvironmentVariable("XDG_DATA_HOME", Path.Combine(root, "data"));
        DesktopIntegration.TellSystemd = false;
        try
        {
            Assert.False(DesktopIntegration.StartsAtLogin);
            var written = DesktopIntegration.Install();
            Assert.All(written, f => Assert.StartsWith(Path.Combine(root, "data"), f));
            Assert.Contains(Path.Combine(root, "data/applications/pucksley.desktop"), written);
            Assert.Contains(Path.Combine(root, "data/icons/hicolor/256x256/apps/pucksley.png"), written);
            Assert.Contains(Path.Combine(root, "data/icons/hicolor/scalable/apps/pucksley.svg"), written);
            Assert.Contains("--open", File.ReadAllText(DesktopIntegration.LauncherFile));
            Assert.StartsWith("<svg", File.ReadAllText(Path.Combine(root, "data/icons/hicolor/scalable/apps/pucksley.svg")));
            Assert.False(DesktopIntegration.StartsAtLogin); // installing doesn't decide that

            DesktopIntegration.SetStartAtLogin(true);
            Assert.True(DesktopIntegration.StartsAtLogin);
            Assert.Equal(Path.Combine(root, "config/autostart/pucksley.desktop"), DesktopIntegration.AutostartFile);
            Assert.DoesNotContain("--open", File.ReadAllText(DesktopIntegration.AutostartFile)); // no browser at every login
            Assert.Equal(DesktopIntegration.KeepRunning, File.ReadAllText(Path.Combine(root, "config/systemd/user/app-pucksley@autostart.service.d/keep-running.conf")));

            // A desktop's own settings switch an entry off by marking it hidden
            File.AppendAllText(DesktopIntegration.AutostartFile, "Hidden=true\n");
            Assert.False(DesktopIntegration.StartsAtLogin);
            DesktopIntegration.SetStartAtLogin(true);
            Assert.True(DesktopIntegration.StartsAtLogin);

            var removed = DesktopIntegration.Uninstall();
            Assert.Equal(written.Count + 2, removed.Count); // and the start at login, with its restart after a crash
            Assert.False(Directory.Exists(Path.Combine(root, "config/systemd/user/app-pucksley@autostart.service.d")));
            Assert.False(File.Exists(DesktopIntegration.LauncherFile));
            Assert.False(DesktopIntegration.StartsAtLogin);
            Assert.Empty(DesktopIntegration.Uninstall());
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", config);
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", data);
            DesktopIntegration.TellSystemd = true;
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
