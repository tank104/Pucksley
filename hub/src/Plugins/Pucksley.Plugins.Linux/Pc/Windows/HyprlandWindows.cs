using System.Globalization;
using System.Text.Json;
using Pucksley.Plugins.Linux.Common;
using Pucksley.Sdk;

namespace Pucksley.Plugins.Linux.Pc.Windows;

/// <summary>Runs <c>hyprctl</c>; a test hands in recorded answers instead.</summary>
internal interface IHyprctl
{
    /// <summary>What <c>hyprctl</c> printed for these arguments.</summary>
    Task<string> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancel);
}

/// <summary>
/// The <c>hyprctl</c> program. A hub that outlives a Hyprland restart is left holding the old
/// session's address (HYPRLAND_INSTANCE_SIGNATURE), so when Hyprland can't be reached that way it
/// is asked for by position instead ("the first Hyprland running").
/// </summary>
internal sealed class Hyprctl : IHyprctl
{
    static readonly TimeSpan Timeout = TimeSpan.FromSeconds(4);
    volatile bool _byPosition;

    public async Task<string> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancel)
    {
        if (!_byPosition)
        {
            var first = await Programs.RunAsync("hyprctl", arguments, Timeout, cancel).ConfigureAwait(false);
            if (Reached(first)) return first.Output;
        }
        var again = await Programs.RunAsync("hyprctl", ["-i", "0", .. arguments], Timeout, cancel).ConfigureAwait(false);
        if (!Reached(again)) throw new PluginException("Hyprland isn't answering (hyprctl couldn't reach it).");
        _byPosition = true;
        return again.Output;
    }

    // It reports an unreachable Hyprland in words, with exit code 0
    static bool Reached(ProcessRunner.Result result) =>
        !result.Output.StartsWith("Couldn't connect", StringComparison.Ordinal) && !result.Output.Contains("HYPRLAND_INSTANCE_SIGNATURE", StringComparison.Ordinal)
        && !result.Error.Contains("HYPRLAND_INSTANCE_SIGNATURE", StringComparison.Ordinal);
}

/// <summary>Hyprland's windows, through <c>hyprctl</c> (what Omarchy runs on).</summary>
internal sealed class HyprlandWindows(IHyprctl hyprctl, AppNamer namer) : IWindowManager
{
    public bool CanSeeWindows => true;

    public async Task<IReadOnlyList<AppWindow>> WindowsAsync(CancellationToken cancel) =>
        ParseClients(await hyprctl.RunAsync(["-j", "clients"], cancel).ConfigureAwait(false), namer, Environment.ProcessId);

    public async Task<AppWindow?> ForegroundAsync(CancellationToken cancel) =>
        ParseActiveWindow(await hyprctl.RunAsync(["-j", "activewindow"], cancel).ConfigureAwait(false), namer);

