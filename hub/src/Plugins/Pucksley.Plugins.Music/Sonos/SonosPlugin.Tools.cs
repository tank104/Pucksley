using System.Xml;
using Pucksley.Plugins.Spotify;
using Pucksley.Sdk;

namespace Pucksley.Plugins.Sonos;

public sealed partial class SonosPlugin
{
    const string RoomParam = "Room, e.g. Kitchen; default: the puck's room";
    const string RoomsOrAllParam = "Room, group or \"all\"; default: the puck's room";

    [Tool("sonos_rooms", "List the Sonos rooms by group, with what each is playing and its volume.")]
    public async Task<string> RoomsTool(CancellationToken cancel)
    {
        var topology = await RequireTopologyAsync(cancel);
        await RefreshStatesAsync(cancel);
        return SonosText.Rooms(topology, _states, _volumes);
    }

    [Tool("sonos_now_playing", "What a Sonos room is playing: title, artist, album, position and volume. Room defaults to the puck's room.")]
    public async Task<string> NowPlayingTool(ToolCaller caller, [Param(RoomParam)] string? room = null, CancellationToken cancel = default)
    {
        var player = await ResolveRoomAsync(room, caller, cancel);
        return await RunAsync(player.RoomName, async topology =>
        {
            var group = GroupFor(topology, player);
            var state = await PollGroupAsync(group, cancel);
            await Task.WhenAll(group.Members.Select(m => QuietlyAsync(() => PollVolumeAsync(m, cancel), m.RoomName)));
            return SonosText.NowPlaying(group, state, _volumes);
        }, cancel);
    }

    [Tool("sonos_play", "Resume playback in a Sonos room. Room defaults to the puck's room.")]
    public async Task<ToolResult> PlayTool(ToolCaller caller, [Param(RoomParam)] string? room = null, CancellationToken cancel = default)
    {
        var player = await ResolveRoomAsync(room, caller, cancel);
        var name = await RunAsync(player.RoomName, async topology =>
        {
            var group = GroupFor(topology, player);
            await PlayAsync(group.Coordinator, cancel);
            return group.Name;
        }, cancel);
        RefreshSoon(player);
        return ToolResult.Ok($"Playing in {name}.", SonosText.Activity($"{name} playing"));
    }

    [Tool("sonos_pause", "Pause a Sonos room. Room defaults to the puck's room; \"all\" pauses every room.")]
    public async Task<ToolResult> PauseTool(ToolCaller caller, [Param("Room, or \"all\"; default: the puck's room")] string? room = null, CancellationToken cancel = default)
    {
        if (SonosRooms.IsAll(room)) return await PauseAllAsync(cancel);
        var player = await ResolveRoomAsync(room, caller, cancel);
        var name = await RunAsync(player.RoomName, async topology =>
        {
            var group = GroupFor(topology, player);
            await Av(group.Coordinator, "Pause", cancel, Instance);
            return group.Name;
        }, cancel);
        RefreshSoon(player);
        return ToolResult.Ok($"Paused {name}.", SonosText.Activity($"{name} paused"));
    }

    async Task<ToolResult> PauseAllAsync(CancellationToken cancel)
    {
        var topology = await RequireTopologyAsync(cancel);
        await RefreshStatesAsync(cancel);
        var paused = new List<string>();
        var cannot = new List<string>();
        await Task.WhenAll(topology.Groups.Select(async g =>
        {
            if (_states.TryGetValue(g.Coordinator.Uuid, out var state) && !state.IsPlaying) return;
            try
            {
                await Av(g.Coordinator, "Pause", cancel, Instance);
                lock (paused) paused.Add(g.Name);
            }
            catch (Exception e) when (e is SonosUpnpException or HttpRequestException)
            {
                lock (paused) cannot.Add(g.Name);
            }
        }));
        foreach (var g in topology.Groups) RefreshSoon(g.Coordinator);
        if (paused.Count == 0 && cannot.Count == 0) return ToolResult.Ok("Nothing was playing.", "Nothing playing");
        var text = paused.Count > 0 ? $"Paused {SonosText.Join(paused.Order())}." : "";
        if (cannot.Count > 0) text += $" Couldn't pause {SonosText.Join(cannot.Order())} (the TV and line-in can't be paused).";
        return ToolResult.Ok(text.Trim(), "Paused everything");
    }

