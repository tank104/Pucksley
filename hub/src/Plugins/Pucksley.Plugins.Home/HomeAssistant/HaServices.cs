using System.Globalization;
using System.Text.Json.Nodes;
using Pucksley.Sdk;

namespace Pucksley.Plugins.HomeAssistant;

internal enum OnOff { On, Off, Toggle }

/// <summary>Which Home Assistant service does what to which kind of entity.</summary>
internal static class HaServices
{
    /// <summary>Domains with their own turn_on / turn_off services.</summary>
    static readonly HashSet<string> OwnOnOff =
        ["light", "switch", "fan", "input_boolean", "media_player", "climate", "automation", "siren", "humidifier", "remote", "water_heater", "script"];

    /// <summary>Domains with their own toggle service (others are toggled from their current state).</summary>
    static readonly HashSet<string> OwnToggle =
        ["light", "switch", "fan", "input_boolean", "media_player", "automation", "siren", "humidifier", "remote", "script", "cover", "valve"];

    /// <summary>Things that report but can't be switched.</summary>
    static readonly HashSet<string> ReadOnly =
        ["sensor", "binary_sensor", "weather", "sun", "zone", "person", "device_tracker", "update", "event", "calendar", "image", "todo", "tts", "stt", "conversation"];

    public static bool IsSwitchable(string domain) => !ReadOnly.Contains(domain);

    /// <summary>The service that turns <paramref name="entity"/> on, off or toggles it.</summary>
    public static (string Domain, string Service) For(HaEntity entity, OnOff action)
    {
        var domain = entity.Domain;
        if (action == OnOff.Toggle && !OwnToggle.Contains(domain) && domain is not ("scene" or "button" or "input_button"))
            action = entity.IsActive ? OnOff.Off : OnOff.On; // e.g. climate, vacuum, group: no toggle service everywhere
        return (domain, action) switch
        {
            ("lock", _) => throw new PluginException("Locks aren't turned on or off: use ha_call_service with lock.lock or lock.unlock."),
            ("alarm_control_panel", _) => throw new PluginException("Use ha_call_service with alarm_control_panel.alarm_arm_away / alarm_disarm."),
            ("cover", OnOff.On) => ("cover", "open_cover"),
            ("cover", OnOff.Off) => ("cover", "close_cover"),
            ("valve", OnOff.On) => ("valve", "open_valve"),
            ("valve", OnOff.Off) => ("valve", "close_valve"),
            ("scene", OnOff.Off) => throw new PluginException($"{entity.Name} is a scene: scenes can be activated but not turned off."),
            ("scene", _) => ("scene", "turn_on"),
            ("button" or "input_button", OnOff.Off) => throw new PluginException($"{entity.Name} is a button: it can only be pressed."),
            ("button" or "input_button", _) => (domain, "press"),
            ("vacuum", OnOff.On) => ("vacuum", "start"),
            ("vacuum", OnOff.Off) => ("vacuum", "return_to_base"),
            (_, OnOff.Toggle) => (domain, "toggle"),
            _ when ReadOnly.Contains(domain) => throw new PluginException($"{entity.Name} is a {domain.Replace('_', ' ')}: it can't be switched."),
            _ when OwnOnOff.Contains(domain) => (domain, action == OnOff.On ? "turn_on" : "turn_off"),
            // groups, input helpers, anything new: the generic service routes it to the right domain
            _ => ("homeassistant", action == OnOff.On ? "turn_on" : "turn_off"),
        };
    }

    /// <summary>Extra <c>light.turn_on</c> data from what was asked. Throws for a colour it can't read.</summary>
    public static JsonObject LightData(int? brightnessPct, string? color, int? colorTempKelvin)
    {
        var data = new JsonObject();
        if (brightnessPct is int b) data["brightness_pct"] = Math.Clamp(b, 1, 100);
        if (!string.IsNullOrWhiteSpace(color)) ApplyColor(data, color);
        if (colorTempKelvin is int k) data["color_temp_kelvin"] = Math.Clamp(k, 1500, 9000);
        return data;
    }

    static void ApplyColor(JsonObject data, string color)
    {
        var c = NameMatcher.Normalize(color);
        // Whites are colour temperatures, not colours
        int? kelvin = c switch
        {
            "warm" or "warm white" or "warmer" or "candle" or "candlelight" => 2700,
            "soft white" => 3000,
            "neutral" or "neutral white" or "natural white" => 4000,
            "cool" or "cool white" or "cold white" or "cooler" => 5000,
            "daylight" or "daylight white" => 6500,
            _ => null,
        };
        if (kelvin is int k)
        {
            data["color_temp_kelvin"] = k;
            return;
        }
        var hex = color.Trim().TrimStart('#');
        if (hex.Length == 6 && hex.All(Uri.IsHexDigit))
        {
            data["rgb_color"] = new JsonArray(
                int.Parse(hex[..2], NumberStyles.HexNumber), int.Parse(hex[2..4], NumberStyles.HexNumber), int.Parse(hex[4..], NumberStyles.HexNumber));
            return;
        }
        var rgb = color.Split(',', StringSplitOptions.TrimEntries);
        if (rgb.Length == 3 && rgb.All(p => int.TryParse(p, out int v) && v is >= 0 and <= 255))
        {
            data["rgb_color"] = new JsonArray(rgb.Select(p => (JsonNode)int.Parse(p)).ToArray());
            return;
        }
        if (c.Length == 0) throw new PluginException($"I don't know the colour '{color}'.");
        // CSS colour names, as Home Assistant expects them ("light blue" → "lightblue")
        data["color_name"] = c.Replace(" ", "");
    }

    /// <summary>"heating" → "heat", "automatic" → "auto": the HVAC mode Home Assistant calls it.</summary>
    public static string HvacMode(string spoken, IReadOnlyList<string> supported)
    {
        var s = NameMatcher.Normalize(spoken).Replace(' ', '_');
        var mode = s switch
        {
            "heating" or "warm" or "heater" => "heat",
            "cooling" or "cold" or "air_conditioning" or "aircon" => "cool",
            "automatic" or "auto_mode" => "auto",
            "heat_and_cool" or "heat_cool" or "heatcool" => "heat_cool",
            "dehumidify" or "dehumidifier" or "dry_mode" => "dry",
            "fan" or "fan_mode" or "fan_only" => "fan_only",
            "off" or "stop" or "turn_off" => "off",
            _ => s,
        };
        if (supported.Count > 0 && !supported.Contains(mode))
            throw new PluginException($"That thermostat doesn't have a '{spoken}' mode. Modes: {string.Join(", ", supported)}.");
        return mode;
    }

    /// <summary>Asking before doing these by voice (opening the house to someone).</summary>
    public static bool NeedsConfirmation(string domain, string service, IEnumerable<HaEntity> entities) => (domain, service) switch
    {
        ("lock", "unlock" or "open") => true,
        ("alarm_control_panel", "alarm_disarm") => true,
        ("cover", "open_cover" or "toggle" or "set_cover_position") => entities.Any(IsGarageOrGate),
        ("homeassistant", "turn_on" or "toggle") => entities.Any(e => e.Domain == "lock" || IsGarageOrGate(e)),
        _ => false,
    };

    static bool IsGarageOrGate(HaEntity e) => e.Domain == "cover" && e.Attr("device_class") is "garage" or "gate";
}
