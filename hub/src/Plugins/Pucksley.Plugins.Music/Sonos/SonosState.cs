using System.Globalization;

namespace Pucksley.Plugins.Sonos;

/// <summary>What a group is playing from, judged by its transport URI.</summary>
public enum SonosSource { None, Queue, Radio, Tv, LineIn, SpotifyConnect, AirPlay, Grouped, Stream }

/// <summary>Sonos URI schemes.</summary>
public static class SonosUris
{
    public static string QueueUri(string coordinatorUuid) => $"x-rincon-queue:{coordinatorUuid}#0";
    public static string JoinGroupUri(string coordinatorUuid) => $"x-rincon:{coordinatorUuid}";
    public static string TvUri(string uuid) => $"x-sonos-htastream:{uuid}:spdif";
    public static string LineInUri(string uuid) => $"x-rincon-stream:{uuid}";

    public static SonosSource Classify(string? uri)
    {
        if (string.IsNullOrWhiteSpace(uri)) return SonosSource.None;
        if (Starts(uri, "x-rincon-queue:")) return SonosSource.Queue;
        if (Starts(uri, "x-sonos-htastream:")) return SonosSource.Tv;
        if (Starts(uri, "x-rincon-stream:")) return SonosSource.LineIn;
        if (Starts(uri, "x-rincon:")) return SonosSource.Grouped;
        if (Starts(uri, "x-sonos-vli:"))
        {
            // "Virtual line-in": another app is streaming to the speaker, e.g.
            // x-sonos-vli:RINCON_…:2,spotify:… (Spotify Connect) or …:1,airplay:… (AirPlay)
            if (uri.Contains("spotify", StringComparison.OrdinalIgnoreCase)) return SonosSource.SpotifyConnect;
            if (uri.Contains("airplay", StringComparison.OrdinalIgnoreCase)) return SonosSource.AirPlay;
            return SonosSource.Stream;
        }
        return IsRadio(uri) ? SonosSource.Radio : SonosSource.Stream;
    }

    /// <summary>Streams that are played directly, never through the queue.</summary>
    public static bool IsRadio(string uri) =>
        Starts(uri, "x-sonosapi-stream:") || Starts(uri, "x-sonosapi-radio:") || Starts(uri, "x-rincon-mp3radio:") ||
        Starts(uri, "x-sonosapi-hls:") || Starts(uri, "aac:") || Starts(uri, "hls-radio:");

    /// <summary>Playlists, albums and saved queues: added to the queue rather than played directly.</summary>
    public static bool IsContainer(string uri) =>
        Starts(uri, "x-rincon-cpcontainer:") || Starts(uri, "x-rincon-playlist:") || Starts(uri, "file:///jffs/settings/savedqueues.rsq");

    static bool Starts(string s, string prefix) => s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Track details from DIDL metadata.</summary>
public sealed record SonosTrack(string? Title, string? Artist, string? Album, string? Station)
{
    public static SonosTrack Empty { get; } = new(null, null, null, null);

    /// <param name="trackMetaData">GetPositionInfo <c>TrackMetaData</c>.</param>
    /// <param name="mediaMetaData">GetMediaInfo <c>CurrentURIMetaData</c> (the station name for radio).</param>
    public static SonosTrack From(string? trackMetaData, string? mediaMetaData, SonosSource source)
    {
        var item = Didl.Parse(trackMetaData).FirstOrDefault();
        string? title = Clean(item?.Title), artist = Clean(item?.Creator), album = Clean(item?.Album);
        string? station = source == SonosSource.Radio ? Clean(Didl.Parse(mediaMetaData).FirstOrDefault()?.Title) : null;

        // Radio streams put "Artist - Title" in r:streamContent (or status text like ZPSTR_CONNECTING).
        var stream = item?.StreamContent;
        if (!string.IsNullOrWhiteSpace(stream) && !stream.StartsWith("ZPSTR_", StringComparison.Ordinal))
        {
            int dash = stream.IndexOf(" - ", StringComparison.Ordinal);
            if (dash > 0)
            {
                artist = stream[..dash].Trim();
                title = stream[(dash + 3)..].Trim();
            }
            else
            {
                title = stream.Trim();
                artist = null;
            }
        }
        else if (source == SonosSource.Radio && title == station)
        {
            title = null; // the "title" of a station between songs is just the station name
        }
        return new SonosTrack(title, artist, album, station);
    }

    /// <summary>Drops values that are URIs or device ids rather than names.</summary>
    static string? Clean(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim();
        if (s.StartsWith("x-", StringComparison.OrdinalIgnoreCase) || s.Contains("://", StringComparison.Ordinal) ||
            s.StartsWith("RINCON_", StringComparison.Ordinal) || s.StartsWith("ZPSTR_", StringComparison.Ordinal))
            return null;
        return s;
    }
}

/// <summary>A group's playback state, as last read from its coordinator.</summary>
public sealed record SonosGroupState
{
    public required string CoordinatorUuid { get; init; }

    /// <summary>PLAYING, PAUSED_PLAYBACK, STOPPED, TRANSITIONING or NO_MEDIA_PRESENT.</summary>
    public string TransportState { get; init; } = "";

    public SonosSource Source { get; init; }
    public SonosTrack Track { get; init; } = SonosTrack.Empty;
    public string? MediaUri { get; init; }
    public string? TrackUri { get; init; }
    public int TrackNumber { get; init; }
    public TimeSpan? Position { get; init; }
    public TimeSpan? Duration { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }

    public bool IsPlaying => TransportState is "PLAYING" or "TRANSITIONING";
    public bool IsPaused => TransportState == "PAUSED_PLAYBACK";