    [Tool("sonos_next", "Skip to the next track in a Sonos room. Room defaults to the puck's room.")]
    public Task<ToolResult> NextTool(ToolCaller caller, [Param(RoomParam)] string? room = null, CancellationToken cancel = default) =>
        SkipAsync("Next", caller, room, cancel);

    [Tool("sonos_previous", "Go back to the previous track in a Sonos room. Room defaults to the puck's room.")]
    public Task<ToolResult> PreviousTool(ToolCaller caller, [Param(RoomParam)] string? room = null, CancellationToken cancel = default) =>
        SkipAsync("Previous", caller, room, cancel);

    async Task<ToolResult> SkipAsync(string action, ToolCaller caller, string? room, CancellationToken cancel)
    {
        var player = await ResolveRoomAsync(room, caller, cancel);
        var (name, state) = await RunAsync(player.RoomName, async topology =>
        {
            var group = GroupFor(topology, player);
            await Av(group.Coordinator, action, cancel, Instance);
            // Read what it skipped to, so the answer can say ("now playing …").
            await Task.Delay(300, cancel);
            SonosGroupState? after = null;
            try
            {
                after = await PollGroupAsync(group, cancel);
            }
            catch (Exception e) when (e is HttpRequestException or SonosUpnpException) { }
            return (group.Name, after);
        }, cancel);
        var verb = action == "Next" ? "Skipped" : "Went back";
        var now = state?.WhatsOn() is { } what && state.Source is not (SonosSource.Tv or SonosSource.LineIn) ? $" Now playing {what}." : "";
        return ToolResult.Ok($"{verb} in {name}.{now}", SonosText.Activity($"{name}: {(action == "Next" ? "next" : "previous")}"));
    }

    [Tool("sonos_set_volume", "Set a Sonos room's volume (0-100), or change it by a relative amount (e.g. change -10). Room \"all\" or a group name sets each room in it. Room defaults to the puck's room.")]
    public async Task<ToolResult> SetVolumeTool(ToolCaller caller,
        [Param(RoomsOrAllParam)] string? room = null,
        [Param("New volume, 0-100", Min = 0, Max = 100)] int? volume = null,
        [Param("Relative change instead, e.g. 5 or -10", Min = -100, Max = 100)] int? change = null,
        CancellationToken cancel = default)
    {
        if (volume is null && change is null) throw new PluginException("Give a volume (0-100) or how much to change it.");
        var targets = await ResolveRoomsAsync(room, caller, cancel);
        var results = await RunAsync(targets.Count == 1 ? targets[0].RoomName : null, topology =>
            Task.WhenAll(targets.Select(async target =>
            {
                var p = Fresh(topology, target);
                if (volume is int absolute) return (Room: p.RoomName, Volume: await SetVolumeAsync(p, absolute, cancel));
                var r = await Rc(p, "SetRelativeVolume", cancel, Instance, Master, ("Adjustment", change!.Value.ToString(Inv)));
                int now = int.TryParse(r.GetValueOrDefault("NewVolume"), out var n) ? n : 0;
                NoteVolume(p, now, null);
                return (Room: p.RoomName, Volume: now);
            })), cancel);

        if (results.Length == 1)
            return ToolResult.Ok($"{results[0].Room} volume is now {results[0].Volume}.", SonosText.Activity($"{results[0].Room} {results[0].Volume}%"));
        var list = string.Join(", ", results.OrderBy(r => r.Room).Select(r => $"{r.Room} {r.Volume}"));
        bool same = results.All(r => r.Volume == results[0].Volume);
        return ToolResult.Ok($"Volume: {list}.", same ? $"All rooms {results[0].Volume}%" : "Volume set");
    }

