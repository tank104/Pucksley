using System.Text.RegularExpressions;
using Pucksley.Plugins.Spotify;

namespace Pucksley.Plugins.Sonos;

/// <summary>A URI plus DIDL metadata to hand to AddURIToQueue / SetAVTransportURI.</summary>
public sealed record SonosQueueItem(string Uri, string MetaData);

/// <summary>
/// Playing Spotify through Sonos's own Spotify account (the "share link" method SoCo uses): a
/// Spotify URI becomes a Sonos URI plus metadata naming the Spotify service. Tracks also need the
/// account's serial number (sn); containers only need the service id (sid).
/// </summary>
public static partial class SonosSpotify
{
    /// <summary>sid 9 → 2311, sid 12 → 3079.</summary>
    public static int ServiceType(int sid) => sid * 256 + 7;

    public static int SidFromServiceType(int serviceType) => (serviceType - 7) / 256;

    public static bool Supports(SpotifyLink link) => link.Type is "track" or "album" or "playlist" or "artist";

    /// <param name="sn">Required for tracks.</param>
    public static SonosQueueItem Build(SpotifyLink link, int sid, int? sn)
    {
        string id = link.Id;
        var (uri, itemId, upnpClass) = link.Type switch
        {
            "track" => ($"x-sonos-spotify:spotify%3atrack%3a{id}?sid={sid}&flags=8224&sn={sn ?? throw new ArgumentException("Tracks need the account serial number (sn).", nameof(sn))}",
                        $"00032020spotify%3atrack%3a{id}", "object.item.audioItem.musicTrack"),
            "album" => ($"x-rincon-cpcontainer:1004206cspotify%3aalbum%3a{id}",
                        $"1004206cspotify%3aalbum%3a{id}", "object.container.album.musicAlbum"),
            "playlist" => ($"x-rincon-cpcontainer:1006206cspotify%3aplaylist%3a{id}",
                           $"1006206cspotify%3aplaylist%3a{id}", "object.container.playlistContainer"),
            // An artist plays as its top tracks.
            "artist" => ($"x-rincon-cpcontainer:100e206cspotify%3aartistTopTracks%3a{id}",
                         $"100e206cspotify%3aartistTopTracks%3a{id}", "object.container.playlistContainer"),
            _ => throw new ArgumentException($"Sonos can't play Spotify {link.Type}s.", nameof(link)),
        };
        return new SonosQueueItem(uri, MetaData(itemId, upnpClass, ServiceType(sid)));
    }

    public static string MetaData(string itemId, string upnpClass, int serviceType) =>
        "<DIDL-Lite xmlns:dc=\"http://purl.org/dc/elements/1.1/\" xmlns:upnp=\"urn:schemas-upnp-org:metadata-1-0/upnp/\" " +
        "xmlns:r=\"urn:schemas-rinconnetworks-com:metadata-1-0/\" xmlns=\"urn:schemas-upnp-org:metadata-1-0/DIDL-Lite/\">" +
        $"<item id=\"{itemId}\" parentID=\"\" restricted=\"true\"><dc:title></dc:title><upnp:class>{upnpClass}</upnp:class>" +
        $"<desc id=\"cdudn\" nameSpace=\"urn:schemas-rinconnetworks-com:metadata-1-0/\">SA_RINCON{serviceType}_X_#Svc{serviceType}-0-Token</desc>" +
        "</item></DIDL-Lite>";

    /// <summary>
    /// The Spotify account (sid, sn) in a Sonos URI for a Spotify item, e.g.
    /// <c>x-sonos-spotify:spotify%3atrack%3a…?sid=9&amp;flags=8224&amp;sn=3</c> or
    /// <c>x-rincon-cpcontainer:1006206cspotify%3aplaylist%3a…?sid=12&amp;flags=8300&amp;sn=5</c>.
    /// Both null for anything that isn't Spotify.
    /// </summary>
    public static (int? Sid, int? Sn) AccountFromUri(string? uri)
    {
        if (string.IsNullOrWhiteSpace(uri) || !uri.Contains("spotify", StringComparison.OrdinalIgnoreCase)) return (null, null);
        var decoded = uri.Replace("&amp;", "&", StringComparison.Ordinal);
        var sid = SidRegex().Match(decoded);
        var sn = SnRegex().Match(decoded);
        return (sid.Success ? int.Parse(sid.Groups[1].Value) : null, sn.Success ? int.Parse(sn.Groups[1].Value) : null);
    }

    [GeneratedRegex(@"[?&]sid=(\d{1,6})(?:&|$)")]
    private static partial Regex SidRegex();

    [GeneratedRegex(@"[?&]sn=(\d{1,6})(?:&|$)")]
    private static partial Regex SnRegex();
}
