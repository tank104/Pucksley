using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Pucksley.Plugins.Sonos;
using Pucksley.Sdk;
using static Pucksley.Plugins.Spotify.SpotifyJson;

namespace Pucksley.Plugins.Spotify;

public sealed partial class SpotifyPlugin
{
    const string DeviceParam = "Where to play: a Spotify Connect device or Sonos room, e.g. Kitchen; default: the puck's room, else the active device";
    const string ControlDeviceParam = "Spotify Connect device; default: the one playing";

    /// <summary>How many Liked Songs to queue on a Sonos room (each is one request to the speaker).</summary>
    const int SonosLikedSongs = 25;

    static readonly string[] LikedSongsNames = ["liked songs", "liked", "my liked songs", "saved songs", "liked music", "my saved songs"];
    static readonly string[] MaybeLikedSongsNames = ["favourites", "favorites", "my favourites", "my favorites", "my songs", "my music", "my library"];

    /// <summary>Where to play: a Spotify Connect device, or a Sonos room played through the Sonos plugin.</summary>
    sealed record PlayTarget(SpotifyDevice? Device, string? SonosRoom)
    {
        public string Name => Device?.Name ?? SonosRoom ?? "";
    }

    [Tool("spotify_play", "Search Spotify and play the best match: an artist, album, track or playlist (or a Spotify link). type narrows the search; for a genre or mood use type playlist. device defaults to the puck's room, else the active device.")]
    public async Task<ToolResult> PlayTool(ToolCaller caller,
        [Param("What to play, e.g. \"Massive Attack\" or \"Teardrop by Massive Attack\"")] string query,
        [Param("track, album, artist or playlist", Values = ["track", "album", "artist", "playlist"])] string? type = null,
        [Param(DeviceParam)] string? device = null,
        CancellationToken cancel = default)
    {
        RequireConnected();
        if (string.IsNullOrWhiteSpace(query)) throw new PluginException("What should I play?");
        var target = await ResolvePlayTargetAsync(device, caller, cancel);
        SpotifyCandidate pick;
        if (SpotifyLink.Parse(query) is { } link)
        {
            pick = new SpotifyCandidate(link.Type, link.Uri, $"Spotify {link.Type}");
        }
        else
        {
            var wanted = NormalizeType(type);
            var json = await _api.GetAsync($"search?q={Uri.EscapeDataString(query)}&type={wanted ?? "track,album,artist,playlist"}&limit=5", cancel);
            pick = SpotifyPicker.Choose(query, wanted, SpotifyPicker.FromSearch(json))
                   ?? throw new PluginException($"Spotify found nothing for \"{query}\".");
        }
        var where = await PlayAsync(target, pick.Uri, pick.Type == "track", null, cancel);
        var what = SpotifyLink.Parse(query) is not null ? "that" : pick.Describe();
        return ToolResult.Ok($"Playing {what} on {where}.", Activity(pick.Name));
    }

    static string? NormalizeType(string? type) => type?.Trim().ToLowerInvariant() switch
    {
        "track" or "song" or "tracks" or "songs" => "track",
        "album" or "albums" or "record" => "album",
        "artist" or "artists" or "band" => "artist",
        "playlist" or "playlists" or "genre" or "mood" => "playlist",
        _ => null,
    };

