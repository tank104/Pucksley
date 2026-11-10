using Pucksley.Plugins.Music.Tests.Support;
using Pucksley.Plugins.Sonos;

namespace Pucksley.Plugins.Music.Tests;

/// <summary>Topology, device descriptions and DIDL metadata, mostly from captured real responses.</summary>
public sealed class SonosParsingTests
{
    internal static SonosTopology RealTopology() =>
        SonosTopology.ParseZoneGroupState(SonosSoapClient.ParseResponse("GetZoneGroupState", Fixtures.Read("ZoneGroupState_real.xml"))["ZoneGroupState"]);

    internal static SonosTopology BondedTopology() => SonosTopology.ParseZoneGroupState(Fixtures.Read("ZoneGroupState_bonded_synthetic.xml"));

    static IReadOnlyDictionary<string, string> Soap(string fixture, string action) => SonosSoapClient.ParseResponse(action, Fixtures.Read(fixture));

    [Fact]
    public void Real_zone_group_state_has_three_rooms_each_in_its_own_group()
    {
        var t = RealTopology();

        Assert.Equal(["Kitchen", "Loft Study", "Sonos Move"], t.Players.Select(p => p.RoomName));
        Assert.Equal(3, t.Groups.Count);
        Assert.All(t.Groups, g => Assert.Single(g.Members));
        var kitchen = t.Players.Single(p => p.RoomName == "Kitchen");
        Assert.Equal("RINCON_000E5800000101400", kitchen.Uuid);
        Assert.Equal("192.168.1.101", kitchen.Host);
        Assert.Same(kitchen, t.GroupOf(kitchen.Uuid)!.Coordinator);
        Assert.Equal("Kitchen", t.GroupOf(kitchen.Uuid)!.Name);
        Assert.Equal("Kitchen, Loft Study, Sonos Move", t.RoomList);
    }

    [Fact]
    public void Invisible_members_satellites_and_bridges_are_skipped()
    {
        var t = BondedTopology();

        Assert.Equal(["Bedroom", "Dining Room", "Lounge", "Office"], t.Players.Select(p => p.RoomName));
        Assert.Equal(["Bedroom", "Lounge + Dining Room", "Office"], t.Groups.Select(g => g.Name));
        var lounge = t.Groups.Single(g => g.Members.Count == 2);
        Assert.Equal("RINCON_LOUNGE01400", lounge.Coordinator.Uuid);
        Assert.Equal(["Lounge", "Dining Room"], lounge.Members.Select(m => m.RoomName));
        Assert.Null(t.Player("RINCON_SUB01400"));
        Assert.Null(t.Player("RINCON_BEDR01400"));
        Assert.Null(t.Player("RINCON_BOOST01400"));
    }

    [Fact]
    public void Topology_signature_changes_with_grouping()
    {
        Assert.NotEqual(RealTopology().Signature, BondedTopology().Signature);
        Assert.Equal(RealTopology().Signature, RealTopology().Signature);
    }

    [Theory]
    [InlineData(new[] { "Kitchen" }, "Kitchen")]
    [InlineData(new[] { "Kitchen", "Dining" }, "Kitchen + Dining")]
    [InlineData(new[] { "Kitchen", "Dining", "Lounge" }, "Kitchen + Dining + Lounge")]
    [InlineData(new[] { "Kitchen", "Dining", "Lounge", "Office" }, "Kitchen + 3 more")]
    public void Group_names(string[] rooms, string expected) => Assert.Equal(expected, SonosTopology.GroupName(rooms));

    [Fact]
    public void Real_device_descriptions_show_the_amp_has_tv_and_line_in()
    {
        var amp = SonosDeviceInfo.Parse(Fixtures.Read("DeviceDescription_amp_real.xml"))!;
        Assert.Equal("RINCON_000E5800000101400", amp.Uuid);
        Assert.Equal("Kitchen", amp.RoomName);
        Assert.Equal("Sonos Amp", amp.Model);
        Assert.True(amp.HasTv);
        Assert.True(amp.HasLineIn);

        var one = SonosDeviceInfo.Parse(Fixtures.Read("DeviceDescription_one_real.xml"))!;
        Assert.Equal("Loft Study", one.RoomName);
        Assert.False(one.HasTv);
        Assert.False(one.HasLineIn);
    }

    [Fact]
    public void Real_spotify_connect_track_is_read_from_track_metadata()
    {
        var state = SonosGroupState.From("RINCON_000E5800000201400",
            Soap("TransportInfo_paused_real.xml", "GetTransportInfo"),
            Soap("PositionInfo_spotify_connect_real.xml", "GetPositionInfo"),
            Soap("MediaInfo_spotify_connect_real.xml", "GetMediaInfo"),
            DateTimeOffset.UtcNow);

        Assert.Equal(SonosSource.SpotifyConnect, state.Source);
        Assert.True(state.IsPaused);
        Assert.Equal(new SonosTrack("Test Pattern", "The Placeholders", "Fixtures", null), state.Track);
        Assert.Equal(TimeSpan.Parse("0:03:13"), state.Position);
        Assert.Equal(TimeSpan.Parse("0:03:52"), state.Duration);
        Assert.Equal("Spotify", state.SourceLabel);
        Assert.Equal("\"Test Pattern\" – The Placeholders", state.WhatsOn());
    }

