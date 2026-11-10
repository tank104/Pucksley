using System.Net.WebSockets;
using System.Text;

namespace Pucksley.Plugins.Tv;

/// <summary>WebSocket plumbing shared by the LG and Samsung clients.</summary>
internal static class TvSocket
{
    /// <summary>
    /// Opens a socket to a TV. TVs use self-signed certificates, so for <c>wss</c> this one socket
    /// accepts any certificate (nothing else in the hub is affected).
    /// </summary>
    public static async Task<ClientWebSocket> ConnectAsync(Uri uri, CancellationToken cancel)
    {
        var ws = new ClientWebSocket();
        if (uri.Scheme == "wss") ws.Options.RemoteCertificateValidationCallback = (_, _, _, _) => true;
        ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        // A TV that's off may not refuse, just not answer: don't wait for the OS's ~20 s connect timeout
        using var connect = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        connect.CancelAfter(ConnectTimeout);
        try
        {
            await ws.ConnectAsync(uri, connect.Token).ConfigureAwait(false);
            return ws;
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
        {
            ws.Dispose();
            throw new WebSocketException($"No answer from {uri.Host}:{uri.Port}.");
        }
        catch
        {
            ws.Dispose();
            throw;
        }
    }

    static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(4);

    /// <summary>One whole text message, or null when the TV closed the socket.</summary>
    public static async Task<string?> ReceiveAsync(WebSocket ws, CancellationToken cancel)
    {
        var buffer = new byte[16 * 1024];
        using var message = new MemoryStream();
        while (true)
        {
            var result = await ws.ReceiveAsync(buffer.AsMemory(), cancel).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            message.Write(buffer, 0, result.Count);
            if (message.Length > 4 * 1024 * 1024) throw new InvalidDataException("The TV sent an oversized message.");
            if (result.EndOfMessage) return Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
        }
    }

    public static Task SendAsync(WebSocket ws, string text, CancellationToken cancel) =>
        ws.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(text)), WebSocketMessageType.Text, endOfMessage: true, cancel);

    /// <summary>Closes politely (best effort) and disposes.</summary>
    public static async Task CloseAsync(ClientWebSocket ws)
    {
        try
        {
            if (ws.State == WebSocketState.Open)
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, cts.Token).ConfigureAwait(false);
            }
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException or ObjectDisposedException)
        {
        }
        ws.Dispose();
    }
}
