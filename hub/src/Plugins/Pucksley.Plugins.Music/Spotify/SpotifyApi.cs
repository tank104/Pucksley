using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Pucksley.Sdk;
using static Pucksley.Plugins.Spotify.SpotifyJson;

namespace Pucksley.Plugins.Spotify;

/// <summary>A Web API answer: the status, its JSON and Spotify's error reason/message (if any).</summary>
public sealed record SpotifyResponse(int Status, JsonNode? Json, string? Reason = null, string? Message = null, TimeSpan? RetryAfter = null)
{
    public bool Ok => Status is >= 200 and < 300;
}

/// <summary>Tokens from Spotify's accounts service.</summary>
/// <param name="Scope">The permissions Spotify actually granted (space-separated), when it says.</param>
public sealed record SpotifyTokens(string AccessToken, string? RefreshToken, TimeSpan ExpiresIn, string? Scope = null);

/// <summary>
/// The Spotify Web API with OAuth token handling: the access token is kept in memory until about a
/// minute before it expires, refreshed (once) on a 401, short 429 waits are honoured, and errors
/// become sentences for people.
/// </summary>
public sealed class SpotifyApi
{
    public const string ApiBase = "https://api.spotify.com/v1/";
    public const string TokenEndpoint = "https://accounts.spotify.com/api/token";
    public const string NotConnectedMessage = "Spotify isn't connected. Connect it on the hub's Plugins page.";
    public const string SignInExpiredMessage = "Spotify's sign-in has expired. Click Connect on the Spotify card in the hub's Plugins page.";

    static readonly TimeSpan MaxRetryWait = TimeSpan.FromSeconds(5);
    static readonly TimeSpan RefreshMargin = TimeSpan.FromSeconds(60);

    readonly HttpClient _http;
    readonly Func<string> _clientId;
    readonly Func<string> _refreshToken;
    readonly Action<string> _saveRefreshToken;
    readonly Action _signInLost;
    readonly SemaphoreSlim _tokenLock = new(1, 1);
    readonly object _gate = new();
    string? _accessToken;
    DateTimeOffset _expiresAt;

    /// <param name="clientId">The app's client id (read on each refresh: settings can change).</param>
    /// <param name="refreshToken">The stored refresh token.</param>
    /// <param name="saveRefreshToken">Persists a rotated refresh token.</param>
    /// <param name="signInLost">The refresh token was refused (revoked, or the app changed).</param>
    public SpotifyApi(HttpClient http, Func<string> clientId, Func<string> refreshToken, Action<string> saveRefreshToken, Action signInLost)
    {
        _http = http;
        _clientId = clientId;
        _refreshToken = refreshToken;
        _saveRefreshToken = saveRefreshToken;
        _signInLost = signInLost;
    }

    /// <summary>Per-request timeout.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>How 429 waits are made (tests replace it).</summary>
    internal Func<TimeSpan, CancellationToken, Task> Delay { get; set; } = Task.Delay;

    /// <summary>Drops the cached access token (after disconnecting or new settings).</summary>
    public void Forget()
    {
        lock (_gate) _accessToken = null;
    }

    // ---------------------------------------------------------------- tokens

