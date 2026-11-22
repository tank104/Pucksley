using Microsoft.Extensions.Logging;
using Pucksley.Plugins.Linux.Common;
using Pucksley.Sdk;
using Tmds.DBus.Protocol;

namespace Pucksley.Plugins.Linux.Audio;

/// <summary>One media player on the session bus, as it describes itself.</summary>
internal sealed record MprisPlayer(string BusName, string Identity, string Status, string? Title, string? Artist, string? Album)
{
    public bool Playing => Status.Equals("Playing", StringComparison.OrdinalIgnoreCase);

    public MediaInfo ToMediaInfo() => new(Title, Artist, Album, Identity, Playing);
}

/// <summary>
/// What's playing on the PC, from the desktop's media players: Spotify, browsers, VLC, mpv and the
/// rest announce themselves on the session bus (MPRIS, the source of a desktop's media controls
/// and of the keyboard's media keys), and take play/pause/next/previous there too.
/// </summary>
internal sealed class MprisMedia : IPcMedia
{
    internal const string Prefix = "org.mpris.MediaPlayer2.";
    const string ObjectPath = "/org/mpris/MediaPlayer2";
    const string RootInterface = "org.mpris.MediaPlayer2";
    const string PlayerInterface = "org.mpris.MediaPlayer2.Player";
    static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(3);
    static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(150);

    readonly ILogger _log;
    readonly Bus _bus;
    readonly object _gate = new();
    readonly List<IDisposable> _subscriptions = [];
    DBusConnection? _subscribedOn;
    Timer? _debounce;
    MediaInfo? _current;
    string? _currentPlayer; // the bus name shown last: it stays the choice while it plays
    int _readSeq;
    bool _disposed;

    public event Action? Changed;

    public MprisMedia(ILogger log, Bus? bus = null)
    {
        _log = log;
        _bus = bus ?? Bus.Session;
    }

    public MediaInfo? Current
    {
        get { lock (_gate) return _current; }
    }

    public async Task StartAsync(CancellationToken cancel)
    {
        await SubscribeAsync(cancel).ConfigureAwait(false);
        await ReadAsync(cancel).ConfigureAwait(false);
    }

    /// <summary>
    /// Listens for players arriving and leaving, and for any player's track or state changing. Done
    /// again whenever the bus connection is a new one (the old subscriptions died with the old one).
    /// </summary>
    async Task SubscribeAsync(CancellationToken cancel)
    {
        var connection = await _bus.ConnectAsync(cancel).ConfigureAwait(false);
        lock (_gate)
        {
            if (_disposed || ReferenceEquals(_subscribedOn, connection)) return;
            foreach (var s in _subscriptions) s.Dispose();
            _subscriptions.Clear();
            _subscribedOn = connection;
        }

        var arrivals = await connection.AddMatchAsync(
            new MatchRule
            {
                Type = MessageType.Signal,
                Sender = "org.freedesktop.DBus",
                Path = "/org/freedesktop/DBus",
                Interface = "org.freedesktop.DBus",
                Member = "NameOwnerChanged",
                Arg0Namespace = RootInterface,
            },
            static (Message m, object? _) => m.GetBodyReader().ReadString(),
            (Notification<string> n) => { if (n.HasValue) ScheduleRead(); },
            emitOnCapturedContext: false).ConfigureAwait(false);

        var changes = await connection.AddMatchAsync(
            new MatchRule
            {
                Type = MessageType.Signal,
                Path = ObjectPath,
                Interface = "org.freedesktop.DBus.Properties",
                Member = "PropertiesChanged",
            },
            static (Message m, object? _) => m.GetBodyReader().ReadString(),
            // A playing track announces its position's neighbours too (volume, rate…): only the player part matters
            (Notification<string> n) => { if (n.HasValue && n.Value == PlayerInterface) ScheduleRead(); },
            emitOnCapturedContext: false).ConfigureAwait(false);

        lock (_gate)
        {
            if (_disposed || !ReferenceEquals(_subscribedOn, connection))
            {
                arrivals.Dispose();
                changes.Dispose();
                return;
            }
            _subscriptions.Add(arrivals);
            _subscriptions.Add(changes);
        }
    }

