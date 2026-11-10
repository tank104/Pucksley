using System.Text.Json.Nodes;
using Pucksley.Sdk;
using static Pucksley.Plugins.Spotify.SpotifyJson;

namespace Pucksley.Plugins.Spotify;

/// <summary>A search result.</summary>
/// <param name="Artist">Artists of a track/album, or a playlist's owner.</param>
/// <param name="Album">A track's album.</param>
/// <param name="Rank">Position in Spotify's results for its type (0 = Spotify's best guess).</param>
public sealed record SpotifyCandidate(string Type, string Uri, string Name, string? Artist = null, string? Album = null, int Rank = 0, int? Popularity = null)
{
    /// <summary>For the answer: "\"Teardrop\" by Massive Attack", "the album Mezzanine by Massive Attack"…</summary>
    public string Describe() => Type switch
    {
        "track" => Artist is null ? $"\"{Name}\"" : $"\"{Name}\" by {Artist}",
        "album" => Artist is null ? $"the album {Name}" : $"the album {Name} by {Artist}",
        "playlist" => $"the playlist {Name}",
        "episode" => $"the episode {Name}",
        "show" => $"the podcast {Name}",
        _ => Name,
    };
}

/// <summary>Chooses what to play from Spotify search results.</summary>
public static class SpotifyPicker
{
    /// <summary>Scores at or above this count as "they said its name".</summary>
    public const double Exact = 0.9;

    static readonly string[] Types = ["track", "album", "artist", "playlist"];

    public static IReadOnlyList<SpotifyCandidate> FromSearch(JsonNode? json)
    {
        var list = new List<SpotifyCandidate>();
        foreach (var type in Types)
        {
            int rank = 0;
            foreach (var item in Items(json?[type + "s"]?["items"]))
            {
                var uri = Str(item["uri"]);
                var name = Str(item["name"]);
                if (uri is null || name is null) continue;
                var artist = type switch
                {
                    "track" or "album" => Names(item["artists"], 2),
                    "playlist" => Str(item["owner"]?["display_name"]),
                    _ => null,
                };
                list.Add(new SpotifyCandidate(type, uri, name, artist, type == "track" ? Str(item["album"]?["name"]) : null, rank++, Int(item["popularity"])));
            }
        }
        return list;
    }

    /// <summary>How well the query names the candidate; tracks and albums also match "title artist" and "title by artist".</summary>
    public static double Score(string query, SpotifyCandidate c)
    {
        double score = NameMatcher.Score(query, c.Name);
        if (c.Artist is not null && c.Type is "track" or "album")
            score = Math.Max(score, Math.Max(NameMatcher.Score(query, $"{c.Name} {c.Artist}"), NameMatcher.Score(query, $"{c.Name} by {c.Artist}")));
        return score;
    }

    /// <summary>
    /// With a type: the best-named result of that type (or Spotify's top one). Without: the artist
    /// if they said an artist's name, else a track, album or playlist they named, else Spotify's top
    /// track. When a track and an album have the name they said, the album wins if it's that
    /// track's album ("play Mezzanine").
    /// </summary>
    public static SpotifyCandidate? Choose(string query, string? type, IReadOnlyList<SpotifyCandidate> candidates)
    {
        if (type is not null)
        {
            var ofType = candidates.Where(c => c.Type == type).ToList();
            if (ofType.Count == 0) return null;
            var best = Best(query, ofType);
            return Score(query, best) >= 0.5 ? best : ofType.MinBy(c => c.Rank);
        }

        var artist = BestOf("artist");
        var track = BestOf("track");
        var album = BestOf("album");
        var playlist = BestOf("playlist");

        // An exact artist wins, unless an exact track is far more popular ("Hello": Adele, not a band called Hello).
        if (IsExact(artist) && !(IsExact(track) && track!.Popularity > artist!.Popularity + 10)) return artist;
        if (IsExact(track) && IsExact(album) && SameName(album!.Name, track!.Album) && SameName(album.Name, track.Name)) return album;
        if (IsExact(track)) return track;
        if (IsExact(album)) return album;
        if (IsExact(playlist)) return playlist;
        return candidates.Where(c => c.Type == "track").MinBy(c => c.Rank) ?? artist ?? album ?? playlist;

        SpotifyCandidate? BestOf(string t)
        {
            var ofType = candidates.Where(c => c.Type == t).ToList();
            return ofType.Count == 0 ? null : Best(query, ofType);
        }

        bool IsExact(SpotifyCandidate? c) => c is not null && Score(query, c) >= Exact;
    }

    static SpotifyCandidate Best(string query, IReadOnlyList<SpotifyCandidate> candidates) =>
        candidates.OrderByDescending(c => Score(query, c)).ThenBy(c => c.Rank).First();

    static bool SameName(string a, string? b) => b is not null && NameMatcher.Normalize(a) == NameMatcher.Normalize(b);
}
