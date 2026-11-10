using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using Pucksley.Plugins.Shared;
using Pucksley.Sdk;

namespace Pucksley.Plugins.Plugs;

/// <summary>
/// Kasa plugs (HS100/HS110/KP115…): the "IOT" JSON protocol (<c>{"system":{"get_sysinfo":{}}}</c>).
/// Older firmware speaks it on TCP 9999 with the XOR cipher; newer firmware closes 9999 and wraps the
/// same JSON in KLAP on port 80, so a refused connection switches to KLAP for good.
/// </summary>
/// <param name="legacyPort">The XOR protocol's port (tests point it elsewhere).</param>
internal sealed class KasaDriver(string name, string host, Func<KlapTransport> klapFactory, int legacyPort = 9999) : IPlugDriver
{
    const int LegacyPort = 9999;
    const int MaxReply = 256 * 1024;

    const string StateQuery = """{"system":{"get_sysinfo":{}},"emeter":{"get_realtime":{}}}""";

    enum Mode { Unknown, Legacy, Klap }

    volatile Mode _mode = Mode.Unknown;
    KlapTransport? _klap;
    readonly object _lock = new();

    public async Task<PlugState> GetStateAsync(CancellationToken cancel) =>
        ParseState(await QueryAsync(StateQuery, cancel).ConfigureAwait(false));

    public async Task SetAsync(bool on, CancellationToken cancel)
    {
        var request = new JsonObject { ["system"] = new JsonObject { ["set_relay_state"] = new JsonObject { ["state"] = on ? 1 : 0 } } };
        var reply = await QueryAsync(request.ToJsonString(), cancel).ConfigureAwait(false);
        if (Json.Num(reply, "system.set_relay_state.err_code") is double err && err != 0)
            throw new PluginException($"{name} refused (error {err}: {Json.Str(reply, "system.set_relay_state.err_msg") ?? "unknown"}).");
    }

    public async Task<bool> ToggleAsync(CancellationToken cancel)
    {
        var state = await GetStateAsync(cancel).ConfigureAwait(false);
        bool target = !(state.On ?? false);
        await SetAsync(target, cancel).ConfigureAwait(false);
        return target;
    }

    /// <summary><c>relay_state</c> from sysinfo; watts from the emeter (<c>power_mw</c>, or <c>power</c> in W on v1 hardware).</summary>
    internal static PlugState ParseState(JsonNode? reply)
    {
        var info = Json.At(reply, "system.get_sysinfo");
        bool? on = Json.Num(info, "relay_state") is double rs ? rs != 0 : null;
        var meter = Json.At(reply, "emeter.get_realtime");
        double? watts = Json.Num(meter, "power_mw") is double mw ? mw / 1000.0 : Json.Num(meter, "power");
        if (Json.Num(meter, "err_code") is double err && err != 0) watts = null;
        return new PlugState(on, watts);
    }

    async Task<JsonNode?> QueryAsync(string json, CancellationToken cancel)
    {
        if (_mode != Mode.Klap)
        {
            try
            {
                var reply = await LegacyQueryAsync(host, json, cancel, legacyPort).ConfigureAwait(false);
                _mode = Mode.Legacy;
                return reply;
            }
            catch (SocketException e) when (_mode == Mode.Unknown && e.SocketErrorCode is SocketError.ConnectionRefused or SocketError.ConnectionReset)
            {
                _mode = Mode.Klap;
            }
        }
        KlapTransport klap;
        lock (_lock) klap = _klap ??= klapFactory();
        return Json.Parse(await klap.SendAsync(json, cancel).ConfigureAwait(false));
    }

    /// <summary>One request on TCP 9999: length-prefixed XOR in, length-prefixed XOR out.</summary>
    internal static async Task<JsonNode?> LegacyQueryAsync(string host, string json, CancellationToken cancel, int port = LegacyPort)
    {
        using var tcp = new TcpClient { NoDelay = true };
        await tcp.ConnectAsync(host, port, cancel).ConfigureAwait(false);
        var stream = tcp.GetStream();
        await stream.WriteAsync(KasaCipher.Encrypt(json), cancel).ConfigureAwait(false);
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, cancel).ConfigureAwait(false);
        int length = BinaryPrimitives.ReadInt32BigEndian(header);
        if (length <= 0 || length > MaxReply) throw new InvalidDataException($"Unexpected Kasa reply length {length}.");
        var payload = new byte[length];
        await stream.ReadExactlyAsync(payload, cancel).ConfigureAwait(false);
        return Json.Parse(KasaCipher.Decrypt(payload));
    }
}