    [Tool("spotify_play_playlist", "Play one of the person's own or followed Spotify playlists by name, or their Liked Songs (\"liked songs\", shuffled). device defaults to the puck's room, else the active device.")]
    public async Task<ToolResult> PlayPlaylistTool(ToolCaller caller,
        [Param("Playlist name, or \"liked songs\"")] string name,
        [Param(DeviceParam)] string? device = null,
        CancellationToken cancel = default)
    {
        RequireConnected();
        var target = await ResolvePlayTargetAsync(device, caller, cancel);
        // Exactly "liked songs" (give or take "my"): no need to look through their playlists.
        bool liked = NameMatcher.Best(name, LikedSongsNames, 0.97) is not null;
        IReadOnlyList<(string Name, string Uri)> playlists = [];
        (string Name, string Uri) match = default;
        if (!liked)
        {
            playlists = await GetMyPlaylistsAsync(cancel);
            // A playlist of theirs with that name wins ("Favourites", "Saved songs 2019"); then Liked Songs.
            match = NameMatcher.Best(name, playlists, p => p.Name, 0.85);
            if (match.Uri is null)
            {
                liked = NameMatcher.Best(name, LikedSongsNames.Concat(MaybeLikedSongsNames), 0.85) is not null;
                if (!liked) match = NameMatcher.Best(name, playlists, p => p.Name);
            }
        }

        if (liked)
        {
            var json = await _api.GetAsync("me/tracks?limit=50", cancel);
            var uris = Items(json?["items"]).Select(i => Str(i["track"]?["uri"])).OfType<string>()
                .Where(u => u.StartsWith("spotify:track:", StringComparison.Ordinal)).ToList();
            if (uris.Count == 0) throw new PluginException("Your Liked Songs is empty.");
            Random.Shared.Shuffle(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(uris));
            var whereLiked = await PlayAsync(target, uris[0], true, uris, cancel);
            return ToolResult.Ok($"Playing your Liked Songs, shuffled, on {whereLiked}.", "Liked Songs");
        }

        if (match.Uri is null) // Best returns default((string, string)) when nothing is close
        {
            var some = string.Join(", ", playlists.Take(20).Select(p => p.Name)) + (playlists.Count > 20 ? ", …" : "");
            throw new PluginException(playlists.Count == 0
                ? $"You don't have any saved playlists. To find a public playlist called \"{name}\", use spotify_play with type playlist."
                : $"I can't find a playlist called \"{name}\" in your library. Your playlists: {some}.");
        }
        var where = await PlayAsync(target, match.Uri, false, null, cancel);
        return ToolResult.Ok($"Playing your playlist {match.Name} on {where}.", Activity(match.Name ?? "Playlist"));
    }

    /// <summary>The person's own and followed playlists (up to 200).</summary>
    async Task<IReadOnlyList<(string Name, string Uri)>> GetMyPlaylistsAsync(CancellationToken cancel)
    {
        var list = new List<(string, string)>();
        for (int offset = 0; offset < 200; offset += 50)
        {
            var json = await _api.GetAsync($"me/playlists?limit=50&offset={offset}", cancel);
            foreach (var p in Items(json?["items"]))
                if (Str(p["name"]) is { } n && Str(p["uri"]) is { } u) list.Add((n, u));
            // Follow "next" (pages can contain null entries, so don't count items).
            if (json?["next"] is null || json["items"] is not JsonArray { Count: > 0 }) break;
        }
        return list;
    }

    [Tool("spotify_pause", "Pause Spotify (the device that's playing, or the one named).")]
    public async Task<ToolResult> PauseTool([Param(ControlDeviceParam)] string? device = null, CancellationToken cancel = default)
    {
        RequireConnected();
        await TransportAsync("pause", await DeviceIdAsync(device, cancel), cancel);
        RefreshSoon();
        return ToolResult.Ok("Paused Spotify.", "Spotify paused");
    }

    [Tool("spotify_resume", "Resume Spotify (on the device that was playing, or the one named).")]
    public async Task<ToolResult> ResumeTool([Param(ControlDeviceParam)] string? device = null, CancellationToken cancel = default)
    {
        RequireConnected();
        await TransportAsync("play", await DeviceIdAsync(device, cancel), cancel);
        RefreshSoon();
        return ToolResult.Ok("Resumed Spotify.", "Spotify playing");
    }

    [Tool("spotify_next", "Skip to the next track on Spotify.")]
    public async Task<ToolResult> NextTool([Param(ControlDeviceParam)] string? device = null, CancellationToken cancel = default)
    {
        RequireConnected();
        await TransportAsync("next", await DeviceIdAsync(device, cancel), cancel);
        var now = await NowPlayingAfterSkipAsync(cancel);
        return ToolResult.Ok(now is null ? "Skipped." : $"Skipped. Now playing {now}.", "Spotify: next");
    }

