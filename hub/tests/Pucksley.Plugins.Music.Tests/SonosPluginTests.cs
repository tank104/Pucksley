using System.Text.Json;
using Pucksley.Plugins.Music.Tests.Support;
using Pucksley.Plugins.Sonos;
using Pucksley.Sdk;

namespace Pucksley.Plugins.Music.Tests;

/// <summary>Tools run the way the hub runs them, against a pretend household that speaks UPnP.</summary>
public sealed class SonosPluginTests
{
    const string Kitchen = "RINCON_000E5800000101400";
    const string Study = "RINCON_000E5800000201400";

    static FakeSonos.Player KitchenOnTv(FakeSonos sonos)
    {
        var k = sonos["Kitchen"];
        k.MediaUri = $"x-sonos-htastream:{Kitchen}:spdif";
        k.TrackUri = k.MediaUri;
        k.TransportState = "PLAYING";
        return k;
    }

    static IEnumerable<string> Commands(FakeSonos sonos, string host) =>
        sonos.Calls.Where(c => c.Host == host && !c.Action.StartsWith("Get", StringComparison.Ordinal) && c.Action != "Browse")
            .Select(c => c.Action);

    [Fact]
    public async Task Volume_defaults_to_the_pucks_room()
    {
        var sonos = FakeSonos.Household();
        var (plugin, _) = sonos.CreatePlugin();

        var r = await FakeSonos.Call(plugin, "sonos_set_volume", """{"volume":30}""");

        Assert.False(r.IsError, r.Text);
        Assert.Equal("Kitchen volume is now 30.", r.Text);
        Assert.Equal("Kitchen 30%", r.Activity);
        var call = Assert.Single(sonos.CallsTo("SetVolume"));
        Assert.Equal("10.0.0.1", call.Host);
        Assert.Equal("30", call.Args["DesiredVolume"]);
        Assert.Equal("Master", call.Args["Channel"]);
        Assert.Equal(30, sonos["Kitchen"].Volume);
    }

    [Fact]
    public async Task Relative_volume_for_all_rooms()
    {
        var sonos = FakeSonos.Household();
        var (plugin, _) = sonos.CreatePlugin();

        var r = await FakeSonos.Call(plugin, "sonos_set_volume", """{"room":"everywhere","change":-10}""");

        Assert.Equal("Volume: Kitchen 8, Loft Study 55, Sonos Move 70.", r.Text);
        Assert.Equal(3, sonos.CallsTo("SetRelativeVolume").Count());
        Assert.All(sonos.CallsTo("SetRelativeVolume"), c => Assert.Equal("-10", c.Args["Adjustment"]));
    }

    [Fact]
    public async Task A_group_name_sets_each_room_in_it()
    {
        var sonos = FakeSonos.Household();
        sonos["Loft Study"].Coordinator = Kitchen;
        var (plugin, _) = sonos.CreatePlugin();

        var r = await FakeSonos.Call(plugin, "sonos_set_volume", """{"room":"the kitchen group","volume":40}""", room: null);

        Assert.Equal("Volume: Kitchen 40, Loft Study 40.", r.Text);
        Assert.Equal(80, sonos["Sonos Move"].Volume); // not in the group: untouched
        Assert.Equal(["10.0.0.1", "10.0.0.2"], sonos.CallsTo("SetVolume").Select(c => c.Host).Order());
    }

    [Fact]
    public async Task An_unknown_room_lists_the_rooms()
    {
        var (plugin, _) = FakeSonos.Household().CreatePlugin();
        var r = await FakeSonos.Call(plugin, "sonos_pause", """{"room":"den"}""");
        Assert.True(r.IsError);
        Assert.Equal("I can't find a Sonos room called 'den'. Rooms: Kitchen, Loft Study, Sonos Move.", r.Text);
    }

    [Fact]
    public async Task With_no_room_anywhere_it_uses_the_only_room_playing()
    {
        var sonos = FakeSonos.Household();
        KitchenOnTv(sonos);
        var (plugin, _) = sonos.CreatePlugin();

        var r = await FakeSonos.Call(plugin, "sonos_now_playing", room: null);

        Assert.Equal("Kitchen: playing the TV. Volume 18.", r.Text);
    }

