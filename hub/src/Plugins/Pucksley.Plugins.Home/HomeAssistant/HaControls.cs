using Pucksley.Sdk;

namespace Pucksley.Plugins.HomeAssistant;

/// <summary>Puck dials for Home Assistant entities: brightness, switches, setpoints, positions, volume, buttons.</summary>
internal static class HaControls
{
    /// <summary>The dial for <paramref name="e"/>, or null for kinds of entity a dial can't drive.</summary>
    public static PuckControl? Build(HaEntity e)
    {
        string name = ShortName(e);
        string? offline = e.Available ? null : "Offline";
        switch (e.Domain)
        {
            case "light":
                bool on = e.State == "on";
                return new PuckControl
                {
                    Id = e.EntityId, Name = name, Group = e.Area, Kind = ControlKind.Number,
                    Value = on ? HaFormat.BrightnessPct(e) ?? 100 : 0, Min = 0, Max = 100, Step = 5, Unit = "%",
                    Text = offline ?? (on ? null : "Off"),
                };

            case "switch" or "fan" or "input_boolean" or "automation" or "siren":
                return new PuckControl { Id = e.EntityId, Name = name, Group = e.Area, Kind = ControlKind.Toggle, On = e.State == "on", Text = offline };

            case "climate":
                double min = e.AttrNum("min_temp") ?? 7, max = e.AttrNum("max_temp") ?? 35;
                double step = e.AttrNum("target_temp_step") is double s && s > 0 ? s : 0.5;
                double set = e.AttrNum("temperature") ?? Math.Clamp(e.AttrNum("current_temperature") ?? 20, min, max);
                return new PuckControl
                {
                    Id = e.EntityId, Name = name, Group = e.Area, Kind = ControlKind.Number,
                    Value = set, Min = min, Max = max, Step = step, Unit = "°C",
                    Text = offline ?? (e.State == "off" ? "Off" : null),
                };

            case "cover":
                return new PuckControl
                {
                    Id = e.EntityId, Name = name, Group = e.Area, Kind = ControlKind.Number,
                    Value = e.AttrNum("current_position") ?? (e.State == "open" ? 100 : 0), Min = 0, Max = 100, Step = 5, Unit = "%",
                    Text = offline,
                };

            case "media_player":
                return new PuckControl
                {
                    Id = e.EntityId, Name = name, Group = e.Area, Kind = ControlKind.Number,
                    Value = Math.Round((e.AttrNum("volume_level") ?? 0) * 100), Min = 0, Max = 100, Step = 1, Unit = "%",
                    Text = offline ?? (e.State == "off" ? "Off" : null),
                };

            case "scene" or "script" or "button" or "input_button":
                return new PuckControl { Id = e.EntityId, Name = name, Group = e.Area, Kind = ControlKind.Action, Text = offline };

            default:
                return null;
        }
    }

    /// <summary>"Kitchen Ceiling" in the Kitchen area → "Ceiling" (the area is the control's header already).</summary>
    internal static string ShortName(HaEntity e)
    {
        var name = e.Name;
        if (e.Area is { Length: > 0 } area && name.Length > area.Length + 1 &&
            name.StartsWith(area + " ", StringComparison.OrdinalIgnoreCase))
            return name[(area.Length + 1)..];
        return name;
    }
}