    [Tool("spotify_previous", "Go back to the previous track on Spotify.")]
    public async Task<ToolResult> PreviousTool([Param(ControlDeviceParam)] string? device = null, CancellationToken cancel = default)
    {
        RequireConnected();
        await TransportAsync("previous", await DeviceIdAsync(device, cancel), cancel);
        var now = await NowPlayingAfterSkipAsync(cancel);
        return ToolResult.Ok(now is null ? "Went back." : $"Went back. Now playing {now}.", "Spotify: previous");
    }

    async Task<string?> NowPlayingAfterSkipAsync(CancellationToken cancel)
    {
        try
        {
            await Task.Delay(500, cancel);
            return (await RefreshPlaybackAsync(cancel))?.Item?.Describe();
        }
        catch (PluginException)
        {
            return null;
        }
    }

    [Tool("spotify_volume", "Set Spotify's volume on the device that's playing (0-100), or change it by a relative amount.")]
    public async Task<ToolResult> VolumeTool(
        [Param("New volume, 0-100", Min = 0, Max = 100)] int? volume = null,
        [Param("Relative change instead, e.g. 10 or -10", Min = -100, Max = 100)] int? change = null,
        [Param(ControlDeviceParam)] string? device = null,
        CancellationToken cancel = default)
    {
        RequireConnected();
        if (volume is null && change is null) throw new PluginException("Give a volume (0-100) or how much to change it.");
        var deviceId = await DeviceIdAsync(device, cancel);
        int target;
        if (volume is int v) target = v;
        else
        {
            var playback = await RefreshPlaybackAsync(cancel);
            target = (playback?.Device?.Volume ?? 50) + change!.Value;
        }
        target = Math.Clamp(target, 0, 100);
        var r = await _api.SendRawAsync(HttpMethod.Put, $"me/player/volume?volume_percent={target}{DeviceQuery(deviceId, "&")}", null, cancel);
        if (!r.Ok) throw new PluginException(SpotifyApi.Describe(r));
        RefreshSoon();
        return ToolResult.Ok($"Spotify volume is now {target}.", $"Spotify {target}%");
    }

    [Tool("spotify_shuffle", "Turn Spotify shuffle on or off.")]
    public async Task<ToolResult> ShuffleTool([Param("true = on, false = off")] bool on = true, CancellationToken cancel = default)
    {
        RequireConnected();
        await _api.SendAsync(HttpMethod.Put, $"me/player/shuffle?state={(on ? "true" : "false")}", null, cancel);
        return ToolResult.Ok($"Shuffle is {(on ? "on" : "off")}.", $"Shuffle {(on ? "on" : "off")}");
    }

    [Tool("spotify_repeat", "Set Spotify repeat: off, track (this song) or context (the album or playlist).")]
    public async Task<ToolResult> RepeatTool([Param("off, track or context", Values = ["off", "track", "context"])] string mode, CancellationToken cancel = default)
    {
        RequireConnected();
        var state = mode.Trim().ToLowerInvariant() switch
        {
            "track" or "one" or "song" or "this" => "track",
            "context" or "all" or "on" or "album" or "playlist" => "context",
            _ => "off",
        };
        await _api.SendAsync(HttpMethod.Put, $"me/player/repeat?state={state}", null, cancel);
        var text = state switch { "track" => "Repeating this track.", "context" => "Repeating the album or playlist.", _ => "Repeat is off." };
        return ToolResult.Ok(text, $"Repeat {state}");
    }

