using System.Globalization;
using Pucksley.Plugins.Linux.Common;
using Pucksley.Plugins.Linux.Pc.Windows;
using Pucksley.Sdk;
using Tmds.DBus.Protocol;

namespace Pucksley.Plugins.Linux.Pc;

/// <summary>
/// Screenshots. There's no one way to take them on Linux, so the ways are tried in the order that
/// suits the desktop: grim (Hyprland, Sway and the other wlroots compositors), Spectacle (KDE),
/// gnome-screenshot, the desktop portal (any Wayland desktop; it may ask the person once), and the
/// X11 programs (maim, scrot, ImageMagick's import).
/// </summary>
internal sealed class ScreenCapture(DesktopSession session, IWindowManager windows)
{
    static readonly TimeSpan Timeout = TimeSpan.FromSeconds(8);

    /// <summary>A grabbed picture (PNG or JPEG bytes), what it shows, and the window title if one was in front.</summary>
    internal sealed record Grab(byte[] Image, string Area, string? WindowTitle);

    public async Task<Screenshot> CaptureAsync(ScreenArea area, CancellationToken cancel)
    {
        var grab = await GrabAsync(area, cancel).ConfigureAwait(false);
        var image = Jpeg.FromEncoded(grab.Image);
        return new Screenshot(image.Jpeg, image.Width, image.Height, image.SourceWidth, image.SourceHeight, grab.Area, grab.WindowTitle);
    }

    internal async Task<Grab> GrabAsync(ScreenArea area, CancellationToken cancel)
    {
        if (!session.Graphical) throw new PluginException("The hub isn't running inside a desktop session on this PC, so there's no screen for it to see.");
        PluginException? last = null;
        foreach (var way in Ways())
        {
            try
            {
                if (await way(area, cancel).ConfigureAwait(false) is { Image.Length: > 0 } grab) return grab;
            }
            catch (PluginException e)
            {
                last = e;
            }
        }
        throw last ?? new PluginException(session.Wayland
            ? "I can't take screenshots on this desktop. Installing grim (Hyprland, Sway), or a desktop portal that offers screenshots, makes it possible."
            : "I can't take screenshots on this desktop. Installing maim or scrot makes it possible.");
    }

    IEnumerable<Func<ScreenArea, CancellationToken, Task<Grab?>>> Ways()
    {
        if (session.Wayland)
        {
            // Where grim belongs, its answer is the answer: asking the portal as well would only
            // put a question on a screen nobody may be looking at
            if (session.Wlroots && Programs.Has("grim"))
            {
                yield return GrimAsync;
                yield break;
            }
            if (session.Compositor == Compositor.Kde && Programs.Has("spectacle")) yield return SpectacleAsync;
            if (Programs.Has("gnome-screenshot")) yield return GnomeScreenshotAsync;
            if (Programs.Has("grim")) yield return GrimAsync;
            yield return PortalAsync;
        }
        else
        {
            if (session.Compositor == Compositor.Kde && Programs.Has("spectacle")) yield return SpectacleAsync;
            if (Programs.Has("maim")) yield return MaimAsync;
            if (Programs.Has("scrot")) yield return ScrotAsync;
            if (Programs.Has("gnome-screenshot")) yield return GnomeScreenshotAsync;
            if (Programs.Has("import")) yield return ImportAsync;
        }
    }

    // ───────────────────────── grim (wlroots compositors)

    async Task<Grab?> GrimAsync(ScreenArea area, CancellationToken cancel)
    {
        ScreenLayout? layout = null;
        try
        {
            layout = await windows.LayoutAsync(cancel).ConfigureAwait(false);
        }
        catch (PluginException)
        {
            // without the layout it's the whole desktop
        }
        var (region, description) = GrimRegion(area, layout);
        // PNG with light compression: it's decoded again straight away
        var result = await Programs.RunBinaryAsync("grim", ["-t", "png", "-l", "1", .. region, "-"], Timeout, cancel).ConfigureAwait(false);
        if (result.ExitCode != 0 || result.Output.Length == 0)
            throw new PluginException($"I couldn't capture the screen ({Text.Clip(result.Error.Length > 0 ? result.Error : "grim gave no picture", 120)}).");
        return new Grab(result.Output, description, layout?.ActiveWindowTitle);
    }