    [Fact]
    public async Task Skipping_with_nothing_to_skip_to_says_so()
    {
        var sonos = FakeSonos.Household();
        KitchenOnTv(sonos);
        var (plugin, _) = sonos.CreatePlugin();

        var r = await FakeSonos.Call(plugin, "sonos_next");

        Assert.True(r.IsError);
        Assert.Equal("There's nothing to skip to.", r.Text);
    }

    [Fact]
    public async Task Skipping_says_what_is_playing_now()
    {
        var sonos = FakeSonos.Household();
        var k = sonos["Kitchen"];
        k.Queue.AddRange(["x-sonos-spotify:spotify%3atrack%3aA?sid=12&flags=8224&sn=7", "x-sonos-spotify:spotify%3atrack%3aB?sid=12&flags=8224&sn=7"]);
        k.PlayQueue(1);
        k.TrackMeta = FakeSonos.DidlDoc("<item id=\"-1\" parentID=\"-1\"><dc:title>Angel</dc:title><dc:creator>Massive Attack</dc:creator><upnp:class>object.item.audioItem.musicTrack</upnp:class></item>");
        var (plugin, _) = sonos.CreatePlugin();

        var r = await FakeSonos.Call(plugin, "sonos_next");

        Assert.Equal("Skipped in Kitchen. Now playing \"Angel\" – Massive Attack.", r.Text);
        Assert.Equal(2, k.Track);
    }

    [Fact]
    public async Task Pause_all_skips_idle_rooms_and_explains_the_tv()
    {
        var sonos = FakeSonos.Household();
        KitchenOnTv(sonos);
        var study = sonos["Loft Study"];
        study.Queue.Add("x-sonos-spotify:spotify%3atrack%3aA?sid=12&flags=8224&sn=7");
        study.PlayQueue(1);
        var (plugin, _) = sonos.CreatePlugin();

        var r = await FakeSonos.Call(plugin, "sonos_pause", """{"room":"all"}""");

        Assert.Equal("Paused Loft Study. Couldn't pause Kitchen (the TV and line-in can't be paused).", r.Text);
        Assert.Equal(["10.0.0.1", "10.0.0.2"], sonos.CallsTo("Pause").Select(c => c.Host).Order());
        Assert.Equal("PAUSED_PLAYBACK", study.TransportState);
    }

    [Fact]
    public async Task A_playlist_favourite_replaces_the_queue_and_plays_it()
    {
        var sonos = FakeSonos.Household();
        sonos.FavoritesDidl = SonosParsingTests.SpotifyFavourites();
        var (plugin, _) = sonos.CreatePlugin();

        var r = await FakeSonos.Call(plugin, "sonos_play_favorite", """{"name":"peaceful piano"}""");

        Assert.Equal("Playing Peaceful Piano in Kitchen.", r.Text);
        Assert.Equal(["RemoveAllTracksFromQueue", "AddURIToQueue", "SetAVTransportURI", "Play"], Commands(sonos, "10.0.0.1"));
        var add = sonos.CallsTo("AddURIToQueue").Single();
        Assert.Equal("x-rincon-cpcontainer:1006206cspotify%3aplaylist%3a37i9dQZF1DX4sWSpwq3LiO?sid=12&flags=8300&sn=7", add.Args["EnqueuedURI"]);
        Assert.Contains("SA_RINCON3079_X_#Svc3079-0-Token", add.Args["EnqueuedURIMetaData"]);
        Assert.Equal($"x-rincon-queue:{Kitchen}#0", sonos.CallsTo("SetAVTransportURI").Single().Args["CurrentURI"]);
        Assert.Equal("PLAYING", sonos["Kitchen"].TransportState);
    }

