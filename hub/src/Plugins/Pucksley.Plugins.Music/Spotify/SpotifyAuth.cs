using System.Security.Cryptography;
using System.Text;

namespace Pucksley.Plugins.Spotify;

/// <summary>PKCE (RFC 7636): sign in with a one-time verifier instead of a client secret.</summary>
public static class Pkce
{
    const string Unreserved = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-._~";

    /// <summary>A random code verifier: 43-128 characters from the unreserved set.</summary>
    public static string CreateVerifier(int length = 64)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(length, 43);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(length, 128);
        return RandomNumberGenerator.GetString(Unreserved, length);
    }

    /// <summary>The S256 challenge: base64url(SHA-256(verifier)), no padding.</summary>
    public static string Challenge(string verifier) => Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    /// <summary>An unguessable state value tying the redirect back to this sign-in.</summary>
    public static string CreateState() => Base64Url(RandomNumberGenerator.GetBytes(16));

    public static string Base64Url(ReadOnlySpan<byte> data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>Spotify's OAuth URLs.</summary>
public static class SpotifyAuth
{
    public const string AuthorizeEndpoint = "https://accounts.spotify.com/authorize";

    public const string Scopes =
        "user-read-playback-state user-modify-playback-state user-read-currently-playing user-library-read " +
        "user-library-modify playlist-read-private playlist-read-collaborative user-read-recently-played user-top-read";

    public static string AuthorizeUrl(string clientId, string redirectUri, string challenge, string state, string scopes = Scopes)
    {
        (string, string)[] query =
        [
            ("client_id", clientId),
            ("response_type", "code"),
            ("redirect_uri", redirectUri),
            ("code_challenge_method", "S256"),
            ("code_challenge", challenge),
            ("state", state),
            ("scope", scopes),
        ];
        return AuthorizeEndpoint + "?" + string.Join("&", query.Select(p => $"{p.Item1}={Uri.EscapeDataString(p.Item2)}"));
    }

    /// <summary>
    /// The code, state and error from the address Spotify redirected to
    /// (<c>http://127.0.0.1:8765/plugins/spotify/callback?code=…&amp;state=…</c>), or a bare code.
    /// </summary>
    public static (string? Code, string? State, string? Error) ParseRedirect(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return (null, null, null);
        var s = text.Trim();
        int q = s.IndexOf('?');
        if (q < 0 && !s.Contains('=')) return (s, null, null);
        var query = q >= 0 ? s[(q + 1)..] : s;
        int hash = query.IndexOf('#');
        if (hash >= 0) query = query[..hash];
        var values = ParseQuery(query);
        return (values.GetValueOrDefault("code"), values.GetValueOrDefault("state"), values.GetValueOrDefault("error"));
    }

    public static Dictionary<string, string> ParseQuery(string query)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = part.IndexOf('=');
            var key = Uri.UnescapeDataString((eq < 0 ? part : part[..eq]).Replace('+', ' '));
            var value = eq < 0 ? "" : Uri.UnescapeDataString(part[(eq + 1)..].Replace('+', ' '));
            values[key] = value;
        }
        return values;
    }
}