    [Tool("sonos_mute", "Mute or unmute a Sonos room, a group, or \"all\". Room defaults to the puck's room.")]
    public async Task<ToolResult> MuteTool(ToolCaller caller,
        [Param("true to mute, false to unmute")] bool muted = true,
        [Param(RoomsOrAllParam)] string? room = null,
        CancellationToken cancel = default)
    {
        var targets = await ResolveRoomsAsync(room, caller, cancel);
        await RunAsync(targets.Count == 1 ? targets[0].RoomName : null, async topology =>
        {
            await Task.WhenAll(targets.Select(t => SetMuteAsync(Fresh(topology, t), muted, cancel)));
            return true;
        }, cancel);
        var names = SonosRooms.IsAll(room) && targets.Count > 1 ? "every room" : SonosText.Join(targets.Select(t => t.RoomName));
        return ToolResult.Ok($"{(muted ? "Muted" : "Unmuted")} {names}.", SonosText.Activity($"{(muted ? "Muted" : "Unmuted")} {names}"));
    }

    [Tool("sonos_group", "Group Sonos rooms so they play the same music: each of rooms joins to_room's group. to_room defaults to the puck's room.")]
    public async Task<ToolResult> GroupTool(ToolCaller caller,
        [Param("Rooms to add, e.g. [\"Dining\"]")] string[] rooms,
        [Param("The room whose group (and music) they join; default: the puck's room")] string? toRoom = null,
        CancellationToken cancel = default)
    {
        var topology = await RequireTopologyAsync(cancel);
        if (rooms.Any(SonosRooms.IsAll)) return await PartyModeTool(caller, toRoom, cancel);
        var joiners = rooms.Where(r => !string.IsNullOrWhiteSpace(r))
            .Select(r => SonosRooms.Match(topology, r) ?? throw SonosRooms.NotFound(topology, r))
            .DistinctBy(p => p.Uuid).ToList();
        if (joiners.Count == 0) throw new PluginException($"Which rooms should I group? Rooms: {topology.RoomList}.");
        await EnsureFreshStatesAsync(topology, cancel);
        var target = ChooseGroupTarget(topology, joiners, toRoom, caller);
        var moving = joiners.Where(j => topology.GroupOf(target.Uuid)?.Contains(j.Uuid) != true).ToList();
        if (moving.Count == 0)
            return ToolResult.Ok($"{SonosText.Join(joiners.Select(j => j.RoomName))} already {(joiners.Count == 1 ? "plays" : "play")} with {target.RoomName}.");

        var name = await RunAsync(target.RoomName, async t =>
        {
            var coordinator = GroupFor(t, target).Coordinator;
            foreach (var j in moving) await SetUriAsync(Fresh(t, j), SonosUris.JoinGroupUri(coordinator.Uuid), "", cancel);
            var fresh = await RefreshTopologyAsync(TimeSpan.Zero, cancel);
            return fresh.GroupOf(target.Uuid)?.Name ?? target.RoomName;
        }, cancel);
        return ToolResult.Ok($"Grouped: {name}.", SonosText.Activity($"Grouped {name}"));
    }

    /// <summary>The room the others join: to_room; else the puck's room (unless it's moving); else a playing room.</summary>
    SonosPlayer ChooseGroupTarget(SonosTopology topology, IReadOnlyList<SonosPlayer> joiners, string? toRoom, ToolCaller caller)
    {
        if (!string.IsNullOrWhiteSpace(toRoom)) return SonosRooms.Match(topology, toRoom) ?? throw SonosRooms.NotFound(topology, toRoom);
        var here = SonosRooms.Implicit(topology, caller.Room, Settings.DefaultRoom);
        if (here is not null && joiners.All(j => j.Uuid != here.Uuid)) return here;
        if (joiners.Count >= 2)
            return joiners.FirstOrDefault(j => topology.GroupOf(j.Uuid) is { } g && IsPlaying(g)) ?? joiners[0];
        var playing = topology.Groups.Where(g => IsPlaying(g) && !g.Contains(joiners[0].Uuid)).ToList();
        if (playing.Count == 1) return playing[0].Coordinator;
        throw new PluginException($"Which room should {joiners[0].RoomName} join? Rooms: {topology.RoomList}.");
    }

