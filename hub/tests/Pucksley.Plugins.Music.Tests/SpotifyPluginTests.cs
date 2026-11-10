using System.Net;
using System.Text.Json.Nodes;
using Pucksley.Plugins.Music.Tests.Support;
using Pucksley.Plugins.Sonos;
using Pucksley.Plugins.Spotify;
using Pucksley.Sdk;
using Pucksley.TestSupport;

namespace Pucksley.Plugins.Music.Tests;

/// <summary>Spotify tools as the hub runs them, including playing through Sonos.</summary>
public sealed class SpotifyPluginTests
{
    const string NoActiveDevice = """{"error":{"status":404,"message":"Player command failed: No active device found","reason":"NO_ACTIVE_DEVICE"}}""";

    static FakeSpotify WithDevices(params (string Id, string Name, bool Active)[] devices)
    {
        var spotify = new FakeSpotify();
        spotify.Route("GET me/player/devices", FakeSpotify.Devices(devices));
        spotify.Route("GET search", FakeSpotify.MassiveAttackSearch);
        spotify.Routes["PUT me/player/play"] = _ => new HttpResponseMessage(HttpStatusCode.NoContent);
        return spotify;
    }

    static async Task<(SonosPlugin Sonos, FakeSonos Household)> DiscoveredSonos()
    {
        var household = FakeSonos.Household();
        var (sonos, _) = household.CreatePlugin();
        await FakeSonos.Call(sonos, "sonos_rooms"); // the Sonos plugin finds its speakers at start
        return (sonos, household);
    }

    [Fact]
    public async Task Play_searches_and_plays_on_the_pucks_room()
    {
        var spotify = WithDevices(("dev-kitchen", "Kitchen", false), ("dev-pc", "MY-PC", true));
        var (plugin, _) = spotify.CreatePlugin();

        var r = await FakeSonos.Call(plugin, "spotify_play", """{"query":"Massive Attack"}""");

        Assert.Equal("Playing Massive Attack on Kitchen.", r.Text);
        Assert.Equal("Massive Attack", r.Activity);
        var search = spotify.Requests("GET", "search").Single();
        var q = SpotifyAuth.ParseQuery(search.Uri.Query);
        Assert.Equal(("Massive Attack", "track,album,artist,playlist", "5"), (q["q"], q["type"], q["limit"]));
        // No market=from_token: Spotify refuses it without the user-read-private scope ("Insufficient client scope")
        Assert.False(q.ContainsKey("market"));
        var play = spotify.Requests("PUT", "me/player/play").Single();
        Assert.Equal("?device_id=dev-kitchen", play.Uri.Query);
        Assert.Equal("""{"context_uri":"spotify:artist:6FXMGgJwohJLUSr5nVlf9X"}""", play.Body);
    }

    [Fact]
    public async Task A_track_plays_as_a_single_uri()
    {
        var spotify = WithDevices(("dev-pc", "MY-PC", true));
        var (plugin, _) = spotify.CreatePlugin();

        var r = await FakeSonos.Call(plugin, "spotify_play", """{"query":"teardrop by massive attack","device":"my pc"}""");

        Assert.Equal("Playing \"Teardrop\" by Massive Attack on MY-PC.", r.Text);
        Assert.Equal("""{"uris":["spotify:track:67Hna13dNDkZvBpTXRIaOJ"]}""", spotify.Requests("PUT", "me/player/play").Single().Body);
    }

    [Fact]
    public async Task With_no_matching_connect_device_it_plays_through_sonos()
    {
        var (sonos, household) = await DiscoveredSonos();
        var spotify = WithDevices(("dev-pc", "MY-PC", true));
        var (plugin, _) = spotify.CreatePlugin(sonos);

        var r = await FakeSonos.Call(plugin, "spotify_play", """{"query":"Massive Attack"}""");

        Assert.False(r.IsError, r.Text);
        Assert.Equal("Playing Massive Attack on Kitchen.", r.Text);
        Assert.Empty(spotify.Requests("PUT", "me/player/play"));
        var add = household.CallsTo("AddURIToQueue").Single();
        Assert.Equal("x-rincon-cpcontainer:100e206cspotify%3aartistTopTracks%3a6FXMGgJwohJLUSr5nVlf9X", add.Args["EnqueuedURI"]);
        Assert.Equal("PLAYING", household["Kitchen"].TransportState);
    }

