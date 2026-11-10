using Microsoft.Extensions.Logging;
using Pucksley.Sdk;

namespace Pucksley.Plugins.Sonos;

// Spoken announcements: snapshot what's playing, play a clip of the hub's voice, put everything back.
public sealed partial class SonosPlugin
{
    static readonly TimeSpan MaxClipLength = TimeSpan.FromSeconds(90);

    [Tool("sonos_announce", "Say something out loud on Sonos speakers; the music pauses for it and then carries on. rooms defaults to the puck's room; [\"all\"] means everywhere.")]
    public async Task<ToolResult> AnnounceTool(ToolCaller caller,
        [Param("What to say")] string text,
        [Param("Rooms, e.g. [\"Kitchen\", \"Lounge\"] or [\"all\"]; default: the puck's room")] string[]? rooms = null,
        CancellationToken cancel = default)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new PluginException("What should I announce?");
        var topology = await RequireTopologyAsync(cancel);
        var players = new List<SonosPlayer>();
        var named = (rooms ?? []).Where(r => !string.IsNullOrWhiteSpace(r)).ToList();
        if (named.Count == 0) players.Add(await ResolveRoomAsync(null, caller, cancel));
        else if (named.Any(SonosRooms.IsAll)) players.AddRange(topology.Players);
        else players.AddRange(named.Select(r => SonosRooms.Match(topology, r) ?? throw SonosRooms.NotFound(topology, r)));
        // A grouped room plays whatever its coordinator plays: announce once per group.
        var groups = players.Select(p => GroupFor(topology, p)).DistinctBy(g => g.Coordinator.Uuid).ToList();

        var wav = await Context.SynthesizeWavAsync(text, cancel);
        var length = ClipLength(wav);
        var clipUrl = Context.PublishClip(wav, "audio/wav", TimeSpan.FromMinutes(5)).ToString();
        var volume = Math.Clamp(Settings.AnnouncementVolume, 0, 100);

