using Pucksley.Plugins.Music.Tests.Support;
using Pucksley.Plugins.Sonos;
using Pucksley.Plugins.Spotify;

namespace Pucksley.Plugins.Music.Tests;

/// <summary>Spotify URIs/links, and how Sonos plays them.</summary>
public sealed class SpotifyLinkTests
{
    [Theory]
    [InlineData("spotify:track:6rqhFgbbKwnb9MLmUQDhG6", "track", "6rqhFgbbKwnb9MLmUQDhG6")]
    [InlineData("SPOTIFY:TRACK:6rqhFgbbKwnb9MLmUQDhG6", "track", "6rqhFgbbKwnb9MLmUQDhG6")]
    [InlineData("https://open.spotify.com/track/6rqhFgbbKwnb9MLmUQDhG6?si=4b1d2c3e", "track", "6rqhFgbbKwnb9MLmUQDhG6")]
    [InlineData("https://open.spotify.com/intl-de/album/1DFixLWuPkv3KT3TnV35m3", "album", "1DFixLWuPkv3KT3TnV35m3")]
    [InlineData("https://open.spotify.com/intl-pt-br/album/1DFixLWuPkv3KT3TnV35m3", "album", "1DFixLWuPkv3KT3TnV35m3")]
    [InlineData("open.spotify.com/playlist/37i9dQZF1DXcBWIGoYBM5M", "playlist", "37i9dQZF1DXcBWIGoYBM5M")]
    [InlineData("https://open.spotify.com/embed/track/6rqhFgbbKwnb9MLmUQDhG6", "track", "6rqhFgbbKwnb9MLmUQDhG6")]
    [InlineData("spotify:user:spotify:playlist:37i9dQZF1DXcBWIGoYBM5M", "playlist", "37i9dQZF1DXcBWIGoYBM5M")]
    [InlineData("https://open.spotify.com/user/spotify/playlist/37i9dQZF1DXcBWIGoYBM5M", "playlist", "37i9dQZF1DXcBWIGoYBM5M")]
    [InlineData("spotify:artist:6FXMGgJwohJLUSr5nVlf9X", "artist", "6FXMGgJwohJLUSr5nVlf9X")]
    [InlineData("spotify%3aalbum%3a1DFixLWuPkv3KT3TnV35m3", "album", "1DFixLWuPkv3KT3TnV35m3")]
    [InlineData("x-sonos-spotify:spotify%3atrack%3a1a2B3c4D5e6F7g8H9i0J1k?sid=12&flags=8224&sn=7", "track", "1a2B3c4D5e6F7g8H9i0J1k")]
    [InlineData("x-sonos-spotify:spotify:track:1a2B3c4D5e6F7g8H9i0J1k?sid=12&flags=0&sn=12", "track", "1a2B3c4D5e6F7g8H9i0J1k")]
    public void Links_and_uris_parse(string text, string type, string id)
    {
        var link = SpotifyLink.Parse(text);
        Assert.NotNull(link);
        Assert.Equal(type, link.Type);
        Assert.Equal(id, link.Id);
        Assert.Equal($"spotify:{type}:{id}", link.Uri);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("massive attack")]
    [InlineData("https://spotify.link/abc123")]
    [InlineData("https://open.spotify.com/")]
    [InlineData("spotify:track:")]
    public void Other_text_is_not_a_link(string? text) => Assert.Null(SpotifyLink.Parse(text));

    [Fact]
    public void A_track_plays_as_x_sonos_spotify_with_the_account()
    {
        var item = SonosSpotify.Build(new SpotifyLink("track", "6rqhFgbbKwnb9MLmUQDhG6"), sid: 9, sn: 3);

        Assert.Equal("x-sonos-spotify:spotify%3atrack%3a6rqhFgbbKwnb9MLmUQDhG6?sid=9&flags=8224&sn=3", item.Uri);
        Assert.Equal(
            "<DIDL-Lite xmlns:dc=\"http://purl.org/dc/elements/1.1/\" xmlns:upnp=\"urn:schemas-upnp-org:metadata-1-0/upnp/\" " +
            "xmlns:r=\"urn:schemas-rinconnetworks-com:metadata-1-0/\" xmlns=\"urn:schemas-upnp-org:metadata-1-0/DIDL-Lite/\">" +
            "<item id=\"00032020spotify%3atrack%3a6rqhFgbbKwnb9MLmUQDhG6\" parentID=\"\" restricted=\"true\"><dc:title></dc:title>" +
            "<upnp:class>object.item.audioItem.musicTrack</upnp:class>" +
            "<desc id=\"cdudn\" nameSpace=\"urn:schemas-rinconnetworks-com:metadata-1-0/\">SA_RINCON2311_X_#Svc2311-0-Token</desc></item></DIDL-Lite>",
            item.MetaData);
    }