    /// <summary>Players fire several signals per track change: coalesce them into one read.</summary>
    void ScheduleRead()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _debounce ??= new Timer(_ => _ = ReadQuietlyAsync());
            _debounce.Change(Debounce, Timeout.InfiniteTimeSpan);
        }
    }

    async Task ReadQuietlyAsync()
    {
        try
        {
            await ReadAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            _log.LogDebug(e, "Couldn't read the media players");
        }
    }

    public async Task<MediaInfo?> ReadAsync(CancellationToken cancel)
    {
        int seq;
        lock (_gate) seq = ++_readSeq;
        MprisPlayer? player;
        try
        {
            await SubscribeAsync(cancel).ConfigureAwait(false);
            player = Pick(await PlayersAsync(cancel).ConfigureAwait(false), PreviousPlayer());
        }
        catch (Exception e) when (!cancel.IsCancellationRequested)
        {
            // A read can race a player closing: keep what we had
            _log.LogDebug(e, "Couldn't read the media players");
            lock (_gate) return _current;
        }

        var info = player?.ToMediaInfo();
        bool changed;
        lock (_gate)
        {
            if (seq != _readSeq) return _current; // a newer read is in flight and decides
            changed = info != _current;
            _current = info;
            _currentPlayer = player?.BusName;
        }
        if (changed) Changed?.Invoke();
        return info;
    }

    string? PreviousPlayer()
    {
        lock (_gate) return _currentPlayer;
    }

    /// <summary>
    /// The player to show and control: the one shown last if it's playing, else any that is
    /// playing, else the one shown last, else the first with a track.
    /// </summary>
    internal static MprisPlayer? Pick(IReadOnlyList<MprisPlayer> players, string? previous)
    {
        var last = previous is null ? null : players.FirstOrDefault(p => p.BusName == previous);
        if (last is { Playing: true }) return last;
        return players.FirstOrDefault(p => p.Playing) ?? last ?? players.FirstOrDefault(p => p.Title is not null) ?? players.FirstOrDefault();
    }

    async Task<List<MprisPlayer>> PlayersAsync(CancellationToken cancel)
    {
        var names = (await _bus.ListNamesAsync(cancel).ConfigureAwait(false))
            .Where(IsPlayerName)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();
        var players = new List<MprisPlayer>();
        foreach (var name in names)
        {
            try
            {
                var properties = await _bus.GetAllAsync(name, ObjectPath, PlayerInterface, CallTimeout, cancel).ConfigureAwait(false);
                string identity = await IdentityAsync(name, cancel).ConfigureAwait(false);
                players.Add(Describe(name, identity, properties));
            }
            catch (Exception e) when (e is DBusExceptionBase or PluginException && !cancel.IsCancellationRequested)
            {
                _log.LogDebug("Skipped media player {Name}: {Message}", name, e.Message); // it went away, or doesn't answer
            }
        }
        return players;
    }

    /// <summary>playerctld only repeats whichever player was used last: showing it would count that one twice.</summary>
    internal static bool IsPlayerName(string busName) =>
        busName.StartsWith(Prefix, StringComparison.Ordinal) && busName.Length > Prefix.Length && !busName.EndsWith(".playerctld", StringComparison.Ordinal);

    async Task<string> IdentityAsync(string busName, CancellationToken cancel)
    {
        try
        {
            var identity = await _bus.GetAsync(busName, ObjectPath, RootInterface, "Identity", CallTimeout, cancel).ConfigureAwait(false);
            if (identity.Type == VariantValueType.String && identity.GetString() is { Length: > 0 } name) return CleanIdentity(name, busName);
        }
        catch (Exception e) when (e is DBusExceptionBase or PluginException && !cancel.IsCancellationRequested)
        {
            // optional in practice: fall back to the bus name
        }
        return NameFromBusName(busName);
    }

    /// <summary>"Mozilla firefox" → "Firefox"; otherwise as the player says.</summary>
    internal static string CleanIdentity(string identity, string busName)
    {
        string name = identity.Trim();
        if (name.Equals("Mozilla firefox", StringComparison.OrdinalIgnoreCase)) return "Firefox";
        return name.Length > 0 ? name : NameFromBusName(busName);
    }

    /// <summary>"org.mpris.MediaPlayer2.chromium.instance1234" → "Chromium".</summary>
    internal static string NameFromBusName(string busName)
    {
        string tail = busName.StartsWith(Prefix, StringComparison.Ordinal) ? busName[Prefix.Length..] : busName;
        string first = tail.Split('.')[0];
        return PulseNames.Friendly(first);
    }

    /// <summary>A player's state from its properties (PlaybackStatus and the xesam: metadata).</summary>
    internal static MprisPlayer Describe(string busName, string identity, IReadOnlyDictionary<string, VariantValue> properties)
    {
        string status = properties.TryGetValue("PlaybackStatus", out var s) && s.Type == VariantValueType.String ? s.GetString() : "Stopped";
        string? title = null, artist = null, album = null;
        if (properties.TryGetValue("Metadata", out var metadata) && metadata.Type == VariantValueType.Dictionary)
        {
            var map = metadata.GetDictionary<string, VariantValue>();
            title = One(map, "xesam:title");
            artist = One(map, "xesam:artist") ?? One(map, "xesam:albumArtist");
            album = One(map, "xesam:album");
        }
        return new MprisPlayer(busName, identity, status, title, artist, album);
    }

    /// <summary>A text value, or the entries of a list joined ("Massive Attack, Tricky"); null when blank.</summary>
    static string? One(Dictionary<string, VariantValue> map, string key)
    {
        if (!map.TryGetValue(key, out var value)) return null;
        string? text = value.Type switch
        {
            VariantValueType.String => value.GetString(),
            VariantValueType.Array when value.ItemType == VariantValueType.String => string.Join(", ", value.GetArray<string>().Where(a => !string.IsNullOrWhiteSpace(a))),
            _ => null,
        };
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    public async Task<bool> SendAsync(MediaAction action, CancellationToken cancel)
    {
        MprisPlayer? player = null;
        try
        {
            player = Pick(await PlayersAsync(cancel).ConfigureAwait(false), PreviousPlayer());
        }
        catch (Exception e) when (e is DBusExceptionBase && !cancel.IsCancellationRequested)
        {
            _log.LogDebug(e, "Couldn't find a media player");
        }

        if (player is null) return false; // nothing is open that could play
        if (action == MediaAction.Pause && !player.Playing) return false;
        if (action == MediaAction.Play && player.Playing) return true;

        string method = action switch
        {
            MediaAction.Play => "Play",
            MediaAction.Pause => "Pause",
            MediaAction.Next => "Next",
            MediaAction.Previous => "Previous",
            _ => "PlayPause",
        };
        try
        {
            await _bus.CallAsync(player.BusName, ObjectPath, PlayerInterface, method, timeout: CallTimeout, cancel: cancel).ConfigureAwait(false);
        }
        catch (DBusExceptionBase e) when (!cancel.IsCancellationRequested)
        {
            throw new PluginException($"{player.Identity} didn't take that ({e.Message}).", e);
        }
        lock (_gate) _currentPlayer = player.BusName;
        ScheduleRead();
        return true;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _debounce?.Dispose();
            foreach (var s in _subscriptions) s.Dispose();
            _subscriptions.Clear();
        }
    }
}