    /// <summary>The windows someone could switch to (Alt+Tab), most recently used first.</summary>
    internal static List<AppWindow> ParseClients(string json, AppNamer namer, int selfPid)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return [];
        return doc.RootElement.EnumerateArray()
            .Where(c => c.ValueKind == JsonValueKind.Object && Bool(c, "mapped", true) && String(c, "title").Length > 0)
            .OrderBy(c => Int(c, "focusHistoryID", int.MaxValue))
            .Select(c => Describe(c, namer))
            .Where(w => w is not null && w.ProcessId != selfPid)
            .ToList()!;
    }

    internal static AppWindow? ParseActiveWindow(string json, AppNamer namer)
    {
        using var doc = JsonDocument.Parse(json);
        // "{}" when nothing is focused (an empty workspace)
        return doc.RootElement.ValueKind == JsonValueKind.Object ? Describe(doc.RootElement, namer) : null;
    }

    static AppWindow? Describe(JsonElement client, AppNamer namer)
    {
        if (ParseAddress(String(client, "address")) is not { } address) return null;
        string windowClass = String(client, "class") is { Length: > 0 } c ? c : String(client, "initialClass");
        int pid = Int(client, "pid", 0);
        var (processName, appName) = namer(windowClass, pid);
        return new AppWindow(address, String(client, "title"), pid, processName, appName);
    }

    /// <summary>"0x56398c5d62e0" → the number it is.</summary>
    internal static nint? ParseAddress(string address)
    {
        if (address.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) address = address[2..];
        return address.Length > 0 && long.TryParse(address, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out long value) ? (nint)value : null;
    }

    internal static string FormatAddress(nint handle) => "0x" + ((long)handle).ToString("x", CultureInfo.InvariantCulture);

    public async Task<bool> FocusAsync(AppWindow window, CancellationToken cancel)
    {
        string address = FormatAddress(window.Handle);
        await DispatchAsync($"hl.dsp.focus({{ window = \"address:{address}\" }})", ["focuswindow", $"address:{address}"], cancel).ConfigureAwait(false);
        var front = await ForegroundAsync(cancel).ConfigureAwait(false);
        return front?.Handle == window.Handle;
    }

    /// <summary>A tiling desktop has no "minimise everything": an empty workspace is its bare desktop.</summary>
    public Task ShowDesktopAsync(CancellationToken cancel) =>
        DispatchAsync("hl.dsp.focus({ workspace = \"empty\" })", ["workspace", "empty"], cancel);

    /// <summary>
    /// Hyprland 0.55 moved its commands to Lua (<c>hl.dsp.focus({…})</c>); older ones take words
    /// (<c>focuswindow address:…</c>). Each refuses the other's, so try the new and fall back.
    /// </summary>
    async Task DispatchAsync(string lua, string[] legacy, CancellationToken cancel)
    {
        string modern = await hyprctl.RunAsync(["dispatch", lua], cancel).ConfigureAwait(false);
        if (modern.Trim() == "ok") return;
        string old = await hyprctl.RunAsync(["dispatch", .. legacy], cancel).ConfigureAwait(false);
        if (old.Trim() == "ok") return;
        throw new PluginException($"Hyprland wouldn't do that ({Text.Clip(FirstLine(old.Length > 0 ? old : modern), 120)}).");
    }

    static string FirstLine(string text) => text.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "no reason given";

    public async Task<bool?> IsLockedAsync(CancellationToken cancel) =>
        ParseLocked(await hyprctl.RunAsync(["-j", "monitors"], cancel).ConfigureAwait(false));

    /// <summary>
    /// Hyprland doesn't report a lock directly, but a locked session is one of the reasons it gives
    /// for a monitor not going "solitary": LOCK in solitaryBlockedBy (how Omarchy tells, too). Null
    /// when this Hyprland doesn't list reasons, or stopped before reaching that one.
    /// </summary>
    internal static bool? ParseLocked(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;
        bool told = false;
        foreach (var monitor in doc.RootElement.EnumerateArray())
        {
            if (monitor.ValueKind != JsonValueKind.Object || !monitor.TryGetProperty("solitaryBlockedBy", out var reasons) || reasons.ValueKind != JsonValueKind.Array) continue;
            var list = reasons.EnumerateArray().Where(r => r.ValueKind == JsonValueKind.String).Select(r => r.GetString()).ToList();
            if (list.Contains("LOCK")) return true;
            // A monitor with no workspace yet stops at that reason, before it gets to the lock
            if (!list.Contains("WORKSPACE")) told = true;
        }
        return told ? false : null;
    }

    public async Task<ScreenLayout?> LayoutAsync(CancellationToken cancel)
    {
        var outputs = ParseMonitors(await hyprctl.RunAsync(["-j", "monitors"], cancel).ConfigureAwait(false));
        var (window, title) = ParseActiveRect(await hyprctl.RunAsync(["-j", "activewindow"], cancel).ConfigureAwait(false));
        return new ScreenLayout(outputs, window, title);
    }

    /// <summary>Each monitor's place on the desktop, in layout units (its pixels divided by its scale, turned if it's rotated).</summary>
    internal static List<ScreenOutput> ParseMonitors(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var outputs = new List<ScreenOutput>();
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return outputs;
        foreach (var m in doc.RootElement.EnumerateArray())
        {
            if (m.ValueKind != JsonValueKind.Object || Bool(m, "disabled", false)) continue;
            double scale = m.TryGetProperty("scale", out var s) && s.TryGetDouble(out double value) && value > 0 ? value : 1;
            int width = (int)Math.Round(Int(m, "width", 0) / scale), height = (int)Math.Round(Int(m, "height", 0) / scale);
            if (Int(m, "transform", 0) % 2 == 1) (width, height) = (height, width); // 90° or 270°
            string name = String(m, "name");
            if (name.Length == 0 || width <= 0 || height <= 0) continue;
            outputs.Add(new ScreenOutput(name, new ScreenRect(Int(m, "x", 0), Int(m, "y", 0), width, height), Bool(m, "focused", false)));
        }
        return outputs;
    }

    internal static (ScreenRect? Rect, string? Title) ParseActiveRect(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var w = doc.RootElement;
        if (w.ValueKind != JsonValueKind.Object || !w.TryGetProperty("at", out var at) || !w.TryGetProperty("size", out var size)) return (null, null);
        if (at.ValueKind != JsonValueKind.Array || size.ValueKind != JsonValueKind.Array || at.GetArrayLength() < 2 || size.GetArrayLength() < 2) return (null, null);
        var rect = new ScreenRect(at[0].GetInt32(), at[1].GetInt32(), size[0].GetInt32(), size[1].GetInt32());
        string title = String(w, "title");
        return (rect, title.Length > 0 ? title : null);
    }

    static string String(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    static int Int(JsonElement e, string name, int fallback) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int n) ? n : fallback;

    static bool Bool(JsonElement e, string name, bool fallback) => e.TryGetProperty(name, out var v) ? v.ValueKind == JsonValueKind.True || (v.ValueKind != JsonValueKind.False && fallback) : fallback;
}