    [Fact]
    public async Task A_named_sonos_room_is_used_even_when_another_device_is_active()
    {
        var (sonos, household) = await DiscoveredSonos();
        var spotify = WithDevices(("dev-pc", "MY-PC", true));
        var (plugin, _) = spotify.CreatePlugin(sonos);

        var r = await FakeSonos.Call(plugin, "spotify_play", """{"query":"mezzanine","type":"album","device":"the study"}""", room: null);

        Assert.Equal("Playing the album Mezzanine by Massive Attack on Loft Study.", r.Text);
        Assert.Equal("10.0.0.2", household.CallsTo("AddURIToQueue").Single().Host);
    }

    [Fact]
    public async Task An_unknown_device_lists_devices_and_rooms()
    {
        var (sonos, _) = await DiscoveredSonos();
        var (plugin, _) = WithDevices(("dev-pc", "MY-PC", true)).CreatePlugin(sonos);

        var r = await FakeSonos.Call(plugin, "spotify_play", """{"query":"Massive Attack","device":"garage"}""");

        Assert.True(r.IsError);
        Assert.Equal("I can't find \"garage\". Spotify devices: MY-PC. Sonos rooms: Kitchen, Loft Study, Sonos Move.", r.Text);
    }

    [Fact]
    public async Task Without_a_room_it_plays_on_the_active_device()
    {
        var spotify = WithDevices(("dev-kitchen", "Kitchen", false), ("dev-pc", "MY-PC", true));
        var (plugin, _) = spotify.CreatePlugin();

        var r = await FakeSonos.Call(plugin, "spotify_play", """{"query":"spotify:album:49MNmJhZQewjt06rpwp6QR"}""", room: "Garage");

        Assert.Equal("Playing that on MY-PC.", r.Text);
        Assert.Empty(spotify.Requests("GET", "search"));
        Assert.Equal("""{"context_uri":"spotify:album:49MNmJhZQewjt06rpwp6QR"}""", spotify.Requests("PUT", "me/player/play").Single().Body);
    }

    [Fact]
    public async Task A_sleeping_device_is_woken_with_a_transfer()
    {
        var spotify = WithDevices(("dev-kitchen", "Kitchen", false));
        spotify.RouteSequence("PUT me/player/play", (HttpStatusCode.NotFound, """{"error":{"status":404,"message":"Device not found"}}"""), (HttpStatusCode.NoContent, ""));
        spotify.Routes["PUT me/player"] = _ => new HttpResponseMessage(HttpStatusCode.NoContent);
        var (plugin, _) = spotify.CreatePlugin();

        var r = await FakeSonos.Call(plugin, "spotify_play", """{"query":"Massive Attack"}""");

        Assert.False(r.IsError, r.Text);
        Assert.Equal(["/v1/me/player/play", "/v1/me/player", "/v1/me/player/play"],
            spotify.ApiRequests.Where(q => q.Method == HttpMethod.Put).Select(q => q.Uri.AbsolutePath));
        Assert.Equal("""{"device_ids":["dev-kitchen"],"play":false}""", spotify.Requests("PUT", "me/player").Single().Body);
    }

    [Fact]
    public async Task Liked_songs_play_shuffled()
    {
        var spotify = WithDevices(("dev-kitchen", "Kitchen", false));
        spotify.Route("GET me/tracks", $$"""{"items":[{"track":{{FakeSpotify.Teardrop}}},{"track":{{FakeSpotify.Angel}}},{"track":null}]}""");
        var (plugin, _) = spotify.CreatePlugin();

        var r = await FakeSonos.Call(plugin, "spotify_play_playlist", """{"name":"my liked songs"}""");

        Assert.Equal("Playing your Liked Songs, shuffled, on Kitchen.", r.Text);
        var body = JsonNode.Parse(spotify.Requests("PUT", "me/player/play").Single().Body!)!;
        Assert.Equal(["spotify:track:67Hna13dNDkZvBpTXRIaOJ", "spotify:track:7uv632EkfwYhXoqf8rhYrg"],
            body["uris"]!.AsArray().Select(u => u!.GetValue<string>()).Order());
        Assert.Empty(spotify.Requests("GET", "me/playlists"));
    }

    [Fact]
    public async Task Own_playlists_are_found_by_name_across_pages()
    {
        var spotify = WithDevices(("dev-kitchen", "Kitchen", false));
        var page1 = new JsonObject
        {
            ["items"] = new JsonArray(Enumerable.Range(1, 50).Select(i => (JsonNode?)(i == 7 ? null : new JsonObject { ["name"] = $"Mix {i}", ["uri"] = $"spotify:playlist:mix{i}" })).ToArray()),
            ["next"] = "https://api.spotify.com/v1/me/playlists?offset=50&limit=50",
        };
        spotify.Routes["GET me/playlists"] = request => FakeHttpHandler.Json(request.Uri.Query.Contains("offset=0")
            ? page1.ToJsonString()
            : """{"items":[{"name":"Dinner Party","uri":"spotify:playlist:dinner"}],"next":null}""");
        var (plugin, _) = spotify.CreatePlugin();

        var r = await FakeSonos.Call(plugin, "spotify_play_playlist", """{"name":"the dinner party playlist"}""");

        Assert.Equal("Playing your playlist Dinner Party on Kitchen.", r.Text);
        Assert.Equal("""{"context_uri":"spotify:playlist:dinner"}""", spotify.Requests("PUT", "me/player/play").Single().Body);
        Assert.Equal(2, spotify.Requests("GET", "me/playlists").Count);
    }

