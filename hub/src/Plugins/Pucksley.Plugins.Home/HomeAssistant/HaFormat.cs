using Pucksley.Plugins.Shared;

namespace Pucksley.Plugins.HomeAssistant;

/// <summary>Short, speakable descriptions of entity states ("on, 60%", "heat, set 21 °C, now 19.5 °C").</summary>
internal static class HaFormat
{
    public static string Describe(HaEntity e)
    {
        if (!e.Available) return "unavailable";
        var parts = new List<string>();
        switch (e.Domain)
        {
            case "light":
                if (e.State != "on") return "off";
                parts.Add("on");
                if (BrightnessPct(e) is int pct) parts.Add($"{pct}%");
                if (e.AttrNum("color_temp_kelvin") is double k) parts.Add($"{k:0} K");
                break;

            case "climate":
                parts.Add(Humanize(e.State));
                if (e.AttrNum("temperature") is double set) parts.Add($"set {Text.Num(set)} °C");
                else if (e.AttrNum("target_temp_low") is double lo && e.AttrNum("target_temp_high") is double hi)
                    parts.Add($"set {Text.Num(lo)}–{Text.Num(hi)} °C");
                if (e.AttrNum("current_temperature") is double now) parts.Add($"now {Text.Num(now)} °C");
                if (e.Attr("hvac_action") is { } action && action is not ("idle" or "off")) parts.Add(Humanize(action));
                break;

            case "cover":
                parts.Add(Humanize(e.State));
                if (e.AttrNum("current_position") is double pos) parts.Add($"{pos:0}% open");
                break;

            case "media_player":
                parts.Add(Humanize(e.State));
                if (e.Attr("media_title") is { Length: > 0 } title)
                    parts.Add(e.Attr("media_artist") is { Length: > 0 } artist ? $"'{title}' by {artist}" : $"'{title}'");
                if (e.AttrNum("volume_level") is double vol) parts.Add($"volume {vol * 100:0}%");
                if (Json.Bool(e.Attributes, "is_volume_muted") == true) parts.Add("muted");
                if (e.Attr("source") is { Length: > 0 } source) parts.Add($"source {source}");
                break;

            case "fan":
                parts.Add(Humanize(e.State));
                if (e.State == "on" && e.AttrNum("percentage") is double speed) parts.Add($"{speed:0}%");
                break;

            case "binary_sensor":
                return BinarySensor(e);

            case "weather":
                parts.Add(Humanize(e.State));
                if (e.AttrNum("temperature") is double t) parts.Add($"{Text.Num(t)} {e.Attr("temperature_unit") ?? "°C"}");
                if (e.AttrNum("humidity") is double h) parts.Add($"humidity {h:0}%");
                break;

            case "person" or "device_tracker":
                return e.State switch { "home" => "home", "not_home" => "away", var s => Humanize(s) };

            default:
                var unit = e.Attr("unit_of_measurement");
                parts.Add(string.IsNullOrEmpty(unit) ? Humanize(e.State) : $"{FormatNumber(e.State)} {unit}");
                break;
        }
        return string.Join(", ", parts);
    }

    /// <summary>"Kitchen Ceiling: on, 60%".</summary>
    public static string Line(HaEntity e) => $"{e.Name}: {Describe(e)}";

    public static int? BrightnessPct(HaEntity e) =>
        e.AttrNum("brightness") is double b ? (int)Math.Round(b / 255.0 * 100) : null;

    static string BinarySensor(HaEntity e)
    {
        bool on = e.State == "on";
        return e.Attr("device_class") switch
        {
            "door" or "window" or "garage_door" or "opening" => on ? "open" : "closed",
            "motion" or "occupancy" or "presence" => on ? "detected" : "clear",
            "moisture" => on ? "wet" : "dry",
            "lock" => on ? "unlocked" : "locked",
            "connectivity" => on ? "connected" : "disconnected",
            "battery" => on ? "low" : "normal",
            "smoke" or "gas" or "carbon_monoxide" or "problem" or "safety" => on ? "ALERT" : "clear",
            _ => on ? "on" : "off",
        };
    }

    static string Humanize(string state) => state switch
    {
        "heat_cool" => "heat/cool",
        "fan_only" => "fan only",
        _ => state.Replace('_', ' '),
    };

    /// <summary>"12.3456" → "12.35" (sensors often over-report precision).</summary>
    static string FormatNumber(string state) =>
        double.TryParse(state, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d)
            ? Text.Num(d, 2)
            : Humanize(state);
}
