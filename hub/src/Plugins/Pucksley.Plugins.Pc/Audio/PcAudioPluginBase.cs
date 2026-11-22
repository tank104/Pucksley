using Microsoft.Extensions.Logging;
using Pucksley.Plugins.Pc.Common;
using Pucksley.Sdk;

namespace Pucksley.Plugins.Pc.Audio;

public sealed class PcAudioSettings
{
    [Setting("Name on the puck", Placeholder = "This PC", Help = "What the puck's Home face calls this PC when it shows what's playing here.")]
    public string SourceName { get; set; } = "This PC";
}

/// <summary>
/// This PC's sound: master volume and mute (of the default output), what's playing (the operating
/// system's media sessions), play/pause/skip, which output is used, and per-app volume. Also a
/// "PC volume" dial and a media source for the puck's Home face.
/// </summary>
/// <remarks>
/// The same on every operating system: each one derives a plugin from this and hands it its mixer
/// (<see cref="IPcAudio"/>) and its media sessions (<see cref="IPcMedia"/>).
/// </remarks>
public abstract class PcAudioPluginBase : PuckPlugin<PcAudioSettings>, IMediaSourceProvider
{
    internal const string SourceId = "pc";
    internal const string VolumeControlId = "volume";
    static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(5);
    static readonly TimeSpan NotifyDebounce = TimeSpan.FromMilliseconds(100);

    readonly string _os;            // "Windows", "Linux": for messages and Claude's instructions
    readonly string _mixer;         // "Windows audio": what a slow or failing call is blamed on
    readonly string _mediaSessions; // "Windows media sessions", "media players (MPRIS)"
    readonly Func<ILogger, IPcAudio> _createAudio;
    readonly Func<ILogger, IPcMedia> _createMedia;
    readonly object _gate = new();
    IPcAudio? _audio;
    IPcMedia? _media;
    Timer? _notifyTimer;
    MasterVolume? _master; // latest known: serves controls and context without touching the mixer
    MasterVolume? _shown;  // what the pucks were last told

    /// <param name="os">The operating system's name, e.g. "Windows".</param>
    /// <param name="mediaSessions">What the system calls its media sessions, as a plural, e.g. "Windows media sessions".</param>
    internal PcAudioPluginBase(string os, string mediaSessions, Func<ILogger, IPcAudio> createAudio, Func<ILogger, IPcMedia> createMedia)
    {
        _os = os;
        _mixer = $"{os} audio";
        _mediaSessions = mediaSessions;
        _createAudio = createAudio;
        _createMedia = createMedia;
    }

    public override string Id => "pc-audio";
    public override string Name => "PC audio";
    public override string Description => "This PC's volume, what's playing on it, which speakers or headphones it uses, and each app's volume.";
    public override string Icon => "volume";
    public override bool EnabledByDefault => true;

    public override string? Instructions =>
        $"pc_volume, pc_media and the other PC audio tools act on the {_os} PC the hub runs on (\"the PC\", \"the computer\"), " +
        "not on speakers in rooms. For one app's loudness (\"turn Spotify down\") use pc_app_volume.";

    string SourceName => string.IsNullOrWhiteSpace(Settings.SourceName) ? "This PC" : Settings.SourceName.Trim();

    // ───────────────────────── Lifecycle