    [Fact]
    public async Task A_radio_favourite_plays_directly_with_its_metadata()
    {
        var sonos = FakeSonos.Household();
        sonos.FavoritesDidl = SonosParsingTests.SpotifyFavourites();
        var (plugin, _) = sonos.CreatePlugin();

        var r = await FakeSonos.Call(plugin, "sonos_play_favorite", """{"name":"RNZ national","room":"study"}""");

        Assert.Equal("Playing RNZ National in Loft Study.", r.Text);
        Assert.Equal(["SetAVTransportURI", "Play"], Commands(sonos, "10.0.0.2"));
        var set = sonos.CallsTo("SetAVTransportURI").Single();
        Assert.Equal("x-sonosapi-stream:s24896?sid=254&flags=8224&sn=0", set.Args["CurrentURI"]);
        Assert.Contains("RNZ National", set.Args["CurrentURIMetaData"]);
    }

    [Fact]
    public async Task An_unknown_favourite_lists_them()
    {
        var sonos = FakeSonos.Household();
        sonos.FavoritesDidl = SonosSoapClient.ParseResponse("Browse", Fixtures.Read("Browse_FV2_real.xml"))["Result"];
        var (plugin, _) = sonos.CreatePlugin();

        var list = await FakeSonos.Call(plugin, "sonos_favorites");
        var r = await FakeSonos.Call(plugin, "sonos_play_favorite", """{"name":"jazz classics"}""");

        Assert.Equal("Sonos Favourites: Discover Sonos Radio (Sonos Radio); Trending Now (Sonos Radio).", list.Text);
        Assert.Equal("I can't find a Sonos Favourite called 'jazz classics'. Favourites: Discover Sonos Radio, Trending Now.", r.Text);
    }

    [Fact]
    public async Task Spotify_playlists_play_with_the_account_learned_from_favourites()
    {
        var sonos = FakeSonos.Household();
        sonos.FavoritesDidl = SonosParsingTests.SpotifyFavourites();
        var (plugin, _) = sonos.CreatePlugin();

        var r = await FakeSonos.Call(plugin, "sonos_play_spotify", """{"uri":"https://open.spotify.com/playlist/37i9dQZF1DXcBWIGoYBM5M?si=x"}""");

        Assert.Equal("Playing the playlist in Kitchen.", r.Text);
        var add = sonos.CallsTo("AddURIToQueue").Single();
        Assert.Equal("x-rincon-cpcontainer:1006206cspotify%3aplaylist%3a37i9dQZF1DXcBWIGoYBM5M", add.Args["EnqueuedURI"]);
        var meta = Didl.Parse(add.Args["EnqueuedURIMetaData"]).Single();
        Assert.Equal("1006206cspotify%3aplaylist%3a37i9dQZF1DXcBWIGoYBM5M", meta.Id);
        Assert.Equal(3079, meta.ServiceType);
        Assert.Equal(["RemoveAllTracksFromQueue", "AddURIToQueue", "SetAVTransportURI", "Play"], Commands(sonos, "10.0.0.1"));
    }

    [Fact]
    public async Task Spotify_tracks_use_sid_and_sn_from_favourites()
    {
        var sonos = FakeSonos.Household();
        sonos.FavoritesDidl = SonosParsingTests.SpotifyFavourites();
        var (plugin, _) = sonos.CreatePlugin();

        var text = await plugin.PlaySpotifyAsync("kitchen", "spotify:track:6rqhFgbbKwnb9MLmUQDhG6", "replace", default);

        Assert.Equal("Playing the track in Kitchen.", text);
        Assert.Equal("x-sonos-spotify:spotify%3atrack%3a6rqhFgbbKwnb9MLmUQDhG6?sid=12&flags=8224&sn=7",
            sonos.CallsTo("AddURIToQueue").Single().Args["EnqueuedURI"]);
    }

    [Fact]
    public async Task Without_a_known_account_a_track_asks_for_a_favourite_and_leaves_the_queue_alone()
    {
        var sonos = FakeSonos.Household();
        var (plugin, _) = sonos.CreatePlugin();

        var r = await FakeSonos.Call(plugin, "sonos_play_spotify", """{"uri":"spotify:track:6rqhFgbbKwnb9MLmUQDhG6"}""");

        Assert.True(r.IsError);
        Assert.Contains("add any Spotify playlist to your Sonos Favourites", r.Text);
        Assert.Empty(sonos.CallsTo("RemoveAllTracksFromQueue"));
        Assert.Empty(sonos.CallsTo("AddURIToQueue"));
    }

