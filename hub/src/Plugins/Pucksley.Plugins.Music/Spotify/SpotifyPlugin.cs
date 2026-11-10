using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Pucksley.Sdk;

namespace Pucksley.Plugins.Spotify;

/// <summary>
/// Spotify through the Web API (Premium needed for playback control): search and play, transport,
/// library and devices, on Spotify Connect devices or on Sonos rooms through the Sonos plugin.
/// Sign-in is OAuth with PKCE, so only a Client ID is needed. Tools are in SpotifyPlugin.Tools.cs.
/// </summary>
public sealed partial class SpotifyPlugin : PuckPlugin<SpotifySettings>, IMediaSourceProvider
{
    const string SourceId = "active";
    static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(15);
    static readonly TimeSpan PendingSignInLifetime = TimeSpan.FromMinutes(30);

    SpotifyApi _api = null!;
    readonly object _gate = new();
    readonly Dictionary<string, PendingSignIn> _pending = new();   // by OAuth state
    SpotifyPlayback? _playback;                                     // last known player state
    DateTimeOffset _playbackAt = DateTimeOffset.MinValue;
    int _volumeBeforeMute = 30;
    CancellationTokenSource? _stop;
    Task? _loop;

    sealed record PendingSignIn(string Verifier, DateTimeOffset Created);

    /// <summary>Tests: how long to let a woken device settle before playing on it.</summary>
    internal TimeSpan TransferSettle { get; set; } = TimeSpan.FromMilliseconds(700);

    public override string Id => "spotify";
    public override string Name => "Spotify";
    public override string Description =>
        "Play and control Spotify (Premium) on Spotify Connect devices, or on Sonos rooms through the Sonos plugin.";
    public override string Icon => "music";
    public override bool EnabledByDefault => false;

    public override string? Instructions =>
        "Spotify: spotify_play searches and plays an artist, album, track or playlist; device can be a Spotify Connect device " +
        "or a Sonos room and defaults to the puck's room. Use spotify_play_playlist for the person's own playlists and Liked Songs, " +
        "and spotify_recent for \"what was that song?\".";

    /// <summary>Where Spotify sends the browser after sign-in (Spotify requires a loopback IP, not "localhost").</summary>
    public string RedirectUri => Context.Hub.LoopbackUrl.ToString().TrimEnd('/') + "/plugins/spotify/callback";

    bool IsConnected => !string.IsNullOrWhiteSpace(Settings.ClientId) && !string.IsNullOrWhiteSpace(Settings.RefreshToken);

    public override void Initialize(IPluginContext context)
    {
        base.Initialize(context);
        _api = new SpotifyApi(context.Http, () => Settings.ClientId.Trim(), () => Settings.RefreshToken, SaveRotatedRefreshToken, OnSignInLost)
        {
            ScopeGranted = OnScopeGranted,
        };
    }

    // Spotify can grant less than was asked for; "Insufficient client scope" then needs the missing names.
    void OnScopeGranted(string scope)
    {
        var missing = SpotifyApi.MissingScopes(scope);
        if (missing.Count == 0) Log.LogInformation("Spotify: granted every permission asked for ({Scope})", scope);
        else Log.LogWarning("Spotify: granted {Scope} but not {Missing}; disconnect and Connect again to approve them", scope, string.Join(" ", missing));
    }

    protected override void OnSettingsApplied() => _api?.Forget();

