using Pucksley.Plugins.Shared;
using Pucksley.Sdk;

namespace Pucksley.Plugins.HomeAssistant;

/// <summary>How many entities a tool may act on.</summary>
internal enum Want { One, Many }

/// <summary>
/// Turns what someone said into entities: an entity id ("light.lounge"), a friendly name ("kitchen
/// ceiling"), area + thing ("the lamp in the lounge"), or a group ("all lights in the kitchen",
/// "kitchen lights", "the lights" = the puck's room). Ambiguity and misses throw a
/// <see cref="PluginException"/> that lists the candidates, so Claude can recover in one step.
/// </summary>
internal static class EntityResolver
{
    const double Threshold = 0.55;
    const double TieMargin = 0.02;

    /// <summary>Words that say what kind of thing is meant.</summary>
    static readonly Dictionary<string, string> DomainWords = new(StringComparer.Ordinal)
    {
        ["light"] = "light", ["lights"] = "light", ["lamp"] = "light", ["lamps"] = "light", ["bulb"] = "light", ["bulbs"] = "light",
        ["downlight"] = "light", ["downlights"] = "light",
        ["switch"] = "switch", ["switches"] = "switch", ["plug"] = "switch", ["plugs"] = "switch", ["socket"] = "switch", ["sockets"] = "switch",
        ["outlet"] = "switch", ["outlets"] = "switch",
        ["fan"] = "fan", ["fans"] = "fan",
        ["heater"] = "climate", ["heaters"] = "climate", ["thermostat"] = "climate", ["thermostats"] = "climate", ["heatpump"] = "climate",
        ["aircon"] = "climate", ["hvac"] = "climate", ["climate"] = "climate",
        ["blind"] = "cover", ["blinds"] = "cover", ["curtain"] = "cover", ["curtains"] = "cover", ["shade"] = "cover", ["shades"] = "cover",
        ["shutter"] = "cover", ["shutters"] = "cover", ["cover"] = "cover", ["covers"] = "cover", ["garage"] = "cover", ["gate"] = "cover",
        ["awning"] = "cover",
        ["tv"] = "media_player", ["tvs"] = "media_player", ["television"] = "media_player", ["speaker"] = "media_player",
        ["speakers"] = "media_player", ["sonos"] = "media_player",
        ["scene"] = "scene", ["scenes"] = "scene", ["script"] = "script", ["scripts"] = "script",
        ["lock"] = "lock", ["locks"] = "lock", ["vacuum"] = "vacuum",
    };

    /// <summary>Plural kind words: "kitchen lights" means all of them.</summary>
    static readonly HashSet<string> Plurals =
    [
        "lights", "lamps", "bulbs", "downlights", "switches", "plugs", "sockets", "outlets", "fans", "heaters", "thermostats",
        "blinds", "curtains", "shades", "shutters", "covers", "tvs", "speakers", "locks",
    ];

    static readonly HashSet<string> AllWords = ["all", "every", "everything", "each"];

    static readonly HashSet<string> Noise =
        ["the", "a", "an", "my", "our", "in", "on", "of", "at", "and", "please", "room", "area", "house", "home", "whole", "entire", "thing", "things"];

    /// <summary>What "everything in the lounge" switches.</summary>
    static readonly string[] EverythingDomains = ["light", "switch", "fan", "media_player"];

    public static IReadOnlyList<HaEntity> Resolve(string? spoken, IReadOnlyCollection<HaEntity> entities, Want want, string? room = null)
    {
        var query = spoken?.Trim() ?? "";
        if (query.Length == 0) throw new PluginException("Say which device or entity.");

        // A list: "light.a, light.b"
        if (want == Want.Many && query.IndexOfAny([',', ';']) >= 0)
        {
            var parts = query.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length > 1)
                return parts.SelectMany(p => Resolve(p, entities, Want.Many, room)).DistinctBy(e => e.EntityId).ToList();
        }

        // 1. An entity id
        var byId = entities.FirstOrDefault(e => string.Equals(e.EntityId, query, StringComparison.OrdinalIgnoreCase));
        if (byId is not null) return [byId];

