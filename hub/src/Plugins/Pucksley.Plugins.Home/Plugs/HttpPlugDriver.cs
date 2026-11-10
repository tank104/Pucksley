using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Pucksley.Plugins.Shared;
using Pucksley.Sdk;

namespace Pucksley.Plugins.Plugs;

/// <summary>Anything with on/off URLs (Tasmota, ESPHome, Node-RED, a webhook): GET the URL to switch, GET the status URL to read.</summary>
internal sealed partial class HttpPlugDriver(HttpClient http, PlugDevice device) : IPlugDriver
{
    /// <summary>Keys a JSON status is likely to keep its on/off in, in order of preference.</summary>
    static readonly string[] StatusKeys = ["ison", "on", "output", "state", "power", "power1", "status", "relay", "value", "switch"];

    volatile int _last = -1; // -1 unknown, 0 off, 1 on (when there's no status URL, the last thing we did)

    public async Task<PlugState> GetStateAsync(CancellationToken cancel)
    {
        if (string.IsNullOrWhiteSpace(device.StatusUrl)) return new PlugState(_last < 0 ? null : _last == 1);
        var body = await GetAsync(device.StatusUrl, cancel).ConfigureAwait(false);
        var on = ParseStatus(body);
        if (on is bool b) _last = b ? 1 : 0;
        return new PlugState(on);
    }

    public async Task SetAsync(bool on, CancellationToken cancel)
    {
        var url = on ? device.OnUrl : device.OffUrl;
        if (string.IsNullOrWhiteSpace(url)) throw new PluginException($"{device.Name} has no {(on ? "On" : "Off")} URL set.");
        await GetAsync(url, cancel).ConfigureAwait(false);
        _last = on ? 1 : 0;
    }

    public async Task<bool> ToggleAsync(CancellationToken cancel)
    {
        var state = await GetStateAsync(cancel).ConfigureAwait(false);
        if (state.On is null) throw new PluginException($"I don't know whether {device.Name} is on. Say on or off instead (or give it a Status URL).");
        await SetAsync(!state.On.Value, cancel).ConfigureAwait(false);
        return !state.On.Value;
    }

    /// <summary>
    /// "on" / "true" / "1" means on. A JSON reply is read by its usual keys first (so <c>{"id":1,"state":"off"}</c>
    /// is off); otherwise the first on/off-like word wins.
    /// </summary>
    internal static bool? ParseStatus(string body)
    {
        var text = body.Trim();
        if (text is "1") return true;
        if (text is "0") return false;
        if (Json.Parse(text) is JsonObject obj)
        {
            foreach (var key in StatusKeys)
            {
                var match = obj.FirstOrDefault(kv => kv.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
                if (match.Value is JsonValue v && Json.Bool(v) is bool b) return b;
            }
        }
        foreach (Match m in Word().Matches(text.ToLowerInvariant()))
        {
            switch (m.Value)
            {
                case "on" or "true" or "yes": return true;
                case "off" or "false" or "no": return false;
            }
        }
        return null;
    }

    [GeneratedRegex("[a-z]+")]
    private static partial Regex Word();

    async Task<string> GetAsync(string url, CancellationToken cancel)
    {
        using var response = await http.GetAsync(url.Trim(), cancel).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancel).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new PluginException($"{device.Name}'s URL answered {(int)response.StatusCode}.");
        return body;
    }
}