    [Tool("spotify_queue", "Find a song on Spotify and add it to the queue, to play after the current track.")]
    public async Task<ToolResult> QueueTool(ToolCaller caller,
        [Param("The song, e.g. \"Teardrop by Massive Attack\" (or a Spotify link)")] string query,
        CancellationToken cancel = default)
    {
        RequireConnected();
        SpotifyCandidate pick;
        if (SpotifyLink.Parse(query) is { Type: "track" or "episode" } link)
        {
            pick = new SpotifyCandidate(link.Type, link.Uri, "that");
        }
        else
        {
            var json = await _api.GetAsync($"search?q={Uri.EscapeDataString(query)}&type=track&limit=5", cancel);
            pick = SpotifyPicker.Choose(query, "track", SpotifyPicker.FromSearch(json)) ?? throw new PluginException($"Spotify found no song for \"{query}\".");
        }
        var r = await _api.SendRawAsync(HttpMethod.Post, $"me/player/queue?uri={Uri.EscapeDataString(pick.Uri)}", null, cancel);
        if (r.Ok) return ToolResult.Ok($"Queued {pick.Describe()}.", Activity("Queued " + pick.Name));

        // Nothing on Spotify Connect: it may be Sonos playing Spotify from its own queue.
        if (r.Reason == "NO_ACTIVE_DEVICE" && Context.GetPlugin<SonosPlugin>() is { } sonos &&
            (NameMatcher.Best(caller.Room, sonos.RoomNames, 0.7) ?? NameMatcher.Best(Settings.DefaultDevice, sonos.RoomNames, 0.7)) is { } room)
        {
            var result = await sonos.PlaySpotifyAsync(room, pick.Uri, "next", cancel);
            return ToolResult.Ok($"{pick.Describe()}: {result}", Activity("Queued " + pick.Name));
        }
        throw new PluginException(SpotifyApi.Describe(r));
    }

    [Tool("spotify_now_playing", "What's playing on Spotify: track, artists, album, device, progress.")]
    public async Task<string> NowPlayingTool(CancellationToken cancel = default)
    {
        RequireConnected();
        var playback = await RefreshPlaybackAsync(cancel);
        if (playback?.Item is not { } item) return "Nothing is playing on Spotify.";
        var text = $"{(playback.IsPlaying ? "Playing" : "Paused")}: {item.Describe()}";
        if (item.Album is not null) text += $", from {item.Album}";
        if (playback.Device is { } device) text += $", on {device.Name}";
        if (item.DurationMs > 0) text += $" ({Time(playback.ProgressMs)} of {Time(item.DurationMs)})";
        if (playback.Device?.Volume is int volume) text += $". Volume {volume}";
        if (playback.Shuffle) text += ", shuffle on";
        if (playback.Repeat != "off") text += $", repeat {playback.Repeat}";
        return text + ".";
    }