    public override async Task StartAsync(CancellationToken cancel)
    {
        var audio = _createAudio(Log);
        var media = _createMedia(Log);
        lock (_gate)
        {
            _audio = audio;
            _media = media;
        }
        audio.Changed += OnAudioChanged;
        media.Changed += OnMediaChanged;

        string? problem = null;
        MasterVolume? master = null;
        try
        {
            await Blocking.RunAsync(audio.Start, TimeSpan.FromSeconds(10), _mixer, cancel).ConfigureAwait(false);
            master = await Blocking.RunAsync(audio.GetMaster, CallTimeout, _mixer, cancel).ConfigureAwait(false);
        }
        catch (Exception e) when (!cancel.IsCancellationRequested)
        {
            Log.LogWarning(e, "Couldn't start {Mixer}", _mixer);
            problem = $"{_mixer} isn't responding";
        }
        lock (_gate)
        {
            _master = master;
            _shown = master; // pucks read the controls when they connect
        }
        try
        {
            await media.StartAsync(cancel).ConfigureAwait(false);
        }
        catch (Exception e) when (!cancel.IsCancellationRequested)
        {
            Log.LogWarning(e, "{MediaSessions} are unavailable", Capitalize(_mediaSessions));
            problem ??= $"Can't see what's playing ({_mediaSessions} unavailable)";
        }

        if (problem is not null) SetStatus(StatusLevel.Warning, problem);
        else if (master is null) SetStatus(StatusLevel.Warning, "No audio output found");
        else SetStatus(StatusLevel.Ok, $"Output: {master.DeviceName}");
    }

    public override async Task StopAsync()
    {
        IPcAudio? audio;
        IPcMedia? media;
        Timer? timer;
        lock (_gate)
        {
            (audio, media, timer) = (_audio, _media, _notifyTimer);
            (_audio, _media, _notifyTimer) = (null, null, null);
        }
        timer?.Dispose();
        if (audio is not null) audio.Changed -= OnAudioChanged;
        if (media is not null) media.Changed -= OnMediaChanged;
        // On Windows these are COM objects: release them from an MTA thread, like everything else that touches them
        await Task.Run(() =>
        {
            audio?.Dispose();
            media?.Dispose();
        }).ConfigureAwait(false);
    }

    // ───────────────────────── Change notifications

    // The mixer fires this for every step of a volume drag: coalesce, then tell the pucks once
    void OnAudioChanged()
    {
        lock (_gate)
        {
            if (_audio is null) return;
            _notifyTimer ??= new Timer(_ => RefreshMaster());
            _notifyTimer.Change(NotifyDebounce, Timeout.InfiniteTimeSpan);
        }
    }

    void RefreshMaster()
    {
        IPcAudio? audio;
        lock (_gate) audio = _audio;
        if (audio is null) return;
        try
        {
            var master = audio.GetMaster();
            lock (_gate)
            {
                if (_audio != audio) return; // stopped meanwhile
            }
            Publish(master);
        }
        catch (Exception e)
        {
            Log.LogDebug(e, "Couldn't read the PC volume");
        }
    }

    void OnMediaChanged() => Context.NowPlayingChanged(SourceId);

    /// <summary>
    /// Remembers the master state and, when it differs from what the pucks show, tells them: the
    /// dial's value (or the whole control when the mute text changes) and the Home face. When the
    /// change came from a puck's dial, <paramref name="pucksShowIt"/> records it as shown without
    /// sending it back (the dial would jump while still being turned).
    /// </summary>
    void Publish(MasterVolume? now, bool pucksShowIt = false)
    {
        MasterVolume? shown;
        lock (_gate)
        {
            _master = now;
            shown = _shown;
            _shown = now;
        }
        if (pucksShowIt || now == shown) return;
        if (now is null || shown is null || now.Muted != shown.Muted) Context.ControlsChanged();
        else Context.ControlValueChanged(VolumeControlId, now.Volume);
        Context.NowPlayingChanged(SourceId);
    }

    // ───────────────────────── Tools

    [Tool("pc_volume", "Get or set this PC's master volume (0-100). Give volume for an exact level or change for a step " +
                       "(e.g. -10 for \"a bit quieter\"); with neither it just reports the volume. A level above 0 also unmutes.")]
    public async Task<ToolResult> Volume(
        [Param("Exact volume 0-100", Min = 0, Max = 100)] int? volume = null,
        [Param("Relative change, e.g. -10 or 20")] int? change = null,
        CancellationToken cancel = default)
    {
        var audio = RequireAudio();
        var (before, after) = await Blocking.RunAsync(() =>
        {
            var now = audio.GetMaster() ?? throw NoOutput();
            int? target = VolumeMath.Target(now.Volume, volume, change);
            if (target is null) return (now, now);
            // Like the volume keys: changing the level unmutes
            bool? unmute = now.Muted && target > 0 ? false : null;
            return (now, audio.SetMaster(target, unmute));
        }, CallTimeout, _mixer, cancel).ConfigureAwait(false);
        Publish(after);

        string activity = Text.Activity($"PC volume {after.Volume}%");
        if (volume is null && change is null)
            return ToolResult.Ok($"PC volume is {after.Volume}%{(after.Muted ? " (muted)" : "")}.", activity);
        if (change is not null && after.Volume == before.Volume && after.Volume is 0 or 100)
            return ToolResult.Ok($"PC volume is already at {after.Volume}%.", activity);
        return ToolResult.Ok($"PC volume is now {after.Volume}%{(before.Muted && !after.Muted ? " (unmuted)" : "")}.", activity);
    }