    /// <summary>The part of the desktop grim should capture (<c>-g "x,y wxh"</c>, <c>-o NAME</c> or nothing), and what to call it.</summary>
    internal static (string[] Arguments, string Description) GrimRegion(ScreenArea area, ScreenLayout? layout)
    {
        var outputs = layout?.Outputs ?? [];
        if (outputs.Count == 0) return ([], "the screen");
        string oneScreen = outputs.Count > 1 ? "the screen with the active window" : "the screen";
        var focused = outputs.FirstOrDefault(o => o.Focused) ?? outputs[0];
        switch (area)
        {
            case ScreenArea.All:
                return ([], outputs.Count > 1 ? "all screens" : "the screen");
            case ScreenArea.Primary:
                // Wayland has no "primary": the one at the desktop's origin is the nearest thing
                var primary = outputs.FirstOrDefault(o => o.Bounds is { X: 0, Y: 0 }) ?? outputs[0];
                return (["-o", primary.Name], outputs.Count > 1 ? "the primary screen" : "the screen");
            case ScreenArea.ActiveWindow when layout?.ActiveWindow is { } window:
                var visible = window.Intersect(ScreenRect.Union(outputs.Select(o => o.Bounds)));
                if (visible.Width >= 16 && visible.Height >= 16)
                    return (["-g", string.Create(CultureInfo.InvariantCulture, $"{visible.X},{visible.Y} {visible.Width}x{visible.Height}")], "the active window");
                goto default;
            default:
                return (["-o", focused.Name], oneScreen);
        }
    }

    // ───────────────────────── Programs that save to a file

    Task<Grab?> SpectacleAsync(ScreenArea area, CancellationToken cancel) => area switch
    {
        ScreenArea.All => FileGrabAsync("spectacle", f => ["-b", "-n", "-f", "-o", f], "all screens", cancel),
        ScreenArea.ActiveWindow => FileGrabAsync("spectacle", f => ["-b", "-n", "-a", "-o", f], "the active window", cancel),
        _ => FileGrabAsync("spectacle", f => ["-b", "-n", "-m", "-o", f], "the screen", cancel),
    };

    Task<Grab?> GnomeScreenshotAsync(ScreenArea area, CancellationToken cancel) => area == ScreenArea.ActiveWindow
        ? FileGrabAsync("gnome-screenshot", f => ["-w", "-f", f], "the active window", cancel)
        : FileGrabAsync("gnome-screenshot", f => ["-f", f], "the screen", cancel);

    Task<Grab?> ScrotAsync(ScreenArea area, CancellationToken cancel) => area == ScreenArea.ActiveWindow
        ? FileGrabAsync("scrot", f => ["-u", "-o", f], "the active window", cancel)
        : FileGrabAsync("scrot", f => ["-o", f], "the screen", cancel);

    Task<Grab?> ImportAsync(ScreenArea area, CancellationToken cancel) =>
        FileGrabAsync("import", f => ["-window", "root", f], "the screen", cancel);

    async Task<Grab?> MaimAsync(ScreenArea area, CancellationToken cancel)
    {
        if (area == ScreenArea.ActiveWindow && Programs.Has("xdotool"))
        {
            var active = await Programs.RunAsync("xdotool", ["getactivewindow"], Timeout, cancel).ConfigureAwait(false);
            if (active.ExitCode == 0 && long.TryParse(active.Output, NumberStyles.Integer, CultureInfo.InvariantCulture, out long id))
                return await FileGrabAsync("maim", f => ["-u", "-i", id.ToString(CultureInfo.InvariantCulture), f], "the active window", cancel).ConfigureAwait(false);
        }
        return await FileGrabAsync("maim", f => ["-u", f], "the screen", cancel).ConfigureAwait(false);
    }