        var results = await Task.WhenAll(groups.Select(g => AnnounceInGroupAsync(g, clipUrl, length, volume, cancel)));
        var done = results.Where(r => r.Error is null).Select(r => r.Group).ToList();
        var failed = results.Where(r => r.Error is not null).ToList();
        if (done.Count == 0) throw new PluginException(failed.Count == 1 ? failed[0].Error! : string.Join(" ", failed.Select(f => $"{f.Group}: {f.Error}")));
        var message = $"Announced in {SonosText.Join(done)}.";
        if (failed.Count > 0) message += " " + string.Join(" ", failed.Select(f => $"{f.Group}: {f.Error}"));
        return ToolResult.Ok(message, "Announcement");
    }

    /// <summary>The length of a WAV clip (5 s if it can't be read), capped so a tool never hangs.</summary>
    static TimeSpan ClipLength(byte[] wav)
    {
        try
        {
            var duration = Wav.Decode(wav).Duration;
            return duration > MaxClipLength ? MaxClipLength : duration;
        }
        catch (FormatException)
        {
            return TimeSpan.FromSeconds(5);
        }
    }

    sealed record AnnounceSnapshot(string MediaUri, string MediaMetaData, int Track, TimeSpan? Position, bool WasPlaying,
        IReadOnlyDictionary<string, SonosVolume> Volumes);

    async Task<(string Group, string? Error)> AnnounceInGroupAsync(SonosGroup group, string clipUrl, TimeSpan length, int volume, CancellationToken cancel)
    {
        // One announcement at a time per group, so a second one doesn't snapshot the first.
        var gate = _announceLocks.GetOrAdd(group.Coordinator.Uuid, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancel);
        try
        {
            var c = group.Coordinator;
            AnnounceSnapshot snapshot;
            try
            {
                snapshot = await SnapshotAsync(group, cancel);
            }
            catch (Exception e) when (e is HttpRequestException or SonosUpnpException)
            {
                return (group.Name, Describe(e, group.Name));
            }

            string? error = null;
            try
            {
                foreach (var m in group.Members)
                {
                    if (snapshot.Volumes.TryGetValue(m.Uuid, out var v) && v.Muted) await SetMuteAsync(m, false, cancel);
                    await SetVolumeAsync(m, volume, cancel);
                }
                await SetUriAsync(c, clipUrl, "", cancel);
                await PlayAsync(c, cancel);
                await WaitForClipAsync(c, clipUrl, length, cancel);
            }
            catch (Exception e) when (e is HttpRequestException or SonosUpnpException)
            {
                error = Describe(e, group.Name);
            }
            catch (OperationCanceledException)
            {
                error = "Cancelled.";
            }
            finally
            {
                await RestoreAsync(group, snapshot); // even when cancelled: the music must come back
            }
            RefreshSoon(c);
            return (group.Name, error);
        }
        finally
        {
            gate.Release();
        }
    }

    static string Describe(Exception e, string where) => e is SonosUpnpException u
        ? u.FriendlyMessage
        : $"I couldn't reach the Sonos speaker in {where}.";

    async Task<AnnounceSnapshot> SnapshotAsync(SonosGroup group, CancellationToken cancel)
    {
        var c = group.Coordinator;
        var media = await Av(c, "GetMediaInfo", cancel, Instance);
        var position = await Av(c, "GetPositionInfo", cancel, Instance);
        var transport = await Av(c, "GetTransportInfo", cancel, Instance);
        var volumes = new Dictionary<string, SonosVolume>();
        foreach (var m in group.Members) volumes[m.Uuid] = await PollVolumeAsync(m, cancel);
        var uri = media.GetValueOrDefault("CurrentURI") ?? "";
        bool queue = SonosUris.Classify(uri) == SonosSource.Queue;
        return new AnnounceSnapshot(
            uri,
            media.GetValueOrDefault("CurrentURIMetaData") ?? "",
            queue && int.TryParse(position.GetValueOrDefault("Track"), out var track) ? track : 0,
            queue ? SonosTime.Parse(position.GetValueOrDefault("RelTime")) : null,
            transport.GetValueOrDefault("CurrentTransportState") is "PLAYING" or "TRANSITIONING",
            volumes);
    }

    /// <summary>Waits for the clip to finish: its length plus a little, then (if it started late) until it stops.</summary>
    async Task WaitForClipAsync(SonosPlayer coordinator, string clipUrl, TimeSpan length, CancellationToken cancel)
    {
        await Task.Delay(length + AnnouncementPadding, cancel);
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < until)
        {
            var transport = await Av(coordinator, "GetTransportInfo", cancel, Instance);
            if (transport.GetValueOrDefault("CurrentTransportState") is not ("PLAYING" or "TRANSITIONING")) return;
            var position = await Av(coordinator, "GetPositionInfo", cancel, Instance);
            if (position.GetValueOrDefault("TrackURI") != clipUrl) return;
            await Task.Delay(500, cancel);
        }
    }

    async Task RestoreAsync(SonosGroup group, AnnounceSnapshot s)
    {
        var c = group.Coordinator;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var cancel = cts.Token;

        async Task Step(string what, Func<Task> step)
        {
            try
            {
                await step();
            }
            catch (Exception e) when (e is HttpRequestException or SonosUpnpException or OperationCanceledException)
            {
                Log.LogInformation("Sonos: couldn't restore the {What} in {Group} after an announcement: {Error}", what, group.Name, e.Message);
            }
        }

        // Volume first, so the music doesn't come back at the announcement volume.
        foreach (var m in group.Members)
        {
            if (!s.Volumes.TryGetValue(m.Uuid, out var v)) continue;
            await Step("volume", () => SetVolumeAsync(m, v.Volume, cancel));
            if (v.Muted) await Step("mute", () => SetMuteAsync(m, true, cancel));
        }
        if (s.MediaUri.Length > 0)
        {
            await Step("music", () => SetUriAsync(c, s.MediaUri, s.MediaMetaData, cancel));
            if (s.Track > 0)
                await Step("track", () => Av(c, "Seek", cancel, Instance, ("Unit", "TRACK_NR"), ("Target", s.Track.ToString(Inv))));
            if (s.Position is { } position && position > TimeSpan.Zero)
                await Step("position", () => Av(c, "Seek", cancel, Instance, ("Unit", "REL_TIME"), ("Target", SonosTime.Format(position))));
        }
        if (s.WasPlaying) await Step("playback", () => PlayAsync(c, cancel));
    }
}
