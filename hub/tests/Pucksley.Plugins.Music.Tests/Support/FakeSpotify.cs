using System.Net;
using System.Text.Json.Nodes;
using Pucksley.Plugins.Sonos;
using Pucksley.Plugins.Spotify;
using Pucksley.TestSupport;

namespace Pucksley.Plugins.Music.Tests.Support;

/// <summary>Spotify's accounts service and Web API, answering from routes ("GET me/player/devices").</summary>
public sealed class FakeSpotify
{
    readonly object _gate = new();
    readonly List<Dictionary<string, string>> _tokenRequests = [];
    int _tokens;

    public FakeSpotify() => Handler = new FakeHttpHandler(Respond);

    public FakeHttpHandler Handler { get; }

    /// <summary>"METHOD path" (without /v1/ and the query) → response.</summary>
    public Dictionary<string, Func<RecordedRequest, HttpResponseMessage>> Routes { get; } = new(StringComparer.Ordinal);

    /// <summary>Hand out this refresh token on refresh (rotation).</summary>
    public string? RotateRefreshTokenTo { get; set; }

    /// <summary>Answer token requests differently (errors).</summary>
    public Func<Dictionary<string, string>, HttpResponseMessage?>? TokenOverride { get; set; }

    public IReadOnlyList<Dictionary<string, string>> TokenRequests
    {
        get { lock (_gate) return _tokenRequests.ToList(); }
    }

    public IReadOnlyList<RecordedRequest> ApiRequests => Handler.Requests.Where(r => r.Uri.Host == "api.spotify.com").ToList();

    public IReadOnlyList<RecordedRequest> Requests(string method, string path) =>
        ApiRequests.Where(r => r.Method.Method == method && r.Uri.AbsolutePath == "/v1/" + path).ToList();

    public HttpClient CreateClient() => new(Handler) { Timeout = TimeSpan.FromSeconds(30) };

    public void Route(string key, string json, HttpStatusCode status = HttpStatusCode.OK) => Routes[key] = _ => FakeHttpHandler.Json(json, status);

    /// <summary>Answers in turn, repeating the last.</summary>
    public void RouteSequence(string key, params (HttpStatusCode Status, string Json)[] answers)
    {
        int i = 0;
        Routes[key] = _ =>
        {
            var (status, json) = answers[Math.Min(Interlocked.Increment(ref i) - 1, answers.Length - 1)];
            return status == HttpStatusCode.NoContent ? new HttpResponseMessage(status) : FakeHttpHandler.Json(json, status);
        };
    }

    public (SpotifyPlugin Plugin, FakePluginContext Context) CreatePlugin(SonosPlugin? sonos = null, SpotifySettings? settings = null)
    {
        var context = new FakePluginContext(http: CreateClient());
        if (sonos is not null) context.Plugins[typeof(SonosPlugin)] = sonos;
        var plugin = new SpotifyPlugin { TransferSettle = TimeSpan.Zero };
        plugin.Initialize(context);
        plugin.ApplySettings(settings ?? new SpotifySettings { ClientId = "client-123", RefreshToken = "refresh-1", AccountName = "Sam" });
        return (plugin, context);
    }

    HttpResponseMessage Respond(RecordedRequest request)
    {
        if (request.Uri.Host == "accounts.spotify.com")
        {
            var form = SpotifyAuth.ParseQuery(request.Body ?? "");
            lock (_gate) _tokenRequests.Add(form);
            if (TokenOverride?.Invoke(form) is { } custom) return custom;
            int n = Interlocked.Increment(ref _tokens);
            var json = new JsonObject { ["access_token"] = $"access-{n}", ["token_type"] = "Bearer", ["expires_in"] = 3600 };
            var refresh = form.GetValueOrDefault("grant_type") == "authorization_code" ? "refresh-new" : RotateRefreshTokenTo;
            if (refresh is not null) json["refresh_token"] = refresh;
            return FakeHttpHandler.Json(json.ToJsonString());
        }
        var key = $"{request.Method.Method} {request.Uri.AbsolutePath["/v1/".Length..]}";
        return Routes.TryGetValue(key, out var route)
            ? route(request)
            : FakeHttpHandler.Json("""{"error":{"status":404,"message":"Service not found"}}""", HttpStatusCode.NotFound);
    }

    // ---------------------------------------------------------------- sample data

    public static string Devices(params (string Id, string Name, bool Active)[] devices) =>
        new JsonObject
        {
            ["devices"] = new JsonArray(devices.Select(d => (JsonNode?)new JsonObject
            {
                ["id"] = d.Id, ["name"] = d.Name, ["type"] = d.Id.Contains("pc") ? "Computer" : "Speaker",
                ["is_active"] = d.Active, ["is_restricted"] = false, ["volume_percent"] = 30,
            }).ToArray()),
        }.ToJsonString();

    public const string Teardrop =
        """{"type":"track","id":"67Hna13dNDkZvBpTXRIaOJ","uri":"spotify:track:67Hna13dNDkZvBpTXRIaOJ","name":"Teardrop","duration_ms":329000,"popularity":70,"artists":[{"name":"Massive Attack"}],"album":{"name":"Mezzanine"}}""";

    public const string Angel =
        """{"type":"track","id":"7uv632EkfwYhXoqf8rhYrg","uri":"spotify:track:7uv632EkfwYhXoqf8rhYrg","name":"Angel","duration_ms":379000,"popularity":65,"artists":[{"name":"Massive Attack"}],"album":{"name":"Mezzanine"}}""";

    public const string MassiveAttackSearch =
        $$$"""
        {"tracks":{"items":[{{{Teardrop}}},{{{Angel}}}]},
         "albums":{"items":[{"name":"Mezzanine","uri":"spotify:album:49MNmJhZQewjt06rpwp6QR","artists":[{"name":"Massive Attack"}]}]},
         "artists":{"items":[{"name":"Massive Attack","uri":"spotify:artist:6FXMGgJwohJLUSr5nVlf9X","popularity":68}]},
         "playlists":{"items":[null,{"name":"This Is Massive Attack","uri":"spotify:playlist:37i9dQZF1DX0hvgh1kSuSq","owner":{"display_name":"Spotify"}}]}}
        """;

    public const string PlayerOnKitchen =
        $$"""
        {"device":{"id":"dev-kitchen","name":"Kitchen","type":"Speaker","volume_percent":30,"is_active":true},
         "is_playing":true,"progress_ms":83000,"shuffle_state":false,"repeat_state":"off",
         "context":{"uri":"spotify:album:49MNmJhZQewjt06rpwp6QR"},"item":{{Teardrop}}}
        """;
}