    async Task<Grab?> FileGrabAsync(string program, Func<string, string[]> arguments, string description, CancellationToken cancel)
    {
        string file = Path.Combine(ScratchFolder(), $"pucksley-shot-{Guid.NewGuid():n}.png");
        try
        {
            var result = await Programs.RunAsync(program, arguments(file), Timeout, cancel).ConfigureAwait(false);
            if (!File.Exists(file) || new FileInfo(file).Length == 0)
                throw new PluginException($"I couldn't capture the screen ({Text.Clip(result.Error.Length > 0 ? result.Error : $"{program} gave no picture", 120)}).");
            string? title = null;
            try
            {
                title = (await windows.ForegroundAsync(cancel).ConfigureAwait(false))?.Title;
            }
            catch (PluginException)
            {
                // the picture matters, not the title
            }
            return new Grab(await File.ReadAllBytesAsync(file, cancel).ConfigureAwait(false), description, title);
        }
        finally
        {
            try { File.Delete(file); } catch (IOException) { /* nothing was written */ }
        }
    }

    /// <summary>Somewhere private and in memory for a picture that exists only for a moment.</summary>
    static string ScratchFolder() =>
        Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") is { Length: > 0 } runtime && Directory.Exists(runtime) ? runtime : Path.GetTempPath();

    // ───────────────────────── The desktop portal (GNOME and any other Wayland desktop)

    /// <summary>
    /// Asks the desktop itself for a screenshot (org.freedesktop.portal.Screenshot). The desktop may
    /// ask the person to allow it the first time. It only does whole screens.
    /// </summary>
    async Task<Grab?> PortalAsync(ScreenArea area, CancellationToken cancel)
    {
        if (!Bus.Session.Exists) return null;
        string? uri;
        try
        {
            var connection = await Bus.Session.ConnectAsync(cancel).ConfigureAwait(false);
            // The answer comes as a signal on a path made from our bus name and a token of our choosing
            string token = "pucksley" + Guid.NewGuid().ToString("n")[..12];
            string sender = (connection.UniqueName ?? "").TrimStart(':').Replace('.', '_');
            var answered = new TaskCompletionSource<(uint Response, string? Uri)>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var watching = await connection.AddMatchAsync(
                new MatchRule
                {
                    Type = MessageType.Signal,
                    Path = $"/org/freedesktop/portal/desktop/request/{sender}/{token}",
                    Interface = "org.freedesktop.portal.Request",
                    Member = "Response",
                },
                static (Message m, object? _) =>
                {
                    var reader = m.GetBodyReader();
                    uint response = reader.ReadUInt32();
                    var results = reader.ReadDictionaryOfStringToVariantValue();
                    return (response, results.TryGetValue("uri", out var u) && u.Type == VariantValueType.String ? u.GetString() : null);
                },
                (Notification<(uint Response, string? Uri)> n) => { if (n.HasValue) answered.TrySetResult(n.Value); },
                emitOnCapturedContext: false).ConfigureAwait(false);

            var options = new Dictionary<string, VariantValue>
            {
                ["handle_token"] = VariantValue.String(token),
                ["interactive"] = VariantValue.Bool(false),
                ["modal"] = VariantValue.Bool(false),
            };
            await Bus.Session.CallAsync("org.freedesktop.portal.Desktop", "/org/freedesktop/portal/desktop", "org.freedesktop.portal.Screenshot", "Screenshot",
                "sa{sv}", (ref MessageWriter w) =>
                {
                    w.WriteString("");
                    w.WriteDictionary(options);
                }, cancel: cancel).ConfigureAwait(false);

            var (response, answer) = await answered.Task.WaitAsync(TimeSpan.FromSeconds(30), cancel).ConfigureAwait(false);
            if (response == 1) throw new PluginException("The screenshot was declined on the PC.");
            if (response != 0 || answer is null) throw new PluginException("This desktop couldn't take a screenshot.");
            uri = answer;
        }
        catch (DBusExceptionBase e)
        {
            if (Bus.IsMissing(e)) return null; // no portal, or one that doesn't do screenshots
            throw new PluginException($"This desktop couldn't take a screenshot ({e.Message}).", e);
        }
        catch (TimeoutException)
        {
            throw new PluginException("Nobody allowed the screenshot on the PC (the desktop asked there).");
        }

        // The portal wrote a file for us: read it and leave nothing behind
        string file = new Uri(uri).LocalPath;
        try
        {
            return new Grab(await File.ReadAllBytesAsync(file, cancel).ConfigureAwait(false), "the screen", null);
        }
        finally
        {
            try { File.Delete(file); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* not ours to remove */ }
        }
    }
}
