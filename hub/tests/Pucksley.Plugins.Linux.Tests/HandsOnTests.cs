using Microsoft.Extensions.Logging.Abstractions;
using Pucksley.Plugins.Linux.Audio;
using Pucksley.Plugins.Linux.Common;
using Pucksley.Plugins.Linux.Pc;
using Pucksley.Plugins.Pc.Desktop;
using Pucksley.Sdk;
using Pucksley.TestSupport;
using Xunit.Abstractions;

namespace Pucksley.Plugins.Linux.Tests;

/// <summary>
/// The tools that change this PC, tried for real, each as small as it can be and put straight back:
/// the volume up one and down again, the other audio output and back, a line on the clipboard and
/// the old one restored, another window in front and the first one back.
/// </summary>
/// <remarks>
/// They only run when asked to: <c>PUCKSLEY_HANDS_ON=1 dotnet test --filter Category=HandsOn</c>.
/// Without that each one passes having done nothing. Typing, locking, sleeping and shutting down are
/// never tried: there's no putting those back.
/// </remarks>
[Trait("Category", "HandsOn")]
public class HandsOnTests(ITestOutputHelper output)
{
    bool Asked
    {
        get
        {
            if (Environment.GetEnvironmentVariable("PUCKSLEY_HANDS_ON") == "1") return true;
            output.WriteLine("Not asked to (set PUCKSLEY_HANDS_ON=1): nothing was changed");
            return false;
        }
    }

    [Fact]
    public void The_volume_steps_and_comes_back()
    {
        if (!Asked) return;
        using var audio = new PulseAudio(NullLogger.Instance);
        audio.Start();
        var before = audio.GetMaster();
        if (before is null)
        {
            output.WriteLine("No output device");
            return;
        }
        int nudged = before.Volume < 100 ? before.Volume + 1 : before.Volume - 1;
        try
        {
            var after = audio.SetMaster(nudged, null);
            output.WriteLine($"{before.Volume}% → {after.Volume}% on {after.DeviceName}");
            Assert.Equal(nudged, after.Volume);
            Assert.Equal(before.Muted, after.Muted);
        }
        finally
        {
            Assert.Equal(before.Volume, audio.SetMaster(before.Volume, before.Muted).Volume);
        }
    }

    [Fact]
    public void The_output_switches_and_comes_back()
    {
        if (!Asked) return;
        using var audio = new PulseAudio(NullLogger.Instance);
        audio.Start();
        var outputs = audio.GetOutputs();
        var current = outputs.FirstOrDefault(o => o.IsDefault);
        var other = outputs.FirstOrDefault(o => !o.IsDefault);
        if (current is null || other is null)
        {
            output.WriteLine($"Only {outputs.Count} output: nothing to switch to");
            return;
        }
        try
        {
            audio.SetDefaultOutput(other.Id);
            Assert.Equal(other.Id, audio.GetOutputs().Single(o => o.IsDefault).Id);
            output.WriteLine($"{current.Name} → {other.Name}");
        }
        finally
        {
            audio.SetDefaultOutput(current.Id);
            Assert.Equal(current.Id, audio.GetOutputs().Single(o => o.IsDefault).Id);
        }
    }

    [Fact]
    public async Task The_clipboard_takes_text_and_gets_its_own_back()
    {
        if (!Asked) return;
        var session = DesktopSession.Current;
        if (!session.Graphical) return;
        var clipboard = new ClipboardAccess(session);
        var before = await clipboard.ReadAsync(default);
        if (before.Text is null && (before.Files is not null || before.ImageJpeg is not null))
        {
            output.WriteLine("The clipboard holds files or a picture, which couldn't be put back: left alone");
            return;
        }
        string line = $"Pucksley clipboard test — “quotes”, tabs\tand\nline breaks {Guid.NewGuid():n}";
        try
        {
            await clipboard.WriteAsync(line, default);
            Assert.Equal(line, (await clipboard.ReadAsync(default)).Text);
            output.WriteLine("Wrote and read back a line");
        }
        finally
        {
            if (!string.IsNullOrEmpty(before.Text))
            {
                await clipboard.WriteAsync(before.Text, default);
                Assert.Equal(before.Text, (await clipboard.ReadAsync(default)).Text);
            }
        }
    }

