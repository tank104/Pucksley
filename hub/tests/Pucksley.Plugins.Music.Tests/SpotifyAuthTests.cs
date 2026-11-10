using System.Net;
using Pucksley.Plugins.Music.Tests.Support;
using Pucksley.Plugins.Spotify;
using Pucksley.Sdk;

namespace Pucksley.Plugins.Music.Tests;

/// <summary>PKCE and the sign-in flow (Connect → Spotify → callback, or a pasted address).</summary>
public sealed class SpotifyAuthTests
{
    static readonly Dictionary<string, string> NoInput = [];

    [Fact]
    public void Challenge_matches_the_rfc_7636_example() =>
        Assert.Equal("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM", Pkce.Challenge("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk"));

    [Fact]
    public void Verifiers_are_random_and_use_only_unreserved_characters()
    {
        var a = Pkce.CreateVerifier();
        var b = Pkce.CreateVerifier();
        Assert.NotEqual(a, b);
        Assert.Equal(64, a.Length);
        Assert.Matches("^[A-Za-z0-9._~-]+$", a);
        Assert.Matches("^[A-Za-z0-9_-]{43}$", Pkce.Challenge(a)); // 32 bytes, base64url, no padding
        Assert.Throws<ArgumentOutOfRangeException>(() => Pkce.CreateVerifier(42));
    }

    [Fact]
    public void Authorize_url_carries_the_pkce_challenge_and_scopes()
    {
        var url = new Uri(SpotifyAuth.AuthorizeUrl("client-123", "http://127.0.0.1:8765/plugins/spotify/callback", "challenge-x", "state-y"));
        var q = SpotifyAuth.ParseQuery(url.Query);

        Assert.Equal("https://accounts.spotify.com/authorize", url.GetLeftPart(UriPartial.Path));
        Assert.Equal("client-123", q["client_id"]);
        Assert.Equal("code", q["response_type"]);
        Assert.Equal("http://127.0.0.1:8765/plugins/spotify/callback", q["redirect_uri"]);
        Assert.Equal("S256", q["code_challenge_method"]);
        Assert.Equal("challenge-x", q["code_challenge"]);
        Assert.Equal("state-y", q["state"]);
        Assert.Contains("user-modify-playback-state", q["scope"]);
        Assert.Contains("user-library-modify", q["scope"]);
        Assert.Contains("user-read-recently-played", q["scope"]);
    }

    [Theory]
    [InlineData("http://127.0.0.1:8765/plugins/spotify/callback?code=AQB-x_1&state=s%2B1", "AQB-x_1", "s+1", null)]
    [InlineData("http://127.0.0.1:8765/plugins/spotify/callback?code=abc&state=def#_=_", "abc", "def", null)]
    [InlineData("code=abc&state=def", "abc", "def", null)]
    [InlineData("  AQBjustthecode  ", "AQBjustthecode", null, null)]
    [InlineData("http://127.0.0.1:8765/plugins/spotify/callback?error=access_denied&state=def", null, "def", "access_denied")]
    public void Redirect_addresses_are_understood(string text, string? code, string? state, string? error) =>
        Assert.Equal((code, state, error), SpotifyAuth.ParseRedirect(text));

    [Fact]
    public async Task Connect_then_callback_saves_the_refresh_token_and_account()
    {
        var spotify = new FakeSpotify();
        spotify.Route("GET me", """{"display_name":"Sam","id":"sam123"}""");
        var (plugin, context) = spotify.CreatePlugin(settings: new SpotifySettings { ClientId = "client-123" });
        await plugin.StartAsync(default);
        try
        {
            Assert.Equal(new PluginStatus(StatusLevel.Warning, "Not connected — click Connect"), plugin.Status);
            Assert.Contains(plugin.GetActions(), a => a.Id == "connect" && a.Description!.Contains("http://127.0.0.1:8765/plugins/spotify/callback"));

            var connect = await plugin.RunActionAsync("connect", NoInput, default);
            Assert.True(connect.Ok);
            var q = SpotifyAuth.ParseQuery(new Uri(connect.OpenUrl!).Query);
            Assert.Equal("http://127.0.0.1:8765/plugins/spotify/callback", q["redirect_uri"]);

            var page = await plugin.HandleHttpAsync(new PluginHttpRequest("GET", "callback",
                new Dictionary<string, string> { ["code"] = "the-code", ["state"] = q["state"] }, NoInput, null), default);

            Assert.NotNull(page);
            Assert.Equal(200, page.Status);
            Assert.Contains("Spotify is connected as Sam.", page.Body);
            var form = Assert.Single(spotify.TokenRequests);
            Assert.Equal("authorization_code", form["grant_type"]);
            Assert.Equal("the-code", form["code"]);
            Assert.Equal("client-123", form["client_id"]);
            Assert.Equal(q["redirect_uri"], form["redirect_uri"]);
            Assert.Equal(q["code_challenge"], Pkce.Challenge(form["code_verifier"]));
            var saved = Assert.IsType<SpotifySettings>(context.SavedSettings.Last());
            Assert.Equal(("refresh-new", "Sam"), (saved.RefreshToken, saved.AccountName));
            Assert.Equal(new PluginStatus(StatusLevel.Ok, "Connected as Sam"), plugin.Status);
            Assert.Contains(plugin.GetActions(), a => a.Id == "disconnect");
        }
        finally
        {
            await plugin.StopAsync();
        }
    }

