using System.Text.Json.Nodes;

namespace Pucksley.Plugins.Spotify;

/// <summary>Null-safe reads from Spotify's JSON (fields go missing or null often enough).</summary>
public static class SpotifyJson
{
    public static string? Str(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0 ? s : null;

    public static int? Int(JsonNode? node)
    {
        if (node is not JsonValue v) return null;
        if (v.TryGetValue<int>(out var i)) return i;
        if (v.TryGetValue<long>(out var l)) return (int)Math.Clamp(l, int.MinValue, int.MaxValue);
        if (v.TryGetValue<double>(out var d)) return (int)d;
        return null;
    }

    public static bool Bool(JsonNode? node) => node is JsonValue v && v.TryGetValue<bool>(out var b) && b;

    /// <summary>The non-null elements of an array (search results can contain nulls).</summary>
    public static IEnumerable<JsonNode> Items(JsonNode? array) =>
        array is JsonArray a ? a.Where(n => n is not null).Select(n => n!) : [];

    /// <summary>"Massive Attack, Tracey Thorn" from an <c>artists</c> array.</summary>
    public static string? Names(JsonNode? artists, int max = 3)
    {
        var names = Items(artists).Select(a => Str(a["name"])).OfType<string>().Take(max).ToList();
        return names.Count == 0 ? null : string.Join(", ", names);
    }
}

/// <summary>A Spotify Connect device.</summary>
public sealed record SpotifyDevice(string Id, string Name, string Type, bool IsActive, bool IsRestricted, int? Volume)
{
    /// <param name="requireId">Devices without an id can't be targeted (Spotify hides it for some).</param>
    public static SpotifyDevice? Parse(JsonNode? node, bool requireId = true)
    {
        var id = SpotifyJson.Str(node?["id"]);
        var name = SpotifyJson.Str(node?["name"]);
        if (name is null || (requireId && id is null)) return null;
        return new SpotifyDevice(id ?? "", name, SpotifyJson.Str(node?["type"]) ?? "", SpotifyJson.Bool(node?["is_active"]),
            SpotifyJson.Bool(node?["is_restricted"]), SpotifyJson.Int(node?["volume_percent"]));
    }
}

/// <summary>A track or podcast episode.</summary>
public sealed record SpotifyItem(string Type, string? Id, string? Uri, string Name, string? Artists, string? Album, int DurationMs)
{
    public static SpotifyItem? Parse(JsonNode? item)
    {
        var name = SpotifyJson.Str(item?["name"]);
        if (name is null) return null;
        var type = SpotifyJson.Str(item?["type"]) ?? "track";
        bool episode = type == "episode";
        return new SpotifyItem(
            type,
            SpotifyJson.Str(item?["id"]),
            SpotifyJson.Str(item?["uri"]),
            name,
            episode ? SpotifyJson.Str(item?["show"]?["name"]) : SpotifyJson.Names(item?["artists"]),
            episode ? null : SpotifyJson.Str(item?["album"]?["name"]),
            SpotifyJson.Int(item?["duration_ms"]) ?? 0);
    }

    /// <summary>"\"Teardrop\" by Massive Attack".</summary>
    public string Describe() => Artists is null ? $"\"{Name}\"" : $"\"{Name}\" by {Artists}";
}

/// <summary>The player state from <c>GET /me/player</c>.</summary>
public sealed record SpotifyPlayback(SpotifyDevice? Device, bool IsPlaying, SpotifyItem? Item, int ProgressMs, bool Shuffle, string Repeat, string? ContextUri)
{
    public static SpotifyPlayback? Parse(JsonNode? json)
    {
        if (json is not JsonObject) return null;
        return new SpotifyPlayback(
            SpotifyDevice.Parse(json["device"], requireId: false),
            SpotifyJson.Bool(json["is_playing"]),
            SpotifyItem.Parse(json["item"]),
            SpotifyJson.Int(json["progress_ms"]) ?? 0,
            SpotifyJson.Bool(json["shuffle_state"]),
            SpotifyJson.Str(json["repeat_state"]) ?? "off",
            SpotifyJson.Str(json["context"]?["uri"]));
    }

    /// <summary>Same as far as the puck's Home face is concerned (ignores progress).</summary>
    public static bool Same(SpotifyPlayback? a, SpotifyPlayback? b) =>
        a is null || b is null
            ? a is null && b is null
            : a.IsPlaying == b.IsPlaying && a.Item?.Uri == b.Item?.Uri && a.Device?.Name == b.Device?.Name && a.Device?.Volume == b.Device?.Volume;
}
