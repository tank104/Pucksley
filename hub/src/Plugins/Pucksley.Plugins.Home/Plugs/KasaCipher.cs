using System.Buffers.Binary;
using System.Text;

namespace Pucksley.Plugins.Plugs;

/// <summary>
/// The legacy Kasa (TCP 9999) "autokey" XOR cipher. The key starts at 171; each cipher byte is
/// plain ^ key and becomes the next key. Messages on the wire carry a 4-byte big-endian length first.
/// </summary>
internal static class KasaCipher
{
    const byte InitialKey = 171;

    /// <summary>Length prefix + encrypted UTF-8 JSON, ready to write to the socket.</summary>
    public static byte[] Encrypt(string json)
    {
        var plain = Encoding.UTF8.GetBytes(json);
        var output = new byte[4 + plain.Length];
        BinaryPrimitives.WriteInt32BigEndian(output, plain.Length);
        byte key = InitialKey;
        for (int i = 0; i < plain.Length; i++)
        {
            key = (byte)(plain[i] ^ key);
            output[4 + i] = key;
        }
        return output;
    }

    /// <summary>Decrypts a payload (without its length prefix).</summary>
    public static string Decrypt(ReadOnlySpan<byte> cipher)
    {
        var plain = new byte[cipher.Length];
        byte key = InitialKey;
        for (int i = 0; i < cipher.Length; i++)
        {
            plain[i] = (byte)(cipher[i] ^ key);
            key = cipher[i];
        }
        return Encoding.UTF8.GetString(plain);
    }
}
