using System.Globalization;
using Microsoft.Win32;
using Pucksley.Plugins.Windows.Common;
using Pucksley.Sdk;
using static Pucksley.Plugins.Windows.Common.NativeMethods;

namespace Pucksley.Plugins.Windows.Pc;

/// <summary>Dark mode and screen brightness.</summary>
internal static class Display
{
    const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    /// <summary>Null when Windows has never stored a choice.</summary>
    public static bool? IsDarkMode()
    {
        using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
        return key?.GetValue("AppsUseLightTheme") is int light ? light == 0 : null;
    }

    /// <summary>Blocking (the broadcast waits on every window): run off the caller's thread.</summary>
    public static void SetDarkMode(bool on)
    {
        using (var key = Registry.CurrentUser.CreateSubKey(PersonalizeKey, writable: true))
        {
            int light = on ? 0 : 1;
            key.SetValue("AppsUseLightTheme", light, RegistryValueKind.DWord);
            key.SetValue("SystemUsesLightTheme", light, RegistryValueKind.DWord);
        }
        // Tells the taskbar and running apps to re-read the theme
        SendMessageTimeout(HWND_BROADCAST, WM_SETTINGCHANGE, 0, "ImmersiveColorSet", SMTO_ABORTIFHUNG, 1000, out _);
    }

    /// <summary>
    /// Reads (both null) or sets the built-in screen's brightness through WMI's
    /// WmiMonitorBrightnessMethods, via Windows PowerShell (System.Management isn't referenced).
    /// Returns the new level.
    /// </summary>
    public static async Task<int> BrightnessAsync(int? level, int? change, CancellationToken cancel)
    {
        // Only integers go into the script, so nothing spoken can inject into it
        const string Current = "(Get-WmiObject -Namespace root/wmi -Class WmiMonitorBrightness -ErrorAction Stop | Select-Object -First 1).CurrentBrightness";
        string? target = level is int l ? Math.Clamp(l, 0, 100).ToString(CultureInfo.InvariantCulture)
            : change is int c ? $"[Math]::Max(0, [Math]::Min(100, [int]{Current} + ({c.ToString(CultureInfo.InvariantCulture)})))"
            : null;
        string script = target is null
            ? $"try {{ $b = {Current}; if ($null -eq $b) {{ 'NONE' }} else {{ 'LEVEL ' + $b }} }} catch {{ 'NONE ' + $_.Exception.Message }}"
            : "try { $m = Get-WmiObject -Namespace root/wmi -Class WmiMonitorBrightnessMethods -ErrorAction Stop; " +
              "if (-not $m) { 'NONE'; return }; " +
              $"$t = {target}; $m | ForEach-Object {{ $_.WmiSetBrightness(1, $t) | Out-Null }}; 'LEVEL ' + $t }} " +
              "catch { 'NONE ' + $_.Exception.Message }";

        var result = await PowerShell.RunAsync(script, TimeSpan.FromSeconds(20), cancel).ConfigureAwait(false);
        var line = result.Output.Split('\n').Select(s => s.Trim()).LastOrDefault(s => s.Length > 0) ?? "";
        if (line.StartsWith("LEVEL ", StringComparison.Ordinal) && int.TryParse(line[6..], NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
            return value;
        throw new PluginException("This PC's screen brightness can't be changed from Windows. That works on laptops and other built-in screens, not on most desktop monitors.");
    }
}