    [Fact]
    public async Task Their_own_playlist_beats_a_liked_songs_phrase_inside_its_name()
    {
        var spotify = WithDevices(("dev-kitchen", "Kitchen", false));
        spotify.Route("GET me/playlists", """{"items":[{"name":"Saved Songs 2019","uri":"spotify:playlist:s2019"}],"next":null}""");
        spotify.Route("GET me/tracks", $$"""{"items":[{"track":{{FakeSpotify.Teardrop}}}]}""");
        var (plugin, _) = spotify.CreatePlugin();

        var own = await FakeSonos.Call(plugin, "spotify_play_playlist", """{"name":"saved songs 2019"}""");
        var liked = await FakeSonos.Call(plugin, "spotify_play_playlist", """{"name":"my favourites"}""");

        Assert.Equal("Playing your playlist Saved Songs 2019 on Kitchen.", own.Text);
        Assert.Equal("Playing your Liked Songs, shuffled, on Kitchen.", liked.Text);
    }

    [Fact]
    public async Task An_unknown_playlist_lists_some_of_theirs()
    {
        var spotify = WithDevices(("dev-kitchen", "Kitchen", false));
        spotify.Route("GET me/playlists", """{"items":[{"name":"Road Trip","uri":"spotify:playlist:a"},{"name":"Gym","uri":"spotify:playlist:b"}],"next":null}""");
        var (plugin, _) = spotify.CreatePlugin();

        var r = await FakeSonos.Call(plugin, "spotify_play_playlist", """{"name":"christmas carols"}""");

        Assert.Equal("I can't find a playlist called \"christmas carols\" in your library. Your playlists: Road Trip, Gym.", r.Text);
    }

    [Fact]
    public async Task Now_playing_is_described_and_feeds_the_context()
    {
        var spotify = new FakeSpotify();
        spotify.Route("GET me/player", FakeSpotify.PlayerOnKitchen);
        var (plugin, _) = spotify.CreatePlugin();

        var r = await FakeSonos.Call(plugin, "spotify_now_playing");

        Assert.Equal("Playing: \"Teardrop\" by Massive Attack, from Mezzanine, on Kitchen (1:23 of 5:29). Volume 30.", r.Text);
        Assert.Equal("Spotify: playing \"Teardrop\" by Massive Attack on Kitchen.", await plugin.GetContextAsync(ToolCaller.Web, default));
        var now = await plugin.GetNowPlayingAsync("active", default);
        Assert.Equal(new NowPlaying { Title = "Teardrop", Artist = "Massive Attack", Album = "Mezzanine", Playing = true, Volume = 30, Muted = false, Source = "Spotify on Kitchen" }, now);
    }

    [Fact]
    public async Task Nothing_playing_is_said_plainly()
    {
        var spotify = new FakeSpotify();
        spotify.Routes["GET me/player"] = _ => new HttpResponseMessage(HttpStatusCode.NoContent);
        var (plugin, _) = spotify.CreatePlugin();

        var r = await FakeSonos.Call(plugin, "spotify_now_playing");

        Assert.Equal("Nothing is playing on Spotify.", r.Text);
        Assert.Null(await plugin.GetContextAsync(ToolCaller.Web, default));
    }

    [Fact]
    public async Task Pausing_when_already_paused_is_fine()
    {
        var spotify = new FakeSpotify();
        spotify.Route("PUT me/player/pause", """{"error":{"status":403,"message":"Player command failed: Already paused","reason":"ALREADY_PAUSED"}}""", HttpStatusCode.Forbidden);
        var (plugin, _) = spotify.CreatePlugin();

        var r = await FakeSonos.Call(plugin, "spotify_pause");

        Assert.False(r.IsError, r.Text);
        Assert.Equal("Paused Spotify.", r.Text);
    }