    public override Task StartAsync(CancellationToken cancel)
    {
        UpdateStatus();
        var stop = new CancellationTokenSource();
        _stop = stop;
        _loop = Task.Run(() => PollLoopAsync(stop.Token), CancellationToken.None);
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

    void UpdateStatus()
    {
        if (string.IsNullOrWhiteSpace(Settings.ClientId))
            SetStatus(StatusLevel.Warning, "Enter your Spotify app's Client ID, then click Connect");
        else if (string.IsNullOrWhiteSpace(Settings.RefreshToken))
            SetStatus(StatusLevel.Warning, "Not connected — click Connect");
        else
            SetStatus(StatusLevel.Ok, string.IsNullOrWhiteSpace(Settings.AccountName) ? "Connected" : $"Connected as {Settings.AccountName}");
    }

    void SaveRotatedRefreshToken(string token)
    {
        lock (_gate) Settings.RefreshToken = token;
        SaveSettings();
    }

    void OnSignInLost()
    {
        lock (_gate)
        {
            Settings.RefreshToken = "";
            _playback = null;
        }
        SaveSettings();
        SetStatus(StatusLevel.Warning, "Sign-in expired — click Connect");
        Context.NowPlayingChanged(SourceId);
    }

    void RequireConnected()
    {
        if (!IsConnected) throw new PluginException(SpotifyApi.NotConnectedMessage);
    }

    // ---------------------------------------------------------------- sign-in

    public override IReadOnlyList<PluginAction> GetActions()
    {
        var actions = new List<PluginAction>
        {
            new("connect", IsConnected ? "Reconnect" : "Connect",
                $"Sign in to Spotify. Your Spotify app's redirect URI must be exactly {RedirectUri}"),
            new("finish", "Finish sign-in",
                "Only if the page after signing in didn't load (the hub is on another computer): paste that page's full address here.",
                [new ActionInput("url", "Address of the page Spotify sent you to", RedirectUri + "?code=…")]),
        };
        if (IsConnected) actions.Add(new PluginAction("disconnect", "Disconnect", "Forget the Spotify sign-in."));
        return actions;
    }

    public override async Task<ActionResult> RunActionAsync(string actionId, IReadOnlyDictionary<string, string> input, CancellationToken cancel)
    {
        switch (actionId)
        {
            case "connect":
                return StartSignIn();
            case "finish":
            {
                var (code, state, error) = SpotifyAuth.ParseRedirect(input.GetValueOrDefault("url"));
                var (ok, message) = await CompleteSignInAsync(code, state, error, cancel);
                return new ActionResult(message, ok);
            }
            case "disconnect":
                lock (_gate)
                {
                    Settings.RefreshToken = "";
                    Settings.AccountName = "";
                    _playback = null;
                }
                _api.Forget();
                SaveSettings();
                UpdateStatus();
                Context.NowPlayingChanged(SourceId);
                return new ActionResult("Spotify disconnected.");
            default:
                return new ActionResult($"Unknown action '{actionId}'.", false);
        }
    }

    ActionResult StartSignIn()
    {
        var clientId = Settings.ClientId.Trim();
        if (clientId.Length == 0) return new ActionResult("Enter your Spotify app's Client ID in the settings and save, then click Connect.", false);
        var verifier = Pkce.CreateVerifier();
        var state = Pkce.CreateState();
        lock (_gate)
        {
            PrunePendingSignIns();
            _pending[state] = new PendingSignIn(verifier, DateTimeOffset.UtcNow);
        }
        var url = SpotifyAuth.AuthorizeUrl(clientId, RedirectUri, Pkce.Challenge(verifier), state);
        return new ActionResult("Opening Spotify's sign-in page…", true, url);
    }

    void PrunePendingSignIns()
    {
        foreach (var (state, p) in _pending.ToList())
            if (DateTimeOffset.UtcNow - p.Created > PendingSignInLifetime) _pending.Remove(state);
    }

    /// <summary>The redirect from Spotify's sign-in page.</summary>
    public override async Task<PluginHttpResponse?> HandleHttpAsync(PluginHttpRequest request, CancellationToken cancel)
    {
        if (!string.Equals(request.Path.Trim('/'), "callback", StringComparison.OrdinalIgnoreCase)) return null;
        var (ok, message) = await CompleteSignInAsync(request.Query.GetValueOrDefault("code"), request.Query.GetValueOrDefault("state"),
            request.Query.GetValueOrDefault("error"), cancel);
        return PluginHttpResponse.Html(ResultPage(ok, message), ok ? 200 : 400);
    }

    /// <summary>Exchanges the code for tokens and saves the refresh token. Never throws.</summary>
    internal async Task<(bool Ok, string Message)> CompleteSignInAsync(string? code, string? state, string? error, CancellationToken cancel)
    {
        if (!string.IsNullOrEmpty(error))
            return (false, error == "access_denied" ? "Spotify sign-in was cancelled." : $"Spotify sign-in failed: {error}.");
        if (string.IsNullOrWhiteSpace(code)) return (false, "There was no sign-in code in that address. Click Connect and try again.");

        PendingSignIn? pending = null;
        lock (_gate)
        {
            PrunePendingSignIns();
            if (state is not null && _pending.Remove(state, out var p)) pending = p;
            else if (state is null && _pending.Count == 1)
            {
                // Only the code was pasted: it can only belong to the one sign-in in progress.
                var only = _pending.First();
                _pending.Remove(only.Key);
                pending = only.Value;
            }
        }
        if (pending is null) return (false, "That sign-in has expired (or the hub restarted since). Click Connect again.");

        try
        {
            var tokens = await _api.ExchangeCodeAsync(Settings.ClientId.Trim(), code, RedirectUri, pending.Verifier, cancel);
            if (string.IsNullOrEmpty(tokens.RefreshToken)) return (false, "Spotify didn't send a refresh token. Click Connect and try again.");
            lock (_gate) Settings.RefreshToken = tokens.RefreshToken;
            string name = "";
            try
            {
                var me = await _api.GetAsync("me", cancel);
                name = SpotifyJson.Str(me?["display_name"]) ?? SpotifyJson.Str(me?["id"]) ?? "";
            }
            catch (PluginException e)
            {
                Log.LogInformation("Spotify: connected, but couldn't read the account name: {Error}", e.Message);
            }
            lock (_gate) Settings.AccountName = name;
            SaveSettings();
            UpdateStatus();
            _ = Task.Run(() => TryRefreshPlaybackAsync(CancellationToken.None), CancellationToken.None);
            return (true, name.Length > 0 ? $"Spotify is connected as {name}." : "Spotify is connected.");
        }
        catch (PluginException e)
        {
            var hint = e.Message.Contains("redirect", StringComparison.OrdinalIgnoreCase)
                ? $" The redirect URI on your Spotify app must be exactly {RedirectUri}"
                : "";
            return (false, e.Message + hint);
        }
    }

    static string ResultPage(bool ok, string message) =>
        $$"""
        <!doctype html><html><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
        <title>Spotify · Pucksley</title>
        <style>body{font-family:system-ui,sans-serif;background:#121212;color:#eee;display:grid;place-items:center;min-height:90vh;margin:0}
        main{max-width:30rem;padding:2rem;text-align:center}h1{color:{{(ok ? "#1db954" : "#f15e6c")}}}</style></head>
        <body><main><h1>{{(ok ? "Connected" : "Not connected")}}</h1><p>{{WebUtility.HtmlEncode(message)}}</p>
        <p>{{(ok ? "You can close this tab." : "Go back to the hub's Plugins page and try again.")}}</p></main></body></html>
        """;

    // ---------------------------------------------------------------- now playing cache

    async Task PollLoopAsync(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            if (IsConnected) await TryRefreshPlaybackAsync(stop);
            try
            {
                await Task.Delay(PollInterval, stop);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    async Task TryRefreshPlaybackAsync(CancellationToken cancel)
    {
        try
        {
            await RefreshPlaybackAsync(cancel);
        }
        catch (Exception e) when (e is PluginException || (e is OperationCanceledException && !cancel.IsCancellationRequested))
        {
            Log.LogDebug("Spotify: couldn't read the player: {Error}", e.Message);
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>Reads the player (one live call) and updates the cache; null when nothing is playing.</summary>
    async Task<SpotifyPlayback?> RefreshPlaybackAsync(CancellationToken cancel)
    {
        var r = await _api.SendRawAsync(HttpMethod.Get, "me/player?additional_types=episode", null, cancel);
        if (!r.Ok) throw new PluginException(SpotifyApi.Describe(r));
        var playback = r.Status == 204 ? null : SpotifyPlayback.Parse(r.Json);
        SpotifyPlayback? old;
        lock (_gate)
        {
            old = _playback;
            _playback = playback;
            _playbackAt = DateTimeOffset.UtcNow;
        }
        if (!SpotifyPlayback.Same(old, playback)) Context.NowPlayingChanged(SourceId);
        return playback;
    }

    (SpotifyPlayback? Playback, DateTimeOffset At) CachedPlayback()
    {
        lock (_gate) return (_playback, _playbackAt);
    }

    /// <summary>Reads the player again shortly after a command, so the pucks and context catch up.</summary>
    void RefreshSoon()
    {
        _ = Task.Run(async () =>
        {
            await Task.Delay(800);
            await TryRefreshPlaybackAsync(CancellationToken.None);
        });
    }

    /// <summary>From the cache only (refreshed every 15 s while connected): <c>Spotify: playing "X" by Y on Kitchen.</c></summary>
    public override Task<string?> GetContextAsync(ToolCaller caller, CancellationToken cancel)
    {
        if (!IsConnected) return Task.FromResult<string?>(null);
        var (playback, at) = CachedPlayback();
        if (playback?.Item is not { } item || DateTimeOffset.UtcNow - at > TimeSpan.FromMinutes(2)) return Task.FromResult<string?>(null);
        var by = item.Artists is null ? "" : $" by {Clip(item.Artists, 60)}";
        var on = playback.Device is null ? "" : $" on {playback.Device.Name}";
        return Task.FromResult<string?>($"Spotify: {(playback.IsPlaying ? "playing" : "paused")} \"{Clip(item.Name, 80)}\"{by}{on}.");
    }

    static string Clip(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    // ---------------------------------------------------------------- IMediaSourceProvider

    public IReadOnlyList<MediaSourceInfo> GetMediaSources() => [new MediaSourceInfo(SourceId, "Spotify")];

    public async Task<NowPlaying?> GetNowPlayingAsync(string sourceId, CancellationToken cancel)
    {
        if (sourceId != SourceId || !IsConnected) return null;
        var (playback, at) = CachedPlayback();
        if (DateTimeOffset.UtcNow - at > TimeSpan.FromSeconds(5))
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            cts.CancelAfter(TimeSpan.FromSeconds(3));
            try
            {
                playback = await RefreshPlaybackAsync(cts.Token);
            }
            catch (Exception e) when (e is PluginException || (e is OperationCanceledException && !cancel.IsCancellationRequested)) { }
        }
        if (playback is null) return new NowPlaying { Playing = false, Source = "Spotify" };
        return new NowPlaying
        {
            Title = playback.Item?.Name,
            Artist = playback.Item?.Artists,
            Album = playback.Item?.Album,
            Playing = playback.IsPlaying,
            Volume = playback.Device?.Volume,
            Muted = playback.Device?.Volume is int v ? v == 0 : null,
            Source = playback.Device is null ? "Spotify" : $"Spotify on {playback.Device.Name}",
        };
    }

    public async Task MediaCommandAsync(string sourceId, MediaCommand command, CancellationToken cancel)
    {
        RequireConnected();
        switch (command.Action)
        {
            case MediaAction.Toggle:
            {
                var (playback, at) = CachedPlayback();
                if (DateTimeOffset.UtcNow - at > TimeSpan.FromSeconds(5)) playback = await RefreshPlaybackAsync(cancel);
                await TransportAsync(playback?.IsPlaying == true ? "pause" : "play", null, cancel);
                break;
            }
            case MediaAction.Play:
                await TransportAsync("play", null, cancel);
                break;
            case MediaAction.Pause:
                await TransportAsync("pause", null, cancel);
                break;
            case MediaAction.Next:
                await TransportAsync("next", null, cancel);
                break;
            case MediaAction.Previous:
                await TransportAsync("previous", null, cancel);
                break;
            case MediaAction.SetVolume when command.Volume is int volume:
                await _api.SendAsync(HttpMethod.Put, $"me/player/volume?volume_percent={Math.Clamp(volume, 0, 100)}", null, cancel);
                break;
            case MediaAction.ToggleMute:
            {
                // Spotify has no mute: go to 0 and back.
                var playback = await RefreshPlaybackAsync(cancel);
                int current = playback?.Device?.Volume ?? 0;
                int target = current > 0 ? 0 : Math.Max(5, _volumeBeforeMute);
                if (current > 0) _volumeBeforeMute = current;
                await _api.SendAsync(HttpMethod.Put, $"me/player/volume?volume_percent={target}", null, cancel);
                break;
            }
        }
        RefreshSoon();
    }

    /// <summary>play / pause / next / previous on the active device (or <paramref name="deviceId"/>), tolerating "already".</summary>
    async Task TransportAsync(string action, string? deviceId, CancellationToken cancel)
    {
        var (method, path, okReasons) = action switch
        {
            "play" => (HttpMethod.Put, "me/player/play", new[] { "NOT_PAUSED", "ALREADY_PLAYING" }),
            "pause" => (HttpMethod.Put, "me/player/pause", new[] { "ALREADY_PAUSED" }),
            "next" => (HttpMethod.Post, "me/player/next", Array.Empty<string>()),
            _ => (HttpMethod.Post, "me/player/previous", Array.Empty<string>()),
        };
        if (deviceId is not null) path += "?device_id=" + Uri.EscapeDataString(deviceId);
        var r = await _api.SendRawAsync(method, path, null, cancel);
        if (r.Ok || (r.Reason is { } reason && okReasons.Contains(reason))) return;
        if (r.Reason == "NO_ACTIVE_DEVICE" && Context.GetPlugin<Sonos.SonosPlugin>() is not null)
            throw new PluginException("Nothing is playing through Spotify Connect. If the music is on a Sonos room, use the Sonos controls for it.");
        throw new PluginException(SpotifyApi.Describe(r));
    }
}
