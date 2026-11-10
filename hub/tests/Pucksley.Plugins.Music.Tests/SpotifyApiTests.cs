using System.Net;
using Pucksley.Plugins.Music.Tests.Support;
using Pucksley.Plugins.Spotify;
using Pucksley.Sdk;

namespace Pucksley.Plugins.Music.Tests;

/// <summary>Token refresh, retries and error messages of the Web API client.</summary>
public sealed class SpotifyApiTests
{
    sealed class Harness
    {
        public FakeSpotify Spotify { get; } = new();
        public string RefreshToken { get; set; } = "refresh-1";
        public List<string> Saved { get; } = [];
        public bool SignInLost { get; private set; }
        public List<TimeSpan> Waits { get; } = [];
        public SpotifyApi Api { get; }

        public Harness()
        {
            Api = new SpotifyApi(Spotify.CreateClient(), () => "client-123", () => RefreshToken, t => { Saved.Add(t); RefreshToken = t; }, () => SignInLost = true)
            {
                Delay = (wait, _) =>
                {
                    Waits.Add(wait);
                    return Task.CompletedTask;
                },
            };
        }
    }

    [Fact]
    public async Task The_access_token_is_refreshed_once_and_reused()
    {
        var h = new Harness();
        h.Spotify.Route("GET me/player/devices", """{"devices":[]}""");

        await h.Api.GetAsync("me/player/devices", default);
        await h.Api.GetAsync("me/player/devices", default);

        var token = Assert.Single(h.Spotify.TokenRequests);
        Assert.Equal(("refresh_token", "refresh-1", "client-123"), (token["grant_type"], token["refresh_token"], token["client_id"]));
        Assert.All(h.Spotify.ApiRequests, r => Assert.Equal("Bearer access-1", r.Headers["Authorization"]));
    }

    [Fact]
    public async Task A_401_refreshes_the_token_and_retries_once()
    {
        var h = new Harness();
        h.Spotify.RouteSequence("GET me/player", (HttpStatusCode.Unauthorized, """{"error":{"status":401,"message":"The access token expired"}}"""),
            (HttpStatusCode.OK, FakeSpotify.PlayerOnKitchen));

        var json = await h.Api.GetAsync("me/player", default);

        Assert.Equal("Kitchen", json?["device"]?["name"]?.GetValue<string>());
        Assert.Equal(2, h.Spotify.TokenRequests.Count);
        Assert.Equal(["Bearer access-1", "Bearer access-2"], h.Spotify.ApiRequests.Select(r => r.Headers["Authorization"]));
    }

    [Fact]
    public async Task A_rotated_refresh_token_is_saved()
    {
        var h = new Harness();
        h.Spotify.RotateRefreshTokenTo = "refresh-2";
        h.Spotify.Route("GET me", "{}");

        await h.Api.GetAsync("me", default);

        Assert.Equal(["refresh-2"], h.Saved);
    }

    [Fact]
    public async Task A_short_429_is_waited_out_and_retried()
    {
        var h = new Harness();
        int calls = 0;
        h.Spotify.Routes["GET me/player/devices"] = _ => ++calls == 1
            ? new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Headers = { { "Retry-After", "2" } } }
            : FakeHttpHandler.Json("""{"devices":[]}""");

        await h.Api.GetAsync("me/player/devices", default);

        Assert.Equal([TimeSpan.FromSeconds(2)], h.Waits);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task A_long_429_is_reported_instead_of_waiting()
    {
        var h = new Harness();
        h.Spotify.Routes["GET me/player/devices"] = _ =>
            new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Headers = { { "Retry-After", "30" } } };

        var e = await Assert.ThrowsAsync<PluginException>(() => h.Api.GetAsync("me/player/devices", default));

