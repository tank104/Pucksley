using Microsoft.Extensions.Logging;
using Pucksley.Plugins.Windows.Common;
using Pucksley.Sdk;
using Windows.Media.Control;
using static Pucksley.Plugins.Windows.Common.NativeMethods;
using Session = Windows.Media.Control.GlobalSystemMediaTransportControlsSession;
using SessionManager = Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager;

namespace Pucksley.Plugins.Windows.Audio;

/// <summary>
/// What's playing on the PC, from Windows' System Media Transport Controls (the same source as the
/// media flyout: Spotify, browsers, Media Player…), and play/pause/next/previous for it. When no app
/// has a media session, or the app ignores a command, the media keys are pressed instead.
/// </summary>
internal sealed class WindowsMedia : IPcMedia
{
    static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(3);
    static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(150);

    readonly ILogger _log;
    readonly object _gate = new();
    SessionManager? _manager;
    List<Session> _watched = [];
    Timer? _debounce;
    MediaInfo? _current;
    int _readSeq;
    bool _disposed;

    public event Action? Changed;

    public WindowsMedia(ILogger log) => _log = log;

    public MediaInfo? Current
    {
        get { lock (_gate) return _current; }
    }

    public async Task StartAsync(CancellationToken cancel)
    {
        var manager = await SessionManager.RequestAsync().AsTask(cancel).WaitAsync(TimeSpan.FromSeconds(10), cancel).ConfigureAwait(false);
        lock (_gate)
        {
            if (_disposed) return;
            _manager = manager;
        }
        manager.SessionsChanged += OnSessionsChanged;
        manager.CurrentSessionChanged += OnCurrentSessionChanged;
        WatchSessions();
        await ReadAsync(cancel).ConfigureAwait(false);
    }

    void OnSessionsChanged(SessionManager sender, SessionsChangedEventArgs args)
    {
        WatchSessions();
        ScheduleRead();
    }

    void OnCurrentSessionChanged(SessionManager sender, CurrentSessionChangedEventArgs args) => ScheduleRead();

    // Bound to both MediaPropertiesChanged and PlaybackInfoChanged
    void OnSessionEvent(Session sender, object args) => ScheduleRead();

    /// <summary>Listens to every session, not only the "current" one, so a player starting elsewhere is noticed.</summary>
    void WatchSessions()
    {
        SessionManager? manager;
        lock (_gate) manager = _manager;
        if (manager is null) return;
        List<Session> sessions;
        try
        {
            sessions = manager.GetSessions().ToList();
        }
        catch (Exception e)
        {
            _log.LogDebug(e, "Couldn't list media sessions");
            return;
        }
        lock (_gate)
        {
            if (_disposed) return;
            foreach (var s in _watched) Unwatch(s);
            _watched = sessions;
            foreach (var s in _watched)
            {
                s.MediaPropertiesChanged += OnSessionEvent;
                s.PlaybackInfoChanged += OnSessionEvent;
            }
        }
    }

    void Unwatch(Session session)
    {
        try
        {
            session.MediaPropertiesChanged -= OnSessionEvent;
            session.PlaybackInfoChanged -= OnSessionEvent;
        }
        catch (Exception)
        {
            // the app (and its session) already went away
        }
    }

