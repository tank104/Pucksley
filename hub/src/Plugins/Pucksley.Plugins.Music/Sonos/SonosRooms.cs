using Pucksley.Sdk;

namespace Pucksley.Plugins.Sonos;

/// <summary>Works out which room someone means.</summary>
public static class SonosRooms
{
    public const string NoSpeakersMessage =
        "I can't find any Sonos speakers on the network. If the hub runs in Docker, add the speakers' IP addresses in the Sonos plugin settings.";

    /// <summary>
    /// Threshold for names nobody said (the puck's room, the default room): stricter than for spoken
    /// names, so a puck in "Bedroom 2" doesn't grab the "Bathroom" speaker.
    /// </summary>
    public const double ImplicitThreshold = 0.7;

    static readonly HashSet<string> AllWords = new(StringComparer.Ordinal)
    {
        "all", "everywhere", "everything", "every room", "all rooms", "all the rooms", "whole house", "house",
        "all speakers", "every speaker", "all the speakers", "entire house", "whole home",
    };

    /// <summary>"all", "everywhere", "the whole house"…</summary>
    public static bool IsAll(string? spoken)
    {
        if (string.IsNullOrWhiteSpace(spoken)) return false;
        var n = NameMatcher.Normalize(spoken);
        if (n.StartsWith("the ", StringComparison.Ordinal)) n = n[4..];
        return AllWords.Contains(n);
    }

    public static SonosPlayer? Match(SonosTopology topology, string? spoken, double threshold = 0.55) =>
        NameMatcher.Best(spoken, topology.Players, p => p.RoomName, threshold);

    public static PluginException NotFound(SonosTopology topology, string spoken) =>
        new($"I can't find a Sonos room called '{spoken}'. Rooms: {topology.RoomList}.");

    /// <summary>
    /// The room a command is for: the one named; else the puck's room if it has a Sonos; else the
    /// default room from settings; else the only group that's playing; else the only room. Otherwise
    /// asks (a <see cref="PluginException"/> listing the rooms).
    /// </summary>
    public static SonosPlayer Resolve(SonosTopology topology, string? spoken, string? callerRoom, string? defaultRoom, Func<SonosGroup, bool> isPlaying)
    {
        if (topology.Players.Count == 0) throw new PluginException(NoSpeakersMessage);
        if (!string.IsNullOrWhiteSpace(spoken)) return Match(topology, spoken) ?? throw NotFound(topology, spoken);
        return Implicit(topology, callerRoom, defaultRoom)
               ?? OnlyPlaying(topology, isPlaying)
               ?? (topology.Players.Count == 1 ? topology.Players[0] : null)
               ?? throw new PluginException($"Which room? Sonos rooms: {topology.RoomList}.");
    }

    /// <summary>The puck's room, else the default room, when either is a Sonos room.</summary>
    public static SonosPlayer? Implicit(SonosTopology topology, string? callerRoom, string? defaultRoom) =>
        Match(topology, callerRoom, ImplicitThreshold) ?? Match(topology, defaultRoom, ImplicitThreshold);

    static SonosPlayer? OnlyPlaying(SonosTopology topology, Func<SonosGroup, bool> isPlaying)
    {
        var playing = topology.Groups.Where(isPlaying).Take(2).ToList();
        return playing.Count == 1 ? playing[0].Coordinator : null;
    }

    /// <summary>
    /// A group someone named: "the kitchen group", or the exact name of a multi-room group
    /// ("Kitchen + Dining"). Null when they named a single room.
    /// </summary>
    public static SonosGroup? MatchGroup(SonosTopology topology, string? spoken)
    {
        if (string.IsNullOrWhiteSpace(spoken)) return null;
        var words = NameMatcher.Normalize(spoken).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Contains("group"))
        {
            var rest = string.Join(' ', words.Where(w => w != "group"));
            return Match(topology, rest) is { } member ? topology.GroupOf(member.Uuid) : null;
        }
        var multi = topology.Groups.Where(g => g.Members.Count > 1).ToList();
        return NameMatcher.Best(spoken, multi, g => g.Name, 0.9);
    }
}
