using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Pucksley.Plugins.Shared;
using Pucksley.Sdk;

namespace Pucksley.Plugins.Plugs;

/// <summary>
/// Tapo plugs (P100, P110, P115…): the "SMART" JSON protocol (<c>{"method": …}</c>) over KLAP.
/// Power comes from <c>get_energy_usage.current_power</c> (mW) on models that meter it.
/// </summary>
internal sealed class TapoDriver(string name, KlapTransport klap) : IPlugDriver
{
    /// <summary>Errors that mean "your session is stale": handshake again and retry once.</summary>
    static readonly HashSet<int> SessionErrors = [9999, -40401, -1012, 1002, 1003, -1005];

    /// <summary>Errors meaning the method isn't there (a plug without energy monitoring).</summary>
    static readonly HashSet<int> Unsupported = [-1, -1002, -1008, -40210];

    readonly string _terminalUuid = Convert.ToBase64String(MD5.HashData(Guid.NewGuid().ToByteArray()));
    volatile bool _noEnergy;

    public async Task<PlugState> GetStateAsync(CancellationToken cancel)
    {
        var info = await CallAsync("get_device_info", null, cancel).ConfigureAwait(false);
        bool? on = Json.Bool(info, "device_on");
        double? watts = null;
        if (on is not null && !_noEnergy)
        {
            try
            {
                var energy = await CallAsync("get_energy_usage", null, cancel).ConfigureAwait(false);
                if (Json.Num(energy, "current_power") is double mw) watts = mw / 1000.0;
            }
            catch (TapoException e) when (Unsupported.Contains(e.Code))
            {
                _noEnergy = true;
            }
            catch (TapoException)
            {
                // a passing hiccup: no watts this time
            }
        }
        return new PlugState(on, watts);
    }

    public Task SetAsync(bool on, CancellationToken cancel) =>
        CallAsync("set_device_info", new JsonObject { ["device_on"] = on }, cancel);

    public async Task<bool> ToggleAsync(CancellationToken cancel)
    {
        var state = await GetStateAsync(cancel).ConfigureAwait(false);
        bool target = !(state.On ?? false);
        await SetAsync(target, cancel).ConfigureAwait(false);
        return target;
    }

    /// <summary>The nickname a Tapo device reports is base64; decode it for display.</summary>
    internal static string? DecodeNickname(string? value)
    {
        if (string.IsNullOrEmpty(value)) return null;
        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(value));
        }
        catch (FormatException)
        {
            return value;
        }
    }

    internal string BuildRequest(string method, JsonObject? parameters)
    {
        var request = new JsonObject
        {
            ["method"] = method,
            ["request_time_milis"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            ["terminal_uuid"] = _terminalUuid,
        };
        if (parameters is not null) request["params"] = parameters;
        return request.ToJsonString();
    }

    async Task<JsonNode?> CallAsync(string method, JsonObject? parameters, CancellationToken cancel)
    {
        for (int attempt = 0; ; attempt++)
        {
            var reply = Json.Parse(await klap.SendAsync(BuildRequest(method, parameters), cancel).ConfigureAwait(false));
            int code = (int)(Json.Num(reply, "error_code") ?? 0);
            if (code == 0) return reply?["result"];
            if (attempt == 0 && SessionErrors.Contains(code))
            {
                klap.Reset();
                continue;
            }
            throw new TapoException(code, $"{name} reported error {code} for {method}.");
        }
    }
}

/// <summary>A Tapo device answered with a non-zero <c>error_code</c>. Reported to people as a <see cref="PluginException"/> by the plugin.</summary>
internal sealed class TapoException(int code, string message) : Exception(message)
{
    public int Code { get; } = code;
}