    [Fact]
    public async Task Transport_with_nothing_on_connect_points_at_the_sonos_controls()
    {
        var (sonos, _) = await DiscoveredSonos();
        var spotify = new FakeSpotify();
        spotify.Route("POST me/player/next", NoActiveDevice, HttpStatusCode.NotFound);
        var (plugin, _) = spotify.CreatePlugin(sonos);

        var r = await FakeSonos.Call(plugin, "spotify_next");

        Assert.True(r.IsError);
        Assert.Equal("Nothing is playing through Spotify Connect. If the music is on a Sonos room, use the Sonos controls for it.", r.Text);
    }

    [Fact]
    public async Task Queueing_with_nothing_on_connect_adds_it_to_the_sonos_queue()
    {
        var (sonos, household) = await DiscoveredSonos();
        household.FavoritesDidl = SonosParsingTests.SpotifyFavourites(); // so Sonos knows the account (sn) for tracks
        var spotify = WithDevices();
        spotify.Route("POST me/player/queue", NoActiveDevice, HttpStatusCode.NotFound);
        var (plugin, _) = spotify.CreatePlugin(sonos);

        var r = await FakeSonos.Call(plugin, "spotify_queue", """{"query":"teardrop"}""");

        Assert.False(r.IsError, r.Text);
        Assert.Contains("Kitchen's queue", r.Text);
        var queued = spotify.Requests("POST", "me/player/queue").Single();
        Assert.Equal("spotify:track:67Hna13dNDkZvBpTXRIaOJ", SpotifyAuth.ParseQuery(queued.Uri.Query)["uri"]);
        Assert.Equal("x-sonos-spotify:spotify%3atrack%3a67Hna13dNDkZvBpTXRIaOJ?sid=12&flags=8224&sn=7",
            household.CallsTo("AddURIToQueue").Single().Args["EnqueuedURI"]);
    }

    [Fact]
    public async Task Liking_saves_the_current_track()
    {
        var spotify = new FakeSpotify();
        spotify.Route("GET me/player/currently-playing", $$"""{"is_playing":true,"item":{{FakeSpotify.Teardrop}}}""");
        spotify.Routes["PUT me/tracks"] = _ => FakeHttpHandler.Json("");
        var (plugin, _) = spotify.CreatePlugin();

        var r = await FakeSonos.Call(plugin, "spotify_like_current");

        Assert.Equal("Saved \"Teardrop\" by Massive Attack to your Liked Songs.", r.Text);
        Assert.Equal("?ids=67Hna13dNDkZvBpTXRIaOJ", spotify.Requests("PUT", "me/tracks").Single().Uri.Query);
    }

    [Fact]
    public async Task Liking_a_song_sonos_plays_from_its_queue_works_too()
    {
        var household = FakeSonos.Household();
        var kitchen = household["Kitchen"];
        kitchen.Queue.Add("x-sonos-spotify:spotify%3atrack%3a67Hna13dNDkZvBpTXRIaOJ?sid=12&flags=8224&sn=7");
        kitchen.PlayQueue(1);
        kitchen.TrackMeta = FakeSonos.DidlDoc("<item id=\"-1\" parentID=\"-1\"><dc:title>Teardrop</dc:title><dc:creator>Massive Attack</dc:creator></item>");
        var (sonos, _) = household.CreatePlugin();
        await FakeSonos.Call(sonos, "sonos_rooms");
        var spotify = new FakeSpotify();
        spotify.Routes["GET me/player/currently-playing"] = _ => new HttpResponseMessage(HttpStatusCode.NoContent);
        spotify.Routes["PUT me/tracks"] = _ => FakeHttpHandler.Json("");
        var (plugin, _) = spotify.CreatePlugin(sonos);

        var r = await FakeSonos.Call(plugin, "spotify_like_current");

        Assert.Equal("Saved \"Teardrop\" by Massive Attack to your Liked Songs.", r.Text);
        Assert.Equal("?ids=67Hna13dNDkZvBpTXRIaOJ", spotify.Requests("PUT", "me/tracks").Single().Uri.Query);
    }

    [Fact]
    public async Task Recently_played_answers_what_was_that_song()
    {
        var spotify = new FakeSpotify();
        var now = DateTimeOffset.UtcNow;
        spotify.Route("GET me/player/recently-played",
            $$"""{"items":[{"track":{{FakeSpotify.Angel}},"played_at":"{{now.AddMinutes(-3).AddSeconds(-5):O}}"},{"track":{{FakeSpotify.Teardrop}},"played_at":"{{now.AddHours(-2).AddMinutes(-1):O}}"}]}""");
        var (plugin, _) = spotify.CreatePlugin();

        var r = await FakeSonos.Call(plugin, "spotify_recent");

        Assert.Equal("Recently played, newest first: \"Angel\" by Massive Attack (3 min ago); \"Teardrop\" by Massive Attack (2 h ago).", r.Text);
    }

