using System.Collections.Concurrent;
using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using Pucksley.Plugins.Spotify;
using Pucksley.Sdk;

namespace Pucksley.Plugins.Sonos;

/// <summary>
/// Sonos speakers over their local UPnP API: discovery (SSDP + zone group topology), transport and
/// volume, grouping, favourites, Spotify through Sonos's own Spotify account, and spoken
/// announcements. Needs no settings. Tools are in SonosPlugin.Tools.cs, the puck's Home face and
/// dials in SonosPlugin.Media.cs, announcements in SonosPlugin.Announce.cs.
/// </summary>
public sealed partial class SonosPlugin : PuckPlugin<SonosSettings>, IMediaSourceProvider
{
    const string ZonePlayerTarget = "urn:schemas-upnp-org:device:ZonePlayer:1";
    static readonly TimeSpan TopologyInterval = TimeSpan.FromSeconds(60);
    static readonly TimeSpan EmptyTopologyRetry = TimeSpan.FromSeconds(15);
    static readonly TimeSpan StateMaxAge = TimeSpan.FromSeconds(15);
    static readonly (string, string) Instance = ("InstanceID", "0");
    static readonly (string, string) Master = ("Channel", "Master");
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    SonosSoapClient _soap = null!;
    SonosSoapClient _probe = null!;                            // shorter timeout, for finding a speaker that answers
    volatile SonosTopology _topology = SonosTopology.Empty;   // replaced whole, never mutated
    volatile string? _lastGoodHost;                            // the last speaker that told us the zone groups
    long _topologyTicks;                                       // when it was last read (UTC ticks)
    volatile bool _topologyStale = true;
    readonly SemaphoreSlim _topologyLock = new(1, 1);
    readonly ConcurrentDictionary<string, SonosGroupState> _states = new();   // by coordinator uuid
    readonly ConcurrentDictionary<string, SonosVolume> _volumes = new();       // by player uuid
    readonly ConcurrentDictionary<string, SonosDeviceInfo> _devices = new();   // by player uuid
    readonly ConcurrentDictionary<string, SemaphoreSlim> _announceLocks = new();
    readonly SpotifyAccount _spotify = new();
    CancellationTokenSource? _stop;
    Task? _loop;

    /// <summary>Tests: replaces SSDP discovery.</summary>
    internal Func<CancellationToken, Task<IReadOnlyList<string>>>? DiscoverOverride { get; set; }

    /// <summary>How often the background loop reads every group's state.</summary>
    internal TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Extra wait after an announcement's length before the music is put back.</summary>
    internal TimeSpan AnnouncementPadding { get; set; } = TimeSpan.FromSeconds(1.5);

    public override string Id => "sonos";
    public override string Name => "Sonos";
    public override string Description =>
        "Control Sonos speakers: play, pause, volume, grouping, favourites, Spotify on Sonos and spoken announcements. Finds the speakers by itself.";
    public override string Icon => "music";
    public override bool EnabledByDefault => true;

    public override string? Instructions =>
        "Sonos: when no room is named, use the room of the puck you're talking to (the tools default to it). " +
        "To play specific music on Sonos, prefer the Spotify tools when they're available (they can target Sonos rooms); " +
        "use sonos_play_favorite for the person's Sonos Favourites.";

    public override void Initialize(IPluginContext context)
    {
        base.Initialize(context);
        _soap = new SonosSoapClient(context.Http);
        _probe = new SonosSoapClient(context.Http) { Timeout = TimeSpan.FromSeconds(2) };
    }

    protected override void OnSettingsApplied() => _topologyStale = true; // the speaker IPs may have changed