    /// <summary>Trades the sign-in code for tokens (authorization code + PKCE).</summary>
    public async Task<SpotifyTokens> ExchangeCodeAsync(string clientId, string code, string redirectUri, string verifier, CancellationToken cancel)
    {
        var tokens = await PostTokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
            ["client_id"] = clientId,
            ["code_verifier"] = verifier,
        }, refreshing: false, cancel).ConfigureAwait(false);
        Remember(tokens);
        return tokens;
    }

    void Remember(SpotifyTokens tokens)
    {
        lock (_gate)
        {
            _accessToken = tokens.AccessToken;
            _expiresAt = DateTimeOffset.UtcNow + tokens.ExpiresIn;
        }
        if (tokens.Scope is { } scope && scope != GrantedScope)
        {
            GrantedScope = scope;
            ScopeGranted?.Invoke(scope);
        }
    }

    /// <summary>What Spotify granted with the last token (null until it says).</summary>
    public string? GrantedScope { get; private set; }

    /// <summary>Spotify granted a (different) set of permissions: on sign-in, and on a refresh that changes them.</summary>
    public Action<string>? ScopeGranted { get; set; }

    /// <summary>Requested permissions that <paramref name="granted"/> lacks.</summary>
    public static IReadOnlyList<string> MissingScopes(string granted) =>
        SpotifyAuth.Scopes.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Except(granted.Split(' ', StringSplitOptions.RemoveEmptyEntries), StringComparer.Ordinal).ToList();

    /// <summary>A usable access token, refreshing it when it's (nearly) expired or was just <paramref name="rejected"/>.</summary>
    async Task<string> AccessTokenAsync(string? rejected, CancellationToken cancel)
    {
        lock (_gate)
            if (_accessToken is not null && _accessToken != rejected && DateTimeOffset.UtcNow < _expiresAt - RefreshMargin)
                return _accessToken;

        await _tokenLock.WaitAsync(cancel).ConfigureAwait(false);
        try
        {
            lock (_gate) // someone else may have refreshed while we waited
                if (_accessToken is not null && _accessToken != rejected && DateTimeOffset.UtcNow < _expiresAt - RefreshMargin)
                    return _accessToken;

            var refresh = _refreshToken();
            var clientId = _clientId();
            if (string.IsNullOrWhiteSpace(refresh) || string.IsNullOrWhiteSpace(clientId)) throw new PluginException(NotConnectedMessage);
            var tokens = await PostTokenAsync(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = refresh,
                ["client_id"] = clientId,
            }, refreshing: true, cancel).ConfigureAwait(false);
            Remember(tokens);
            // PKCE refresh tokens can rotate: keep the new one or the next refresh fails.
            if (!string.IsNullOrEmpty(tokens.RefreshToken) && tokens.RefreshToken != refresh) _saveRefreshToken(tokens.RefreshToken);
            return tokens.AccessToken;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    async Task<SpotifyTokens> PostTokenAsync(Dictionary<string, string> form, bool refreshing, CancellationToken cancel)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        cts.CancelAfter(Timeout);
        string text;
        int status;
        try
        {
            using var response = await _http.PostAsync(TokenEndpoint, new FormUrlEncodedContent(form), cts.Token).ConfigureAwait(false);
            status = (int)response.StatusCode;
            text = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
        {
            throw new PluginException("Spotify's sign-in service didn't answer in time. Try again in a moment.");
        }
        catch (HttpRequestException e)
        {
            throw new PluginException($"I couldn't reach Spotify ({e.Message}).", e);
        }

        var json = TryParse(text);
        if (status is < 200 or >= 300)
        {
            // {"error":"invalid_grant","error_description":"Refresh token revoked"}
            var error = Str(json?["error"]) ?? Str(json?["error"]?["message"]);
            var description = Str(json?["error_description"]);
            if (refreshing && (error is "invalid_grant" or "invalid_client" || status == 401))
            {
                Forget();
                _signInLost();
                throw new PluginException(SignInExpiredMessage);
            }
            throw new PluginException($"Spotify sign-in failed: {description ?? error ?? $"HTTP {status}"}.");
        }
        var access = Str(json?["access_token"]) ?? throw new PluginException("Spotify's sign-in answer had no access token.");
        return new SpotifyTokens(access, Str(json?["refresh_token"]), TimeSpan.FromSeconds(Int(json?["expires_in"]) ?? 3600), Str(json?["scope"]));
    }

    // ---------------------------------------------------------------- requests

    public Task<JsonNode?> GetAsync(string path, CancellationToken cancel) => SendAsync(HttpMethod.Get, path, null, cancel);

    /// <summary>A request that must succeed: errors throw a <see cref="PluginException"/> with a friendly message.</summary>
    public async Task<JsonNode?> SendAsync(HttpMethod method, string path, JsonNode? body = null, CancellationToken cancel = default)
    {
        var response = await SendRawAsync(method, path, body, cancel).ConfigureAwait(false);
        if (!response.Ok) throw new PluginException(Describe(response));
        return response.Json;
    }

    /// <summary>
    /// A request whose failures the caller wants to look at (e.g. to fall back to Sonos on
    /// NO_ACTIVE_DEVICE). Only sign-in and network problems throw.
    /// </summary>
    public async Task<SpotifyResponse> SendRawAsync(HttpMethod method, string path, JsonNode? body = null, CancellationToken cancel = default)
    {
        string? rejected = null;
        bool refreshed = false, waited = false;
        while (true)
        {
            var token = await AccessTokenAsync(rejected, cancel).ConfigureAwait(false);
            using var request = new HttpRequestMessage(method, path.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? path : ApiBase + path.TrimStart('/'));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (body is not null) request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
            else if (method == HttpMethod.Put || method == HttpMethod.Post) request.Content = new ByteArrayContent([]); // Content-Length: 0

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            cts.CancelAfter(Timeout);
            int status;
            string text;
            TimeSpan? retryAfter;
            try
            {
                using var response = await _http.SendAsync(request, cts.Token).ConfigureAwait(false);
                status = (int)response.StatusCode;
                retryAfter = response.Headers.RetryAfter?.Delta
                             ?? (response.Headers.RetryAfter?.Date is { } date ? date - DateTimeOffset.UtcNow : null);
                text = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
            {
                throw new PluginException("Spotify didn't answer in time. Try again in a moment.");
            }
            catch (HttpRequestException e)
            {
                throw new PluginException($"I couldn't reach Spotify ({e.Message}).", e);
            }

            if (status == 401 && !refreshed)
            {
                refreshed = true;
                rejected = token;
                continue;
            }
            if (status == 429 && !waited && (retryAfter ?? TimeSpan.FromSeconds(1)) <= MaxRetryWait)
            {
                waited = true;
                await Delay(retryAfter is { } wait && wait > TimeSpan.Zero ? wait : TimeSpan.FromSeconds(1), cancel).ConfigureAwait(false);
                continue;
            }

            var json = TryParse(text);
            if (status is >= 200 and < 300) return new SpotifyResponse(status, json);
            // {"error":{"status":404,"message":"Player command failed: No active device found","reason":"NO_ACTIVE_DEVICE"}}
            var error = json?["error"];
            return new SpotifyResponse(status, json, Str(error?["reason"]), Str(error?["message"]) ?? Str(error), retryAfter);
        }
    }

    static JsonNode? TryParse(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        try
        {
            return JsonNode.Parse(text);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>An error as a sentence for the person.</summary>
    public static string Describe(SpotifyResponse r) => (r.Status, r.Reason) switch
    {
        (_, "NO_ACTIVE_DEVICE") => "No Spotify device is playing right now. Say where to play, e.g. \"on the kitchen speaker\".",
        (_, "PREMIUM_REQUIRED") => "Controlling Spotify playback needs Spotify Premium.",
        (_, "NO_PREV_TRACK") => "There's no previous track.",
        (_, "NO_NEXT_TRACK") => "There's no next track.",
        (_, "NO_SPECIFIC_TRACK") => "Spotify couldn't find that track.",
        (_, "ALREADY_PAUSED") => "Spotify is already paused.",
        (_, "NOT_PAUSED" or "ALREADY_PLAYING") => "Spotify is already playing.",
        (_, "NOT_PLAYING_LOCALLY" or "NOT_PLAYING_TRACK" or "NOT_PLAYING_CONTEXT") => "Nothing is playing on Spotify right now.",
        (_, "ENDLESS_CONTEXT") => "Shuffle and repeat aren't available for what's playing.",
        (_, "CONTEXT_DISALLOW") => "Spotify doesn't allow that for what's playing.",
        (_, "REMOTE_CONTROL_DISALLOW" or "DEVICE_NOT_CONTROLLABLE") => "That device can't be controlled from here.",
        (_, "VOLUME_CONTROL_DISALLOW") => "That device's volume can't be changed through Spotify.",
        (_, "RATE_LIMITED") => "Spotify is rate-limiting requests; try again in a few seconds.",
        (429, _) => $"Spotify is rate-limiting requests; try again in {Math.Max(1, (int)Math.Ceiling((r.RetryAfter ?? TimeSpan.FromSeconds(5)).TotalSeconds))} seconds.",
        (401, _) => "Spotify rejected the sign-in. Click Connect on the Spotify card in the hub's Plugins page.",
        (403, _) when r.Message?.Contains("scope", StringComparison.OrdinalIgnoreCase) == true =>
            "Spotify hasn't given the hub permission for that. Disconnect and Connect again on the Spotify card, and approve everything it asks for.",
        (403, _) => r.Message is { Length: > 0 } m ? $"Spotify won't allow that ({m})." : "Spotify won't allow that.",
        (404, _) when r.Message?.Contains("device", StringComparison.OrdinalIgnoreCase) == true =>
            "That Spotify device isn't available; it may be asleep. Open Spotify on it, or pick another device.",
        (404, _) => "Spotify couldn't find that.",
        ( >= 500, _) => "Spotify is having problems right now. Try again shortly.",
        _ => $"Spotify said: {r.Message ?? $"HTTP {r.Status}"}.",
    };
}
