using System.Net;
using System.Text.Json.Nodes;
using Pucksley.Plugins.Shared;
using Pucksley.Sdk;

namespace Pucksley.Plugins.Plugs;

/// <summary>What <c>GET /shelly</c> says about a device.</summary>
internal sealed record ShellyInfo(int Gen, string? Model, string? Id, bool AuthEnabled)
{
    /// <summary>Gen 2+ (Plus, Pro, Gen3, Gen4) answer with <c>"gen": n</c> and speak RPC; Gen 1 has <c>"type"</c> instead.</summary>
    public static ShellyInfo? Parse(string json)
    {
        if (Json.Parse(json) is not JsonObject o) return null;
        if (Json.Num(o, "gen") is double gen && gen >= 2)
            return new ShellyInfo((int)gen, Json.Str(o, "model") ?? Json.Str(o, "app"), Json.Str(o, "id"), Json.Bool(o, "auth_en") == true);
        if (o.ContainsKey("type"))
            return new ShellyInfo(1, Json.Str(o, "type"), Json.Str(o, "mac"), Json.Bool(o, "auth") == true);
        return null;
    }
}

/// <summary>
/// Shelly relays and plugs, channel 0, over local HTTP. The generation is detected once with
/// <c>GET /shelly</c>: Gen 2+ use the RPC API (<c>/rpc/Switch.*</c>), Gen 1 the REST one (<c>/relay/0</c>).
/// </summary>
internal sealed class ShellyDriver(HttpClient http, string name, string host) : IPlugDriver
{
    ShellyInfo? _info;

    public async Task<PlugState> GetStateAsync(CancellationToken cancel)
    {
        var info = await InfoAsync(cancel).ConfigureAwait(false);
        if (info.Gen >= 2) return ParseRpcStatus(await GetAsync("rpc/Switch.GetStatus?id=0", cancel).ConfigureAwait(false));
        return ParseGen1Status(await GetAsync("status", cancel).ConfigureAwait(false));
    }

    public async Task SetAsync(bool on, CancellationToken cancel)
    {
        var info = await InfoAsync(cancel).ConfigureAwait(false);
        await GetAsync(info.Gen >= 2 ? $"rpc/Switch.Set?id=0&on={(on ? "true" : "false")}" : $"relay/0?turn={(on ? "on" : "off")}", cancel)
            .ConfigureAwait(false);
    }

    public async Task<bool> ToggleAsync(CancellationToken cancel)
    {
        var info = await InfoAsync(cancel).ConfigureAwait(false);
        if (info.Gen >= 2)
        {
            var reply = Json.Parse(await GetAsync("rpc/Switch.Toggle?id=0", cancel).ConfigureAwait(false));
            return Json.Bool(reply, "was_on") is bool wasOn ? !wasOn : (await GetStateAsync(cancel).ConfigureAwait(false)).On ?? false;
        }
        return Json.Bool(Json.Parse(await GetAsync("relay/0?turn=toggle", cancel).ConfigureAwait(false)), "ison") ?? false;
    }

    /// <summary><c>Switch.GetStatus</c>: <c>output</c>, and <c>apower</c> on models that meter power.</summary>
    internal static PlugState ParseRpcStatus(string json)
    {
        var s = Json.Parse(json);
        return new PlugState(Json.Bool(s, "output"), Json.Num(s, "apower"));
    }

    /// <summary>Gen 1 <c>/status</c>: <c>relays[0].ison</c> and <c>meters[0].power</c>.</summary>
    internal static PlugState ParseGen1Status(string json)
    {
        var s = Json.Parse(json);
        double? watts = Json.Bool(s, "meters.0.is_valid") == false ? null : Json.Num(s, "meters.0.power");
        return new PlugState(Json.Bool(s, "relays.0.ison"), watts);
    }

    async Task<ShellyInfo> InfoAsync(CancellationToken cancel)
    {
        if (_info is { } known) return known;
        var info = ShellyInfo.Parse(await GetAsync("shelly", cancel).ConfigureAwait(false))
                   ?? throw new PluginException($"{name} ({host}) doesn't look like a Shelly.");
        return _info = info;
    }

    async Task<string> GetAsync(string path, CancellationToken cancel)
    {
        using var response = await http.GetAsync(new Uri($"http://{host}/{path}"), cancel).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
            throw new PluginException($"{name} has a login password set. Turn off restricted login in the Shelly app to use it here.");
        var body = await response.Content.ReadAsStringAsync(cancel).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new PluginException($"{name} answered {(int)response.StatusCode}: {Json.Str(Json.Parse(body), "message") ?? response.ReasonPhrase}.");
        return body;
    }
}