    public override Task StartAsync(CancellationToken cancel)
    {
        SetStatus(StatusLevel.Ok, "Looking for speakers…");
        _topologyStale = true;
        var stop = new CancellationTokenSource();
        _stop = stop;
        _loop = Task.Run(() => BackgroundAsync(stop.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public override async Task StopAsync()
    {
        var stop = Interlocked.Exchange(ref _stop, null);
        if (stop is null) return;
        stop.Cancel();
        try
        {
            if (_loop is not null) await _loop.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (Exception e) when (e is OperationCanceledException or TimeoutException) { }
        stop.Dispose();
        _loop = null;
    }

    // ---------------------------------------------------------------- for the Spotify plugin

    /// <summary>The visible rooms (empty until the speakers have been found).</summary>
    public IReadOnlyList<string> RoomNames => _topology.Players.Select(p => p.RoomName).ToList();

    /// <summary>
    /// Plays a Spotify URI or open.spotify.com link (track, album, playlist or artist) in a room
    /// through Sonos's own Spotify account. <paramref name="mode"/>: replace (play now), next or end
    /// (add to the queue). Returns a sentence saying what happened.
    /// </summary>
    public async Task<string> PlaySpotifyAsync(string roomName, string spotifyUri, string mode, CancellationToken cancel)
    {
        var topology = await RequireTopologyAsync(cancel).ConfigureAwait(false);
        var player = SonosRooms.Match(topology, roomName) ?? throw SonosRooms.NotFound(topology, roomName);
        return await PlaySpotifyOnAsync(player, spotifyUri, mode, cancel).ConfigureAwait(false);
    }

    /// <summary>
    /// What a room is playing, from the cache (no network). <paramref name="roomName"/> null = the
    /// only group that's playing. Used by Spotify to "like" a song Sonos is playing.
    /// </summary>
    public SonosRoomTrack? GetCurrentTrack(string? roomName)
    {
        var topology = _topology;
        SonosGroup? group = SonosRooms.Match(topology, roomName, SonosRooms.ImplicitThreshold) is { } p
            ? topology.GroupOf(p.Uuid)
            : topology.Groups.Where(g => _states.GetValueOrDefault(g.Coordinator.Uuid)?.IsPlaying == true).Take(2).ToList() is [var only] ? only : null;
        if (group is null || _states.GetValueOrDefault(group.Coordinator.Uuid) is not { } state) return null;
        return new SonosRoomTrack(group.Name, state.Track.Title, state.Track.Artist, state.TrackUri, state.IsPlaying);
    }

    // ---------------------------------------------------------------- background loop

    async Task BackgroundAsync(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                var due = _topology.Players.Count == 0 ? EmptyTopologyRetry : TopologyInterval;
                if (_topologyStale || TopologyAge >= due) await RefreshTopologyAsync(TimeSpan.Zero, stop).ConfigureAwait(false);
                await PollAsync(stop).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                break;
            }
            catch (Exception e)
            {
                Log.LogDebug(e, "Sonos background refresh failed");
            }
            try
            {
                await Task.Delay(PollInterval, stop).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    TimeSpan TopologyAge => TimeSpan.FromTicks(DateTime.UtcNow.Ticks - Volatile.Read(ref _topologyTicks));

    // ---------------------------------------------------------------- topology

    /// <summary>The topology, discovering the speakers first if needed. Throws if there are none.</summary>
    async Task<SonosTopology> RequireTopologyAsync(CancellationToken cancel)
    {
        var topology = _topology;
        if (topology.Players.Count == 0 || _topologyStale)
            topology = await RefreshTopologyAsync(TimeSpan.FromSeconds(2), cancel).ConfigureAwait(false);
        if (topology.Players.Count == 0) throw new PluginException(SonosRooms.NoSpeakersMessage);
        return topology;
    }

    /// <summary>
    /// Re-reads the zone groups (unless that happened within <paramref name="maxAge"/>, or while we
    /// waited for another refresh). Tries the configured IPs and known players first, then SSDP.
    /// </summary>
    async Task<SonosTopology> RefreshTopologyAsync(TimeSpan maxAge, CancellationToken cancel)
    {
        long requested = DateTime.UtcNow.Ticks;
        await _topologyLock.WaitAsync(cancel).ConfigureAwait(false);
        try
        {
            if (!_topologyStale && _topology.Players.Count > 0 &&
                (Volatile.Read(ref _topologyTicks) >= requested || TopologyAge <= maxAge))
                return _topology;

            var tried = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var host in CandidateHosts())
                if (tried.Add(host) && await TryReadTopologyAsync(host, cancel).ConfigureAwait(false) is { } t)
                    return Apply(t);
            foreach (var host in await DiscoverHostsAsync(cancel).ConfigureAwait(false))
                if (tried.Add(host) && await TryReadTopologyAsync(host, cancel).ConfigureAwait(false) is { } t)
                    return Apply(t);

            // Nobody answered. Keep what we knew (speakers can be briefly unreachable) and retry later.
            Volatile.Write(ref _topologyTicks, DateTime.UtcNow.Ticks);
            _topologyStale = false;
            if (_topology.Players.Count == 0)
                SetStatus(StatusLevel.Warning, Settings.SpeakerIps.Count > 0
                    ? "No speakers answered at the IP addresses in settings"
                    : "No Sonos speakers found — if the hub runs in Docker, add their IP addresses");
            return _topology;
        }
        finally
        {
            _topologyLock.Release();
        }
    }

    IEnumerable<string> CandidateHosts()
    {
        if (_lastGoodHost is { } last) yield return last; // usually answers straight away
        foreach (var raw in Settings.SpeakerIps)
            if (NormalizeHost(raw) is { } host) yield return host;
        var topology = _topology;
        foreach (var g in topology.Groups) yield return g.Coordinator.Host;
        foreach (var p in topology.Players) yield return p.Host;
    }

    /// <summary>"192.168.1.40", "http://192.168.1.40:1400/…" or "host:port" → a host for <see cref="SonosSoapClient"/>.</summary>
    internal static string? NormalizeHost(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var s = raw.Trim();
        if (s.Contains("://", StringComparison.Ordinal)) return SonosTopology.HostFromLocation(s);
        return s.TrimEnd('/');
    }

    async Task<SonosTopology?> TryReadTopologyAsync(string host, CancellationToken cancel)
    {
        try
        {
            var r = await _probe.CallAsync(host, SonosService.ZoneGroupTopology, "GetZoneGroupState", [], cancel).ConfigureAwait(false);
            var topology = SonosTopology.ParseZoneGroupState(r.GetValueOrDefault("ZoneGroupState") ?? "");
            if (topology.Players.Count == 0) return null;
            _lastGoodHost = host;
            return topology;
        }
        catch (Exception e) when (e is HttpRequestException or SonosUpnpException or XmlException)
        {
            Log.LogDebug("Sonos: no zone groups from {Host}: {Error}", host, e.Message);
            return null;
        }
    }

    async Task<IReadOnlyList<string>> DiscoverHostsAsync(CancellationToken cancel)
    {
        if (DiscoverOverride is not null) return await DiscoverOverride(cancel).ConfigureAwait(false);
        try
        {
            var answers = await LanTools.SsdpSearchAsync(ZonePlayerTarget, TimeSpan.FromSeconds(3), cancel).ConfigureAwait(false);
            return answers.Select(a => NormalizeHost(a.Location)).OfType<string>().Distinct().ToList();
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Log.LogDebug(e, "Sonos SSDP discovery failed");
            return [];
        }
    }

    SonosTopology Apply(SonosTopology topology)
    {
        var old = _topology;
        _topology = topology;
        Volatile.Write(ref _topologyTicks, DateTime.UtcNow.Ticks);
        _topologyStale = false;
        // Always refresh the status: after a restart it says "Looking for speakers…" even if nothing changed.
        var rooms = topology.Players.Count;
        var status = new PluginStatus(StatusLevel.Ok, SonosText.Clip($"{rooms} room{(rooms == 1 ? "" : "s")}: {topology.RoomList}", 90));
        if (Status != status) SetStatus(status);
        if (old.Signature == topology.Signature) return topology;

        foreach (var uuid in _states.Keys)
            if (topology.Groups.All(g => g.Coordinator.Uuid != uuid)) _states.TryRemove(uuid, out _);
        Log.LogInformation("Sonos: {Groups}", string.Join("; ", topology.Groups.Select(g => $"{g.Name} [{string.Join(", ", g.Members.Select(m => m.Host))}]")));
        Context.ControlsChanged();
        foreach (var g in topology.Groups) NotifyNowPlaying(g);
        _ = Task.Run(() => FetchDeviceInfoAsync(topology));
        return topology;
    }

    async Task FetchDeviceInfoAsync(SonosTopology topology)
    {
        foreach (var p in topology.Players)
        {
            if (_devices.ContainsKey(p.Uuid)) continue;
            try
            {
                await GetDeviceInfoAsync(p, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                Log.LogDebug("Sonos: no device description from {Room}: {Error}", p.RoomName, e.Message);
            }
        }
    }

    /// <summary>The player's device_description.xml (cached): model, TV and line-in inputs.</summary>
    async Task<SonosDeviceInfo?> GetDeviceInfoAsync(SonosPlayer player, CancellationToken cancel)
    {
        if (_devices.TryGetValue(player.Uuid, out var known)) return known;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        cts.CancelAfter(TimeSpan.FromSeconds(4));
        var xml = await Context.Http.GetStringAsync(SonosSoapClient.BaseUrl(player.Host) + "/xml/device_description.xml", cts.Token).ConfigureAwait(false);
        var info = SonosDeviceInfo.Parse(xml);
        if (info is not null) _devices[player.Uuid] = info;
        return info;
    }

    // ---------------------------------------------------------------- state polling

    async Task PollAsync(CancellationToken cancel)
    {
        var topology = _topology;
        if (topology.Groups.Count == 0) return;
        var tasks = topology.Groups.Select(g => QuietlyAsync(() => PollGroupAsync(g, cancel), g.Name))
            .Concat(topology.Players.Select(p => QuietlyAsync(() => PollVolumeAsync(p, cancel), p.RoomName)));
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    /// <summary>Live state for every group, but give up after a few seconds and use what we have.</summary>
    async Task RefreshStatesAsync(CancellationToken cancel)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        cts.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            await PollAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested) { }
    }

    /// <summary>Makes sure cached states are recent enough to know which group is playing.</summary>
    async Task EnsureFreshStatesAsync(SonosTopology topology, CancellationToken cancel)
    {
        var now = DateTimeOffset.UtcNow;
        if (topology.Groups.All(g => _states.TryGetValue(g.Coordinator.Uuid, out var s) && now - s.UpdatedAt < StateMaxAge)) return;
        await RefreshStatesAsync(cancel).ConfigureAwait(false);
    }

    async Task QuietlyAsync(Func<Task> poll, string what)
    {
        try
        {
            await poll().ConfigureAwait(false);
        }
        catch (HttpRequestException e)
        {
            _topologyStale = true; // its address may have changed: rediscover on the next loop
            Log.LogDebug("Sonos: {What} didn't answer: {Error}", what, e.Message);
        }
        catch (SonosUpnpException e)
        {
            Log.LogDebug("Sonos: {What}: {Error}", what, e.Message);
        }
    }

    async Task<SonosGroupState> PollGroupAsync(SonosGroup group, CancellationToken cancel)
    {
        var c = group.Coordinator;
        var transport = await Av(c, "GetTransportInfo", cancel, Instance).ConfigureAwait(false);
        var position = await Av(c, "GetPositionInfo", cancel, Instance).ConfigureAwait(false);
        var media = await Av(c, "GetMediaInfo", cancel, Instance).ConfigureAwait(false);
        var state = SonosGroupState.From(c.Uuid, transport, position, media, DateTimeOffset.UtcNow);
        LearnSpotifyFromPlayback(position.GetValueOrDefault("TrackURI"), position.GetValueOrDefault("TrackMetaData"));
        var old = _states.GetValueOrDefault(c.Uuid);
        _states[c.Uuid] = state;
        if (!state.SameAs(old)) NotifyNowPlaying(group);
        return state;
    }

    async Task<SonosVolume> PollVolumeAsync(SonosPlayer player, CancellationToken cancel)
    {
        var v = await Rc(player, "GetVolume", cancel, Instance, Master).ConfigureAwait(false);
        var m = await Rc(player, "GetMute", cancel, Instance, Master).ConfigureAwait(false);
        int volume = int.TryParse(v.GetValueOrDefault("CurrentVolume"), NumberStyles.Integer, Inv, out var n) ? n : 0;
        bool muted = m.GetValueOrDefault("CurrentMute") == "1";
        NoteVolume(player, volume, muted);
        return new SonosVolume(volume, muted);
    }

    /// <summary>Records a player's volume/mute and tells the pucks when it changed.</summary>
    void NoteVolume(SonosPlayer player, int? volume, bool? muted)
    {
        SonosVolume old, updated;
        bool had;
        do
        {
            had = _volumes.TryGetValue(player.Uuid, out old);
            updated = new SonosVolume(volume ?? old.Volume, muted ?? old.Muted);
        }
        while (!(had ? _volumes.TryUpdate(player.Uuid, updated, old) : _volumes.TryAdd(player.Uuid, updated)));

        if ((!had || old.Volume != updated.Volume) && DialPlayers(_topology).Any(p => p.Uuid == player.Uuid))
            Context.ControlValueChanged(VolumeControlId(player), updated.Volume);
        if ((!had || old != updated) && _topology.GroupOf(player.Uuid) is { } group) NotifyNowPlaying(group);
    }

    void NotifyNowPlaying(SonosGroup group)
    {
        // Pucks may follow the group (coordinator id) or one of its rooms.
        foreach (var m in group.Members) Context.NowPlayingChanged(m.Uuid);
    }

    /// <summary>Reads a group's state again shortly after a command, so the pucks show the result.</summary>
    void RefreshSoon(SonosPlayer player)
    {
        _ = Task.Run(async () =>
        {
            await Task.Delay(400).ConfigureAwait(false);
            if (_topology.GroupOf(player.Uuid) is not { } group) return;
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await QuietlyAsync(() => PollGroupAsync(group, cts.Token), group.Name).ConfigureAwait(false);
            foreach (var m in group.Members) await QuietlyAsync(() => PollVolumeAsync(m, cts.Token), m.RoomName).ConfigureAwait(false);
        });
    }

    // ---------------------------------------------------------------- commands

    Task<IReadOnlyDictionary<string, string>> Call(SonosPlayer player, SonosService service, string action, CancellationToken cancel, params (string Name, string Value)[] args) =>
        _soap.CallAsync(player.Host, service, action, args, cancel);

    Task<IReadOnlyDictionary<string, string>> Av(SonosPlayer player, string action, CancellationToken cancel, params (string, string)[] args) =>
        Call(player, SonosService.AVTransport, action, cancel, args);

    Task<IReadOnlyDictionary<string, string>> Rc(SonosPlayer player, string action, CancellationToken cancel, params (string, string)[] args) =>
        Call(player, SonosService.RenderingControl, action, cancel, args);

    Task PlayAsync(SonosPlayer coordinator, CancellationToken cancel) => Av(coordinator, "Play", cancel, Instance, ("Speed", "1"));

    Task SetUriAsync(SonosPlayer coordinator, string uri, string metaData, CancellationToken cancel) =>
        Av(coordinator, "SetAVTransportURI", cancel, Instance, ("CurrentURI", uri), ("CurrentURIMetaData", metaData));

    async Task<int> SetVolumeAsync(SonosPlayer player, int volume, CancellationToken cancel)
    {
        volume = Math.Clamp(volume, 0, 100);
        await Rc(player, "SetVolume", cancel, Instance, Master, ("DesiredVolume", volume.ToString(Inv))).ConfigureAwait(false);
        NoteVolume(player, volume, null);
        return volume;
    }

    async Task SetMuteAsync(SonosPlayer player, bool muted, CancellationToken cancel)
    {
        await Rc(player, "SetMute", cancel, Instance, Master, ("DesiredMute", muted ? "1" : "0")).ConfigureAwait(false);
        NoteVolume(player, null, muted);
    }

    /// <summary>Sets a multi-room group's overall volume (Sonos keeps the rooms' balance).</summary>
    async Task SetGroupVolumeAsync(SonosGroup group, int volume, CancellationToken cancel)
    {
        var c = group.Coordinator;
        await Call(c, SonosService.GroupRenderingControl, "SnapshotGroupVolume", cancel, Instance).ConfigureAwait(false);
        await Call(c, SonosService.GroupRenderingControl, "SetGroupVolume", cancel, Instance, ("DesiredVolume", Math.Clamp(volume, 0, 100).ToString(Inv))).ConfigureAwait(false);
    }

    /// <summary>Clears the queue, adds the item, switches to the queue and plays: "play this now".</summary>
    async Task ReplaceQueueAndPlayAsync(SonosPlayer coordinator, string uri, string metaData, CancellationToken cancel)
    {
        await Av(coordinator, "RemoveAllTracksFromQueue", cancel, Instance).ConfigureAwait(false);
        await Av(coordinator, "AddURIToQueue", cancel, Instance, ("EnqueuedURI", uri), ("EnqueuedURIMetaData", metaData),
            ("DesiredFirstTrackNumberEnqueued", "0"), ("EnqueueAsNext", "0")).ConfigureAwait(false);
        await SetUriAsync(coordinator, SonosUris.QueueUri(coordinator.Uuid), "", cancel).ConfigureAwait(false);
        await PlayAsync(coordinator, cancel).ConfigureAwait(false);
    }

    async Task<IReadOnlyList<DidlItem>> BrowseAsync(SonosPlayer player, string objectId, int max, CancellationToken cancel)
    {
        var items = new List<DidlItem>();
        int start = 0;
        while (items.Count < max)
        {
            var r = await Call(player, SonosService.ContentDirectory, "Browse", cancel,
                ("ObjectID", objectId), ("BrowseFlag", "BrowseDirectChildren"),
                ("Filter", "dc:title,res,dc:creator,upnp:artist,upnp:album,upnp:albumArtURI"),
                ("StartingIndex", start.ToString(Inv)), ("RequestedCount", Math.Min(100, max - items.Count).ToString(Inv)),
                ("SortCriteria", "")).ConfigureAwait(false);
            var page = Didl.Parse(r.GetValueOrDefault("Result"));
            items.AddRange(page);
            int returned = int.TryParse(r.GetValueOrDefault("NumberReturned"), out var n) ? n : page.Count;
            int total = int.TryParse(r.GetValueOrDefault("TotalMatches"), out var t) ? t : 0;
            start += returned;
            if (returned == 0 || start >= total) break;
        }
        return items;
    }

    /// <summary>
    /// Runs a speaker operation. When a speaker can't be reached (its IP may have changed) or Sonos
    /// refuses because the grouping just changed, rediscovers once and tries again. UPnP faults and
    /// unreachable speakers become messages for the person.
    /// </summary>
    async Task<T> RunAsync<T>(string? where, Func<SonosTopology, Task<T>> operation, CancellationToken cancel)
    {
        var topology = await RequireTopologyAsync(cancel).ConfigureAwait(false);
        try
        {
            try
            {
                return await operation(topology).ConfigureAwait(false);
            }
            catch (Exception e) when (IsUnreachable(e, cancel) || e is SonosUpnpException { ErrorCode: 800 })
            {
                Log.LogDebug("Sonos: {Where}: {Error}; rediscovering", where ?? "command", e.Message);
                _topologyStale = true;
                var fresh = await RefreshTopologyAsync(TimeSpan.Zero, cancel).ConfigureAwait(false);
                // Only worth another try if something moved (a new address, a new coordinator).
                if (fresh.Signature == topology.Signature) throw;
                return await operation(fresh).ConfigureAwait(false);
            }
        }
        catch (SonosUpnpException e)
        {
            throw new PluginException(e.FriendlyMessage, e);
        }
        catch (Exception e) when (IsUnreachable(e, cancel))
        {
            throw new PluginException($"I couldn't reach the Sonos speaker{(where is null ? "" : $" in {where}")}. Is it switched on?", e);
        }
    }

    static bool IsUnreachable(Exception e, CancellationToken cancel) =>
        e is HttpRequestException || (e is OperationCanceledException && !cancel.IsCancellationRequested);

    /// <summary>The player's group in this (possibly fresher) topology.</summary>
    static SonosGroup GroupFor(SonosTopology topology, SonosPlayer player) =>
        topology.GroupOf(player.Uuid) ?? throw new PluginException($"{player.RoomName} isn't on the network any more. Rooms: {topology.RoomList}.");

    /// <summary>The same player in this (possibly fresher) topology: its address may have changed.</summary>
    static SonosPlayer Fresh(SonosTopology topology, SonosPlayer player) =>
        topology.Player(player.Uuid) ?? throw new PluginException($"{player.RoomName} isn't on the network any more. Rooms: {topology.RoomList}.");

    // ---------------------------------------------------------------- room resolution

    async Task<SonosPlayer> ResolveRoomAsync(string? room, ToolCaller caller, CancellationToken cancel)
    {
        var topology = await RequireTopologyAsync(cancel).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(room) && SonosRooms.Implicit(topology, caller.Room, Settings.DefaultRoom) is null)
            await EnsureFreshStatesAsync(topology, cancel).ConfigureAwait(false); // to find "the only group playing"
        return SonosRooms.Resolve(topology, room, caller.Room, Settings.DefaultRoom, IsPlaying);
    }

    /// <summary>Rooms for commands that accept "all" or a group name; otherwise the one room.</summary>
    async Task<IReadOnlyList<SonosPlayer>> ResolveRoomsAsync(string? room, ToolCaller caller, CancellationToken cancel)
    {
        var topology = await RequireTopologyAsync(cancel).ConfigureAwait(false);
        if (SonosRooms.IsAll(room)) return topology.Players;
        if (SonosRooms.MatchGroup(topology, room) is { } group) return group.Members;
        return [await ResolveRoomAsync(room, caller, cancel).ConfigureAwait(false)];
    }

    bool IsPlaying(SonosGroup group) => _states.GetValueOrDefault(group.Coordinator.Uuid)?.IsPlaying == true;

    // ---------------------------------------------------------------- Spotify account on Sonos

    const string SpotifySetupHint =
        "I don't know which Spotify account your Sonos uses yet. In the Sonos app, add any Spotify playlist to your Sonos Favourites once, then try again.";

    /// <summary>The Spotify (sid, sn) to use: settings, else learned from favourites, the queue or playback.</summary>
    async Task<(int Sid, int? Sn)> GetSpotifyAccountAsync(SonosPlayer player, bool needSn, CancellationToken cancel)
    {
        var (sid, sn) = CurrentSpotifyAccount();
        if (sid is null || (needSn && sn is null))
        {
            await LearnSpotifyAccountAsync(player, cancel).ConfigureAwait(false);
            (sid, sn) = CurrentSpotifyAccount();
        }
        if (sid is null)
        {
            await LearnRegionSpotifySidAsync(player, cancel).ConfigureAwait(false);
            (sid, sn) = CurrentSpotifyAccount();
        }
        if (sid is null || (needSn && sn is null)) throw new PluginException(SpotifySetupHint);
        return (sid.Value, sn);
    }

    (int? Sid, int? Sn) CurrentSpotifyAccount()
    {
        var (sid, sn) = _spotify.Get();
        return (Settings.SpotifySid > 0 ? Settings.SpotifySid : sid, Settings.SpotifySn > 0 ? Settings.SpotifySn : sn);
    }

    /// <summary>Looks for any Spotify item in the Sonos Favourites, then the queue: their URIs carry sid and sn.</summary>
    async Task LearnSpotifyAccountAsync(SonosPlayer player, CancellationToken cancel)
    {
        try
        {
            LearnSpotifyFromItems(await BrowseAsync(player, "FV:2", 300, cancel).ConfigureAwait(false));
            if (_spotify.HasAccount) return;
            var coordinator = _topology.GroupOf(player.Uuid)?.Coordinator ?? player;
            LearnSpotifyFromItems(await BrowseAsync(coordinator, "Q:0", 100, cancel).ConfigureAwait(false));
        }
        catch (Exception e) when (e is HttpRequestException or SonosUpnpException)
        {
            Log.LogDebug("Sonos: couldn't browse for a Spotify account: {Error}", e.Message);
        }
    }

    void LearnSpotifyFromItems(IEnumerable<DidlItem> items)
    {
        foreach (var item in items)
        {
            var (sid, sn) = SonosSpotify.AccountFromUri(item.Res);
            if (sid is null)
            {
                // Some favourites only name the service in their metadata (SA_RINCON3079_…).
                var inner = Didl.Parse(item.ResMetaData).FirstOrDefault() ?? item;
                if (inner.ServiceType is int type && inner.Id.Contains("spotify", StringComparison.OrdinalIgnoreCase))
                    sid = SonosSpotify.SidFromServiceType(type);
            }
            _spotify.Learn(sid, sn);
            if (_spotify.HasAccount) return;
        }
    }

    /// <summary>
    /// Playback started from the Sonos app's queue (<c>x-sonos-spotify:…?sid=&amp;sn=</c>) shows the
    /// real account. Spotify Connect sessions (<c>x-sonos-vli</c>) only tell us the service id.
    /// </summary>
    void LearnSpotifyFromPlayback(string? trackUri, string? trackMetaData)
    {
        if (string.IsNullOrEmpty(trackUri)) return;
        if (trackUri.StartsWith("x-sonos-spotify:", StringComparison.OrdinalIgnoreCase))
        {
            var (sid, sn) = SonosSpotify.AccountFromUri(trackUri);
            _spotify.Learn(sid, sn);
        }
        else if (trackUri.StartsWith("x-sonos-vli:", StringComparison.OrdinalIgnoreCase) &&
                 Didl.Parse(trackMetaData).FirstOrDefault()?.Res is { } res)
        {
            _spotify.LearnHint(SonosSpotify.AccountFromUri(res).Sid);
        }
    }

    /// <summary>The Spotify service id in this region, from the speaker's list of music services.</summary>
    async Task LearnRegionSpotifySidAsync(SonosPlayer player, CancellationToken cancel)
    {
        try
        {
            var r = await Call(player, SonosService.MusicServices, "ListAvailableServices", cancel).ConfigureAwait(false);
            var xml = r.GetValueOrDefault("AvailableServiceDescriptorList");
            if (string.IsNullOrWhiteSpace(xml)) return;
            var spotify = XDocument.Parse(xml).Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "Service" && string.Equals((string?)e.Attribute("Name"), "Spotify", StringComparison.OrdinalIgnoreCase));
            if (int.TryParse((string?)spotify?.Attribute("Id"), out var sid)) _spotify.LearnHint(sid);
        }
        catch (Exception e) when (e is HttpRequestException or SonosUpnpException or XmlException)
        {
            Log.LogDebug("Sonos: couldn't list music services: {Error}", e.Message);
        }
    }

    /// <summary>What we know about Sonos's Spotify account (thread-safe).</summary>
    sealed class SpotifyAccount
    {
        readonly object _gate = new();
        int? _sid, _sn, _hint;

        public bool HasAccount
        {
            get { lock (_gate) return _sid is not null && _sn is not null; }
        }

        /// <summary>From something the Sonos app saved: a favourite, a queue item, queue playback.</summary>
        public void Learn(int? sid, int? sn)
        {
            if (sid is null) return;
            lock (_gate)
            {
                if (sn is null && _sn is not null) return; // don't trade a full account for half of one
                _sid = sid;
                _sn = sn;
            }
        }

        /// <summary>A service id with no account (Spotify Connect playback, the region's service list).</summary>
        public void LearnHint(int? sid)
        {
            if (sid is null) return;
            lock (_gate) _hint ??= sid;
        }

        public (int? Sid, int? Sn) Get()
        {
            lock (_gate) return (_sid ?? _hint, _sn);
        }
    }
}

/// <summary>What a Sonos room is playing (from the cache).</summary>
/// <param name="TrackUri">e.g. <c>x-sonos-spotify:spotify%3atrack%3a…</c> when it's Spotify via Sonos.</param>
public sealed record SonosRoomTrack(string Room, string? Title, string? Artist, string? TrackUri, bool Playing);