    [Fact]
    public async Task The_brightness_steps_and_comes_back()
    {
        if (!Asked) return;
        var display = new Display(DesktopSession.Current);
        int before;
        try
        {
            before = await display.BrightnessAsync(null, null, default);
        }
        catch (PluginException e)
        {
            output.WriteLine(e.Message);
            return;
        }
        int nudged = before < 100 ? before + 1 : before - 1;
        try
        {
            int after = await display.BrightnessAsync(nudged, null, default);
            output.WriteLine($"{before}% → {after}%");
            Assert.InRange(after, nudged - 1, nudged + 1); // some screens round to their own steps
        }
        finally
        {
            await display.BrightnessAsync(before, null, default);
        }
    }

    [Fact]
    public async Task Dark_mode_can_be_written()
    {
        if (!Asked) return;
        var display = new Display(DesktopSession.Current);
        if (await display.IsDarkModeAsync(default) is not bool dark)
        {
            output.WriteLine("This desktop doesn't say whether it's dark");
            return;
        }
        await display.SetDarkModeAsync(dark, default); // the value it already has: written, nothing changes
        Assert.Equal(dark, await display.IsDarkModeAsync(default));
        output.WriteLine($"Dark mode stays {dark}");
    }

    [Fact]
    public async Task Another_window_comes_to_the_front_and_the_first_goes_back()
    {
        if (!Asked) return;
        var desktop = new LinuxDesktop();
        await desktop.LoadAppsAsync(NullLogger.Instance, default);
        IReadOnlyList<AppWindow> windows;
        try
        {
            windows = await desktop.WindowsAsync(default);
        }
        catch (PluginException e)
        {
            output.WriteLine(e.Message);
            return;
        }
        var front = await desktop.ForegroundAsync(default);
        var other = windows.FirstOrDefault(w => w.Handle != front?.Handle);
        if (front is null || other is null)
        {
            output.WriteLine("Fewer than two windows: nothing to switch between");
            return;
        }
        if (await desktop.IsLockedAsync(default))
        {
            output.WriteLine("The screen is locked, and a locked screen keeps the focus: try this one unlocked");
            return;
        }
        try
        {
            Assert.True(await desktop.FocusAsync(other, default));
            Assert.Equal(other.Handle, (await desktop.ForegroundAsync(default))?.Handle);
            output.WriteLine($"{front.AppName} → {other.AppName}");
        }
        finally
        {
            Assert.True(await desktop.FocusAsync(front, default));
        }
    }

    [Fact]
    public async Task The_bare_desktop_shows_and_the_window_comes_back()
    {
        if (!Asked) return;
        var desktop = new LinuxDesktop();
        await desktop.LoadAppsAsync(NullLogger.Instance, default);
        AppWindow? front;
        try
        {
            front = await desktop.ForegroundAsync(default);
            if (front is null) return;
            if (await desktop.IsLockedAsync(default))
            {
                output.WriteLine("The screen is locked, and a locked screen keeps the focus: try this one unlocked");
                return;
            }
            await desktop.ShowDesktopAsync(default);
        }
        catch (PluginException e)
        {
            output.WriteLine(e.Message);
            return;
        }
        try
        {
            await Task.Delay(300);
            var now = await desktop.ForegroundAsync(default);
            output.WriteLine($"In front after showing the desktop: {now?.AppName ?? "(nothing)"}");
            Assert.NotEqual(front.Handle, now?.Handle);
        }
        finally
        {
            Assert.True(await desktop.FocusAsync(front, default));
        }
    }
}
