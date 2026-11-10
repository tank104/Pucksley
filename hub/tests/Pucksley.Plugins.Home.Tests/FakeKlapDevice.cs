using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace Pucksley.Plugins.Home.Tests;

/// <summary>
/// A simulated KLAP device (Tapo, or Kasa with new firmware). It implements the device's side of the
/// handshake and the AES/signature framing itself — independently of the plugin's KlapSession — so
/// the transport is tested against a second implementation.
/// </summary>
public sealed class FakeKlapDevice : HttpMessageHandler
{
    readonly int _version;
    readonly byte[] _auth;
    readonly Func<string, string> _answer;
    readonly object _lock = new();
    byte[]? _local, _remote;
    string? _session;
    bool _confirmed;

    public FakeKlapDevice(string email, string password, int version, Func<string, string> answer)
    {
        _version = version;
        _auth = version == 2
            ? SHA256.HashData(Cat(SHA1.HashData(Encoding.UTF8.GetBytes(email)), SHA1.HashData(Encoding.UTF8.GetBytes(password))))
            : MD5.HashData(Cat(MD5.HashData(Encoding.UTF8.GetBytes(email)), MD5.HashData(Encoding.UTF8.GetBytes(password))));
        _answer = answer;
    }

    /// <summary>Decrypted JSON requests, in order.</summary>
    public List<string> Requests { get; } = [];

    public int Handshakes { get; private set; }

    /// <summary>Answer the next request with 403, as a device does when its session expired.</summary>
    public bool ExpireNextRequest { get; set; }

    public HttpClient Client() => new(this);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel)
    {
        var body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancel);
        var cookie = request.Headers.TryGetValues("Cookie", out var c) ? string.Join(";", c) : "";
        lock (_lock)
        {
            switch (request.RequestUri!.AbsolutePath)
            {
                case "/app/handshake1":
                {
                    Handshakes++;
                    _local = body;
                    _remote = RandomNumberGenerator.GetBytes(16);
                    _session = Guid.NewGuid().ToString("N").ToUpperInvariant();
                    _confirmed = false;
                    var proof = _version == 2 ? SHA256.HashData(Cat(_local, _remote, _auth)) : SHA256.HashData(Cat(_local, _auth));
                    var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Cat(_remote, proof)) };
                    response.Headers.Add("Set-Cookie", $"TP_SESSIONID={_session};TIMEOUT=86400");
                    return response;
                }
                case "/app/handshake2":
                {
                    var expected = _version == 2 ? SHA256.HashData(Cat(_remote!, _local!, _auth)) : SHA256.HashData(Cat(_remote!, _auth));
                    if (cookie != $"TP_SESSIONID={_session}" || !body.AsSpan().SequenceEqual(expected)) return new HttpResponseMessage(HttpStatusCode.Forbidden);
                    _confirmed = true;
                    return new HttpResponseMessage(HttpStatusCode.OK);
                }
                case "/app/request":
                {
                    if (!_confirmed || cookie != $"TP_SESSIONID={_session}") return new HttpResponseMessage(HttpStatusCode.Forbidden);
                    if (ExpireNextRequest)
                    {
                        ExpireNextRequest = false;
                        _confirmed = false;
                        return new HttpResponseMessage(HttpStatusCode.Forbidden);
                    }
                    int seq = int.Parse(request.RequestUri.Query.TrimStart('?').Split('=')[1]);
                    var (key, iv, sig) = Derive(_local!, _remote!, _auth);
                    var seqBytes = new byte[4];
                    BinaryPrimitives.WriteInt32BigEndian(seqBytes, seq);
                    var cipher = body[32..];
                    if (!body.AsSpan(0, 32).SequenceEqual(SHA256.HashData(Cat(sig, seqBytes, cipher))))
                        return new HttpResponseMessage(HttpStatusCode.BadRequest);
                    using var aes = Aes.Create();
                    aes.Key = key;
                    var text = Encoding.UTF8.GetString(aes.DecryptCbc(cipher, Cat(iv, seqBytes), PaddingMode.PKCS7));
                    Requests.Add(text);
                    var reply = aes.EncryptCbc(Encoding.UTF8.GetBytes(_answer(text)), Cat(iv, seqBytes), PaddingMode.PKCS7);
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Cat(SHA256.HashData(Cat(sig, seqBytes, reply)), reply)) };
                }
                default:
                    return new HttpResponseMessage(HttpStatusCode.NotFound);
            }
        }
    }

    static (byte[] Key, byte[] Iv, byte[] Sig) Derive(byte[] local, byte[] remote, byte[] auth) =>
    (
        SHA256.HashData(Cat("lsk"u8.ToArray(), local, remote, auth))[..16],
        SHA256.HashData(Cat("iv"u8.ToArray(), local, remote, auth))[..12],
        SHA256.HashData(Cat("ldk"u8.ToArray(), local, remote, auth))[..28]
    );

    static byte[] Cat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();
}