    [Tool("sonos_ungroup", "Take a Sonos room out of its group so it plays on its own. Room defaults to the puck's room; \"all\" splits every group.")]
    public async Task<ToolResult> UngroupTool(ToolCaller caller, [Param("Room, or \"all\"; default: the puck's room")] string? room = null, CancellationToken cancel = default)
    {
        var topology = await RequireTopologyAsync(cancel);
        bool all = SonosRooms.IsAll(room);
        List<SonosPlayer> leaving;
        if (all)
        {
            leaving = topology.Groups.Where(g => g.Members.Count > 1).SelectMany(g => g.Members.Skip(1)).ToList();
            if (leaving.Count == 0) return ToolResult.Ok("No rooms are grouped.");
        }
        else
        {
            var player = await ResolveRoomAsync(room, caller, cancel);
            if (GroupFor(topology, player).Members.Count == 1) return ToolResult.Ok($"{player.RoomName} is already playing on its own.");
            leaving = [player];
        }
        await RunAsync(all ? null : leaving[0].RoomName, async t =>
        {
            foreach (var p in leaving) await Av(Fresh(t, p), "BecomeCoordinatorOfStandaloneGroup", cancel, Instance);
            await RefreshTopologyAsync(TimeSpan.Zero, cancel);
            return true;
        }, cancel);
        return ToolResult.Ok(all ? "Every room now plays on its own." : $"{leaving[0].RoomName} now plays on its own.", "Ungrouped");
    }

    [Tool("sonos_party_mode", "Put every Sonos room into one group playing the same music: that of room, else whatever is playing.")]
    public async Task<ToolResult> PartyModeTool(ToolCaller caller, [Param("Room whose music everyone plays; default: whatever is playing")] string? room = null, CancellationToken cancel = default)
    {
        var topology = await RequireTopologyAsync(cancel);
        await EnsureFreshStatesAsync(topology, cancel);
        SonosPlayer leader;
        if (!string.IsNullOrWhiteSpace(room))
        {
            leader = SonosRooms.Match(topology, room) ?? throw SonosRooms.NotFound(topology, room);
        }
        else
        {
            var here = SonosRooms.Implicit(topology, caller.Room, Settings.DefaultRoom);
            var hereGroup = here is null ? null : topology.GroupOf(here.Uuid);
            leader = hereGroup is not null && IsPlaying(hereGroup)
                ? hereGroup.Coordinator
                : topology.Groups.FirstOrDefault(IsPlaying)?.Coordinator ?? hereGroup?.Coordinator ?? topology.Groups[0].Coordinator;
        }
        var count = await RunAsync(leader.RoomName, async t =>
        {
            var group = GroupFor(t, leader);
            foreach (var p in t.Players.Where(p => !group.Contains(p.Uuid)))
                await SetUriAsync(p, SonosUris.JoinGroupUri(group.Coordinator.Uuid), "", cancel);
            var fresh = await RefreshTopologyAsync(TimeSpan.Zero, cancel);
            return fresh.GroupOf(leader.Uuid)?.Members.Count ?? t.Players.Count;
        }, cancel);
        return ToolResult.Ok($"Party mode: {count} rooms are playing what {leader.RoomName} plays.", "Party mode");
    }

    [Tool("sonos_favorites", "List the person's Sonos Favourites (playlists, albums and stations saved in the Sonos app).")]
    public async Task<string> FavoritesTool(CancellationToken cancel)
    {
        var favorites = await GetFavoritesAsync(cancel);
        if (favorites.Count == 0) return "There are no Sonos Favourites yet (they're added in the Sonos app).";
        return "Sonos Favourites: " + string.Join("; ", favorites.Select(f => f.Service is null ? f.Title : $"{f.Title} ({f.Service})")) + ".";
    }