    [Fact]
    public void Real_tv_input_has_no_track_metadata()
    {
        var state = SonosGroupState.From("RINCON_000E5800000101400",
            Soap("TransportInfo_playing_real.xml", "GetTransportInfo"),
            Soap("PositionInfo_tv_real.xml", "GetPositionInfo"),
            Soap("MediaInfo_tv_real.xml", "GetMediaInfo"),
            DateTimeOffset.UtcNow);

        Assert.Equal(SonosSource.Tv, state.Source);
        Assert.True(state.IsPlaying);
        Assert.Equal(SonosTrack.Empty, state.Track); // "NOT_IMPLEMENTED", and dc:title is just the device id
        Assert.Equal("TV", state.WhatsOn());
        Assert.Null(state.Position);
    }

    [Fact]
    public void Real_spotify_connect_session_without_metadata_says_spotify()
    {
        var position = Soap("PositionInfo_vli_no_metadata_real.xml", "GetPositionInfo");
        var state = SonosGroupState.From("RINCON_000E5800000301400", Soap("TransportInfo_paused_real.xml", "GetTransportInfo"),
            position, new Dictionary<string, string>(), DateTimeOffset.UtcNow);

        Assert.Equal(SonosSource.SpotifyConnect, state.Source);
        Assert.Null(state.Track.Title);
        Assert.Equal("Spotify", state.WhatsOn());
    }

    [Fact]
    public void Radio_stream_content_gives_artist_and_title_and_the_station_comes_from_media_info()
    {
        const string stream = "x-sonosapi-stream:s24896?sid=254&flags=8224&sn=0";
        var trackMeta = FakeSonos.DidlDoc(
            $"<item id=\"-1\" parentID=\"-1\" restricted=\"true\"><res protocolInfo=\"sonos.com-http:*:audio/mpeg:*\">{System.Security.SecurityElement.Escape(stream)}</res>" +
            $"<r:streamContent>Massive Attack - Teardrop</r:streamContent><dc:title>{System.Security.SecurityElement.Escape(stream)}</dc:title><upnp:class>object.item</upnp:class></item>");
        var mediaMeta = FakeSonos.DidlDoc(
            "<item id=\"-1\" parentID=\"-1\" restricted=\"true\"><dc:title>RNZ National</dc:title><upnp:class>object.item.audioItem.audioBroadcast</upnp:class></item>");

        var track = SonosTrack.From(trackMeta, mediaMeta, SonosUris.Classify(stream));

        Assert.Equal(new SonosTrack("Teardrop", "Massive Attack", null, "RNZ National"), track);
    }

    [Fact]
    public void Radio_status_text_and_uri_titles_are_ignored()
    {
        var trackMeta = FakeSonos.DidlDoc(
            "<item id=\"-1\" parentID=\"-1\"><r:streamContent>ZPSTR_CONNECTING</r:streamContent><dc:title>x-sonosapi-stream:s1?sid=254</dc:title></item>");
        var track = SonosTrack.From(trackMeta, null, SonosSource.Radio);
        Assert.Null(track.Title);
        Assert.Null(track.Artist);
    }

    [Theory]
    [InlineData("x-rincon-queue:RINCON_1#0", SonosSource.Queue)]
    [InlineData("x-sonos-htastream:RINCON_000E5800000101400:spdif", SonosSource.Tv)]
    [InlineData("x-rincon-stream:RINCON_1", SonosSource.LineIn)]
    [InlineData("x-rincon:RINCON_1", SonosSource.Grouped)]
    [InlineData("x-sonos-vli:RINCON_000E5800000201400:2,spotify:5d1f0c3a9e7b4a28b6c1d0e9f8a7b6c5", SonosSource.SpotifyConnect)]
    [InlineData("x-sonos-vli:RINCON_1:1,airplay:abc", SonosSource.AirPlay)]
    [InlineData("x-sonosapi-stream:s24896?sid=254&flags=8224&sn=0", SonosSource.Radio)]
    [InlineData("x-rincon-mp3radio://stream.example.com/live", SonosSource.Radio)]
    [InlineData("x-sonos-spotify:spotify%3atrack%3a1?sid=9", SonosSource.Stream)]
    [InlineData("", SonosSource.None)]
    public void Uris_are_classified(string uri, SonosSource expected) => Assert.Equal(expected, SonosUris.Classify(uri));

    [Fact]
    public void Real_favourites_are_shortcuts_whose_container_id_is_used_as_the_uri()
    {
        var items = Didl.Parse(SonosSoapClient.ParseResponse("Browse", Fixtures.Read("Browse_FV2_real.xml"))["Result"]);
        var favourites = items.Select(SonosFavorite.From).OfType<SonosFavorite>().ToList();

        Assert.Equal(["Discover Sonos Radio", "Trending Now"], favourites.Select(f => f.Title));
        var discover = favourites[0];
        Assert.True(discover.IsShortcut);
        Assert.True(discover.IsContainer);
        Assert.Equal("Sonos Radio", discover.Service);
        Assert.Equal("x-rincon-cpcontainer:10fe0044%2fstations%2fen-US%2fNZ%2fc2Q6Tlo6ZGlzY292ZXItc29ub3MtcmFkaW8", discover.Uri);
        Assert.Contains("SA_RINCON77575_X_#Svc77575-0-Token", discover.MetaData);
        // The resMD names Sonos Radio's service: 77575 = 303 × 256 + 7.
        Assert.Equal(77575, Didl.Parse(discover.MetaData).Single().ServiceType);
    }