    /// <summary>Players fire several events per track change: coalesce them into one read.</summary>
    void ScheduleRead()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _debounce ??= new Timer(_ => _ = ReadAsync(CancellationToken.None));
            _debounce.Change(Debounce, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>
    /// The session to show and control: the current one if it's playing, else any that is playing,
    /// else the current one (Windows keeps a paused tab "current" while Spotify plays elsewhere).
    /// </summary>
    Session? PickSession()
    {
        SessionManager? manager;
        lock (_gate) manager = _manager;
        if (manager is null) return null;
        var current = manager.GetCurrentSession();
        if (current is not null && IsPlaying(current)) return current;
        var sessions = manager.GetSessions();
        return sessions.FirstOrDefault(IsPlaying) ?? current ?? sessions.FirstOrDefault();
    }

    static bool IsPlaying(Session session)
    {
        try
        {
            return session.GetPlaybackInfo()?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public async Task<MediaInfo?> ReadAsync(CancellationToken cancel)
    {
        int seq;
        lock (_gate) seq = ++_readSeq;
        MediaInfo? info = null;
        try
        {
            var session = await Blocking.RunAsync(PickSession, CallTimeout, "Windows media", cancel).ConfigureAwait(false);
            if (session is not null)
            {
                var props = await session.TryGetMediaPropertiesAsync().AsTask(cancel).WaitAsync(CallTimeout, cancel).ConfigureAwait(false);
                string? artist = props?.Artist is { Length: > 0 } a ? a : props?.AlbumArtist;
                info = new MediaInfo(Blank(props?.Title), Blank(artist), Blank(props?.AlbumTitle),
                    AppNames.FromAumid(session.SourceAppUserModelId), IsPlaying(session));
            }
        }
        catch (Exception e) when (!cancel.IsCancellationRequested)
        {
            // A read can race an app closing its session: keep what we had
            _log.LogDebug(e, "Couldn't read the media session");
            lock (_gate) return _current;
        }

        bool changed;
        lock (_gate)
        {
            if (seq != _readSeq) return _current; // a newer read is in flight and decides
            changed = info != _current;
            _current = info;
        }
        if (changed) Changed?.Invoke();
        return info;
    }

    public async Task<bool> SendAsync(MediaAction action, CancellationToken cancel)
    {
        Session? session = null;
        try
        {
            session = await Blocking.RunAsync(PickSession, CallTimeout, "Windows media", cancel).ConfigureAwait(false);
        }
        catch (Exception e) when (!cancel.IsCancellationRequested)
        {
            _log.LogDebug(e, "Couldn't find a media session");
        }

        // The fallback key is play/pause: pressing it when nothing is known to play would *start* something
        if (action == MediaAction.Pause && (session is null || !IsPlaying(session))) return false;

        bool done = false;
        if (session is not null)
        {
            if (action == MediaAction.Play && IsPlaying(session)) return true;
            try
            {
                var operation = action switch
                {
                    MediaAction.Play => session.TryPlayAsync(),
                    MediaAction.Pause => session.TryPauseAsync(),
                    MediaAction.Next => session.TrySkipNextAsync(),
                    MediaAction.Previous => session.TrySkipPreviousAsync(),
                    _ => session.TryTogglePlayPauseAsync(),
                };
                done = await operation.AsTask(cancel).WaitAsync(CallTimeout, cancel).ConfigureAwait(false);
            }
            catch (Exception e) when (!cancel.IsCancellationRequested)
            {
                _log.LogDebug(e, "Media session command {Action} failed", action);
            }
        }

        // No media session (or the app ignored it): the media keys reach whichever player Windows routes them to
        if (!done) MediaKeys.Press(action);
        ScheduleRead();
        return true;
    }

    static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    public void Dispose()
    {
        SessionManager? manager;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _debounce?.Dispose();
            foreach (var s in _watched) Unwatch(s);
            _watched = [];
            manager = _manager;
            _manager = null;
        }
        if (manager is null) return;
        try
        {
            manager.SessionsChanged -= OnSessionsChanged;
            manager.CurrentSessionChanged -= OnCurrentSessionChanged;
        }
        catch (Exception)
        {
            // shutting down anyway
        }
    }
}

/// <summary>The keyboard's media keys, for players without a media session.</summary>
internal static class MediaKeys
{
    public static void Press(MediaAction action)
    {
        byte vk = action switch
        {
            MediaAction.Next => VK_MEDIA_NEXT_TRACK,
            MediaAction.Previous => VK_MEDIA_PREV_TRACK,
            _ => VK_MEDIA_PLAY_PAUSE,
        };
        keybd_event(vk, 0, KEYEVENTF_EXTENDEDKEY, 0);
        keybd_event(vk, 0, KEYEVENTF_EXTENDEDKEY | KEYEVENTF_KEYUP, 0);
    }
}
