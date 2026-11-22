using Microsoft.Extensions.Logging.Abstractions;
using Pucksley.Plugins.Linux.Audio;
using Pucksley.Plugins.Linux.Common;
using Pucksley.Plugins.Linux.Pc;
using Pucksley.Plugins.Linux.Pc.Windows;
using Pucksley.Plugins.Pc.Desktop;
using Pucksley.Sdk;
using Pucksley.TestSupport;
using Xunit.Abstractions;

namespace Pucksley.Plugins.Linux.Tests;

/// <summary>
/// Reads this PC's real state, and changes nothing: no volume, mute, output, media key, app launch,
/// typing, focus, theme or power change. Each test passes on a machine without the hardware or the
/// desktop (it just reports what it found).
/// </summary>
[Trait("Category", "Live")]
public class LiveReadOnlyTests(ITestOutputHelper output)
{
    [Fact]
    public void The_session_is_recognised()
    {
        var session = DesktopSession.Current;
        output.WriteLine($"{session.Name}: compositor {session.Compositor}, wayland {session.Wayland}, x11 {session.X11}");
        Assert.Equal(session.Compositor != Compositor.None, session.Graphical);
    }

    [Fact]
    public void The_sound_server_reports_the_master_volume_outputs_and_apps()
    {
        if (!Programs.Has("pactl"))
        {
            output.WriteLine("pactl isn't installed");
            return;
        }
        using var audio = new PulseAudio(NullLogger.Instance);
        try
        {
            audio.Start();
        }
        catch (PluginException e)
        {
            output.WriteLine($"No sound server: {e.Message}");
            return;
        }
        var master = audio.GetMaster();
        var outputs = audio.GetOutputs();
        var apps = audio.GetApps();
        output.WriteLine(master is null ? "No output device" : $"Master {master.Volume}%{(master.Muted ? " muted" : "")} on {master.DeviceName}");
        output.WriteLine($"{outputs.Count} outputs: {string.Join("; ", outputs.Select(o => o.Name + (o.IsDefault ? " (default)" : "")))}");
        output.WriteLine($"{apps.Count} apps with streams: {string.Join(", ", apps.Select(a => $"{a.Name} [{a.ProcessName}] {a.Volume}%{(a.Muted ? " muted" : "")}{(a.Playing ? " playing" : "")}"))}");
        if (master is null) return;
        Assert.InRange(master.Volume, 0, 100);
        var current = Assert.Single(outputs, o => o.IsDefault);
        Assert.Equal(master.DeviceName, current.Name);
    }

