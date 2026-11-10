namespace Pucksley.Sdk;

/// <summary>
/// A plugin that plays music somewhere (Sonos rooms, Spotify, this PC). The puck's Home face shows
/// what's playing on one source and its dial is that source's volume; tap = play/pause, swipe =
/// next/previous. Which source a puck follows is set per puck in the hub (e.g. the kitchen puck
/// follows the Kitchen Sonos).
/// </summary>
public interface IMediaSourceProvider
{
    /// <summary>The sources this plugin offers, e.g. one per Sonos room. Ids are local to the plugin.</summary>
    IReadOnlyList<MediaSourceInfo> GetMediaSources();

    Task<NowPlaying?> GetNowPlayingAsync(string sourceId, CancellationToken cancel);

    Task MediaCommandAsync(string sourceId, MediaCommand command, CancellationToken cancel);
}

public sealed record MediaSourceInfo(string Id, string Name);

public sealed record NowPlaying
{
    public string? Title { get; init; }
    public string? Artist { get; init; }
    public string? Album { get; init; }
    public bool Playing { get; init; }
    /// <summary>0–100, null if unknown.</summary>
    public int? Volume { get; init; }
    public bool? Muted { get; init; }
    /// <summary>Where it's playing ("Kitchen", "Spotify").</summary>
    public string? Source { get; init; }
}

/// <summary>A media key or a volume change.</summary>
public sealed record MediaCommand(MediaAction Action, int? Volume = null)
{
    public static MediaCommand Toggle { get; } = new(MediaAction.Toggle);
    public static MediaCommand Next { get; } = new(MediaAction.Next);
    public static MediaCommand Previous { get; } = new(MediaAction.Previous);
    public static MediaCommand Mute { get; } = new(MediaAction.ToggleMute);
    public static MediaCommand SetVolume(int volume) => new(MediaAction.SetVolume, Math.Clamp(volume, 0, 100));
}

public enum MediaAction { Toggle, Play, Pause, Next, Previous, SetVolume, ToggleMute }