    [Fact]
    public async Task Finish_accepts_the_pasted_address()
    {
        var spotify = new FakeSpotify();
        spotify.Route("GET me", """{"id":"sam123"}""");
        var (plugin, _) = spotify.CreatePlugin(settings: new SpotifySettings { ClientId = "client-123" });
        var connect = await plugin.RunActionAsync("connect", NoInput, default);
        var state = SpotifyAuth.ParseQuery(new Uri(connect.OpenUrl!).Query)["state"];

        var result = await plugin.RunActionAsync("finish",
            new Dictionary<string, string> { ["url"] = $"http://127.0.0.1:8765/plugins/spotify/callback?code=c2&state={state}" }, default);

        Assert.True(result.Ok, result.Message);
        Assert.Equal("Spotify is connected as sam123.", result.Message);
    }

    [Fact]
    public async Task Finish_accepts_just_the_code_when_one_sign_in_is_waiting()
    {
        var (plugin, _) = new FakeSpotify().CreatePlugin(settings: new SpotifySettings { ClientId = "client-123" });
        await plugin.RunActionAsync("connect", NoInput, default);

        var result = await plugin.RunActionAsync("finish", new Dictionary<string, string> { ["url"] = "AQBcode" }, default);

        Assert.True(result.Ok, result.Message);
    }

    [Fact]
    public async Task An_unknown_state_is_refused()
    {
        var spotify = new FakeSpotify();
        var (plugin, _) = spotify.CreatePlugin(settings: new SpotifySettings { ClientId = "client-123" });
        await plugin.RunActionAsync("connect", NoInput, default);

        var page = await plugin.HandleHttpAsync(new PluginHttpRequest("GET", "/callback/",
            new Dictionary<string, string> { ["code"] = "x", ["state"] = "forged" }, NoInput, null), default);

        Assert.Equal(400, page!.Status);
        Assert.Contains("expired", page.Body);
        Assert.Empty(spotify.TokenRequests);
    }

    [Fact]
    public async Task Cancelling_on_spotifys_page_is_reported()
    {
        var (plugin, _) = new FakeSpotify().CreatePlugin(settings: new SpotifySettings { ClientId = "client-123" });
        var page = await plugin.HandleHttpAsync(new PluginHttpRequest("GET", "callback",
            new Dictionary<string, string> { ["error"] = "access_denied", ["state"] = "s" }, NoInput, null), default);
        Assert.Contains("Spotify sign-in was cancelled.", page!.Body);
    }

    [Fact]
    public async Task A_wrong_redirect_uri_explains_the_fix()
    {
        var spotify = new FakeSpotify
        {
            TokenOverride = _ => FakeHttpHandler.Json("""{"error":"invalid_grant","error_description":"Invalid redirect URI"}""", HttpStatusCode.BadRequest),
        };
        var (plugin, _) = spotify.CreatePlugin(settings: new SpotifySettings { ClientId = "client-123" });
        await plugin.RunActionAsync("connect", NoInput, default);

        var result = await plugin.RunActionAsync("finish", new Dictionary<string, string> { ["url"] = "code-only" }, default);

        Assert.False(result.Ok);
        Assert.Equal("Spotify sign-in failed: Invalid redirect URI. The redirect URI on your Spotify app must be exactly http://127.0.0.1:8765/plugins/spotify/callback", result.Message);
    }

    [Fact]
    public async Task Connect_needs_a_client_id()
    {
        var (plugin, _) = new FakeSpotify().CreatePlugin(settings: new SpotifySettings());
        var result = await plugin.RunActionAsync("connect", NoInput, default);
        Assert.False(result.Ok);
        Assert.Null(result.OpenUrl);
    }

    [Fact]
    public async Task Other_paths_are_not_found()
    {
        var (plugin, _) = new FakeSpotify().CreatePlugin();
        Assert.Null(await plugin.HandleHttpAsync(new PluginHttpRequest("GET", "other", NoInput, NoInput, null), default));
    }

    [Fact]
    public async Task Disconnect_forgets_the_sign_in()
    {
        var (plugin, context) = new FakeSpotify().CreatePlugin();
        var result = await plugin.RunActionAsync("disconnect", NoInput, default);
        Assert.True(result.Ok);
        var saved = Assert.IsType<SpotifySettings>(context.SavedSettings.Last());
        Assert.Equal("", saved.RefreshToken);
        Assert.Equal(StatusLevel.Warning, plugin.Status.Level);
    }
}