    async Task<IReadOnlyList<SonosFavorite>> GetFavoritesAsync(CancellationToken cancel) =>
        await RunAsync<IReadOnlyList<SonosFavorite>>(null, async topology =>
        {
            var items = await BrowseAsync(topology.Groups[0].Coordinator, "FV:2", 300, cancel);
            LearnSpotifyFromItems(items);
            return items.Select(SonosFavorite.From).OfType<SonosFavorite>().ToList();
        }, cancel);

    [Tool("sonos_play_favorite", "Play one of the person's Sonos Favourites (a playlist, album or station saved in the Sonos app) in a room. Room defaults to the puck's room.")]
    public async Task<ToolResult> PlayFavoriteTool(ToolCaller caller,
        [Param("The favourite's name")] string name,
        [Param(RoomParam)] string? room = null,
        CancellationToken cancel = default)
    {
        var player = await ResolveRoomAsync(room, caller, cancel);
        var favorites = await GetFavoritesAsync(cancel);
        if (favorites.Count == 0) throw new PluginException("There are no Sonos Favourites yet. Add some in the Sonos app.");
        var favorite = NameMatcher.Best(name, favorites, f => f.Title)
                       ?? throw new PluginException($"I can't find a Sonos Favourite called '{name}'. Favourites: {string.Join(", ", favorites.Select(f => f.Title))}.");
        if (favorite.Uri is not { } uri)
            throw new PluginException($"Sonos doesn't let other apps start '{favorite.Title}'. Try another favourite.");

        var where = await RunAsync(player.RoomName, async topology =>
        {
            var group = GroupFor(topology, player);
            var c = group.Coordinator;
            try
            {
                if (favorite.IsContainer)
                {
                    await ReplaceQueueAndPlayAsync(c, uri, favorite.MetaData, cancel);
                }
                else
                {
                    await SetUriAsync(c, uri, favorite.MetaData, cancel);
                    await PlayAsync(c, cancel);
                }
            }
            catch (SonosUpnpException e) when (favorite.IsShortcut)
            {
                throw new PluginException($"Sonos couldn't start '{favorite.Title}': it's a Sonos app shortcut, which other apps can't always play.", e);
            }
            return group.Name;
        }, cancel);
        RefreshSoon(player);
        return ToolResult.Ok($"Playing {favorite.Title} in {where}.", SonosText.Activity(favorite.Title));
    }

    [Tool("sonos_play_spotify", "Play a Spotify URI or open.spotify.com link (track, album, playlist or artist) in a Sonos room through Sonos's own Spotify account. mode: replace (default: play now), next or end (add to the queue). Room defaults to the puck's room.")]
    public async Task<ToolResult> PlaySpotifyTool(ToolCaller caller,
        [Param("spotify:… URI or https://open.spotify.com/… link")] string uri,
        [Param(RoomParam)] string? room = null,
        [Param("replace (play now), next (play after this track) or end (add to the end of the queue)", Values = ["replace", "next", "end"])] string mode = "replace",
        CancellationToken cancel = default)
    {
        var player = await ResolveRoomAsync(room, caller, cancel);
        var text = await PlaySpotifyOnAsync(player, uri, mode, cancel);
        return ToolResult.Ok(text, SonosText.Activity(player.RoomName + (ParseQueueMode(mode) == "replace" ? " playing" : " queued")));
    }

