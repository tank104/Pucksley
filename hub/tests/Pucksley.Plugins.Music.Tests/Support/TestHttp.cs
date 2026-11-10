using System.Collections.Concurrent;
using System.Net;
using System.Text;

namespace Pucksley.Plugins.Music.Tests.Support;

/// <summary>A request as the fake handler saw it.</summary>
public sealed record RecordedRequest(HttpMethod Method, Uri Uri, string? Body, IReadOnlyDictionary<string, string> Headers)
{
    public string PathAndQuery => Uri.PathAndQuery;
}

/// <summary>An <see cref="HttpMessageHandler"/> that answers from a function and records every request.</summary>
public sealed class FakeHttpHandler(Func<RecordedRequest, HttpResponseMessage> respond) : HttpMessageHandler
{
    public ConcurrentQueue<RecordedRequest> Requests { get; } = new();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        var headers = request.Headers.Concat(request.Content?.Headers ?? Enumerable.Empty<KeyValuePair<string, IEnumerable<string>>>())
            .ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
        var recorded = new RecordedRequest(request.Method, request.RequestUri!, body, headers);
        Requests.Enqueue(recorded);
        return respond(recorded);
    }

    public static HttpResponseMessage Text(string body, HttpStatusCode status = HttpStatusCode.OK, string type = "text/xml") =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, type) };

    public static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) => Text(body, status, "application/json");
}

/// <summary>Captured fixture files (copied next to the test assembly).</summary>
public static class Fixtures
{
    public static string Read(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));
}