    [Tool("pc_mute", "Mute or unmute this PC's sound; leave muted out to toggle.")]
    public async Task<ToolResult> Mute(
        [Param("true to mute, false to unmute; omit to toggle")] bool? muted = null,
        CancellationToken cancel = default)
    {
        var audio = RequireAudio();
        var after = await Blocking.RunAsync(() =>
        {
            var now = audio.GetMaster() ?? throw NoOutput();
            return audio.SetMaster(null, muted ?? !now.Muted);
        }, CallTimeout, _mixer, cancel).ConfigureAwait(false);
        Publish(after);
        return after.Muted
            ? ToolResult.Ok("The PC is muted.", "PC muted")
            : ToolResult.Ok($"The PC is unmuted (volume {after.Volume}%).", "PC unmuted");
    }

    [Tool("pc_media", "Play, pause, skip or go back on whatever is playing on this PC (Spotify, a browser, Media Player…).")]
    public async Task<ToolResult> Media(
        [Param("What to do", Values = ["play", "pause", "toggle", "next", "previous"])] string action,
        CancellationToken cancel = default)
    {
        var media = RequireMedia();
        var what = ParseMediaAction(action);
        var before = media.Current;
        if (!await media.SendAsync(what, cancel).ConfigureAwait(false))
            return ToolResult.Ok("Nothing is playing on the PC.", "PC: nothing playing");

        string app = before?.App is { Length: > 0 } name ? name : "the PC";
        switch (what)
        {
            case MediaAction.Next or MediaAction.Previous:
                var after = await WaitForTrackChangeAsync(media, before?.Title, cancel).ConfigureAwait(false);
                if (after?.Title is { } title && title != before?.Title)
                    return ToolResult.Ok($"{(what == MediaAction.Next ? "Next" : "Back to")}: {DescribeTrack(after)}.", Text.Activity($"PC: {title}"));
                return what == MediaAction.Next
                    ? ToolResult.Ok($"Skipped to the next track on {app}.", "PC: next track")
                    : ToolResult.Ok($"Went back a track on {app}.", "PC: previous track");
            case MediaAction.Play:
                return ToolResult.Ok($"Playing on {app}.", "PC: playing");
            case MediaAction.Pause:
                return ToolResult.Ok($"Paused {app}.", "PC: paused");
            default:
                return before?.Playing == true
                    ? ToolResult.Ok($"Paused {app}.", "PC: paused")
                    : ToolResult.Ok($"Playing on {app}.", "PC: playing");
        }
    }

    [Tool("pc_now_playing", "What's playing on this PC (title, artist and app) and the PC's volume.")]
    public async Task<ToolResult> NowPlayingOnPc(CancellationToken cancel = default)
    {
        var media = RequireMedia();
        var info = await media.ReadAsync(cancel).ConfigureAwait(false);
        var master = await TryReadMasterAsync(cancel).ConfigureAwait(false);
        string volume = master is null ? "" : $" PC volume {master.Volume}%{(master.Muted ? " (muted)" : "")}.";
        if (info?.Title is null) return ToolResult.Ok($"Nothing is playing on the PC.{volume}", "PC: nothing playing");
        string app = info.App is { Length: > 0 } a ? a : "Something";
        return ToolResult.Ok($"{app} {(info.Playing ? "is playing" : "has paused")} {DescribeTrack(info)}.{volume}", Text.Activity($"PC: {info.Title}"));
    }

