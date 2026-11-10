using System.Text.RegularExpressions;
using Pucksley.Plugins.Shared;
using Pucksley.Sdk;

namespace Pucksley.Plugins.Tv;

/// <summary>Spoken names → remote keys, inputs and app ids for each kind of TV.</summary>
internal static partial class TvKeys
{
    /// <summary>Samsung Tizen app ids for the usual streaming apps.</summary>
    public static readonly IReadOnlyDictionary<string, string> SamsungApps = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["Netflix"] = "3201907018807",
        ["YouTube"] = "111299001912",
        ["Prime Video"] = "3201910019365",
        ["Amazon Prime"] = "3201910019365",
        ["Disney+"] = "3201901017640",
        ["Disney Plus"] = "3201901017640",
        ["Plex"] = "3201512006963",
    };

    /// <summary>The Samsung app id for a spoken app name, or null when it isn't one we know.</summary>
    public static string? SamsungAppId(string spoken)
    {
        var key = Names.Best(spoken, SamsungApps.Keys, k => k, 0.7);
        return key is null ? null : SamsungApps[key];
    }

    /// <summary>"HDMI 2", "hdmi2", "HDMI two" → 2.</summary>
    public static int? HdmiNumber(string spoken)
    {
        var m = Hdmi().Match(NameMatcher.Normalize(spoken));
        if (!m.Success) return null;
        return m.Groups[1].Value switch
        {
            "1" or "one" => 1,
            "2" or "two" => 2,
            "3" or "three" => 3,
            "4" or "four" => 4,
            _ => null,
        };
    }

    [GeneratedRegex(@"\bhdmi\s*(1|2|3|4|one|two|three|four)\b")]
    private static partial Regex Hdmi();

    /// <summary>Live TV: the aerial / Freeview tuner.</summary>
    public static bool IsLiveTv(string spoken) => NameMatcher.Normalize(spoken) is
        "tv" or "live tv" or "television" or "antenna" or "aerial" or "freeview" or "tuner" or "broadcast" or "free to air" or "channels";

    /// <summary>The Samsung key that selects an input: KEY_HDMI1..4, KEY_TV, KEY_HDMI (cycles) or KEY_SOURCE (the menu).</summary>
    public static string SamsungInputKey(string spoken)
    {
        if (HdmiNumber(spoken) is int n) return $"KEY_HDMI{n}";
        if (IsLiveTv(spoken)) return "KEY_TV";
        return NameMatcher.Normalize(spoken) == "hdmi" ? "KEY_HDMI" : "KEY_SOURCE";
    }

    /// <summary>The Roku key that selects an input (Roku TVs), or null.</summary>
    public static string? RokuInputKey(string spoken)
    {
        if (HdmiNumber(spoken) is int n) return $"InputHDMI{n}";
        if (IsLiveTv(spoken)) return "InputTuner";
        return NameMatcher.Normalize(spoken) is "av" or "composite" or "av 1" ? "InputAV1" : null;
    }

    public static string SamsungMediaKey(string action) => Action(action) switch
    {
        "play" => "KEY_PLAY",
        "pause" => "KEY_PAUSE",
        "stop" => "KEY_STOP",
        "home" => "KEY_HOME",
        "back" => "KEY_RETURN",
        _ => throw UnknownAction(action),
    };

    /// <summary>Roku has one play/pause key and no stop: Back leaves the video.</summary>
    public static string RokuMediaKey(string action) => Action(action) switch
    {
        "play" or "pause" => "Play",
        "stop" or "back" => "Back",
        "home" => "Home",
        _ => throw UnknownAction(action),
    };

    /// <summary>Normalizes a media action ("resume" → play, "return" → back).</summary>
    public static string Action(string action) => NameMatcher.Normalize(action) switch
    {
        "play" or "resume" or "unpause" => "play",
        "pause" => "pause",
        "stop" => "stop",
        "home" or "home screen" => "home",
        "back" or "return" or "go back" => "back",
        var other => other,
    };

    static PluginException UnknownAction(string action) => new($"I can't '{action}' a TV: try play, pause, stop, home or back.");
}
