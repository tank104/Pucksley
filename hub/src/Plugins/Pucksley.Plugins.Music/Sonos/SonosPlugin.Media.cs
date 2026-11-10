using Pucksley.Sdk;

namespace Pucksley.Plugins.Sonos;

// The puck's Home face (media sources), its volume dials (controls) and the per-turn context.
public sealed partial class SonosPlugin
{
    static string VolumeControlId(SonosPlayer player) => player.Uuid + ".vol";

    /// <summary>Rooms shown as dials: the configured ones (by name), or all.</summary>
    IReadOnlyList<SonosPlayer> DialPlayers(SonosTopology topology)
    {
        var wanted = Settings.DialRooms.Where(r => !string.IsNullOrWhiteSpace(r)).ToList();
        if (wanted.Count == 0) return topology.Players;
        return wanted.Select(r => SonosRooms.Match(topology, r)).OfType<SonosPlayer>().DistinctBy(p => p.Uuid).ToList();
    }

    public override IReadOnlyList<PuckControl> GetControls() =>
        DialPlayers(_topology).Select(p => new PuckControl
        {
            Id = VolumeControlId(p),
            Name = p.RoomName,
            Group = "Sonos",
            Kind = ControlKind.Number,
            Value = _volumes.TryGetValue(p.Uuid, out var v) ? v.Volume : 0,
            Min = 0,
            Max = 100,
            Step = 2,
            Unit = "%",
        }).ToList();

    public override async Task OnControlAsync(string controlId, ControlInput input, CancellationToken cancel)
    {
        if (!controlId.EndsWith(".vol", StringComparison.Ordinal) || input.Value is not double value) return;
        var topology = await RequireTopologyAsync(cancel);
        if (topology.Player(controlId[..^4]) is not { } player) return;
        await RunAsync(player.RoomName, t => SetVolumeAsync(Fresh(t, player), (int)Math.Round(value), cancel), cancel);
    }

    /// <summary>From the cache only: this runs before every turn with a ~1 s budget.</summary>
    public override Task<string?> GetContextAsync(ToolCaller caller, CancellationToken cancel)
    {
        var topology = _topology;
        return Task.FromResult<string?>(topology.Groups.Count == 0 ? null : SonosText.Context(topology, _states, _volumes));
    }

    // ---------------------------------------------------------------- IMediaSourceProvider

    /// <summary>One source per group, identified by its coordinator's uuid.</summary>
    public IReadOnlyList<MediaSourceInfo> GetMediaSources() =>
        _topology.Groups.Select(g => new MediaSourceInfo(g.Coordinator.Uuid, g.Name)).ToList();

    public async Task<NowPlaying?> GetNowPlayingAsync(string sourceId, CancellationToken cancel)
    {
        // Any room's uuid works: a puck following "Kitchen" keeps working when Kitchen joins a group.
        if (_topology.GroupOf(sourceId) is not { } group) return null;
        var state = _states.GetValueOrDefault(group.Coordinator.Uuid);
        if (state is null || DateTimeOffset.UtcNow - state.UpdatedAt > StateMaxAge)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            cts.CancelAfter(TimeSpan.FromSeconds(2));
            try
            {
                state = await PollGroupAsync(group, cts.Token);
            }
            catch (Exception e) when (e is HttpRequestException or SonosUpnpException ||
                                      (e is OperationCanceledException && !cancel.IsCancellationRequested)) { }
        }
        return BuildNowPlaying(group, sourceId, state);
    }

    NowPlaying BuildNowPlaying(SonosGroup group, string sourceId, SonosGroupState? state)
    {
        // Following the group (coordinator id): group volume. Following one room of it: that room's.
        var members = sourceId == group.Coordinator.Uuid ? group.Members : group.Members.Where(m => m.Uuid == sourceId).ToList();
        var volumes = members.Select(m => _volumes.TryGetValue(m.Uuid, out var v) ? v : (SonosVolume?)null).ToList();
        bool known = volumes.Count > 0 && volumes.All(v => v is not null);
        var track = state?.Track ?? SonosTrack.Empty;
        return new NowPlaying
        {
            Title = track.Title ?? state?.Source switch
            {
                SonosSource.Tv => "TV",
                SonosSource.LineIn => "Line-in",
                SonosSource.SpotifyConnect => "Spotify",
                SonosSource.AirPlay => "AirPlay",
                SonosSource.Radio => track.Station,
                _ => null,
            },
            Artist = track.Artist,
            Album = track.Album ?? (track.Title is null ? null : track.Station),
            Playing = state?.IsPlaying ?? false,
            Volume = known ? (int)Math.Round(volumes.Average(v => v!.Value.Volume)) : null,
            Muted = known ? volumes.All(v => v!.Value.Muted) : null,
            Source = group.Name,
        };
    }

    public async Task MediaCommandAsync(string sourceId, MediaCommand command, CancellationToken cancel)
    {
        var topology = await RequireTopologyAsync(cancel);
        var player = topology.Player(sourceId) ?? throw new PluginException("That Sonos room isn't on the network any more.");
        await RunAsync(player.RoomName, async t =>
        {
            var group = GroupFor(t, player);
            var c = group.Coordinator;
            IReadOnlyList<SonosPlayer> rooms = player.Uuid == c.Uuid ? group.Members : [Fresh(t, player)];
            switch (command.Action)
            {
                case MediaAction.Toggle:
                {
                    var state = _states.GetValueOrDefault(c.Uuid) ?? await PollGroupAsync(group, cancel);
                    if (!state.IsPlaying) await PlayAsync(c, cancel);
                    else if (state.Source is SonosSource.Tv or SonosSource.LineIn) await ToggleMuteAsync(rooms, cancel); // can't be paused
                    else await Av(c, "Pause", cancel, Instance);
                    break;
                }
                case MediaAction.Play:
                    await PlayAsync(c, cancel);
                    break;
                case MediaAction.Pause:
                    await Av(c, "Pause", cancel, Instance);
                    break;
                case MediaAction.Next:
                    await Av(c, "Next", cancel, Instance);
                    break;
                case MediaAction.Previous:
                    await Av(c, "Previous", cancel, Instance);
                    break;
                case MediaAction.SetVolume when command.Volume is int volume:
                    if (rooms.Count > 1) await SetGroupVolumeAsync(group, volume, cancel);
                    else await SetVolumeAsync(rooms[0], volume, cancel);
                    break;
                case MediaAction.ToggleMute:
                    await ToggleMuteAsync(rooms, cancel);
                    break;
            }
            return true;
        }, cancel);
        RefreshSoon(player);
    }

    async Task ToggleMuteAsync(IReadOnlyList<SonosPlayer> rooms, CancellationToken cancel)
    {
        bool mute = !rooms.All(r => _volumes.TryGetValue(r.Uuid, out var v) && v.Muted);
        await Task.WhenAll(rooms.Select(r => SetMuteAsync(r, mute, cancel)));
    }
}