    [Fact]
    public async Task Without_favourites_an_artist_still_plays_using_the_regions_spotify_service()
    {
        var sonos = FakeSonos.Household();
        var (plugin, _) = sonos.CreatePlugin();

        var r = await FakeSonos.Call(plugin, "sonos_play_spotify", """{"uri":"spotify:artist:6FXMGgJwohJLUSr5nVlf9X"}""");

        Assert.Equal("Playing the artist's top tracks in Kitchen.", r.Text);
        var add = sonos.CallsTo("AddURIToQueue").Single();
        Assert.Equal("x-rincon-cpcontainer:100e206cspotify%3aartistTopTracks%3a6FXMGgJwohJLUSr5nVlf9X", add.Args["EnqueuedURI"]);
        Assert.Contains("SA_RINCON3079_X_#Svc3079-0-Token", add.Args["EnqueuedURIMetaData"]);
        Assert.Single(sonos.CallsTo("ListAvailableServices"));
    }

    [Fact]
    public async Task Settings_override_the_spotify_account()
    {
        var sonos = FakeSonos.Household();
        var (plugin, _) = sonos.CreatePlugin(new SonosSettings { SpeakerIps = ["10.0.0.1"], SpotifySid = 9, SpotifySn = 2 });

        await plugin.PlaySpotifyAsync("Kitchen", "spotify:track:6rqhFgbbKwnb9MLmUQDhG6", "end", default);

        var add = sonos.CallsTo("AddURIToQueue").Single();
        Assert.Equal("x-sonos-spotify:spotify%3atrack%3a6rqhFgbbKwnb9MLmUQDhG6?sid=9&flags=8224&sn=2", add.Args["EnqueuedURI"]);
        Assert.Contains("SA_RINCON2311_X_#Svc2311-0-Token", add.Args["EnqueuedURIMetaData"]);
        Assert.Empty(sonos.CallsTo("Browse"));
        Assert.Empty(sonos.CallsTo("RemoveAllTracksFromQueue"));
    }

    [Fact]
    public async Task Play_next_goes_right_after_the_current_track()
    {
        var sonos = FakeSonos.Household();
        sonos.FavoritesDidl = SonosParsingTests.SpotifyFavourites();
        var k = sonos["Kitchen"];
        k.Queue.AddRange(["a", "b", "c"]);
        k.PlayQueue(2);
        var (plugin, _) = sonos.CreatePlugin();

        var r = await FakeSonos.Call(plugin, "sonos_play_spotify", """{"uri":"spotify:album:1DFixLWuPkv3KT3TnV35m3","mode":"next"}""");

        Assert.Equal("Added the album to Kitchen's queue, to play next.", r.Text);
        var add = sonos.CallsTo("AddURIToQueue").Single();
        Assert.Equal("3", add.Args["DesiredFirstTrackNumberEnqueued"]);
        Assert.Equal("1", add.Args["EnqueueAsNext"]);
        Assert.Equal("x-rincon-cpcontainer:1004206cspotify%3aalbum%3a1DFixLWuPkv3KT3TnV35m3", k.Queue[2]);
        Assert.Empty(sonos.CallsTo("RemoveAllTracksFromQueue"));
    }

    [Fact]
    public async Task An_unknown_room_for_spotify_lists_rooms()
    {
        var (plugin, _) = FakeSonos.Household().CreatePlugin();
        var e = await Assert.ThrowsAsync<PluginException>(() => plugin.PlaySpotifyAsync("garage", "spotify:album:1DFixLWuPkv3KT3TnV35m3", "replace", default));
        Assert.Equal("I can't find a Sonos room called 'garage'. Rooms: Kitchen, Loft Study, Sonos Move.", e.Message);
    }

