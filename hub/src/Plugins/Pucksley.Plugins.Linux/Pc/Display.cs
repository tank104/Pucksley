using System.Globalization;
using Pucksley.Plugins.Linux.Common;
using Pucksley.Sdk;
using Tmds.DBus.Protocol;

namespace Pucksley.Plugins.Linux.Pc;

/// <summary>Dark mode and screen brightness.</summary>
internal sealed class Display(DesktopSession session)
{
    static readonly TimeSpan Timeout = TimeSpan.FromSeconds(8);
    // The portal answers at once when it's well; one that has hung shouldn't hold the answer up
    static readonly TimeSpan PortalTimeout = TimeSpan.FromSeconds(2);
    const string Interface = "org.gnome.desktop.interface";

    // ───────────────────────── Dark mode

    /// <summary>
    /// What apps are told to prefer: the desktop portal's colour scheme (every desktop publishes it
    /// there), or GNOME's setting where there's no portal. Null when neither can say.
    /// </summary>
    public async Task<bool?> IsDarkModeAsync(CancellationToken cancel)
    {
        if (Bus.Session.Exists)
        {
            try
            {
                var value = await Bus.Session.CallAsync("org.freedesktop.portal.Desktop", "/org/freedesktop/portal/desktop", "org.freedesktop.portal.Settings", "Read",
                    static (m, _) => m.GetBodyReader().ReadVariantValue(), "ss", (ref MessageWriter w) =>
                    {
                        w.WriteString("org.freedesktop.appearance");
                        w.WriteString("color-scheme");
                    }, PortalTimeout, cancel).ConfigureAwait(false);
                while (value.Type == VariantValueType.Variant) value = value.GetVariantValue(); // it comes wrapped twice
                if (value.Type == VariantValueType.UInt32) return value.GetUInt32() == 1;      // 0 no preference, 1 dark, 2 light
            }
            catch (Exception e) when (e is DBusExceptionBase or PluginException)
            {
                // no portal: ask the settings directly
            }
        }
        if (Programs.Has("gsettings"))
        {
            var result = await Programs.RunAsync("gsettings", ["get", Interface, "color-scheme"], Timeout, cancel).ConfigureAwait(false);
            if (result.ExitCode == 0) return result.Output.Contains("dark", StringComparison.OrdinalIgnoreCase);
        }
        return null;
    }

    /// <summary>
    /// Tells apps (and the websites that follow them) to go dark or light. On KDE that's Plasma's
    /// colour scheme; elsewhere GNOME's colour-scheme setting, which GTK apps, browsers and the
    /// desktop portal all read (Hyprland and Omarchy included).
    /// </summary>
    public async Task SetDarkModeAsync(bool on, CancellationToken cancel)
    {
        if (session.Compositor == Compositor.Kde && Programs.Has("plasma-apply-colorscheme"))
        {
            await Programs.OutputAsync("plasma-apply-colorscheme", [on ? "BreezeDark" : "BreezeLight"], Timeout, cancel).ConfigureAwait(false);
            return;
        }
        if (!Programs.Has("gsettings")) throw new PluginException($"I don't know how to change dark mode on this desktop ({session.Name}).");
        await Programs.OutputAsync("gsettings", ["set", Interface, "color-scheme", on ? "prefer-dark" : "prefer-light"], Timeout, cancel).ConfigureAwait(false);
        // Older GTK apps follow the theme's name instead: flip it when it's the stock pair
        var theme = await Programs.RunAsync("gsettings", ["get", Interface, "gtk-theme"], Timeout, cancel).ConfigureAwait(false);
        if (theme.ExitCode == 0 && theme.Output.Trim('\'', '"') is "Adwaita" or "Adwaita-dark")
            await Programs.RunAsync("gsettings", ["set", Interface, "gtk-theme", on ? "Adwaita-dark" : "Adwaita"], Timeout, cancel).ConfigureAwait(false);
    }

    // ───────────────────────── Brightness

    /// <summary>
    /// Reads (both null) or sets the screen's brightness; returns the new level. Tried in turn:
    /// Omarchy's own command (it knows laptop panels, DDC monitors and Apple displays), a laptop's
    /// backlight, and a desktop monitor over DDC (ddcutil).
    /// </summary>
    public async Task<int> BrightnessAsync(int? level, int? change, CancellationToken cancel)
    {
        foreach (var screen in new Func<CancellationToken, Task<IBrightness?>>[] { OmarchyAsync, c => Task.FromResult<IBrightness?>(Backlight.Find("/sys/class/backlight")), DdcAsync })
        {
            IBrightness? found;
            try
            {
                found = await screen(cancel).ConfigureAwait(false);
            }
            catch (PluginException)
            {
                continue;
            }
            if (found is null) continue;
            int now = await found.GetAsync(cancel).ConfigureAwait(false);
            int? target = level is int l ? Math.Clamp(l, 0, 100) : change is int c ? Math.Clamp(now + c, 0, 100) : null;
            if (target is null || target == now) return now;
            await found.SetAsync(target.Value, cancel).ConfigureAwait(false);
            return await found.GetAsync(cancel).ConfigureAwait(false);
        }
        throw new PluginException("This PC's screen brightness can't be changed from here: it has no laptop backlight, and reaching a desktop monitor needs ddcutil (and a monitor that takes DDC commands).");
    }

    /// <summary>One screen whose brightness can be read and set, 0–100.</summary>
    internal interface IBrightness
    {
        Task<int> GetAsync(CancellationToken cancel);
        Task SetAsync(int percent, CancellationToken cancel);
    }