        Assert.Equal("Spotify is rate-limiting requests; try again in 30 seconds.", e.Message);
        Assert.Empty(h.Waits);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, "NO_ACTIVE_DEVICE", "No Spotify device is playing right now. Say where to play, e.g. \"on the kitchen speaker\".")]
    [InlineData(HttpStatusCode.Forbidden, "PREMIUM_REQUIRED", "Controlling Spotify playback needs Spotify Premium.")]
    [InlineData(HttpStatusCode.Forbidden, "NO_NEXT_TRACK", "There's no next track.")]
    public async Task Player_errors_become_sentences(HttpStatusCode status, string reason, string expected)
    {
        var h = new Harness();
        h.Spotify.Route("POST me/player/next", $$$"""{"error":{"status":{{{(int)status}}},"message":"Player command failed","reason":"{{{reason}}}"}}""", status);

        var e = await Assert.ThrowsAsync<PluginException>(() => h.Api.SendAsync(HttpMethod.Post, "me/player/next", null, default));

        Assert.Equal(expected, e.Message);
    }

    [Fact]
    public async Task Raw_requests_expose_the_reason_for_fallbacks()
    {
        var h = new Harness();
        h.Spotify.Route("POST me/player/queue", """{"error":{"status":404,"message":"Player command failed: No active device found","reason":"NO_ACTIVE_DEVICE"}}""", HttpStatusCode.NotFound);

        var r = await h.Api.SendRawAsync(HttpMethod.Post, "me/player/queue?uri=spotify%3Atrack%3Ax", null, default);

        Assert.False(r.Ok);
        Assert.Equal((404, "NO_ACTIVE_DEVICE"), (r.Status, r.Reason));
    }

    [Fact]
    public async Task A_revoked_refresh_token_asks_to_reconnect()
    {
        var h = new Harness();
        h.Spotify.TokenOverride = _ => FakeHttpHandler.Json("""{"error":"invalid_grant","error_description":"Refresh token revoked"}""", HttpStatusCode.BadRequest);

        var e = await Assert.ThrowsAsync<PluginException>(() => h.Api.GetAsync("me", default));

        Assert.Equal(SpotifyApi.SignInExpiredMessage, e.Message);
        Assert.True(h.SignInLost);
    }

    [Fact]
    public async Task Without_a_refresh_token_it_says_to_connect()
    {
        var h = new Harness { RefreshToken = "" };
        var e = await Assert.ThrowsAsync<PluginException>(() => h.Api.GetAsync("me", default));
        Assert.Equal(SpotifyApi.NotConnectedMessage, e.Message);
        Assert.Empty(h.Spotify.TokenRequests);
    }

    [Fact]
    public async Task Bodies_are_sent_as_json()
    {
        var h = new Harness();
        h.Spotify.Routes["PUT me/player/play"] = _ => new HttpResponseMessage(HttpStatusCode.NoContent);

        var r = await h.Api.SendRawAsync(HttpMethod.Put, "me/player/play?device_id=d1",
            new System.Text.Json.Nodes.JsonObject { ["context_uri"] = "spotify:album:1" }, default);

        Assert.True(r.Ok);
        var request = h.Spotify.Requests("PUT", "me/player/play").Single();
        Assert.Equal("""{"context_uri":"spotify:album:1"}""", request.Body);
        Assert.StartsWith("application/json", request.Headers["Content-Type"]);
        Assert.Equal("?device_id=d1", request.Uri.Query);
    }

    [Fact]
    public void Missing_scopes_are_the_requested_ones_spotify_left_out()
    {
        var all = SpotifyAuth.Scopes;
        Assert.Empty(SpotifyApi.MissingScopes(all));
        Assert.Equal(["user-modify-playback-state"], SpotifyApi.MissingScopes(all.Replace("user-modify-playback-state", "")));
    }

    [Fact]
    public void A_scope_error_says_to_connect_again()
    {
        var text = SpotifyApi.Describe(new SpotifyResponse(403, null, Message: "Insufficient client scope"));
        Assert.Contains("Connect again", text);
    }
}