    /// <summary>
    /// Plays a few seconds of silence, finds that stream among the apps, and turns it (only it) down:
    /// nothing is heard and nothing of the person's is changed.
    /// </summary>
    [Fact]
    public async Task A_silent_test_stream_is_listed_and_its_own_volume_set()
    {
        string? player = Programs.FirstOf("paplay", "pw-play");
        if (!Programs.Has("pactl") || player is null)
        {
            output.WriteLine("No pactl, or nothing to play a test sound with");
            return;
        }
        using var audio = new PulseAudio(NullLogger.Instance);
        try
        {
            audio.Start();
        }
        catch (PluginException e)
        {
            output.WriteLine($"No sound server: {e.Message}");
            return;
        }
        if (audio.GetMaster() is null)
        {
            output.WriteLine("No output device");
            return;
        }

        string file = Path.Combine(Path.GetTempPath(), $"pucksley-silence-{Guid.NewGuid():n}.wav");
        await File.WriteAllBytesAsync(file, Wav.Encode(new short[16000 * 6], 16000));
        using var playing = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Programs.Find(player)!, [file]) { RedirectStandardError = true, RedirectStandardOutput = true })!;
        try
        {
            AppAudio? stream = null;
            // paplay is "pacat" under another name, and pw-play is "pw-cat": the stream carries one or the other
            await Wait.UntilAsync(() => (stream = audio.GetApps().FirstOrDefault(a => a.Name.Equals(player, StringComparison.OrdinalIgnoreCase)
                || a.ProcessName is "paplay" or "pacat" or "pw-play" or "pw-cat")) is not null, 5000);
            output.WriteLine($"Found {stream!.Name} [{stream.ProcessName}] at {stream.Volume}%{(stream.Playing ? ", playing" : "")}");
            Assert.True(stream.Playing);

            var quieter = audio.SetApp(stream.Key, 35, null);
            Assert.Equal(35, quieter?.Volume);
            var muted = audio.SetApp(stream.Key, null, true);
            Assert.True(muted?.Muted);
            output.WriteLine($"Set it to {quieter!.Volume}% and muted it");
        }
        finally
        {
            try { playing.Kill(); } catch (InvalidOperationException) { /* it finished */ }
            File.Delete(file);
        }
    }

    [Fact]
    public async Task Media_players_are_read_from_the_session_bus()
    {
        if (!Bus.Session.Exists)
        {
            output.WriteLine("No session bus");
            return;
        }
        using var media = new MprisMedia(NullLogger.Instance);
        try
        {
            await media.StartAsync(default);
        }
        catch (PluginException e)
        {
            output.WriteLine($"No session bus: {e.Message}");
            return;
        }
        var info = await media.ReadAsync(default);
        output.WriteLine(info is null ? "No media players" : $"{info.App}: {(info.Playing ? "playing" : "paused")} \"{info.Title}\" – {info.Artist} ({info.Album})");
        Assert.Equal(info, media.Current);
    }

    [Fact]
    public async Task The_app_list_comes_from_the_desktop_files()
    {
        var linux = new LinuxApps();
        var apps = AppIndex.Clean(await linux.LoadAsync(default));
        output.WriteLine($"{apps.Count} apps: {string.Join(", ", apps.Take(40).Select(a => a.Name))}{(apps.Count > 40 ? "…" : "")}");
        foreach (var name in new[] { "the browser", "files", "file manager", "terminal", "calculator", "settings", "chrome", "spotify", "vs code", "text editor" })
        {
            var match = linux.Find(name, apps);
            output.WriteLine($"  \"{name}\" → {(match is null ? "(none)" : $"{match.Name}  [{Path.GetFileName(match.Target)}]")}");
        }
        Assert.All(apps, a => Assert.Equal(AppKind.DesktopFile, a.Kind));
        Assert.Equal(apps.Count, apps.Select(a => NameMatcher.Normalize(a.Name)).Distinct().Count());
    }

    [Fact]
    public async Task Windows_and_the_lock_state_are_read()
    {
        var session = DesktopSession.Current;
        var desktop = new LinuxDesktop(session);
        await desktop.LoadAppsAsync(NullLogger.Instance, default); // names windows by their launcher entries
        bool locked = await desktop.IsLockedAsync(default);
        output.WriteLine($"Locked: {locked}");
        try
        {
            var windows = await desktop.WindowsAsync(default);
            var front = await desktop.ForegroundAsync(default);
            output.WriteLine($"{windows.Count} windows; in front: {(front is null ? "(nothing)" : $"{front.AppName} [{front.ProcessName}] \"{front.Title}\"")}");
            foreach (var w in windows) output.WriteLine($"  {w.AppName} [{w.ProcessName}, pid {w.ProcessId}] \"{w.Title}\"");
            Assert.All(windows, w => Assert.False(string.IsNullOrWhiteSpace(w.AppName)));
            if (front is not null) Assert.Contains(windows, w => w.Handle == front.Handle);
        }
        catch (PluginException e)
        {
            output.WriteLine($"This desktop keeps its windows to itself: {e.Message}");
        }
    }

    [Fact]
    public async Task System_information_reads()
    {
        var text = await SystemInfo.DescribeAsync(default);
        output.WriteLine(text);
        Assert.Contains("CPU ", text);
        Assert.Contains("Up ", text);
        Assert.Contains("Linux", text);
    }

    [Fact]
    public async Task Dark_mode_and_brightness_read()
    {
        var display = new Display(DesktopSession.Current);
        bool? dark = await display.IsDarkModeAsync(default);
        output.WriteLine($"Dark mode: {dark?.ToString() ?? "unknown"}");
        try
        {
            output.WriteLine($"Brightness {await display.BrightnessAsync(null, null, default)}%");
        }
        catch (PluginException e)
        {
            output.WriteLine($"Brightness: {e.Message}");
        }
    }

    [Fact]
    public async Task The_clipboard_reads()
    {
        var session = DesktopSession.Current;
        if (!session.Graphical)
        {
            output.WriteLine("No desktop session");
            return;
        }
        try
        {
            var c = await new ClipboardAccess(session).ReadAsync(default);
            output.WriteLine(c.Text is { } t ? $"Text, {t.Length} characters" : c.Files is { } f ? $"{f.Count} files" : c.ImageJpeg is { } j ? $"Image {c.ImageWidth}×{c.ImageHeight} ({j.Length} bytes as JPEG)" : "Empty");
        }
        catch (PluginException e)
        {
            output.WriteLine(e.Message);
        }
    }

    [Theory]
    [InlineData("active_window")]
    [InlineData("screen")]
    [InlineData("all")]
    public async Task A_screenshot_is_captured_and_kept_in_memory(string which)
    {
        var area = ScreenAreas.Parse(which);
        var session = DesktopSession.Current;
        if (!session.Graphical)
        {
            output.WriteLine("No desktop session");
            return;
        }
        var windows = LinuxDesktop.WindowsFor(session, (c, _) => (c, c));
        try
        {
            var shot = await new ScreenCapture(session, windows).CaptureAsync(area, default);
            output.WriteLine($"{area}: {shot.Area}, {shot.SourceWidth}×{shot.SourceHeight} sent at {shot.Width}×{shot.Height}, {shot.Jpeg.Length / 1024} KB JPEG; window \"{shot.WindowTitle}\"");
            Assert.InRange(Math.Max(shot.Width, shot.Height), 1, ScreenshotMath.MaxLongSide);
            Assert.Equal(0xFF, shot.Jpeg[0]);
            Assert.Equal(0xD8, shot.Jpeg[1]); // a JPEG
            // To look at what was captured: set PUCKSLEY_TEST_SHOT_DIR to a folder
            if (Environment.GetEnvironmentVariable("PUCKSLEY_TEST_SHOT_DIR") is { Length: > 0 } folder)
                await File.WriteAllBytesAsync(Path.Combine(folder, $"shot-{which}.jpg"), shot.Jpeg);
        }
        catch (PluginException e)
        {
            output.WriteLine($"{area}: {e.Message}");
        }
    }

    [Fact]
    public async Task The_plugin_answers_its_read_only_tools()
    {
        var plugin = new PcControlPlugin();
        plugin.Initialize(new FakePluginContext());
        plugin.ApplySettings(new NoSettings());
        await plugin.StartAsync(default);
        await Wait.UntilAsync(() => plugin.Status.Message.Contains("apps found"), 30_000);
        output.WriteLine($"Status: {plugin.Status.Message}");
        output.WriteLine($"Context: {await plugin.GetContextAsync(ToolCaller.Web, default)}");
        foreach (var tool in new[] { "pc_running_apps", "pc_system_info", "pc_clipboard_get", "pc_brightness", "pc_screenshot" })
        {
            var result = await FakePluginContext.CallToolAsync(plugin, tool);
            string text = result.Text.Length > 300 ? result.Text[..300] + "…" : result.Text;
            output.WriteLine($"{tool}: {(result.IsError ? "ERROR " : "")}{text}{(result.Image is { } image ? $" [+ {image.Length / 1024} KB image]" : "")}");
        }
    }
}