    [Fact]
    public async Task Announcements_snapshot_play_and_restore()
    {
        var sonos = FakeSonos.Household();
        var k = sonos["Kitchen"];
        k.Queue.AddRange(["a", "b", "c", "d"]);
        k.PlayQueue(3, "0:01:10");
        k.Muted = true;
        var (plugin, context) = sonos.CreatePlugin();

        var r = await FakeSonos.Call(plugin, "sonos_announce", """{"text":"Dinner is ready"}""");

        Assert.False(r.IsError, r.Text);
        Assert.Equal("Announced in Kitchen.", r.Text);
        var clip = Assert.Single(context.Clips);
        Assert.Equal("audio/wav", clip.Value.ContentType);
        Assert.Equal(
            ["SetMute", "SetVolume", "SetAVTransportURI", "Play", "SetVolume", "SetMute", "SetAVTransportURI", "Seek", "Seek", "Play"],
            Commands(sonos, "10.0.0.1"));
        var sets = sonos.CallsTo("SetAVTransportURI").ToList();
        Assert.StartsWith("http://192.168.1.10:8765/clips/", sets[0].Args["CurrentURI"]);
        Assert.Equal($"x-rincon-queue:{Kitchen}#0", sets[1].Args["CurrentURI"]);
        Assert.Equal("35", sonos.CallsTo("SetVolume").First().Args["DesiredVolume"]);
        // Everything is back as it was.
        Assert.Equal((18, true, 3, "0:01:10", "PLAYING"), (k.Volume, k.Muted, k.Track, k.RelTime, k.TransportState));
    }

    [Fact]
    public async Task Announcing_in_a_paused_room_leaves_it_paused()
    {
        var sonos = FakeSonos.Household();
        var study = sonos["Loft Study"];
        study.MediaUri = "x-sonosapi-stream:s24896?sid=254&flags=8224&sn=0";
        study.TransportState = "PAUSED_PLAYBACK";
        var (plugin, _) = sonos.CreatePlugin();

        var r = await FakeSonos.Call(plugin, "sonos_announce", """{"text":"Hello","rooms":["study"]}""");

        Assert.Equal("Announced in Loft Study.", r.Text);
        Assert.Equal(["SetVolume", "SetAVTransportURI", "Play", "SetVolume", "SetAVTransportURI"], Commands(sonos, "10.0.0.2"));
        Assert.Equal("x-sonosapi-stream:s24896?sid=254&flags=8224&sn=0", study.MediaUri);
        Assert.Equal(65, study.Volume);
        Assert.DoesNotContain(sonos.Calls, c => c.Host != "10.0.0.2" && c.Action is "SetVolume" or "Play");
    }

    [Fact]
    public async Task Grouping_joins_rooms_to_the_pucks_room()
    {
        var sonos = FakeSonos.Household();
        var (plugin, _) = sonos.CreatePlugin();

        var r = await FakeSonos.Call(plugin, "sonos_group", """{"rooms":["study"]}""");

        Assert.Equal("Grouped: Kitchen + Loft Study.", r.Text);
        var join = sonos.CallsTo("SetAVTransportURI").Single();
        Assert.Equal("10.0.0.2", join.Host);
        Assert.Equal($"x-rincon:{Kitchen}", join.Args["CurrentURI"]);
        Assert.Equal(Kitchen, sonos["Loft Study"].Coordinator);
        Assert.Contains(plugin.GetMediaSources(), s => s.Name == "Kitchen + Loft Study");
    }

    [Fact]
    public async Task Ungrouping_makes_a_room_standalone()
    {
        var sonos = FakeSonos.Household();
        sonos["Loft Study"].Coordinator = Kitchen;
        var (plugin, _) = sonos.CreatePlugin();

        var r = await FakeSonos.Call(plugin, "sonos_ungroup", """{"room":"study"}""");

        Assert.Equal("Loft Study now plays on its own.", r.Text);
        Assert.Equal("10.0.0.2", sonos.CallsTo("BecomeCoordinatorOfStandaloneGroup").Single().Host);
        Assert.Null(sonos["Loft Study"].Coordinator);
    }

