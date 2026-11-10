using System.Text.RegularExpressions;

namespace Pucksley.Plugins.Spotify;

/// <summary>A Spotify item named by a URI (<c>spotify:track:ID</c>) or a share link (<c>https://open.spotify.com/track/ID?si=…</c>).</summary>
/// <param name="Type">track, album, playlist, artist, episode or show.</param>
/// <param name="Id">The base-62 id.</param>
public sealed partial record SpotifyLink(string Type, string Id)
{
    public string Uri => $"spotify:{Type}:{Id}";

    /// <summary>
    /// Understands <c>spotify:track:ID</c>, legacy <c>spotify:user:NAME:playlist:ID</c>,
    /// <c>open.spotify.com/(intl-xx/)(embed/)track/ID?si=…</c>, percent-encoded forms and Sonos URIs
    /// that wrap one (<c>x-sonos-spotify:spotify%3atrack%3aID?sid=…</c>). Null for anything else
    /// (including spotify.link short links, which need a web request to resolve).
    /// </summary>
    public static SpotifyLink? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var s = text.Trim();
        if (s.Contains('%')) s = System.Uri.UnescapeDataString(s);
        var m = UriRegex().Match(s);
        if (!m.Success) m = WebRegex().Match(s);
        return m.Success ? new SpotifyLink(m.Groups["type"].Value.ToLowerInvariant(), m.Groups["id"].Value) : null;
    }

    [GeneratedRegex(@"spotify:(?:user:[^:\s]+:)?(?<type>track|album|playlist|artist|episode|show):(?<id>[A-Za-z0-9]{12,40})", RegexOptions.IgnoreCase)]
    private static partial Regex UriRegex();

    [GeneratedRegex(@"open\.spotify\.com/(?:intl-[a-z]{2}(?:-[a-z]{2})?/)?(?:embed/)?(?:user/[^/\s]+/)?(?<type>track|album|playlist|artist|episode|show)/(?<id>[A-Za-z0-9]{12,40})", RegexOptions.IgnoreCase)]
    private static partial Regex WebRegex();
}