    [Theory]
    [InlineData("album", "x-rincon-cpcontainer:1004206cspotify%3aalbum%3a1DFixLWuPkv3KT3TnV35m3", "1004206cspotify%3aalbum%3a1DFixLWuPkv3KT3TnV35m3", "object.container.album.musicAlbum")]
    [InlineData("playlist", "x-rincon-cpcontainer:1006206cspotify%3aplaylist%3a1DFixLWuPkv3KT3TnV35m3", "1006206cspotify%3aplaylist%3a1DFixLWuPkv3KT3TnV35m3", "object.container.playlistContainer")]
    [InlineData("artist", "x-rincon-cpcontainer:100e206cspotify%3aartistTopTracks%3a1DFixLWuPkv3KT3TnV35m3", "100e206cspotify%3aartistTopTracks%3a1DFixLWuPkv3KT3TnV35m3", "object.container.playlistContainer")]
    public void Containers_play_by_item_id_with_the_service_type(string type, string uri, string itemId, string upnpClass)
    {
        var item = SonosSpotify.Build(new SpotifyLink(type, "1DFixLWuPkv3KT3TnV35m3"), sid: 12, sn: null);

        Assert.Equal(uri, item.Uri);
        var didl = Assert.Single(Didl.Parse(item.MetaData));
        Assert.Equal(itemId, didl.Id);
        Assert.Equal(upnpClass, didl.Class);
        Assert.Equal(3079, didl.ServiceType); // sid 12 × 256 + 7
        Assert.True(didl.IsContainer);
    }

    [Fact]
    public void A_track_needs_the_account_number() =>
        Assert.Throws<ArgumentException>(() => SonosSpotify.Build(new SpotifyLink("track", "6rqhFgbbKwnb9MLmUQDhG6"), 12, null));

    [Theory]
    [InlineData(9, 2311)]
    [InlineData(12, 3079)]
    [InlineData(303, 77575)]
    public void Service_types(int sid, int serviceType)
    {
        Assert.Equal(serviceType, SonosSpotify.ServiceType(sid));
        Assert.Equal(sid, SonosSpotify.SidFromServiceType(serviceType));
    }

    [Theory]
    [InlineData("x-sonos-spotify:spotify%3atrack%3a4u7EnebtmKWzUH433cf5Qv?sid=9&flags=8224&sn=7", 9, 7)]
    [InlineData("x-rincon-cpcontainer:1006206cspotify%3aplaylist%3a37i9dQZF1DX4sWSpwq3LiO?sid=12&flags=8300&sn=4", 12, 4)]
    [InlineData("x-sonos-spotify:spotify%3atrack%3a4u7EnebtmKWzUH433cf5Qv?sid=12&amp;flags=8224&amp;sn=2", 12, 2)]
    [InlineData("x-rincon-cpcontainer:1006206cspotify%3aplaylist%3a37i9dQZF1DX4sWSpwq3LiO", null, null)]
    [InlineData("x-sonosapi-stream:s24896?sid=254&flags=8224&sn=0", null, null)]
    [InlineData(null, null, null)]
    public void Spotify_accounts_are_read_from_uris(string? uri, int? sid, int? sn) =>
        Assert.Equal((sid, sn), SonosSpotify.AccountFromUri(uri));

    [Fact]
    public void Real_spotify_connect_metadata_names_sid_12()
    {
        var position = SonosSoapClient.ParseResponse("GetPositionInfo", Fixtures.Read("PositionInfo_spotify_connect_real.xml"));
        var res = Didl.Parse(position["TrackMetaData"]).Single().Res;
        Assert.Equal("x-sonos-spotify:spotify:track:1a2B3c4D5e6F7g8H9i0J1k?sid=12&flags=0&sn=12", res);
        Assert.Equal((12, 12), SonosSpotify.AccountFromUri(res));
    }
}