    [Fact]
    public async Task Party_mode_joins_everything_to_whatever_is_playing()
    {
        var sonos = FakeSonos.Household();
        KitchenOnTv(sonos);
        var (plugin, _) = sonos.CreatePlugin();

        var r = await FakeSonos.Call(plugin, "sonos_party_mode", room: "Loft Study");

        Assert.Equal("Party mode: 3 rooms are playing what Kitchen plays.", r.Text);
        Assert.All(sonos.CallsTo("SetAVTransportURI"), c => Assert.Equal($"x-rincon:{Kitchen}", c.Args["CurrentURI"]));
        Assert.Equal(["10.0.0.2", "10.0.0.3"], sonos.CallsTo("SetAVTransportURI").Select(c => c.Host).Order());
    }

    [Fact]
    public async Task Tv_and_line_in_follow_the_rooms_inputs()
    {
        var sonos = FakeSonos.Household();
        var (plugin, _) = sonos.CreatePlugin();

        var tv = await FakeSonos.Call(plugin, "sonos_tv_or_line_in");
        var lineIn = await FakeSonos.Call(plugin, "sonos_tv_or_line_in", """{"input":"line_in"}""");
        var none = await FakeSonos.Call(plugin, "sonos_tv_or_line_in", """{"room":"study"}""");

        Assert.Equal("Kitchen is now playing the TV.", tv.Text);
        Assert.Equal("Kitchen is now playing line-in.", lineIn.Text);
        Assert.Equal([$"x-sonos-htastream:{Kitchen}:spdif", $"x-rincon-stream:{Kitchen}"], sonos.CallsTo("SetAVTransportURI").Select(c => c.Args["CurrentURI"]));
        Assert.True(none.IsError);
        Assert.Equal("Loft Study has no TV or line-in input.", none.Text);
    }

    [Fact]
    public async Task Sleep_timer_takes_minutes_and_zero_cancels()
    {
        var sonos = FakeSonos.Household();
        var (plugin, _) = sonos.CreatePlugin();

        var set = await FakeSonos.Call(plugin, "sonos_sleep_timer", """{"minutes":90}""");
        Assert.Equal("01:30:00", sonos["Kitchen"].SleepTimer);
        var cancel = await FakeSonos.Call(plugin, "sonos_sleep_timer", """{"minutes":0}""");

        Assert.Equal("Kitchen will stop in 90 minutes.", set.Text);
        Assert.Equal("Sleep timer cancelled in Kitchen.", cancel.Text);
        Assert.Equal("", sonos["Kitchen"].SleepTimer);
    }

    [Fact]
    public async Task Shuffle_and_repeat_keep_each_other()
    {
        var sonos = FakeSonos.Household();
        sonos["Kitchen"].PlayMode = "REPEAT_ALL";
        var (plugin, _) = sonos.CreatePlugin();

        await FakeSonos.Call(plugin, "sonos_shuffle", """{"on":true}""");
        Assert.Equal("SHUFFLE", sonos["Kitchen"].PlayMode);
        var r = await FakeSonos.Call(plugin, "sonos_repeat", """{"mode":"one"}""");
        Assert.Equal("SHUFFLE_REPEAT_ONE", sonos["Kitchen"].PlayMode);
        Assert.Equal("Repeating this track in Kitchen.", r.Text);
    }

    [Fact]
    public async Task Context_controls_and_sources_come_from_the_cache()
    {
        var sonos = FakeSonos.Household();
        KitchenOnTv(sonos);
        var study = sonos["Loft Study"];
        study.Queue.Add("x-sonos-spotify:spotify%3atrack%3aA?sid=12&flags=8224&sn=7");
        study.PlayQueue(1);
        study.TransportState = "PAUSED_PLAYBACK";
        var (plugin, _) = sonos.CreatePlugin();

        Assert.Null(await plugin.GetContextAsync(ToolCaller.Web, default)); // nothing known yet
        var rooms = await FakeSonos.Call(plugin, "sonos_rooms");

        Assert.Equal("Kitchen: playing TV, vol 18\nLoft Study: paused, vol 65\nSonos Move: idle, vol 80", rooms.Text);
        Assert.Equal("Sonos rooms: Kitchen (playing TV, vol 18); Loft Study (paused); Sonos Move (idle).",
            await plugin.GetContextAsync(ToolCaller.Web, default));

        var controls = plugin.GetControls();
        Assert.Equal([$"{Kitchen}.vol", $"{Study}.vol", "RINCON_000E5800000301400.vol"], controls.Select(c => c.Id));
        Assert.All(controls, c => Assert.Equal((ControlKind.Number, 0d, 100d, 2d, "%", "Sonos"), (c.Kind, c.Min, c.Max, c.Step, c.Unit, c.Group)));
        Assert.Equal([18d, 65d, 80d], controls.Select(c => c.Value));

        Assert.Equal(["Kitchen", "Loft Study", "Sonos Move"], plugin.GetMediaSources().Select(s => s.Name));
        var now = await plugin.GetNowPlayingAsync(Kitchen, default);
        Assert.Equal(new NowPlaying { Title = "TV", Playing = true, Volume = 18, Muted = false, Source = "Kitchen" }, now);
    }

