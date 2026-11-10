using System.Net;
using System.Net.Sockets;

namespace Pucksley.Plugins.Speech;

/// <summary>Base URLs as people type them into settings.</summary>
internal static class ServerUrl
{
    static readonly string[] LocalSuffixes = [".local", ".lan", ".home", ".internal", ".home.arpa", ".localhost"];

    /// <summary>
    /// Parses a base URL: trims it, adds a scheme when missing (http for hosts on this machine or the LAN, https
    /// otherwise) and drops a trailing slash. Null when it isn't an http(s) URL.
    /// </summary>
    public static Uri? ParseBase(string? text)
    {
        text = text?.Trim();
        if (string.IsNullOrEmpty(text)) return null;
        if (!text.Contains("://", StringComparison.Ordinal))
        {
            if (!Uri.TryCreate("http://" + text, UriKind.Absolute, out var probe)) return null;
            text = (IsLocal(probe) ? "http://" : "https://") + text;
        }
        if (!Uri.TryCreate(text.TrimEnd('/'), UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return null;
        return string.IsNullOrEmpty(uri.Host) ? null : uri;
    }

    /// <summary><c>{base}/{path}</c>, without doubled slashes (any query on the base is dropped).</summary>
    public static Uri Combine(Uri baseUrl, string path) =>
        new(baseUrl.GetLeftPart(UriPartial.Path).TrimEnd('/') + "/" + path.TrimStart('/'));

    /// <summary>Same scheme, host and port: whether a key meant for one may be sent to the other.</summary>
    public static bool SameServer(Uri a, Uri b) =>
        Uri.Compare(a, b, UriComponents.SchemeAndServer, UriFormat.Unescaped, StringComparison.OrdinalIgnoreCase) == 0;

    /// <summary>
    /// Whether a server is on this machine or the local network: loopback, a private or link-local address,
    /// a .local/.lan name, or a bare host name such as a Docker service ("speaches"). Self-hosted speech servers
    /// (Speaches, Kokoro-FastAPI) there usually need no API key.
    /// </summary>
    public static bool IsLocal(Uri uri)
    {
        if (uri.IsLoopback) return true;
        var host = uri.DnsSafeHost.TrimEnd('.').ToLowerInvariant();
        if (IPAddress.TryParse(host, out var ip)) return IsPrivate(ip);
        if (!host.Contains('.')) return true;
        foreach (var suffix in LocalSuffixes)
            if (host.EndsWith(suffix, StringComparison.Ordinal)) return true;
        return false;
    }

    static bool IsPrivate(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip)) return true;
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
            return ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6UniqueLocal;
        var b = ip.GetAddressBytes();
        return b[0] == 10
               || (b[0] == 172 && b[1] is >= 16 and <= 31)
               || (b[0] == 192 && b[1] == 168)
               || (b[0] == 169 && b[1] == 254)
               || (b[0] == 100 && b[1] is >= 64 and <= 127);  // carrier-grade NAT range, used by Tailscale
    }
}