    [Fact]
    public void Spotify_playlist_and_radio_favourites_play_differently()
    {
        var items = Didl.Parse(SpotifyFavourites());
        var favourites = items.Select(SonosFavorite.From).OfType<SonosFavorite>().ToList();

        var playlist = favourites.Single(f => f.Title == "Peaceful Piano");
        Assert.True(playlist.IsContainer);
        Assert.False(playlist.IsShortcut);
        Assert.Equal("Spotify Playlist", playlist.Service);

        var radio = favourites.Single(f => f.Title == "RNZ National");
        Assert.False(radio.IsContainer);
        Assert.Equal("x-sonosapi-stream:s24896?sid=254&flags=8224&sn=0", radio.Uri);
    }

    /// <summary>A Spotify playlist (sid 12, account 7) and a TuneIn station, as Sonos stores favourites.</summary>
    internal static string SpotifyFavourites() => FakeSonos.DidlDoc(
        FakeSonos.Favorite("FV:2/3", "Peaceful Piano",
            "x-rincon-cpcontainer:1006206cspotify%3aplaylist%3a37i9dQZF1DX4sWSpwq3LiO?sid=12&flags=8300&sn=7",
            FakeSonos.DidlDoc("<item id=\"1006206cspotify%3aplaylist%3a37i9dQZF1DX4sWSpwq3LiO\" parentID=\"10fe2064spotify%3aplaylists\" restricted=\"true\">" +
                              "<dc:title>Peaceful Piano</dc:title><upnp:class>object.container.playlistContainer</upnp:class>" +
                              "<desc id=\"cdudn\" nameSpace=\"urn:schemas-rinconnetworks-com:metadata-1-0/\">SA_RINCON3079_X_#Svc3079-0-Token</desc></item>"),
            "Spotify Playlist"),
        FakeSonos.Favorite("FV:2/4", "RNZ National",
            "x-sonosapi-stream:s24896?sid=254&flags=8224&sn=0",
            FakeSonos.DidlDoc("<item id=\"F00092020s24896\" parentID=\"L\" restricted=\"true\"><dc:title>RNZ National</dc:title>" +
                              "<upnp:class>object.item.audioItem.audioBroadcast</upnp:class>" +
                              "<desc id=\"cdudn\" nameSpace=\"urn:schemas-rinconnetworks-com:metadata-1-0/\">SA_RINCON65031_</desc></item>"),
            "TuneIn"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("NOT_IMPLEMENTED")]
    [InlineData("<DIDL-Lite><item")]
    public void Missing_or_broken_metadata_parses_to_nothing(string? didl) => Assert.Empty(Didl.Parse(didl));

    [Fact]
    public void Empty_queue_browse_has_no_items()
    {
        var r = SonosSoapClient.ParseResponse("Browse", Fixtures.Read("Browse_Q0_empty_real.xml"));
        Assert.Empty(Didl.Parse(r["Result"]));
        Assert.Equal("0", r["TotalMatches"]);
    }

    [Theory]
    [InlineData("0:03:52", 232)]
    [InlineData("1:02:03", 3723)]
    [InlineData("0:00:01.500", 1.5)]
    public void Times_parse(string text, double seconds) => Assert.Equal(TimeSpan.FromSeconds(seconds), SonosTime.Parse(text));

    [Theory]
    [InlineData("NOT_IMPLEMENTED")]
    [InlineData("")]
    [InlineData("3:52")]
    public void Bad_times_are_null(string text) => Assert.Null(SonosTime.Parse(text));

    [Fact]
    public void Times_format_for_seek_people_and_sleep_timers()
    {
        Assert.Equal("0:01:10", SonosTime.Format(TimeSpan.FromSeconds(70)));
        Assert.Equal("1:10", SonosTime.Short(TimeSpan.FromSeconds(70)));
        Assert.Equal("01:30:00", SonosTime.SleepDuration(TimeSpan.FromMinutes(90)));
    }

    [Theory]
    [InlineData("NORMAL", false, "off")]
    [InlineData("REPEAT_ALL", false, "all")]
    [InlineData("REPEAT_ONE", false, "one")]
    [InlineData("SHUFFLE_NOREPEAT", true, "off")]
    [InlineData("SHUFFLE", true, "all")]
    [InlineData("SHUFFLE_REPEAT_ONE", true, "one")]
    public void Play_modes_round_trip(string mode, bool shuffle, string repeat)
    {
        Assert.Equal((shuffle, repeat), SonosPlayModes.Parse(mode));
        Assert.Equal(mode, SonosPlayModes.Build(shuffle, repeat));
    }
}
