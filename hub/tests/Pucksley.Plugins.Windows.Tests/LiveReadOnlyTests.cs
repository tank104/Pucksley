using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging.Abstractions;
using Pucksley.Plugins.Windows.Audio;
using Pucksley.Plugins.Windows.Common;
using Pucksley.Plugins.Windows.Pc;
using Pucksley.Sdk;
using Pucksley.TestSupport;
using Xunit.Abstractions;

namespace Pucksley.Plugins.Windows.Tests;

/// <summary>
/// Reads this PC's real state, and changes nothing: no volume, mute, output, media key, app launch,
/// typing, focus, theme or power change. Each test passes on a PC without the hardware (it just
/// reports what it found).
/// </summary>
[Trait("Category", "Live")]
public class LiveReadOnlyTests(ITestOutputHelper output)
{
    [Fact]
    public void Core_audio_reads_the_master_volume_outputs_and_sessions()
    {
        using var audio = new CoreAudio(NullLogger.Instance);
        audio.Start();
        var master = audio.GetMaster();
        var outputs = audio.GetOutputs();
        var apps = audio.GetApps();
        output.WriteLine(master is null ? "No output device" : $"Master {master.Volume}%{(master.Muted ? " muted" : "")} on {master.DeviceName}");
        output.WriteLine($"{outputs.Count} outputs: {string.Join("; ", outputs.Select(o => o.Name + (o.IsDefault ? " (default)" : "")))}");
        output.WriteLine($"{apps.Count} apps with sessions: {string.Join(", ", apps.Select(a => $"{a.Name} [{a.ProcessName}] {a.Volume}%{(a.Muted ? " muted" : "")}{(a.Playing ? " playing" : "")}"))}");
        if (master is null) return;
        Assert.InRange(master.Volume, 0, 100);
        var current = Assert.Single(outputs, o => o.IsDefault);
        Assert.Equal(master.DeviceName, current.Name);
    }

    [Fact]
    public void Policy_config_is_laid_out_as_declared()
    {
        // Reads the default output's name through IPolicyConfig::GetPropertyValue, two vtable slots
        // before SetDefaultEndpoint: proves the interface declaration without switching anything
        using var audio = new CoreAudio(NullLogger.Instance);
        audio.Start();
        var current = audio.GetOutputs().FirstOrDefault(o => o.IsDefault);
        if (current is null) return;
        var name = PolicyConfig.ReadFriendlyName(current.Id);
        output.WriteLine($"NAudio says '{current.Name}', IPolicyConfig says '{name}'");
        Assert.Equal(current.Name, name);
    }

    [Fact]
    public async Task Media_sessions_report_what_is_playing()
    {
        using var media = new WindowsMedia(NullLogger.Instance);
        await media.StartAsync(default);
        var now = await media.ReadAsync(default);
        output.WriteLine(now is null ? "No app has a media session" : $"{now.App}: {(now.Playing ? "playing" : "paused")}, has title: {now.Title is not null}, has artist: {now.Artist is not null}");
        if (now is not null) Assert.False(string.IsNullOrEmpty(now.App));
    }

    [Fact]
    public async Task App_index_reads_the_start_menu_and_apps_folder()
    {
        var shortcuts = AppSources.StartMenuShortcuts();
        var appsFolder = await Blocking.RunStaAsync(AppSources.AppsFolder, TimeSpan.FromSeconds(20), "AppsFolder", default);
        var apps = AppIndex.Clean([.. shortcuts, .. appsFolder]);
        output.WriteLine($"{shortcuts.Count} Start Menu shortcuts + {appsFolder.Count} shell:AppsFolder entries = {apps.Count} apps after cleaning");
        foreach (var name in new[] { "Settings", "Calculator", "File Explorer", "Edge", "Notepad", "Terminal", "Outlook", "Spotify", "Chrome", "Teams", "VS Code" })
        {
            var match = AppIndex.Match(name, apps);
            output.WriteLine($"  '{name}' -> {(match is null ? "(not installed)" : $"{match.Name} [{match.Kind}]")}");
        }
        Assert.NotEmpty(appsFolder); // the native shell enumeration works (no PowerShell fallback needed)
        Assert.NotNull(AppIndex.Match("Settings", apps));
        Assert.NotNull(AppIndex.Match("File Explorer", apps));
    }

    [Fact]
    public async Task Screenshots_are_captured_in_memory_at_physical_resolution()
    {
        if (SessionState.IsLocked())
        {
            output.WriteLine("The PC is locked: nothing to capture");
            return;
        }
        foreach (var area in Enum.GetValues<ScreenArea>())
        {
            var shot = await Task.Run(() => ScreenCapture.Capture(area));
            output.WriteLine($"{area}: {shot.Area}, {shot.SourceWidth}x{shot.SourceHeight} sent as {shot.Width}x{shot.Height}, {shot.Jpeg.Length / 1024} KB");
            Assert.True(Math.Max(shot.Width, shot.Height) <= ScreenshotMath.MaxLongSide);
            Assert.Equal(new byte[] { 0xFF, 0xD8 }, shot.Jpeg[..2]); // a JPEG
            if (area == ScreenArea.Primary)
            {
                // Physical pixels even though this test process isn't DPI aware
                var (width, height) = PrimaryPhysicalSize();
                output.WriteLine($"Primary display is physically {width}x{height}");
                Assert.Equal((width, height), (shot.SourceWidth, shot.SourceHeight));
            }
        }
    }