    [Fact]
    public async Task Context_stays_short_with_many_long_titles()
    {
        var sonos = new FakeSonos();
        for (int i = 0; i < 9; i++)
        {
            var p = sonos.Add($"RINCON_{i:D2}", $"Room number {i}", $"10.0.1.{i}");
            p.Queue.Add("x");
            p.PlayQueue(1);
            p.TrackMeta = FakeSonos.DidlDoc($"<item id=\"-1\" parentID=\"-1\"><dc:title>{new string('t', 60)} {i}</dc:title><dc:creator>{new string('a', 50)}</dc:creator></item>");
        }
        var (plugin, _) = sonos.CreatePlugin();
        await FakeSonos.Call(plugin, "sonos_rooms");

        var context = await plugin.GetContextAsync(ToolCaller.Web, default);
        Assert.NotNull(context);
        Assert.True(context.Length <= 400, $"{context.Length} chars: {context}");
        Assert.StartsWith("Sonos rooms: Room number 0 (playing", context);
    }

    [Fact]
    public async Task Dial_rooms_setting_limits_the_controls()
    {
        var (plugin, _) = FakeSonos.Household().CreatePlugin(new SonosSettings { SpeakerIps = ["10.0.0.1"], DialRooms = ["the move", "kitchen"] });
        await FakeSonos.Call(plugin, "sonos_rooms");
        Assert.Equal(["Sonos Move", "Kitchen"], plugin.GetControls().Select(c => c.Name));
    }

    [Fact]
    public async Task Turning_a_dial_sets_that_rooms_volume()
    {
        var sonos = FakeSonos.Household();
        var (plugin, _) = sonos.CreatePlugin();
        await plugin.OnControlAsync($"{Study}.vol", new ControlInput { Value = 42.4 }, default);
        Assert.Equal(42, sonos["Loft Study"].Volume);
    }

    [Fact]
    public async Task Tapping_play_pause_on_the_tv_mutes_instead()
    {
        var sonos = FakeSonos.Household();
        KitchenOnTv(sonos);
        var (plugin, _) = sonos.CreatePlugin();

        await plugin.MediaCommandAsync(Kitchen, MediaCommand.Toggle, default);

        Assert.True(sonos["Kitchen"].Muted);
        Assert.Empty(sonos.CallsTo("Pause"));
    }

    [Fact]
    public async Task The_home_dial_on_a_group_sets_the_group_volume()
    {
        var sonos = FakeSonos.Household();
        sonos["Loft Study"].Coordinator = Kitchen;
        var (plugin, _) = sonos.CreatePlugin();

        await plugin.MediaCommandAsync(Kitchen, MediaCommand.SetVolume(33), default);

        Assert.Equal(["SnapshotGroupVolume", "SetGroupVolume"], Commands(sonos, "10.0.0.1"));
        Assert.Equal("33", sonos.CallsTo("SetGroupVolume").Single().Args["DesiredVolume"]);
    }

    [Fact]
    public async Task A_speaker_that_changed_address_is_found_again()
    {
        var sonos = FakeSonos.Household();
        var (plugin, _) = sonos.CreatePlugin();
        await FakeSonos.Call(plugin, "sonos_rooms");

        sonos["Kitchen"].Host = "10.0.0.9"; // DHCP gave it a new address
        var r = await FakeSonos.Call(plugin, "sonos_set_volume", """{"volume":25}""");

        Assert.False(r.IsError, r.Text);
        Assert.Equal("10.0.0.9", sonos.CallsTo("SetVolume").Single().Host);
        Assert.Equal(25, sonos["Kitchen"].Volume);
    }