    [Tool("pc_audio_outputs", "List this PC's audio outputs (speakers, headphones, monitors) and which one is in use.")]
    public async Task<ToolResult> AudioOutputs(CancellationToken cancel = default)
    {
        var audio = RequireAudio();
        var outputs = await Blocking.RunAsync(audio.GetOutputs, CallTimeout, _mixer, cancel).ConfigureAwait(false);
        if (outputs.Count == 0) return ToolResult.Ok("This PC has no audio outputs switched on.", "No audio outputs");
        var current = outputs.FirstOrDefault(o => o.IsDefault);
        return ToolResult.Ok($"In use: {current?.Name ?? "none"}. Outputs: {string.Join("; ", outputs.Select(o => o.Name))}.", "Audio outputs");
    }

    [Tool("pc_set_audio_output", "Switch this PC's sound to another output, for all apps: \"headphones\", \"speakers\", \"monitor\", or part of a device's name.")]
    public async Task<ToolResult> SetAudioOutput(
        [Param("The output, e.g. headphones, speakers, monitor, or part of its name")] string name,
        CancellationToken cancel = default)
    {
        var audio = RequireAudio();
        var outputs = await Blocking.RunAsync(audio.GetOutputs, CallTimeout, _mixer, cancel).ConfigureAwait(false);
        var target = OutputMatcher.Find(name, outputs);
        string activity = Text.Activity($"Output: {Text.ShortDeviceName(target.Name)}");
        if (target.IsDefault) return ToolResult.Ok($"The PC is already playing through {target.Name}.", activity);

        var now = await Blocking.RunAsync(() =>
        {
            audio.SetDefaultOutput(target.Id);
            return audio.GetOutputs();
        }, CallTimeout, _mixer, cancel).ConfigureAwait(false);
        if (!now.Any(o => o.Id == target.Id && o.IsDefault))
            throw new PluginException($"{_os} didn't switch to {target.Name}.");
        return ToolResult.Ok($"The PC now plays through {target.Name}.", activity);
    }

    [Tool("pc_app_volume", "Get or set one app's volume on this PC (e.g. Spotify, Chrome, Teams), 0-100 relative to the PC's volume, " +
                           "or mute it. Give volume for an exact level or change for a step; with neither (and no muted) it reports it.")]
    public async Task<ToolResult> AppVolume(
        [Param("The app, e.g. Spotify, Chrome, Teams")] string app,
        [Param("Exact volume 0-100", Min = 0, Max = 100)] int? volume = null,
        [Param("Relative change, e.g. -20")] int? change = null,
        [Param("true to mute the app, false to unmute it")] bool? muted = null,
        CancellationToken cancel = default)
    {
        var audio = RequireAudio();
        var (found, after) = await Blocking.RunAsync(() =>
        {
            var match = AppAudioMatcher.Find(app, audio.GetApps());
            int? target = VolumeMath.Target(match.Volume, volume, change);
            // Turning an app up unmutes it, unless muting was asked for too
            bool? mute = muted ?? (match.Muted && target > 0 ? false : null);
            if (target is null && mute is null) return (match, match);
            return (match, audio.SetApp(match.Key, target, mute) ?? match);
        }, CallTimeout, _mixer, cancel).ConfigureAwait(false);

        if (after.Muted)
            return ToolResult.Ok($"{after.Name} is muted{(volume is null && change is null ? "" : $" (its volume is {after.Volume}%)")}.", Text.Activity($"{after.Name} muted"));
        bool changed = volume is not null || change is not null || muted is not null;
        string text = changed ? $"{after.Name}'s volume is now {after.Volume}%." : $"{after.Name}'s volume is {after.Volume}%.";
        if (found.Muted) text += " (Unmuted.)";
        return ToolResult.Ok(text, Text.Activity($"{after.Name} {after.Volume}%"));
    }