    async Task<string> PlaySpotifyOnAsync(SonosPlayer player, string spotifyUri, string mode, CancellationToken cancel)
    {
        var link = SpotifyLink.Parse(spotifyUri)
                   ?? throw new PluginException($"'{spotifyUri}' isn't a Spotify link I understand (I need spotify:track:… or an open.spotify.com link).");
        if (!SonosSpotify.Supports(link)) throw new PluginException($"Sonos can play Spotify tracks, albums, playlists and artists, but not {link.Type}s.");
        var how = ParseQueueMode(mode);
        // Check we know the account before touching the queue, so a failure doesn't wipe it.
        var (sid, sn) = await GetSpotifyAccountAsync(player, needSn: link.Type == "track", cancel);
        var item = SonosSpotify.Build(link, sid, sn);
        var what = link.Type switch
        {
            "track" => "the track",
            "album" => "the album",
            "playlist" => "the playlist",
            _ => "the artist's top tracks",
        };
        var text = await RunAsync(player.RoomName, async topology =>
        {
            var group = GroupFor(topology, player);
            var c = group.Coordinator;
            if (how == "replace")
            {
                await ReplaceQueueAndPlayAsync(c, item.Uri, item.MetaData, cancel);
                return $"Playing {what} in {group.Name}.";
            }
            var media = await Av(c, "GetMediaInfo", cancel, Instance);
            bool onQueue = SonosUris.Classify(media.GetValueOrDefault("CurrentURI")) == SonosSource.Queue;
            int position = 0;
            if (how == "next" && onQueue)
            {
                var pos = await Av(c, "GetPositionInfo", cancel, Instance);
                if (int.TryParse(pos.GetValueOrDefault("Track"), out var current) && current > 0) position = current + 1;
            }
            await Av(c, "AddURIToQueue", cancel, Instance, ("EnqueuedURI", item.Uri), ("EnqueuedURIMetaData", item.MetaData),
                ("DesiredFirstTrackNumberEnqueued", position.ToString(Inv)), ("EnqueueAsNext", how == "next" ? "1" : "0"));
            if (!onQueue) return $"Added {what} to {group.Name}'s queue (it isn't playing from the queue right now).";
            return position > 0 ? $"Added {what} to {group.Name}'s queue, to play next." : $"Added {what} to the end of {group.Name}'s queue.";
        }, cancel);
        RefreshSoon(player);
        return text;
    }

    static string ParseQueueMode(string? mode) => mode?.Trim().ToLowerInvariant() switch
    {
        "next" or "play next" or "play_next" or "after" => "next",
        "end" or "add" or "queue" or "add to queue" or "last" or "append" => "end",
        _ => "replace",
    };

    [Tool("sonos_tv_or_line_in", "Switch a Sonos room to its TV (HDMI/optical) or line-in input. Room defaults to the puck's room.")]
    public async Task<ToolResult> TvOrLineInTool(ToolCaller caller,
        [Param(RoomParam)] string? room = null,
        [Param("tv or line_in; default: the TV if the room has one", Values = ["tv", "line_in"])] string? input = null,
        CancellationToken cancel = default)
    {
        var player = await ResolveRoomAsync(room, caller, cancel);
        SonosDeviceInfo? info;
        try
        {
            info = await GetDeviceInfoAsync(player, cancel);
        }
        catch (Exception e) when (e is HttpRequestException or XmlException || (e is OperationCanceledException && !cancel.IsCancellationRequested))
        {
            info = null;
        }
        if (info is null) throw new PluginException($"I couldn't read {player.RoomName}'s inputs. Is it switched on?");

        var wanted = input?.Trim().ToLowerInvariant().Replace('-', '_').Replace(' ', '_');
        bool lineIn = wanted is "line_in" or "linein" or "line" or "aux" or "analog" or "analogue";
        string uri, label;
        if (!lineIn && info.HasTv)
            (uri, label) = (SonosUris.TvUri(player.Uuid), "the TV");
        else if ((lineIn || wanted is null) && info.HasLineIn)
            (uri, label) = (SonosUris.LineInUri(player.Uuid), "line-in");
        else if (lineIn)
            throw new PluginException($"{player.RoomName} has no line-in input.");
        else
            throw new PluginException(wanted is null ? $"{player.RoomName} has no TV or line-in input." : $"{player.RoomName} has no TV input.");

        var name = await RunAsync(player.RoomName, async topology =>
        {
            var group = GroupFor(topology, player);
            await SetUriAsync(group.Coordinator, uri, "", cancel);
            try
            {
                await PlayAsync(group.Coordinator, cancel);
            }
            catch (SonosUpnpException) { } // the TV input usually starts by itself
            return group.Name;
        }, cancel);
        RefreshSoon(player);
        return ToolResult.Ok($"{name} is now playing {label}.", SonosText.Activity($"{player.RoomName} {(label == "the TV" ? "TV" : "line-in")}"));
    }

