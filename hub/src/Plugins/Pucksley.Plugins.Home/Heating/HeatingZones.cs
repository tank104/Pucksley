using Pucksley.Sdk;

namespace Pucksley.Plugins.Heating;

/// <summary>
/// Spoken zones → the controller's floors and valve groups. Three floors (ground, middle, top);
/// the middle and top floors each have a sun and a shade group. "Upstairs" = top, "downstairs" /
/// "ground floor" = ground, "middle" / "first floor" = middle (NZ usage: the first floor is the one
/// above the ground floor). A floor means both of its groups unless a side is named.
/// </summary>
internal static class HeatingZones
{
    public static readonly string[] Floors = ["ground", "middle", "top"];
    public static readonly string[] Groups = ["ground", "middle_sun", "middle_shade", "top_sun", "top_shade"];

    const string ZoneHelp = "Zones: downstairs (ground), middle (first floor), upstairs (top); middle and upstairs also have a sunny and a shady side, or 'whole house'.";

    /// <summary>The floor a phrase names, or null.</summary>
    public static string? FloorOf(string spoken)
    {
        var s = " " + NameMatcher.Normalize(spoken).Replace('_', ' ') + " ";
        if (ContainsAny(s, " top ", " upstairs ", " up stairs ", " second floor ", " 2nd floor ", " attic ", " loft ", " upper ")) return "top";
        if (ContainsAny(s, " middle ", " mid ", " first floor ", " 1st floor ")) return "middle";
        if (ContainsAny(s, " ground ", " downstairs ", " down stairs ", " bottom ", " lower ")) return "ground";
        return null;
    }

    /// <summary>The floor for history: like <see cref="FloorOf"/>, but says which exist when nothing matches.</summary>
    public static string ResolveFloor(string spoken) =>
        FloorOf(spoken) ?? throw new PluginException($"I don't know the floor '{spoken}'. Floors: downstairs (ground), middle (first floor), upstairs (top).");

    /// <summary>The valve groups a phrase names ("upstairs" → top_sun + top_shade; "upstairs sunny side" → top_sun).</summary>
    public static IReadOnlyList<string> ResolveGroups(string spoken)
    {
        var normalized = NameMatcher.Normalize(spoken).Replace('_', ' ');
        var s = " " + normalized + " ";
        var exact = Groups.FirstOrDefault(g => g.Replace('_', ' ') == normalized);
        if (exact is not null) return [exact];

        string? side = ContainsAny(s, " sun ", " sunny ", " sunnyside ") ? "sun"
            : ContainsAny(s, " shade ", " shady ", " shaded ") ? "shade"
            : null;
        var floor = FloorOf(spoken);
        if (floor is null)
        {
            if (side is not null) return [$"middle_{side}", $"top_{side}"];
            if (ContainsAny(s, " whole house ", " house ", " everywhere ", " all ", " all zones ", " every zone ", " entire house ")) return Groups;
            throw new PluginException($"I don't know the zone '{spoken}'. {ZoneHelp}");
        }
        if (floor == "ground") return ["ground"]; // one group: no sides
        return side is null ? [$"{floor}_sun", $"{floor}_shade"] : [$"{floor}_{side}"];
    }

    /// <summary>"top_sun" → "upstairs sunny side", for speech.</summary>
    public static string Describe(string group) => group switch
    {
        "ground" => "downstairs",
        "middle_sun" => "middle sunny side",
        "middle_shade" => "middle shady side",
        "top_sun" => "upstairs sunny side",
        "top_shade" => "upstairs shady side",
        _ => group.Replace('_', ' '),
    };

    /// <summary>"middle" → "middle floor", "top" → "upstairs", "ground" → "downstairs".</summary>
    public static string DescribeFloor(string floor) => floor switch
    {
        "ground" => "downstairs",
        "middle" => "middle floor",
        "top" => "upstairs",
        _ => floor,
    };

    /// <summary>Override kinds, accepting a few spoken forms.</summary>
    public static string Kind(string spoken) => NameMatcher.Normalize(spoken).Replace(' ', '_') switch
    {
        "force_open" or "open" or "on" or "heat" or "heating" or "force_on" => "force_open",
        "force_closed" or "force_close" or "closed" or "close" or "off" or "no_heat" or "force_off" => "force_closed",
        "hold_at_setpoint" or "hold" or "hold_setpoint" or "setpoint" => "hold_at_setpoint",
        _ => throw new PluginException("The override must be force_open (heat regardless), force_closed (no heat) or hold_at_setpoint."),
    };

    static bool ContainsAny(string haystack, params string[] needles) => needles.Any(n => haystack.Contains(n, StringComparison.Ordinal));
}