        // 2. A name, exactly
        var norm = NameMatcher.Normalize(query);
        var exact = entities.Where(e => NamesOf(e).Any(n => NameMatcher.Normalize(n) == norm)).ToList();
        if (exact.Count == 1) return exact;
        if (exact.Count > 1)
        {
            var inRoom = InRoom(exact, room);
            if (inRoom.Count == 1) return inRoom;
            throw Ambiguous(query, exact);
        }

        // 3. A group: "all lights in the kitchen", "kitchen lights", "the lights"
        var words = norm.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (want == Want.Many && (words.Any(AllWords.Contains) || words.Any(Plurals.Contains)))
        {
            var group = ResolveGroup(words, entities, room, out var groupError);
            if (group is not null) return group;
            // Not a known area: maybe it's just one thing's name ("christmas lights" on a switch)
            return [BestMatch(query, words, entities, room) ?? throw new PluginException(groupError!)];
        }

        // 4. The closest name
        return [BestMatch(query, words, entities, room) ?? throw NotFound(query, entities)];
    }

    /// <summary>Everything matching <paramref name="query"/> (for search), best first.</summary>
    public static IReadOnlyList<HaEntity> Rank(string query, IEnumerable<HaEntity> entities, double threshold = 0.5) =>
        entities.Select(e => (Entity: e, Score: Score(query, e)))
            .Where(s => s.Score >= threshold)
            .OrderByDescending(s => s.Score)
            .ThenBy(s => s.Entity.Name, StringComparer.OrdinalIgnoreCase)
            .Select(s => s.Entity)
            .ToList();

    /// <summary>0..1: how well <paramref name="query"/> names the entity (by name, object id, or area + name).</summary>
    public static double Score(string query, HaEntity e) => NamesOf(e).Max(n => ScoreName(query, n));

    /// <summary>Words that don't tell entities apart (as in <see cref="NameMatcher"/>, plus plurals).</summary>
    static readonly HashSet<string> Filler =
        ["the", "a", "an", "my", "our", "in", "on", "of", "speaker", "speakers", "room", "sonos", "light", "lights", "lamp", "lamps", "plug", "tv", "device"];

    /// <summary>
    /// <see cref="Names.Score"/> (NameMatcher's), except when the query merely contains a shorter name: "Kitchen"
    /// inside "kitchen pendant" is a far weaker hint than "kitchen pendant" inside "Kitchen Pendants",
    /// so that case is scaled by how much of the query the name accounts for.
    /// </summary>
    internal static double ScoreName(string query, string name)
    {
        double score = Names.Score(query, name);
        var q = Words(query);
        var n = Words(name);
        if (n.Count > 0 && n.Count < q.Count && n.All(w => q.Any(t => SameWord(t, w))))
            score = Math.Min(score, 0.85 * (0.4 + 0.6 * n.Count / q.Count));
        return score;

        static List<string> Words(string s) => NameMatcher.Normalize(s).Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => !Filler.Contains(w)).ToList();

        // "pendant" ~ "pendants", "switch" ~ "switches"
        static bool SameWord(string a, string b) =>
            a == b || (Math.Abs(a.Length - b.Length) <= 2 && Math.Min(a.Length, b.Length) >= 4 && (a.StartsWith(b, StringComparison.Ordinal) || b.StartsWith(a, StringComparison.Ordinal)));
    }

    /// <summary>The domain a word implies ("lamps" → light), or null.</summary>
    public static string? DomainOf(string word) => DomainWords.GetValueOrDefault(NameMatcher.Normalize(word));

    static IEnumerable<string> NamesOf(HaEntity e)
    {
        yield return e.Name;
        yield return e.ObjectId.Replace('_', ' ');
        if (e.Area is { Length: > 0 } area) yield return area + " " + e.Name;
    }

    static List<HaEntity>? ResolveGroup(string[] words, IReadOnlyCollection<HaEntity> entities, string? room, out string? error)
    {
        error = null;
        bool explicitAll = words.Any(AllWords.Contains);
        // The kind: plural words ("lights"), else any kind word ("all the light"), else everything switchable
        var kindWords = words.Where(Plurals.Contains).ToList();
        if (kindWords.Count == 0) kindWords = words.Where(DomainWords.ContainsKey).ToList();
        var domains = kindWords.Count > 0 ? kindWords.Select(w => DomainWords[w]).ToHashSet() : EverythingDomains.ToHashSet();
        string kind = kindWords.Count > 0 ? PluralOf(kindWords[0]) : "devices";
        var ofKind = entities.Where(e => domains.Contains(e.Domain)).ToList();

        // The place: whatever is left ("all the lights in the kitchen" → "kitchen")
        var place = string.Join(' ', words.Where(w => !AllWords.Contains(w) && !Noise.Contains(w) && !kindWords.Contains(w)));
        var areas = entities.Select(e => e.Area).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();
        string? area = null;
        if (place.Length > 0)
        {
            area = Names.Best(place, areas, a => a, 0.7);
            if (area is null)
            {
                // Not an area: the things of that kind whose names match ("all the christmas lights")
                var named = ofKind.Where(e => NamesOf(e).Any(n => ScoreName(place, n) >= 0.7)).ToList();
                if (named.Count > 0) return named;
                error = areas.Count > 0
                    ? $"I can't find an area called '{place}'. Areas: {Text.List(areas)}."
                    : $"I can't find '{place}' in Home Assistant (no areas are set up there).";
                return null;
            }
        }
        else if (!explicitAll)
        {
            // "turn on the lights" means the puck's room
            area = room is null ? null : Names.Best(room, areas, a => a, 0.7);
            if (area is null)
            {
                error = $"Which room? Areas: {Text.List(areas)}. Or say 'all {kind}' for the whole house.";
                return null;
            }
        }

        var found = area is null ? ofKind : ofKind.Where(e => string.Equals(e.Area, area, StringComparison.OrdinalIgnoreCase)).ToList();
        if (found.Count == 0)
            throw new PluginException(area is null ? $"There are no {kind} in Home Assistant." : $"There are no {kind} in the {area} area.");
        return found;
    }

    static HaEntity? BestMatch(string query, string[] words, IReadOnlyCollection<HaEntity> entities, string? room)
    {
        var scored = entities.Select(e => (Entity: e, Score: Score(query, e))).Where(s => s.Score >= Threshold).ToList();
        // "kitchen light" shouldn't pick the Kitchen speaker: prefer the kind of thing named, when any match
        var hinted = words.Select(w => DomainWords.GetValueOrDefault(w)).OfType<string>().ToHashSet();
        if (hinted.Count > 0 && scored.Any(s => hinted.Contains(s.Entity.Domain)))
            scored = scored.Where(s => hinted.Contains(s.Entity.Domain)).ToList();
        if (scored.Count == 0) return null;

        double best = scored.Max(s => s.Score);
        var top = scored.Where(s => s.Score >= best - TieMargin).Select(s => s.Entity).ToList();
        if (top.Count == 1) return top[0];
        var inRoom = InRoom(top, room);
        if (inRoom.Count == 1) return inRoom[0];
        throw Ambiguous(query, top);
    }

    static List<HaEntity> InRoom(IEnumerable<HaEntity> list, string? room) =>
        string.IsNullOrWhiteSpace(room) ? [] : list.Where(e => e.Area is not null && NameMatcher.Score(room, e.Area) >= 0.85).ToList();

    static string PluralOf(string word) => word.EndsWith('s') ? word : DomainWords[word] switch
    {
        "light" => "lights",
        "switch" => "switches",
        "climate" => "heaters",
        "cover" => "blinds",
        "media_player" => "media players",
        var d => d + "s",
    };

    internal static string Label(HaEntity e) => e.Area is null ? $"{e.Name} ({e.EntityId})" : $"{e.Name} in {e.Area} ({e.EntityId})";

    static PluginException Ambiguous(string query, IReadOnlyList<HaEntity> options)
    {
        var shown = string.Join(", ", options.Take(8).Select(Label));
        var more = options.Count > 8 ? $" and {options.Count - 8} more" : "";
        return new PluginException($"Which one? '{query}' could be {shown}{more}. Use the entity id, or say e.g. 'all lights in the kitchen' for several.");
    }

    static PluginException NotFound(string query, IReadOnlyCollection<HaEntity> entities)
    {
        var close = Rank(query, entities, 0.35).Take(5).Select(Label).ToList();
        return new PluginException(close.Count > 0
            ? $"I can't find '{query}' in Home Assistant. Closest: {string.Join(", ", close)}."
            : $"I can't find '{query}' in Home Assistant. Use ha_find to search.");
    }
}