    [Tool("sonos_sleep_timer", "Stop a Sonos room's music after some minutes (0 cancels the sleep timer). Room defaults to the puck's room.")]
    public async Task<ToolResult> SleepTimerTool(ToolCaller caller,
        [Param("Minutes until it stops; 0 cancels", Min = 0, Max = 1439)] int minutes,
        [Param(RoomParam)] string? room = null,
        CancellationToken cancel = default)
    {
        minutes = Math.Clamp(minutes, 0, 1439);
        var player = await ResolveRoomAsync(room, caller, cancel);
        var name = await RunAsync(player.RoomName, async topology =>
        {
            var group = GroupFor(topology, player);
            var duration = minutes == 0 ? "" : SonosTime.SleepDuration(TimeSpan.FromMinutes(minutes));
            await Av(group.Coordinator, "ConfigureSleepTimer", cancel, Instance, ("NewSleepTimerDuration", duration));
            return group.Name;
        }, cancel);
        return minutes == 0
            ? ToolResult.Ok($"Sleep timer cancelled in {name}.", "Sleep timer off")
            : ToolResult.Ok($"{name} will stop in {minutes} minute{(minutes == 1 ? "" : "s")}.", SonosText.Activity($"Sleep in {minutes} min"));
    }

    [Tool("sonos_shuffle", "Turn shuffle on or off for a Sonos room's queue. Room defaults to the puck's room.")]
    public async Task<ToolResult> ShuffleTool(ToolCaller caller,
        [Param("true = shuffle on, false = off")] bool on = true,
        [Param(RoomParam)] string? room = null,
        CancellationToken cancel = default)
    {
        var name = await ChangePlayModeAsync(caller, room, current => (on, current.Repeat), cancel);
        return ToolResult.Ok($"Shuffle is {(on ? "on" : "off")} in {name}.", $"Shuffle {(on ? "on" : "off")}");
    }

    [Tool("sonos_repeat", "Set repeat for a Sonos room's queue: off, all, or one (repeat the current track). Room defaults to the puck's room.")]
    public async Task<ToolResult> RepeatTool(ToolCaller caller,
        [Param("off, all or one", Values = ["off", "all", "one"])] string mode,
        [Param(RoomParam)] string? room = null,
        CancellationToken cancel = default)
    {
        var repeat = mode.Trim().ToLowerInvariant() switch
        {
            "all" or "on" or "queue" or "context" or "playlist" => "all",
            "one" or "track" or "song" or "this" or "current" => "one",
            _ => "off",
        };
        var name = await ChangePlayModeAsync(caller, room, current => (current.Shuffle, repeat), cancel);
        var text = repeat switch
        {
            "all" => $"Repeating the queue in {name}.",
            "one" => $"Repeating this track in {name}.",
            _ => $"Repeat is off in {name}.",
        };
        return ToolResult.Ok(text, $"Repeat {repeat}");
    }

    async Task<string> ChangePlayModeAsync(ToolCaller caller, string? room, Func<(bool Shuffle, string Repeat), (bool Shuffle, string Repeat)> change, CancellationToken cancel)
    {
        var player = await ResolveRoomAsync(room, caller, cancel);
        return await RunAsync(player.RoomName, async topology =>
        {
            var group = GroupFor(topology, player);
            var settings = await Av(group.Coordinator, "GetTransportSettings", cancel, Instance);
            var (shuffle, repeat) = change(SonosPlayModes.Parse(settings.GetValueOrDefault("PlayMode")));
            await Av(group.Coordinator, "SetPlayMode", cancel, Instance, ("NewPlayMode", SonosPlayModes.Build(shuffle, repeat)));
            return group.Name;
        }, cancel);
    }
}
