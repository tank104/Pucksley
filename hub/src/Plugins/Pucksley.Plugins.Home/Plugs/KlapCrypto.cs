using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Pucksley.Plugins.Plugs;

/// <summary>A way the device may know us: KLAP version and the auth hash for one set of credentials.</summary>
internal sealed record KlapAuth(int Version, byte[] Hash, string Label);

/// <summary>
/// KLAP handshake hashes. v2 (Tapo) uses SHA256(SHA1(email) ‖ SHA1(password)); v1 (newer Kasa
/// firmware) uses MD5(MD5(email) ‖ MD5(password)) and shorter handshake hashes. Session keys are
/// derived the same way for both (<see cref="KlapSession"/>).
/// </summary>
internal static class KlapCrypto
{
    /// <summary>The TP-Link setup credentials devices fall back to (from python-kasa): Kasa and Tapo.</summary>
    static readonly (string Email, string Password, string Label)[] Defaults =
    [
        ("kasa@tp-link.net", "kasaSetup", "Kasa default"),
        ("test@tp-link.net", "test", "Tapo default"),
    ];

    public static byte[] AuthHashV2(string email, string password) =>
        SHA256.HashData(Concat(SHA1.HashData(Encoding.UTF8.GetBytes(email)), SHA1.HashData(Encoding.UTF8.GetBytes(password))));

    public static byte[] AuthHashV1(string email, string password) =>
        MD5.HashData(Concat(MD5.HashData(Encoding.UTF8.GetBytes(email)), MD5.HashData(Encoding.UTF8.GetBytes(password))));

    /// <summary>What the device sends back in handshake1 to prove it knows the credentials.</summary>
    public static byte[] Handshake1Hash(int version, byte[] localSeed, byte[] remoteSeed, byte[] authHash) =>
        version >= 2 ? SHA256.HashData(Concat(localSeed, remoteSeed, authHash)) : SHA256.HashData(Concat(localSeed, authHash));

    /// <summary>What we send in handshake2 to prove the same.</summary>
    public static byte[] Handshake2Hash(int version, byte[] localSeed, byte[] remoteSeed, byte[] authHash) =>
        version >= 2 ? SHA256.HashData(Concat(remoteSeed, localSeed, authHash)) : SHA256.HashData(Concat(remoteSeed, authHash));

    /// <summary>Every (version, credentials) pair worth trying: the account's, then TP-Link's defaults, then blank (never-registered devices).</summary>
    public static IReadOnlyList<KlapAuth> Candidates(string email, string password)
    {
        var list = new List<KlapAuth>();
        if (!string.IsNullOrEmpty(email))
        {
            list.Add(new KlapAuth(2, AuthHashV2(email, password), "account"));
            list.Add(new KlapAuth(1, AuthHashV1(email, password), "account"));
        }
        foreach (var (e, p, label) in Defaults)
        {
            list.Add(new KlapAuth(2, AuthHashV2(e, p), label));
            list.Add(new KlapAuth(1, AuthHashV1(e, p), label));
        }
        list.Add(new KlapAuth(2, AuthHashV2("", ""), "blank"));
        list.Add(new KlapAuth(1, AuthHashV1("", ""), "blank"));
        return list;
    }

    /// <summary>The candidate whose handshake1 hash matches what the device sent, or null.</summary>
    public static KlapAuth? Match(byte[] localSeed, byte[] remoteSeed, byte[] serverHash, IEnumerable<KlapAuth> candidates) =>
        candidates.FirstOrDefault(c => CryptographicOperations.FixedTimeEquals(Handshake1Hash(c.Version, localSeed, remoteSeed, c.Hash), serverHash));

    internal static byte[] Concat(params byte[][] parts)
    {
        var output = new byte[parts.Sum(p => p.Length)];
        int offset = 0;
        foreach (var p in parts)
        {
            p.CopyTo(output, offset);
            offset += p.Length;
        }
        return output;
    }
}

/// <summary>
/// One KLAP session's encryption: AES-128-CBC with a key, IV and signing key derived from both
/// seeds and the auth hash. The last 4 IV bytes are a signed big-endian sequence number that goes up
/// by one per request; each request body is SHA256(sig ‖ seq ‖ ciphertext) ‖ ciphertext. Not
/// thread-safe: the transport serializes requests.
/// </summary>
internal sealed class KlapSession
{
    int _seq;

    public KlapSession(byte[] localSeed, byte[] remoteSeed, byte[] authHash)
    {
        Key = SHA256.HashData(KlapCrypto.Concat("lsk"u8.ToArray(), localSeed, remoteSeed, authHash))[..16];
        var ivSeed = SHA256.HashData(KlapCrypto.Concat("iv"u8.ToArray(), localSeed, remoteSeed, authHash));
        Iv = ivSeed[..12];
        _seq = BinaryPrimitives.ReadInt32BigEndian(ivSeed.AsSpan(28, 4));
        Signature = SHA256.HashData(KlapCrypto.Concat("ldk"u8.ToArray(), localSeed, remoteSeed, authHash))[..28];
    }

    public byte[] Key { get; }

    /// <summary>The first 12 IV bytes (the sequence number completes it).</summary>
    public byte[] Iv { get; }

    public byte[] Signature { get; }

    public int Seq => _seq;

    /// <summary>Encrypts a request; returns the body to POST and the sequence number for <c>?seq=</c>.</summary>
    public (byte[] Body, int Seq) Encrypt(byte[] plaintext)
    {
        int seq = unchecked(++_seq);
        using var aes = Aes.Create();
        aes.Key = Key;
        var ciphertext = aes.EncryptCbc(plaintext, IvFor(seq), PaddingMode.PKCS7);
        return (KlapCrypto.Concat(Sign(seq, ciphertext), ciphertext), seq);
    }

    /// <summary>Checks the signature and decrypts a response to the request with <paramref name="seq"/>.</summary>
    public byte[] Decrypt(byte[] body, int seq)
    {
        if (body.Length < 48 || (body.Length - 32) % 16 != 0)
            throw new InvalidDataException($"Not a KLAP message ({body.Length} bytes).");
        var ciphertext = body[32..];
        if (!CryptographicOperations.FixedTimeEquals(Sign(seq, ciphertext), body.AsSpan(0, 32)))
            throw new InvalidDataException("The KLAP response signature doesn't match.");
        using var aes = Aes.Create();
        aes.Key = Key;
        return aes.DecryptCbc(ciphertext, IvFor(seq), PaddingMode.PKCS7);
    }

    byte[] Sign(int seq, byte[] ciphertext) => SHA256.HashData(KlapCrypto.Concat(Signature, SeqBytes(seq), ciphertext));

    byte[] IvFor(int seq) => KlapCrypto.Concat(Iv, SeqBytes(seq));

    static byte[] SeqBytes(int seq)
    {
        var b = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(b, seq);
        return b;
    }
}
