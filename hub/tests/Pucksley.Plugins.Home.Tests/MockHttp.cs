using System.Net;
using System.Text;

namespace Pucksley.Plugins.Home.Tests;

/// <summary>One request the plugin made.</summary>
public sealed record SeenRequest(HttpMethod Method, Uri Uri, byte[] Body, IReadOnlyDictionary<string, string> Headers)
{
    public string Text => Encoding.UTF8.GetString(Body);

    public string PathAndQuery => Uri.PathAndQuery;
}

/// <summary>
/// A fake HTTP server for tests: routes match on method + path (and query when the route has one);
/// the newest matching route answers. Every request is recorded.
/// </summary>
public sealed class MockHttp : HttpMessageHandler
{
    readonly List<(HttpMethod Method, string Path, Func<SeenRequest, HttpResponseMessage> Respond)> _routes = [];
    readonly List<SeenRequest> _seen = [];

    public IReadOnlyList<SeenRequest> Requests
    {
        get
        {
            lock (_seen) return _seen.ToList();
        }
    }

    public HttpClient Client() => new(this) { Timeout = TimeSpan.FromSeconds(30) };

    public MockHttp On(HttpMethod method, string path, Func<SeenRequest, HttpResponseMessage> respond)
    {
        lock (_routes) _routes.Add((method, path, respond));
        return this;
    }

    public MockHttp On(HttpMethod method, string path, string body, HttpStatusCode status = HttpStatusCode.OK, string contentType = "application/json") =>
        On(method, path, _ => Respond(body, status, contentType));

    public static HttpResponseMessage Respond(string body, HttpStatusCode status = HttpStatusCode.OK, string contentType = "application/json") =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, contentType) };

    public IReadOnlyList<SeenRequest> To(HttpMethod method, string path) =>
        Requests.Where(r => r.Method == method && Matches(path, r.Uri)).ToList();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel)
    {
        var body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancel);
        var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(", ", h.Value), StringComparer.OrdinalIgnoreCase);
        var seen = new SeenRequest(request.Method, request.RequestUri!, body, headers);
        lock (_seen) _seen.Add(seen);
        Func<SeenRequest, HttpResponseMessage>? respond;
        lock (_routes)
            respond = _routes.LastOrDefault(r => r.Method == request.Method && Matches(r.Path, request.RequestUri!)).Respond;
        return respond is null ? Respond("{\"message\":\"no route\"}", HttpStatusCode.NotFound) : respond(seen);
    }

    static bool Matches(string route, Uri uri) =>
        route.Contains('?') ? uri.PathAndQuery.Equals(route, StringComparison.Ordinal) : uri.AbsolutePath.Equals(route, StringComparison.Ordinal);
}
