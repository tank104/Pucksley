using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Pucksley.Plugins.Speech.Tests;

/// <summary>A request as received by <see cref="MiniHttpServer"/>.</summary>
public sealed record MiniRequest(string Method, string Path, IReadOnlyDictionary<string, string> Headers, byte[] Body)
{
    public string Text => Encoding.UTF8.GetString(Body);
}

/// <summary>Writes a response, chunk by chunk if you like (Transfer-Encoding: chunked, flushed per chunk).</summary>
public sealed class MiniResponse(Stream stream)
{
    public async Task StartChunkedAsync(int status, string contentType)
    {
        var head = $"HTTP/1.1 {status} {(status == 200 ? "OK" : "Error")}\r\nContent-Type: {contentType}\r\nTransfer-Encoding: chunked\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head));
        await stream.FlushAsync();
    }

    public async Task WriteChunkAsync(ReadOnlyMemory<byte> data)
    {
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"{data.Length:X}\r\n"));
        await stream.WriteAsync(data);
        await stream.WriteAsync("\r\n"u8.ToArray());
        await stream.FlushAsync();
    }

    public async Task EndChunkedAsync()
    {
        await stream.WriteAsync("0\r\n\r\n"u8.ToArray());
        await stream.FlushAsync();
    }
}

/// <summary>
/// A bare-bones HTTP/1.1 server on a loopback TCP port (no http.sys URL reservations needed), for testing streaming
/// over real sockets. One request per connection.
/// </summary>
public sealed class MiniHttpServer : IAsyncDisposable
{
    readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    readonly Func<MiniRequest, MiniResponse, Task> _handler;
    readonly CancellationTokenSource _stop = new();
    readonly Task _accepting;

    public MiniHttpServer(Func<MiniRequest, MiniResponse, Task> handler)
    {
        _handler = handler;
        _listener.Start();
        BaseUrl = new Uri($"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/");
        _accepting = AcceptAsync();
    }

    public Uri BaseUrl { get; }

    /// <summary>The first exception a handler threw (assertion failures inside the server end up here).</summary>
    public Exception? Failure { get; private set; }

    async Task AcceptAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stop.Token);
            }
            catch (Exception)
            {
                return;
            }
            _ = ServeAsync(client);
        }
    }

    async Task ServeAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                var request = await ReadRequestAsync(stream);
                await _handler(request, new MiniResponse(stream));
            }
            catch (Exception e)
            {
                Failure ??= e;
            }
        }
    }

    static async Task<MiniRequest> ReadRequestAsync(NetworkStream stream)
    {
        var buffer = new List<byte>();
        var one = new byte[1];
        while (buffer.Count < 4 || !(buffer[^4] == '\r' && buffer[^3] == '\n' && buffer[^2] == '\r' && buffer[^1] == '\n'))
        {
            if (await stream.ReadAsync(one) == 0) throw new IOException("Connection closed before the request headers ended.");
            buffer.Add(one[0]);
        }
        var lines = Encoding.ASCII.GetString(buffer.ToArray()).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        var first = lines[0].Split(' ');
        var headers = lines.Skip(1).Select(l => l.Split(':', 2)).ToDictionary(kv => kv[0].Trim(), kv => kv[1].Trim(), StringComparer.OrdinalIgnoreCase);
        var body = new byte[headers.TryGetValue("Content-Length", out var length) ? int.Parse(length) : 0];
        await stream.ReadExactlyAsync(body);
        return new MiniRequest(first[0], first[1], headers, body);
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _listener.Stop();
        await _accepting;
        _stop.Dispose();
    }
}