    [Fact]
    public void Windows_are_listed()
    {
        var windows = WindowList.AppWindows();
        var front = WindowList.Foreground();
        output.WriteLine($"{windows.Count} app windows from {windows.Select(w => w.AppName).Distinct().Count()} apps; in front: {front?.AppName ?? "(desktop)"}");
        foreach (var app in windows.GroupBy(w => w.AppName)) output.WriteLine($"  {app.Key} [{app.First().ProcessName}] x{app.Count()}");
        Assert.All(windows, w => Assert.False(string.IsNullOrWhiteSpace(w.AppName)));
    }

    [Fact]
    public async Task System_info_reads_the_vital_signs()
    {
        var text = await SystemInfo.DescribeAsync(default);
        output.WriteLine(text);
        Assert.Contains("CPU", text);
        Assert.Contains("Memory", text);
        Assert.Contains("Windows", text);
    }

    [Fact]
    public void Power_session_and_theme_state_can_be_read()
    {
        var (s3, modernStandby) = Power.SleepSupport();
        output.WriteLine($"Sleep: S3 {s3}, Modern Standby {modernStandby}. Locked: {SessionState.IsLocked()}. Dark mode: {Display.IsDarkMode()}. {SystemInfo.WindowsVersion()}");
    }

    [Fact]
    public async Task Brightness_is_read_or_explained()
    {
        try
        {
            output.WriteLine($"Brightness {await Display.BrightnessAsync(null, null, default)}%");
        }
        catch (PluginException e)
        {
            output.WriteLine(e.Message);
            Assert.Contains("can't be changed", e.Message);
        }
    }

    [Fact]
    public async Task Clipboard_can_be_read()
    {
        var c = await ClipboardAccess.ReadAsync(default);
        output.WriteLine(c.Text is not null ? $"Text, {c.Text.Length} characters"
            : c.Files is not null ? $"{c.Files.Count} files"
            : c.ImageJpeg is not null ? $"An image, {c.ImageWidth}x{c.ImageHeight}"
            : "Empty");
    }

    [Fact]
    public async Task Pc_audio_plugin_answers_read_only_tools()
    {
        var plugin = new PcAudioPlugin();
        plugin.Initialize(new FakePluginContext());
        plugin.ApplySettings(new PcAudioSettings());
        await plugin.StartAsync(default);
        try
        {
            output.WriteLine($"Status: {plugin.Status.Level}: {plugin.Status.Message}");
            output.WriteLine($"Context: {await plugin.GetContextAsync(ToolCaller.Web, default)}");
            output.WriteLine($"Dial: {plugin.GetControls()[0].Value}% {plugin.GetControls()[0].Text}");
            // Without arguments these only read
            foreach (var tool in new[] { "pc_volume", "pc_now_playing", "pc_audio_outputs", "pc_apps_playing_audio" })
            {
                var result = await FakePluginContext.CallToolAsync(plugin, tool);
                output.WriteLine($"{tool}: {result.Text} [{result.Activity}]");
                Assert.False(result.IsError, result.Text);
                Assert.True((result.Activity?.Length ?? 0) <= 24);
            }
            var now = await plugin.GetNowPlayingAsync("pc", default);
            output.WriteLine($"Home face: source {now?.Source}, playing {now?.Playing}, volume {now?.Volume}");
        }
        finally
        {
            await plugin.StopAsync();
        }
    }

    [Fact]
    public async Task Pc_control_plugin_answers_read_only_tools()
    {
        var plugin = new PcControlPlugin();
        plugin.Initialize(new FakePluginContext());
        plugin.ApplySettings(new NoSettings());
        await plugin.StartAsync(default);
        output.WriteLine($"Context: {await plugin.GetContextAsync(ToolCaller.Web, default)}");

        var tools = new List<string> { "pc_running_apps", "pc_system_info" };
        if (!SessionState.IsLocked()) tools.Add("pc_screenshot");
        foreach (var tool in tools)
        {
            var result = await FakePluginContext.CallToolAsync(plugin, tool);
            Assert.False(result.IsError, result.Text);
            string image = result.Image is null ? "" : $" + {result.ImageMediaType} {result.Image.Length / 1024} KB";
            output.WriteLine($"{tool}: {result.Text.Length} chars{image} [{result.Activity}]");
        }

        await Wait.UntilAsync(() => plugin.Status.Message.Contains("apps found"), 30_000);
        output.WriteLine($"Status: {plugin.Status.Message}");
    }

    static (int Width, int Height) PrimaryPhysicalSize()
    {
        const int DESKTOPVERTRES = 117, DESKTOPHORZRES = 118;
        var dc = GetDC(0);
        try
        {
            return (GetDeviceCaps(dc, DESKTOPHORZRES), GetDeviceCaps(dc, DESKTOPVERTRES));
        }
        finally
        {
            ReleaseDC(0, dc);
        }
    }

    [DllImport("user32.dll")]
    static extern nint GetDC(nint hwnd);

    [DllImport("user32.dll")]
    static extern int ReleaseDC(nint hwnd, nint dc);

    [DllImport("gdi32.dll")]
    static extern int GetDeviceCaps(nint dc, int index);
}
