using System.Text.RegularExpressions;
using Pucksley.Plugins.Shared;
using Pucksley.Sdk;

namespace Pucksley.Plugins.Tv;

/// <summary>A TV found on the network.</summary>
internal sealed record FoundTv(string Type, string Host, string Name, string? Mac);

/// <summary>Finds LG, Samsung and Roku TVs with SSDP (read-only: descriptions and info pages only).</summary>
internal static partial class TvDiscovery
{
    public static async Task<IReadOnlyList<FoundTv>> FindAsync(HttpClient http, CancellationToken cancel)
    {
        var window = TimeSpan.FromSeconds(3);
        var searches = await Task.WhenAll(
            LanTools.SsdpSearchAsync("ssdp:all", window, cancel),
            LanTools.SsdpSearchAsync("roku:ecp", window, cancel),
            LanTools.SsdpSearchAsync("urn:lge-com:service:webos-second-screen:1", window, cancel)).ConfigureAwait(false);

        var byHost = searches.SelectMany(s => s).GroupBy(r => r.From.Address.ToString());
        var found = await Task.WhenAll(byHost.Select(g => IdentifyAsync(http, g.Key, g.ToList(), cancel))).ConfigureAwait(false);
        return found.OfType<FoundTv>().OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    static async Task<FoundTv?> IdentifyAsync(HttpClient http, string host, IReadOnlyList<SsdpResponse> answers, CancellationToken cancel)
    {
        bool Has(string text) => answers.Any(a =>
            a.SearchTarget.Contains(text, StringComparison.OrdinalIgnoreCase) ||
            (a.Headers.TryGetValue("SERVER", out var server) && server.Contains(text, StringComparison.OrdinalIgnoreCase)));
        try
        {
            if (Has("roku:ecp") || Has("Roku"))
            {
                var info = await GetAsync(http, $"http://{host}:8060/query/device-info", cancel).ConfigureAwait(false);
                return info is null ? null : new FoundTv("roku", host, RokuDriver.DeviceName(info) ?? "Roku", null);
            }
            if (Has("lge-com:service:webos") || Has("WebOS"))
            {
                var location = answers.Select(a => a.Location).FirstOrDefault(l => l.Length > 0);
                var xml = location is null ? null : await GetAsync(http, location, cancel).ConfigureAwait(false);
                return new FoundTv("lg", host, Tag(xml, "friendlyName") ?? "LG TV", null);
            }
            if (Has("Samsung"))
            {
                var info = await GetAsync(http, $"http://{host}:8001/api/v2/", cancel).ConfigureAwait(false);
                var device = Json.At(Json.Parse(info ?? ""), "device");
                if (device is null || !(Json.Str(device, "type") ?? "").Contains("TV", StringComparison.OrdinalIgnoreCase)) return null;
                return new FoundTv("samsung", host, CleanName(Json.Str(device, "name") ?? "Samsung TV"), Json.Str(device, "wifiMac"));
            }
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException && !cancel.IsCancellationRequested)
        {
        }
        return null;
    }

    /// <summary>"[TV] Downstairs Samsung" → "Downstairs Samsung".</summary>
    internal static string CleanName(string name) => Bracket().Replace(name, "").Trim();

    [GeneratedRegex(@"^\s*\[[^\]]*\]\s*")]
    private static partial Regex Bracket();

    static async Task<string?> GetAsync(HttpClient http, string url, CancellationToken cancel)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        cts.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            return await http.GetStringAsync(url, cts.Token).ConfigureAwait(false);
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException && !cancel.IsCancellationRequested)
        {
            return null;
        }
    }

    static string? Tag(string? xml, string tag)
    {
        if (xml is null) return null;
        var m = Regex.Match(xml, $"<{tag}>(.*?)</{tag}>", RegexOptions.Singleline);
        return m.Success ? System.Net.WebUtility.HtmlDecode(m.Groups[1].Value.Trim()) : null;
    }
}