    [Fact]
    public async Task A_switched_off_speaker_is_reported_without_bothering_the_others()
    {
        var sonos = FakeSonos.Household();
        var (plugin, _) = sonos.CreatePlugin();
        await FakeSonos.Call(plugin, "sonos_rooms");
        sonos["Sonos Move"].Unreachable = true; // battery ran out; Sonos still lists it for a while

        var r = await FakeSonos.Call(plugin, "sonos_set_volume", """{"room":"the move","volume":10}""");
        var kitchen = await FakeSonos.Call(plugin, "sonos_set_volume", """{"volume":12}""");

        Assert.True(r.IsError);
        Assert.Equal("I couldn't reach the Sonos speaker in Sonos Move. Is it switched on?", r.Text);
        Assert.Equal("Kitchen volume is now 12.", kitchen.Text);
    }

    [Fact]
    public async Task No_speakers_at_all_explains_what_to_do()
    {
        var sonos = new FakeSonos();
        var plugin = new SonosPlugin { DiscoverOverride = _ => Task.FromResult<IReadOnlyList<string>>([]) };
        var context = new TestSupport.FakePluginContext(http: sonos.CreateClient());
        plugin.Initialize(context);
        plugin.ApplySettings(new SonosSettings());

        var r = await FakeSonos.Call(plugin, "sonos_rooms");

        Assert.True(r.IsError);
        Assert.Equal(SonosRooms.NoSpeakersMessage, r.Text);
        Assert.Equal(StatusLevel.Warning, plugin.Status.Level);
    }

    [Fact]
    public async Task Background_loop_discovers_and_polls_while_started()
    {
        var sonos = FakeSonos.Household();
        KitchenOnTv(sonos);
        var (plugin, _) = sonos.CreatePlugin();
        plugin.PollInterval = TimeSpan.FromMilliseconds(50);

        await plugin.StartAsync(default);
        try
        {
            string? context = null;
            for (int i = 0; i < 100 && context?.Contains("vol 18") != true; i++)
            {
                await Task.Delay(50);
                context = await plugin.GetContextAsync(ToolCaller.Web, default);
            }
            Assert.Equal("Sonos rooms: Kitchen (playing TV, vol 18); Loft Study (idle); Sonos Move (idle).", context);
            Assert.Equal("3 rooms: Kitchen, Loft Study, Sonos Move", plugin.Status.Message);
            Assert.Equal(["Kitchen", "Loft Study", "Sonos Move"], plugin.RoomNames);

            // Restarted (as after saving settings) with nothing changed: the status still comes back.
            await plugin.StopAsync();
            await plugin.StartAsync(default);
            for (int i = 0; i < 100 && plugin.Status.Message.StartsWith("Looking", StringComparison.Ordinal); i++) await Task.Delay(50);
            Assert.Equal(new PluginStatus(StatusLevel.Ok, "3 rooms: Kitchen, Loft Study, Sonos Move"), plugin.Status);
        }
        finally
        {
            await plugin.StopAsync();
        }
    }

    [Fact]
    public void Every_tool_has_a_valid_unique_name_and_a_schema()
    {
        var plugin = new SonosPlugin();
        plugin.Initialize(new TestSupport.FakePluginContext());
        var tools = plugin.GetTools();
        Assert.Equal(19, tools.Count);
        Assert.All(tools, t => Assert.Matches("^sonos_[a-z0-9_]{1,58}$", t.Name));
        Assert.Equal(tools.Count, tools.Select(t => t.Name).Distinct().Count());
        var group = tools.Single(t => t.Name == "sonos_group").InputSchema.ToJsonString();
        Assert.Contains("\"to_room\"", group);
        Assert.Contains("\"required\":[\"rooms\"]", group);
        Assert.DoesNotContain("caller", JsonSerializer.Serialize(tools.Select(t => t.InputSchema.ToJsonString())));
    }
}