    static string Time(int ms)
    {
        var t = TimeSpan.FromMilliseconds(ms);
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:D2}:{t.Seconds:D2}" : $"{t.Minutes}:{t.Seconds:D2}";
    }

    [Tool("spotify_like_current", "Save the song that's playing to the person's Spotify Liked Songs.")]
    public async Task<ToolResult> LikeCurrentTool(ToolCaller caller, CancellationToken cancel = default)
    {
        RequireConnected();
        var r = await _api.SendRawAsync(HttpMethod.Get, "me/player/currently-playing", null, cancel);
        if (!r.Ok) throw new PluginException(SpotifyApi.Describe(r));
        string? id = null, description = null;
        if (SpotifyItem.Parse(r.Json?["item"]) is { Type: "track", Id: { } trackId } item)
        {
            id = trackId;
            description = item.Describe();
        }
        else if (Context.GetPlugin<SonosPlugin>()?.GetCurrentTrack(caller.Room) is { } sonosTrack &&
                 SpotifyLink.Parse(sonosTrack.TrackUri) is { Type: "track" } link)
        {
            // Sonos playing Spotify from its own queue (Spotify Connect doesn't see that).
            id = link.Id;
            description = sonosTrack.Title is null ? "that song" : sonosTrack.Artist is null ? $"\"{sonosTrack.Title}\"" : $"\"{sonosTrack.Title}\" by {sonosTrack.Artist}";
        }
        if (id is null) throw new PluginException("No Spotify song is playing right now.");
        await _api.SendAsync(HttpMethod.Put, $"me/tracks?ids={Uri.EscapeDataString(id)}", null, cancel);
        return ToolResult.Ok($"Saved {description} to your Liked Songs.", "Liked");
    }

    [Tool("spotify_recent", "The last few songs played on Spotify (for \"what was that song?\").")]
    public async Task<string> RecentTool(CancellationToken cancel = default)
    {
        RequireConnected();
        var json = await _api.GetAsync("me/player/recently-played?limit=5", cancel);
        var now = DateTimeOffset.UtcNow;
        var lines = Items(json?["items"]).Select(i =>
        {
            var item = SpotifyItem.Parse(i["track"]);
            if (item is null) return null;
            var ago = DateTimeOffset.TryParse(Str(i["played_at"]), out var at) ? $" ({Ago(now - at)})" : "";
            return item.Describe() + ago;
        }).OfType<string>().ToList();
        if (lines.Count == 0) return "Spotify has no recently played songs.";
        return "Recently played, newest first: " + string.Join("; ", lines) + ".";
    }

    static string Ago(TimeSpan t) => t.TotalMinutes < 1 ? "just now"
        : t.TotalMinutes < 60 ? $"{(int)t.TotalMinutes} min ago"
        : t.TotalHours < 24 ? $"{(int)t.TotalHours} h ago"
        : $"{(int)t.TotalDays} days ago";

    [Tool("spotify_devices", "List the Spotify Connect devices (and Sonos rooms Spotify can play on).")]
    public async Task<string> DevicesTool(CancellationToken cancel = default)
    {
        RequireConnected();
        var devices = await GetDevicesAsync(cancel);
        var sonosRooms = Context.GetPlugin<SonosPlugin>()?.RoomNames ?? [];
        var parts = devices.Select(d =>
        {
            var notes = new List<string> { d.Type.ToLowerInvariant() };
            if (d.IsActive) notes.Add("active");
            if (d.Volume is int v) notes.Add($"vol {v}");
            if (d.IsRestricted) notes.Add("can't be controlled");
            return $"{d.Name} ({string.Join(", ", notes)})";
        }).ToList();
        var text = parts.Count == 0 ? "No Spotify Connect devices are online." : "Spotify devices: " + string.Join("; ", parts) + ".";
        var extra = sonosRooms.Where(r => devices.All(d => NameMatcher.Score(r, d.Name) < 0.97)).ToList();
        if (extra.Count > 0) text += $" Sonos rooms (played through Sonos): {string.Join(", ", extra)}.";
        return text;
    }

    [Tool("spotify_transfer", "Move Spotify playback to another device or Sonos room, e.g. \"play this in the kitchen\".")]
    public async Task<ToolResult> TransferTool(ToolCaller caller,
        [Param("The device or Sonos room to move to")] string device,
        CancellationToken cancel = default)
    {
        RequireConnected();
        var target = await ResolvePlayTargetAsync(device, caller, cancel);
        if (target.Device is { } d)
        {
            await _api.SendAsync(HttpMethod.Put, "me/player", new JsonObject { ["device_ids"] = new JsonArray(d.Id), ["play"] = true }, cancel);
            RefreshSoon();
            return ToolResult.Ok($"Moved Spotify to {d.Name}.", Activity($"Spotify on {d.Name}"));
        }

        // A Sonos room that isn't a Connect device: start the same album/playlist (or song) there, then stop the old device.
        var playback = await RefreshPlaybackAsync(cancel);
        if (playback?.Item is not { } item) throw new PluginException("Nothing is playing on Spotify to move.");
        var context = SpotifyLink.Parse(playback.ContextUri);
        bool useContext = context is { Type: "album" or "playlist" or "artist" };
        var uri = useContext ? context!.Uri : item.Uri ?? throw new PluginException("That can't be moved to Sonos.");
        await PlayAsync(target, uri, !useContext, null, cancel);
        if (playback.IsPlaying && playback.Device is { Id.Length: > 0 } old)
        {
            var paused = await _api.SendRawAsync(HttpMethod.Put, $"me/player/pause?device_id={Uri.EscapeDataString(old.Id)}", null, cancel);
            if (!paused.Ok) Log.LogDebug("Spotify: couldn't pause {Device} after moving: {Error}", old.Name, SpotifyApi.Describe(paused));
        }
        var note = useContext ? $"(from the start of the {context!.Type})" : "";
        return ToolResult.Ok($"Moved to {target.Name} through Sonos{(note.Length > 0 ? " " + note : "")}: {item.Describe()}.", Activity($"Spotify on {target.Name}"));
    }

    // ---------------------------------------------------------------- devices

    async Task<IReadOnlyList<SpotifyDevice>> GetDevicesAsync(CancellationToken cancel)
    {
        var json = await _api.GetAsync("me/player/devices", cancel);
        return Items(json?["devices"]).Select(d => SpotifyDevice.Parse(d)).OfType<SpotifyDevice>().ToList();
    }

    /// <summary>
    /// The device named (a Connect device first, else a Sonos room); with no name: the puck's
    /// room, then the default device, then the active device. Sonos rooms that aren't Connect
    /// devices are played through the Sonos plugin.
    /// </summary>
    async Task<PlayTarget> ResolvePlayTargetAsync(string? device, ToolCaller caller, CancellationToken cancel)
    {
        var devices = await GetDevicesAsync(cancel);
        var sonos = Context.GetPlugin<SonosPlugin>();
        IReadOnlyList<string> sonosRooms = sonos?.RoomNames ?? [];

        PlayTarget? Match(string? name, double threshold)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            // Restricted devices refuse Web API commands; a Sonos room of the same name can still play.
            if (NameMatcher.Best(name, devices.Where(d => !d.IsRestricted), d => d.Name, threshold) is { } d) return new PlayTarget(d, null);
            if (sonos is not null && NameMatcher.Best(name, sonosRooms, threshold) is { } room) return new PlayTarget(null, room);
            return null;
        }

        if (!string.IsNullOrWhiteSpace(device))
        {
            if (Match(device, 0.55) is { } named) return named;
            // Sonos may still be discovering its speakers: let it try the name itself.
            if (sonos is not null && sonosRooms.Count == 0) return new PlayTarget(null, device);
            throw new PluginException($"I can't find \"{device}\". {Places(devices, sonosRooms)}");
        }
        var active = devices.FirstOrDefault(d => d.IsActive && !d.IsRestricted);
        return Match(caller.Room, 0.7)
               ?? Match(Settings.DefaultDevice, 0.7)
               ?? (active is not null ? new PlayTarget(active, null) : null)
               ?? throw new PluginException(devices.Count + sonosRooms.Count == 0
                   ? "No Spotify devices are available. Open Spotify on a phone, computer or speaker first."
                   : $"Where should I play it? {Places(devices, sonosRooms)}");
    }

    static string Places(IReadOnlyList<SpotifyDevice> devices, IReadOnlyList<string> sonosRooms)
    {
        var parts = new List<string>();
        if (devices.Count > 0) parts.Add("Spotify devices: " + string.Join(", ", devices.Select(d => d.Name)) + ".");
        if (sonosRooms.Count > 0) parts.Add("Sonos rooms: " + string.Join(", ", sonosRooms) + ".");
        return parts.Count == 0 ? "No Spotify devices are online." : string.Join(" ", parts);
    }

    /// <summary>The id of a named Connect device for transport commands (null = the active one).</summary>
    async Task<string?> DeviceIdAsync(string? device, CancellationToken cancel)
    {
        if (string.IsNullOrWhiteSpace(device)) return null;
        var devices = await GetDevicesAsync(cancel);
        if (NameMatcher.Best(device, devices, d => d.Name) is { } d) return d.Id;
        var sonosRooms = Context.GetPlugin<SonosPlugin>()?.RoomNames ?? [];
        if (NameMatcher.Best(device, sonosRooms) is { } room)
            throw new PluginException($"{room} isn't playing through Spotify Connect; use the Sonos controls for it.");
        throw new PluginException($"I can't find \"{device}\". {Places(devices, sonosRooms)}");
    }

    static string DeviceQuery(string? deviceId, string separator) =>
        deviceId is null ? "" : $"{separator}device_id={Uri.EscapeDataString(deviceId)}";

    /// <summary>
    /// Plays a track (or several <paramref name="uris"/>) or a context URI on the target. Wakes a
    /// sleeping Connect device first if needed; falls back to Sonos when Spotify can't reach a
    /// speaker that is also a Sonos room. Returns where it's playing.
    /// </summary>
    async Task<string> PlayAsync(PlayTarget target, string uri, bool isTrack, IReadOnlyList<string>? uris, CancellationToken cancel)
    {
        if (target.SonosRoom is { } room) return await PlayOnSonosAsync(room, uri, uris, cancel);

        var device = target.Device!;
        JsonObject body = uris is { Count: > 0 }
            ? new JsonObject { ["uris"] = new JsonArray(uris.Select(u => (JsonNode?)JsonValue.Create(u)).ToArray()) }
            : isTrack ? new JsonObject { ["uris"] = new JsonArray(uri) } : new JsonObject { ["context_uri"] = uri };
        var path = $"me/player/play?device_id={Uri.EscapeDataString(device.Id)}";
        var r = await _api.SendRawAsync(HttpMethod.Put, path, body, cancel);
        if (r.Status == 404 && r.Reason != "PREMIUM_REQUIRED")
        {
            // A device that's gone to sleep often needs a transfer before it takes a play command.
            var transfer = await _api.SendRawAsync(HttpMethod.Put, "me/player", new JsonObject { ["device_ids"] = new JsonArray(device.Id), ["play"] = false }, cancel);
            if (transfer.Ok)
            {
                await Task.Delay(TransferSettle, cancel);
                r = await _api.SendRawAsync(HttpMethod.Put, path, body, cancel);
            }
        }
        if (!r.Ok)
        {
            if (r.Status is 403 or 404 && r.Reason != "PREMIUM_REQUIRED" && Context.GetPlugin<SonosPlugin>() is { } sonos &&
                NameMatcher.Best(device.Name, sonos.RoomNames, 0.9) is { } sonosRoom)
                return await PlayOnSonosAsync(sonosRoom, uri, uris, cancel);
            throw new PluginException(SpotifyApi.Describe(r));
        }
        RefreshSoon();
        return device.Name;
    }

    async Task<string> PlayOnSonosAsync(string room, string uri, IReadOnlyList<string>? uris, CancellationToken cancel)
    {
        var sonos = Context.GetPlugin<SonosPlugin>() ?? throw new PluginException("The Sonos plugin isn't enabled.");
        await sonos.PlaySpotifyAsync(room, uris is { Count: > 0 } ? uris[0] : uri, "replace", cancel);
        if (uris is { Count: > 1 })
        {
            // The rest go on the queue in the background: each is a request to the speaker.
            var rest = uris.Skip(1).Take(SonosLikedSongs - 1).ToList();
            _ = Task.Run(async () =>
            {
                foreach (var u in rest)
                {
                    try
                    {
                        await sonos.PlaySpotifyAsync(room, u, "end", CancellationToken.None);
                    }
                    catch (PluginException e)
                    {
                        Log.LogDebug("Spotify: couldn't queue {Uri} on Sonos: {Error}", u, e.Message);
                        break;
                    }
                }
            }, CancellationToken.None);
        }
        return room;
    }

    static string Activity(string text) => text.Length <= 24 ? text : text[..23] + "…";
}
