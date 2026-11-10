using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Pucksley.Sdk;

namespace Pucksley.Plugins.Plugs;

/// <summary>
/// The KLAP transport (Tapo, and Kasa firmware that closed port 9999): POST <c>/app/handshake1</c>
/// with a random seed, check which credentials the device's reply proves, POST
/// <c>/app/handshake2</c>, then send AES-encrypted JSON to <c>/app/request?seq=N</c> with the
/// <c>TP_SESSIONID</c> cookie. Requests to one device are serialized (the sequence must increase).
/// </summary>
internal sealed class KlapTransport
{
    static readonly TimeSpan ExpiryMargin = TimeSpan.FromMinutes(20);

    readonly HttpClient _http;
    readonly Uri _app;
    readonly string _host;
    readonly IReadOnlyList<KlapAuth> _candidates;
    readonly SemaphoreSlim _gate = new(1, 1);
    readonly Func<DateTimeOffset> _clock;
    KlapSession? _session;
    string? _cookie;
    DateTimeOffset _expires;

    /// <param name="http">A client without a cookie container (the session cookie is sent by hand).</param>
    public KlapTransport(HttpClient http, string host, string email, string password, Func<DateTimeOffset>? clock = null)
    {
        _http = http;
        _host = host;
        _app = new Uri($"http://{host}/app/");
        _candidates = KlapCrypto.Candidates(email.Trim(), password);
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>Which credentials the device accepted at the last handshake ("account", "blank"…), for diagnostics.</summary>
    public string? AuthLabel { get; private set; }

    /// <summary>Forget the session (after the device reports a session error); the next request handshakes again.</summary>
    public void Reset() => _session = null;

    public async Task<string> SendAsync(string json, CancellationToken cancel)
    {
        await _gate.WaitAsync(cancel).ConfigureAwait(false);
        try
        {
            for (int attempt = 0; ; attempt++)
            {
                if (_session is null || _clock() >= _expires) await HandshakeAsync(cancel).ConfigureAwait(false);
                var session = _session!;
                var (body, seq) = session.Encrypt(Encoding.UTF8.GetBytes(json));
                using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(_app, $"request?seq={seq}")) { Content = Bytes(body) };
                request.Headers.Add("Cookie", _cookie);
                using var response = await _http.SendAsync(request, cancel).ConfigureAwait(false);
                if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized && attempt == 0)
                {
                    _session = null; // expired or rejected session: handshake again once
                    continue;
                }
                if (!response.IsSuccessStatusCode)
                    throw new PluginException($"The plug at {_host} answered {(int)response.StatusCode} to a request.");
                var data = await response.Content.ReadAsByteArrayAsync(cancel).ConfigureAwait(false);
                try
                {
                    return Encoding.UTF8.GetString(session.Decrypt(data, seq));
                }
                catch (Exception e) when (e is InvalidDataException or CryptographicException)
                {
                    _session = null;
                    if (attempt == 0) continue;
                    throw new PluginException($"The plug at {_host} sent a reply that couldn't be decrypted.", e);
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    async Task HandshakeAsync(CancellationToken cancel)
    {
        _session = null;
        var localSeed = RandomNumberGenerator.GetBytes(16);

        using var request1 = new HttpRequestMessage(HttpMethod.Post, new Uri(_app, "handshake1")) { Content = Bytes(localSeed) };
        using var response1 = await _http.SendAsync(request1, cancel).ConfigureAwait(false);
        if (response1.StatusCode != HttpStatusCode.OK)
            throw new PluginException($"The plug at {_host} refused the connection (handshake answered {(int)response1.StatusCode}).");
        var reply = await response1.Content.ReadAsByteArrayAsync(cancel).ConfigureAwait(false);
        if (reply.Length != 48) throw new PluginException($"The plug at {_host} doesn't speak KLAP (unexpected handshake reply).");
        var remoteSeed = reply[..16];
        var serverHash = reply[16..];
        var (sessionId, timeoutSeconds) = ReadCookies(response1.Headers);
        if (sessionId is null) throw new PluginException($"The plug at {_host} didn't start a session.");

        var auth = KlapCrypto.Match(localSeed, remoteSeed, serverHash, _candidates)
                   ?? throw new PluginException($"The plug at {_host} didn't accept the TP-Link account. Check the email and password on the Plugs settings (both are case-sensitive).");

        var cookie = $"TP_SESSIONID={sessionId}"; // the device doesn't want its TIMEOUT cookie back
        using var request2 = new HttpRequestMessage(HttpMethod.Post, new Uri(_app, "handshake2"))
        {
            Content = Bytes(KlapCrypto.Handshake2Hash(auth.Version, localSeed, remoteSeed, auth.Hash)),
        };
        request2.Headers.Add("Cookie", cookie);
        using var response2 = await _http.SendAsync(request2, cancel).ConfigureAwait(false);
        if (response2.StatusCode != HttpStatusCode.OK)
            throw new PluginException($"The plug at {_host} rejected the login (answered {(int)response2.StatusCode}).");

        _session = new KlapSession(localSeed, remoteSeed, auth.Hash);
        _cookie = cookie;
        var lifetime = TimeSpan.FromSeconds(timeoutSeconds ?? 86400) - ExpiryMargin;
        _expires = _clock() + (lifetime < TimeSpan.FromMinutes(1) ? TimeSpan.FromMinutes(1) : lifetime);
        AuthLabel = auth.Label;
    }

    /// <summary>Reads <c>TP_SESSIONID</c> and <c>TIMEOUT</c> from the Set-Cookie headers.</summary>
    internal static (string? SessionId, int? TimeoutSeconds) ReadCookies(HttpResponseHeaders headers)
    {
        string? id = null;
        int? timeout = null;
        if (!headers.TryGetValues("Set-Cookie", out var values)) return (null, null);
        foreach (var value in values)
        {
            foreach (var part in value.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = part.IndexOf('=');
                if (eq <= 0) continue;
                var name = part[..eq].Trim();
                var v = part[(eq + 1)..].Trim();
                if (name.Equals("TP_SESSIONID", StringComparison.OrdinalIgnoreCase)) id = v;
                else if (name.Equals("TIMEOUT", StringComparison.OrdinalIgnoreCase) && int.TryParse(v, out int t)) timeout = t;
            }
        }
        return (id, timeout);
    }

    static ByteArrayContent Bytes(byte[] body)
    {
        var content = new ByteArrayContent(body);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        return content;
    }
}