    [Tool("pc_apps_playing_audio", "Which apps on this PC are playing sound right now, with their volumes.")]
    public async Task<ToolResult> AppsPlayingAudio(CancellationToken cancel = default)
    {
        var audio = RequireAudio();
        var apps = (await Blocking.RunAsync(audio.GetApps, CallTimeout, _mixer, cancel).ConfigureAwait(false))
            .Where(a => a.Key != "system")
            .ToList();
        var playing = apps.Where(a => a.Playing).ToList();
        var idle = apps.Where(a => !a.Playing).ToList();
        string text = playing.Count == 0
            ? "No apps are playing sound on the PC."
            : $"Playing sound: {string.Join(", ", playing.Select(a => $"{a.Name} {a.Volume}%{(a.Muted ? " (muted)" : "")}"))}.";
        if (idle.Count > 0) text += $" Quiet apps with a volume setting: {Text.List(idle.Select(a => a.Name), 8)}.";
        return ToolResult.Ok(text, playing.Count switch
        {
            0 => "No apps playing",
            1 => Text.Activity($"{playing[0].Name} playing"),
            _ => $"{playing.Count} apps playing",
        });
    }

    // ───────────────────────── Puck: dial, Home face, context

    public override IReadOnlyList<PuckControl> GetControls()
    {
        MasterVolume? master;
        lock (_gate) master = _master;
        return
        [
            new PuckControl
            {
                Id = VolumeControlId,
                Name = "PC volume",
                Kind = ControlKind.Number,
                Value = master?.Volume ?? 0,
                Min = 0,
                Max = 100,
                Step = 2,
                Unit = "%",
                Text = master is null ? "No output" : master.Muted ? "Muted" : null,
            },
        ];
    }

    public override async Task OnControlAsync(string controlId, ControlInput input, CancellationToken cancel)
    {
        if (controlId == VolumeControlId && input.Value is double value)
            await SetVolumeFromPuckAsync((int)Math.Round(value), cancel).ConfigureAwait(false);
    }

    /// <summary>The puck owns the number while it's being turned, so only a mute change is sent back.</summary>
    async Task SetVolumeFromPuckAsync(int volume, CancellationToken cancel)
    {
        var audio = RequireAudio();
        volume = Math.Clamp(volume, 0, 100);
        var after = await Blocking.RunAsync(() =>
        {
            var now = audio.GetMaster() ?? throw NoOutput();
            return audio.SetMaster(volume, now.Muted && volume > 0 ? false : null);
        }, CallTimeout, _mixer, cancel).ConfigureAwait(false);
        bool muteChanged;
        lock (_gate) muteChanged = _shown is not null && _shown.Muted != after.Muted;
        Publish(after, pucksShowIt: !muteChanged);
    }

    public override Task<string?> GetContextAsync(ToolCaller caller, CancellationToken cancel)
    {
        MasterVolume? master;
        IPcMedia? media;
        lock (_gate) (master, media) = (_master, _media);
        if (media is null && master is null) return Task.FromResult<string?>(null);
        return Task.FromResult<string?>(FormatContext(master, media?.Current));
    }

    /// <summary><c>PC: Spotify playing "Teardrop" – Massive Attack; volume 30.</c></summary>
    internal static string FormatContext(MasterVolume? master, MediaInfo? media)
    {
        string playing = media?.Title is { } title
            ? $"{(media.App is { Length: > 0 } app ? app : "something")} {(media.Playing ? "playing" : "paused")} \"{Text.Clip(title, 80)}\"" +
              (media.Artist is { } artist ? $" – {Text.Clip(artist, 60)}" : "")
            : "nothing playing";
        string volume = master is null ? "no audio output" : $"volume {master.Volume}{(master.Muted ? " (muted)" : "")}";
        return $"PC: {playing}; {volume}.";
    }

    public IReadOnlyList<MediaSourceInfo> GetMediaSources() => [new MediaSourceInfo(SourceId, SourceName)];