    /// <summary>"playing", "paused" or "idle".</summary>
    public string StateWord => IsPlaying ? "playing" : IsPaused ? "paused" : "idle";

    /// <summary>Same music and state (ignoring the position), for change notifications.</summary>
    public bool SameAs(SonosGroupState? other) =>
        other is not null && other.TransportState == TransportState && other.Source == Source &&
        other.Track == Track && other.TrackUri == TrackUri;

    public static SonosGroupState From(string coordinatorUuid, IReadOnlyDictionary<string, string> transport,
        IReadOnlyDictionary<string, string> position, IReadOnlyDictionary<string, string> media, DateTimeOffset now)
    {
        var mediaUri = media.GetValueOrDefault("CurrentURI");
        var trackUri = position.GetValueOrDefault("TrackURI");
        var source = SonosUris.Classify(string.IsNullOrEmpty(mediaUri) ? trackUri : mediaUri);
        return new SonosGroupState
        {
            CoordinatorUuid = coordinatorUuid,
            TransportState = transport.GetValueOrDefault("CurrentTransportState") ?? "",
            Source = source,
            Track = SonosTrack.From(position.GetValueOrDefault("TrackMetaData"), media.GetValueOrDefault("CurrentURIMetaData"), source),
            MediaUri = mediaUri,
            TrackUri = trackUri,
            TrackNumber = int.TryParse(position.GetValueOrDefault("Track"), out var n) ? n : 0,
            Position = SonosTime.Parse(position.GetValueOrDefault("RelTime")),
            Duration = SonosTime.Parse(position.GetValueOrDefault("TrackDuration")),
            UpdatedAt = now,
        };
    }

    /// <summary>Where the music comes from, for people: "Spotify", "the TV", a station… or null.</summary>
    public string? SourceLabel => Source switch
    {
        SonosSource.Tv => "the TV",
        SonosSource.LineIn => "line-in",
        SonosSource.SpotifyConnect => "Spotify",
        SonosSource.AirPlay => "AirPlay",
        SonosSource.Radio => Track.Station ?? "radio",
        SonosSource.Queue when TrackUri?.StartsWith("x-sonos-spotify:", StringComparison.OrdinalIgnoreCase) == true => "Spotify",
        _ => null,
    };

    /// <summary>What's on, briefly: "\"Teardrop\" – Massive Attack", "TV", "Spotify"; null when nothing.</summary>
    public string? WhatsOn(int maxTitle = 40)
    {
        var t = Track;
        string? song = t.Title is null ? null : t.Artist is null
            ? $"\"{SonosText.Clip(t.Title, maxTitle)}\""
            : $"\"{SonosText.Clip(t.Title, maxTitle)}\" – {SonosText.Clip(t.Artist, maxTitle)}";
        return Source switch
        {
            SonosSource.Tv => "TV",
            SonosSource.LineIn => "line-in",
            SonosSource.Radio => song is null ? t.Station ?? "radio" : t.Station is null ? song : $"{song} on {t.Station}",
            SonosSource.SpotifyConnect => song ?? "Spotify",
            SonosSource.AirPlay => song ?? "AirPlay",
            SonosSource.None => null,
            _ => song,
        };
    }
}

/// <summary>A player's volume and mute.</summary>
public readonly record struct SonosVolume(int Volume, bool Muted);

/// <summary>UPnP times ("0:03:52").</summary>
public static class SonosTime
{
    /// <summary>"H:MM:SS" (optionally with fractions) → time; null for "NOT_IMPLEMENTED", "" and junk.</summary>
    public static TimeSpan? Parse(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var parts = s.Trim().Split(':');
        if (parts.Length != 3 ||
            !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var h) ||
            !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var m) ||
            !double.TryParse(parts[2], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var sec))
            return null;
        return new TimeSpan(h, m, 0) + TimeSpan.FromSeconds(sec);
    }

    /// <summary>For Seek REL_TIME: "0:01:23".</summary>
    public static string Format(TimeSpan t) => $"{(int)t.TotalHours}:{t.Minutes:D2}:{t.Seconds:D2}";

    /// <summary>For people: "3:52" or "1:02:03".</summary>
    public static string Short(TimeSpan t) => t.TotalHours >= 1 ? Format(t) : $"{t.Minutes}:{t.Seconds:D2}";

    /// <summary>For ConfigureSleepTimer: "01:30:00".</summary>
    public static string SleepDuration(TimeSpan t) => $"{(int)t.TotalHours:D2}:{t.Minutes:D2}:{t.Seconds:D2}";
}

/// <summary>Sonos play modes ↔ shuffle + repeat.</summary>
public static class SonosPlayModes
{
    /// <summary>Repeat is "off", "all" or "one".</summary>
    public static (bool Shuffle, string Repeat) Parse(string? mode) => mode?.Trim().ToUpperInvariant() switch
    {
        "REPEAT_ALL" => (false, "all"),
        "REPEAT_ONE" => (false, "one"),
        "SHUFFLE_NOREPEAT" => (true, "off"),
        "SHUFFLE" => (true, "all"),
        "SHUFFLE_REPEAT_ONE" => (true, "one"),
        _ => (false, "off"),
    };

    public static string Build(bool shuffle, string repeat) => (shuffle, repeat) switch
    {
        (false, "all") => "REPEAT_ALL",
        (false, "one") => "REPEAT_ONE",
        (false, _) => "NORMAL",
        (true, "all") => "SHUFFLE",
        (true, "one") => "SHUFFLE_REPEAT_ONE",
        (true, _) => "SHUFFLE_NOREPEAT",
    };
}
