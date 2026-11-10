using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Pucksley.Plugins.Speech.Tests;

/// <summary>One request, as the server would see it.</summary>
public sealed class RecordedRequest
{
    public required HttpMethod Method { get; init; }
    public required Uri Url { get; init; }

    /// <summary>Request and content headers, raw (exactly as they'd go on the wire).</summary>
    public required Dictionary<string, string> Headers { get; init; }

    public required byte[] Body { get; init; }

    public string Text => Encoding.UTF8.GetString(Body);
    public JsonObject Json => JsonNode.Parse(Body)!.AsObject();
    public string? Header(string name) => Headers.TryGetValue(name, out var v) ? v : null;
    public IReadOnlyList<MultipartPart> Parts => Multipart.Parse(Header("Content-Type")!, Body);
}

/// <summary>Records each request (headers and body bytes) and answers with a scripted response.</summary>
public sealed class RecordingHandler(Func<RecordedRequest, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
{
    readonly List<RecordedRequest> _requests = [];

    public RecordingHandler(Func<RecordedRequest, HttpResponseMessage> respond) : this((r, _) => Task.FromResult(respond(r)))
    {
    }

    public IReadOnlyList<RecordedRequest> Requests
    {
        get { lock (_requests) return _requests.ToList(); }
    }

    public RecordedRequest Single => Assert.Single(Requests);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var h in request.Headers.NonValidated) headers[h.Key] = string.Join(", ", h.Value);
        byte[] body = [];
        if (request.Content is not null)
        {
            body = await request.Content.ReadAsByteArrayAsync(cancel);
            foreach (var h in request.Content.Headers.NonValidated) headers[h.Key] = string.Join(", ", h.Value);
        }
        var recorded = new RecordedRequest { Method = request.Method, Url = request.RequestUri!, Headers = headers, Body = body };
        lock (_requests) _requests.Add(recorded);
        var response = await respond(recorded, cancel);
        response.RequestMessage = request;
        return response;
    }
}

public static class Responses
{
    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    public static HttpResponseMessage Stream(Stream body, string contentType = "audio/pcm")
    {
        var content = new StreamContent(body);
        content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }
}

/// <summary>
/// A body that hands its bytes out in fixed-size reads (cycling through e.g. 1, 3, 4097 bytes), optionally
/// pausing or failing before a given read.
/// </summary>
public sealed class TrickleStream(byte[] data, int[] readSizes, Func<int, CancellationToken, Task>? beforeRead = null) : Stream
{
    int _position, _reads;

    public bool Disposed { get; private set; }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancel = default)
    {
        if (beforeRead is not null) await beforeRead(_reads, cancel);
        cancel.ThrowIfCancellationRequested();
        if (_position >= data.Length || buffer.Length == 0) return 0;
        int size = readSizes[_reads++ % readSizes.Length];
        int n = Math.Min(Math.Min(size, buffer.Length), data.Length - _position);
        data.AsSpan(_position, n).CopyTo(buffer.Span);
        _position += n;
        return n;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancel) =>
        ReadAsync(buffer.AsMemory(offset, count), cancel).AsTask();

    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    protected override void Dispose(bool disposing)
    {
        Disposed = true;
        base.Dispose(disposing);
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

public sealed record MultipartPart(string? Name, string? FileName, string? ContentType, byte[] Data, string RawDisposition)
{
    public string Text => Encoding.UTF8.GetString(Data);
}

/// <summary>Parses a multipart/form-data body from its bytes, the way a server would.</summary>
public static partial class Multipart
{
    public static IReadOnlyList<MultipartPart> Parse(string contentType, byte[] body)
    {
        var boundary = MediaTypeHeaderValue.Parse(contentType).Parameters.Single(p => p.Name == "boundary").Value!.Trim('"');
        var delimiter = Encoding.ASCII.GetBytes("--" + boundary);
        var parts = new List<MultipartPart>();
        int pos = IndexOf(body, delimiter, 0);
        Assert.True(pos >= 0, "No multipart boundary in the body.");
        while (true)
        {
            pos += delimiter.Length;
            if (body.AsSpan(pos).StartsWith("--"u8)) break;
            Assert.True(body.AsSpan(pos).StartsWith("\r\n"u8), "Boundary not followed by CRLF.");
            pos += 2;
            int headerEnd = IndexOf(body, "\r\n\r\n"u8.ToArray(), pos);
            var headers = Encoding.UTF8.GetString(body, pos, headerEnd - pos)
                .Split("\r\n")
                .Select(line => line.Split(':', 2))
                .ToDictionary(kv => kv[0].Trim(), kv => kv[1].Trim(), StringComparer.OrdinalIgnoreCase);
            int dataStart = headerEnd + 4;
            int next = IndexOf(body, delimiter, dataStart);
            Assert.True(next >= 0, "Unterminated multipart part.");
            var disposition = headers.GetValueOrDefault("Content-Disposition") ?? "";
            parts.Add(new MultipartPart(
                Param(disposition, "name"),
                Param(disposition, "filename"),
                headers.GetValueOrDefault("Content-Type"),
                body[dataStart..(next - 2)],
                disposition));
            pos = next;
        }
        return parts;
    }

    public static string Field(this IReadOnlyList<MultipartPart> parts, string name) => Assert.Single(parts, p => p.Name == name).Text;

    static string? Param(string disposition, string name)
    {
        var m = Regex.Match(disposition, $@"(?:^|;)\s*{name}=(?:""(?<v>[^""]*)""|(?<v>[^;]*))");
        return m.Success ? m.Groups["v"].Value.Trim() : null;
    }

    static int IndexOf(byte[] haystack, byte[] needle, int start)
    {
        int i = haystack.AsSpan(start).IndexOf(needle);
        return i < 0 ? -1 : start + i;
    }
}