    public Task<NowPlaying?> GetNowPlayingAsync(string sourceId, CancellationToken cancel)
    {
        if (sourceId != SourceId) return Task.FromResult<NowPlaying?>(null);
        MasterVolume? master;
        IPcMedia? media;
        lock (_gate) (master, media) = (_master, _media);
        var info = media?.Current;
        return Task.FromResult<NowPlaying?>(new NowPlaying
        {
            Title = info?.Title,
            Artist = info?.Artist,
            Album = info?.Album,
            Playing = info?.Playing ?? false,
            Volume = master?.Volume,
            Muted = master?.Muted,
            Source = info?.App is { Length: > 0 } app ? app : SourceName,
        });
    }

    public async Task MediaCommandAsync(string sourceId, MediaCommand command, CancellationToken cancel)
    {
        if (sourceId != SourceId) return;
        switch (command.Action)
        {
            case MediaAction.SetVolume:
                if (command.Volume is int volume) await SetVolumeFromPuckAsync(volume, cancel).ConfigureAwait(false);
                break;
            case MediaAction.ToggleMute:
                var audio = RequireAudio();
                var after = await Blocking.RunAsync(() =>
                {
                    var now = audio.GetMaster() ?? throw NoOutput();
                    return audio.SetMaster(null, !now.Muted);
                }, CallTimeout, _mixer, cancel).ConfigureAwait(false);
                Publish(after); // the puck's mute icon needs the result
                break;
            default:
                await RequireMedia().SendAsync(command.Action, cancel).ConfigureAwait(false);
                break;
        }
    }

    // ───────────────────────── Helpers

    IPcAudio RequireAudio()
    {
        lock (_gate) return _audio ?? throw new PluginException("PC audio isn't running. Turn on the PC audio plugin in the hub's Plugins page.");
    }

    IPcMedia RequireMedia()
    {
        lock (_gate) return _media ?? throw new PluginException("PC audio isn't running. Turn on the PC audio plugin in the hub's Plugins page.");
    }

    static PluginException NoOutput() => new("This PC has no audio output right now.");

    static string Capitalize(string s) => s.Length > 0 ? char.ToUpperInvariant(s[0]) + s[1..] : s;

    async Task<MasterVolume?> TryReadMasterAsync(CancellationToken cancel)
    {
        IPcAudio? audio;
        lock (_gate) audio = _audio;
        if (audio is null) return null;
        try
        {
            var master = await Blocking.RunAsync(audio.GetMaster, CallTimeout, _mixer, cancel).ConfigureAwait(false);
            Publish(master);
            return master;
        }
        catch (Exception e) when (!cancel.IsCancellationRequested)
        {
            Log.LogDebug(e, "Couldn't read the PC volume");
            lock (_gate) return _master;
        }
    }

    /// <summary>Players take a moment to report the new track after a skip.</summary>
    static async Task<MediaInfo?> WaitForTrackChangeAsync(IPcMedia media, string? oldTitle, CancellationToken cancel)
    {
        MediaInfo? info = null;
        for (int i = 0; i < 6; i++)
        {
            await Task.Delay(250, cancel).ConfigureAwait(false);
            info = await media.ReadAsync(cancel).ConfigureAwait(false);
            if (info?.Title is not null && info.Title != oldTitle) break;
        }
        return info;
    }

    /// <summary>The action names Claude might send, including near-synonyms.</summary>
    internal static MediaAction ParseMediaAction(string action) => action.Trim().ToLowerInvariant() switch
    {
        "play" or "resume" or "unpause" or "start" => MediaAction.Play,
        "pause" or "stop" => MediaAction.Pause,
        "toggle" or "play_pause" or "playpause" or "play/pause" => MediaAction.Toggle,
        "next" or "skip" or "forward" or "next_track" => MediaAction.Next,
        "previous" or "prev" or "back" or "previous_track" or "last" => MediaAction.Previous,
        _ => throw new PluginException($"'{action}' isn't something I can do to the PC's media. Try play, pause, toggle, next or previous."),
    };

    /// <summary><c>"Teardrop" by Massive Attack</c></summary>
    internal static string DescribeTrack(MediaInfo info) =>
        $"\"{Text.Clip(info.Title, 100)}\"" + (info.Artist is { } artist ? $" by {Text.Clip(artist, 60)}" : "");
}