    [Fact]
    public async Task Devices_include_sonos_rooms_that_spotify_cannot_see()
    {
        var (sonos, _) = await DiscoveredSonos();
        var spotify = WithDevices(("dev-kitchen", "Kitchen", true), ("dev-pc", "MY-PC", false));
        var (plugin, _) = spotify.CreatePlugin(sonos);

        var r = await FakeSonos.Call(plugin, "spotify_devices");

        Assert.Equal("Spotify devices: Kitchen (speaker, active, vol 30); MY-PC (computer, vol 30). Sonos rooms (played through Sonos): Loft Study, Sonos Move.", r.Text);
    }

    [Fact]
    public async Task Transfer_moves_playback_to_a_connect_device()
    {
        var spotify = WithDevices(("dev-kitchen", "Kitchen", false), ("dev-pc", "MY-PC", true));
        spotify.Routes["PUT me/player"] = _ => new HttpResponseMessage(HttpStatusCode.NoContent);
        var (plugin, _) = spotify.CreatePlugin();

        var r = await FakeSonos.Call(plugin, "spotify_transfer", """{"device":"kitchen"}""", room: null);

        Assert.Equal("Moved Spotify to Kitchen.", r.Text);
        Assert.Equal("""{"device_ids":["dev-kitchen"],"play":true}""", spotify.Requests("PUT", "me/player").Single().Body);
    }

    [Fact]
    public async Task Transfer_to_a_sonos_only_room_restarts_the_album_there_and_pauses_the_old_device()
    {
        var (sonos, household) = await DiscoveredSonos();
        var spotify = WithDevices(("dev-pc", "MY-PC", true));
        spotify.Route("GET me/player", FakeSpotify.PlayerOnKitchen.Replace("dev-kitchen", "dev-pc").Replace("\"Kitchen\"", "\"MY-PC\""));
        spotify.Routes["PUT me/player/pause"] = _ => new HttpResponseMessage(HttpStatusCode.NoContent);
        var (plugin, _) = spotify.CreatePlugin(sonos);

        var r = await FakeSonos.Call(plugin, "spotify_transfer", """{"device":"kitchen"}""", room: null);

        Assert.Equal("Moved to Kitchen through Sonos (from the start of the album): \"Teardrop\" by Massive Attack.", r.Text);
        Assert.Equal("x-rincon-cpcontainer:1004206cspotify%3aalbum%3a49MNmJhZQewjt06rpwp6QR", household.CallsTo("AddURIToQueue").Single().Args["EnqueuedURI"]);
        Assert.Equal("?device_id=dev-pc", spotify.Requests("PUT", "me/player/pause").Single().Uri.Query);
    }

    [Fact]
    public async Task Volume_can_be_relative()
    {
        var spotify = new FakeSpotify();
        spotify.Route("GET me/player", FakeSpotify.PlayerOnKitchen);
        spotify.Routes["PUT me/player/volume"] = _ => new HttpResponseMessage(HttpStatusCode.NoContent);
        var (plugin, _) = spotify.CreatePlugin();

        var r = await FakeSonos.Call(plugin, "spotify_volume", """{"change":-10}""");

        Assert.Equal("Spotify volume is now 20.", r.Text);
        Assert.Equal("?volume_percent=20", spotify.Requests("PUT", "me/player/volume").Single().Uri.Query);
    }

    [Fact]
    public async Task Tools_need_a_connection()
    {
        var (plugin, _) = new FakeSpotify().CreatePlugin(settings: new SpotifySettings { ClientId = "client-123" });
        var r = await FakeSonos.Call(plugin, "spotify_play", """{"query":"anything"}""");
        Assert.True(r.IsError);
        Assert.Equal(SpotifyApi.NotConnectedMessage, r.Text);
        Assert.Null(await plugin.GetContextAsync(ToolCaller.Web, default));
        Assert.Null(await plugin.GetNowPlayingAsync("active", default));
    }

    [Fact]
    public void Every_tool_has_a_valid_unique_name()
    {
        var plugin = new SpotifyPlugin();
        plugin.Initialize(new FakePluginContext());
        var tools = plugin.GetTools();
        Assert.Equal(15, tools.Count);
        Assert.All(tools, t => Assert.Matches("^spotify_[a-z0-9_]{1,56}$", t.Name));
        Assert.Equal(tools.Count, tools.Select(t => t.Name).Distinct().Count());
        Assert.Contains("\"required\":[\"query\"]", tools.Single(t => t.Name == "spotify_play").InputSchema.ToJsonString());
    }
}