    static async Task<IBrightness?> OmarchyAsync(CancellationToken cancel)
    {
        if (!Programs.Has("omarchy-brightness-display")) return null;
        var screen = new OmarchyBrightness();
        // It answers with nothing when the focused display can't be dimmed
        return await screen.TryGetAsync(cancel).ConfigureAwait(false) is null ? null : screen;
    }

    sealed class OmarchyBrightness : IBrightness
    {
        public async Task<int?> TryGetAsync(CancellationToken cancel)
        {
            var result = await Programs.RunAsync("omarchy-brightness-display", [], TimeSpan.FromSeconds(15), cancel).ConfigureAwait(false);
            return result.ExitCode == 0 ? ParsePercent(result.Output) : null;
        }

        public async Task<int> GetAsync(CancellationToken cancel) =>
            await TryGetAsync(cancel).ConfigureAwait(false) ?? throw new PluginException("The display didn't report its brightness.");

        public Task SetAsync(int percent, CancellationToken cancel) =>
            Programs.OutputAsync("omarchy-brightness-display", [percent.ToString(CultureInfo.InvariantCulture) + "%"], TimeSpan.FromSeconds(15), cancel);
    }

    /// <summary>The first whole number in what a brightness command printed ("60", "60%", "current value = 60").</summary>
    internal static int? ParsePercent(string output)
    {
        foreach (var word in output.Split([' ', '\n', '\t', '%', '=', ','], StringSplitOptions.RemoveEmptyEntries))
        {
            if (int.TryParse(word, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) && value is >= 0 and <= 100) return value;
        }
        return null;
    }

    /// <summary>A laptop panel's backlight in /sys/class/backlight.</summary>
    internal sealed class Backlight(string folder) : IBrightness
    {
        /// <summary>The panel's own driver when there are several (a generic "acpi_video0" often sits beside it and does less).</summary>
        public static Backlight? Find(string root)
        {
            if (!Directory.Exists(root)) return null;
            var devices = Directory.EnumerateDirectories(root).OrderBy(d => Path.GetFileName(d).StartsWith("acpi", StringComparison.Ordinal) ? 1 : 0).ThenBy(d => d, StringComparer.Ordinal).ToList();
            return devices.Count > 0 ? new Backlight(devices[0]) : null;
        }

        long Read(string file) => long.TryParse(File.ReadAllText(Path.Combine(folder, file)).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long v) ? v : 0;

        public Task<int> GetAsync(CancellationToken cancel)
        {
            long max = Read("max_brightness");
            return Task.FromResult(max <= 0 ? 0 : (int)Math.Round(100.0 * Read("brightness") / max));
        }

        public async Task SetAsync(int percent, CancellationToken cancel)
        {
            string name = Path.GetFileName(folder);
            if (Programs.Has("brightnessctl"))
            {
                await Programs.OutputAsync("brightnessctl", ["-d", name, "set", percent.ToString(CultureInfo.InvariantCulture) + "%"], Timeout, cancel).ConfigureAwait(false);
                return;
            }
            // logind sets it for the person at the screen, with no special permissions
            uint raw = (uint)Math.Round(Read("max_brightness") * percent / 100.0);
            try
            {
                await Bus.SystemWide.CallAsync("org.freedesktop.login1", "/org/freedesktop/login1/session/auto", "org.freedesktop.login1.Session", "SetBrightness", "ssu",
                    (ref MessageWriter w) =>
                    {
                        w.WriteString("backlight");
                        w.WriteString(name);
                        w.WriteUInt32(raw);
                    }, cancel: cancel).ConfigureAwait(false);
            }
            catch (DBusExceptionBase e)
            {
                throw new PluginException($"The screen's brightness couldn't be set ({e.Message}). Installing brightnessctl usually sorts it.", e);
            }
        }
    }

    static async Task<IBrightness?> DdcAsync(CancellationToken cancel)
    {
        if (!Programs.Has("ddcutil")) return null;
        var monitor = new DdcMonitor();
        try
        {
            await monitor.GetAsync(cancel).ConfigureAwait(false);
            return monitor;
        }
        catch (PluginException)
        {
            return null; // no monitor answers DDC (or no permission for the i2c bus)
        }
    }

    /// <summary>A desktop monitor's brightness control (VCP feature 10) over DDC.</summary>
    sealed class DdcMonitor : IBrightness
    {
        public async Task<int> GetAsync(CancellationToken cancel)
        {
            string output = await Programs.OutputAsync("ddcutil", ["getvcp", "10", "--brief"], TimeSpan.FromSeconds(15), cancel).ConfigureAwait(false);
            return ParseVcp(output) ?? throw new PluginException("The monitor didn't report its brightness.");
        }

        public Task SetAsync(int percent, CancellationToken cancel) =>
            Programs.OutputAsync("ddcutil", ["setvcp", "10", percent.ToString(CultureInfo.InvariantCulture)], TimeSpan.FromSeconds(15), cancel);
    }

    /// <summary>"VCP 10 C 60 100" (current, then the most it goes to) → 60 %.</summary>
    internal static int? ParseVcp(string output)
    {
        var line = output.Split('\n').FirstOrDefault(l => l.TrimStart().StartsWith("VCP 10", StringComparison.OrdinalIgnoreCase));
        var words = line?.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words is not { Length: >= 5 } || !int.TryParse(words[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out int current)
            || !int.TryParse(words[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out int max) || max <= 0) return null;
        return (int)Math.Clamp(Math.Round(100.0 * current / max), 0, 100);
    }
}
