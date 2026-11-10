using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using Pucksley.Plugins.Plugs;
using static Pucksley.Plugins.Home.Tests.TestPlugins;

namespace Pucksley.Plugins.Home.Tests;

public class KasaTests
{
    // Reference vectors computed independently in Python (python-kasa's XorEncryption algorithm).
    const string SysinfoVector = "0000001dd0f281f88bff9af7d5ef94b6d1b4c09fec95e68fe187e8caf08bf68bf6";
    const string RelayOnVector = "0000002ad0f281f88bff9af7d5ef94b6c5a0d48bf99cf091e8b7c4b0d1a5c0e2d8a381f286e793f6d4eedfa2dfa2";

    [Fact]
    public void Xor_encrypts_known_vectors_with_length_prefix()
    {
        Assert.Equal(SysinfoVector, Hex(KasaCipher.Encrypt("""{"system":{"get_sysinfo":{}}}""")));
        Assert.Equal(RelayOnVector, Hex(KasaCipher.Encrypt("""{"system":{"set_relay_state":{"state":1}}}""")));
    }

    [Fact]
    public void Xor_decrypts_known_vector()
    {
        var packet = Bytes(RelayOnVector);
        Assert.Equal(packet.Length - 4, BinaryPrimitives.ReadInt32BigEndian(packet));
        Assert.Equal("""{"system":{"set_relay_state":{"state":1}}}""", KasaCipher.Decrypt(packet.AsSpan(4)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("""{"system":{"get_sysinfo":{"alias":"Lämp ☕","relay_state":1}}}""")]
    public void Xor_round_trips(string json)
    {
        var packet = KasaCipher.Encrypt(json);
        Assert.Equal(json, KasaCipher.Decrypt(packet.AsSpan(4)));
    }

    [Fact]
    public void Parses_sysinfo_and_emeter_in_milliwatts_or_watts()
    {
        var v2 = JsonNode.Parse("""{"system":{"get_sysinfo":{"relay_state":1,"alias":"Heater"}},"emeter":{"get_realtime":{"power_mw":1234560,"err_code":0}}}""");
        var state = KasaDriver.ParseState(v2);
        Assert.True(state.On);
        Assert.Equal(1234.56, state.Watts!.Value, 3);

        var v1 = JsonNode.Parse("""{"system":{"get_sysinfo":{"relay_state":0}},"emeter":{"get_realtime":{"power":12.5,"err_code":0}}}""");
        Assert.Equal(new PlugState(false, 12.5), KasaDriver.ParseState(v1));

        var noMeter = JsonNode.Parse("""{"system":{"get_sysinfo":{"relay_state":1}},"emeter":{"err_code":-1,"err_msg":"module not support"}}""");
        Assert.Equal(new PlugState(true, null), KasaDriver.ParseState(noMeter));
    }

    [Fact]
    public async Task Talks_to_a_legacy_plug_over_tcp()
    {
        // A fake HS110 on loopback: reads one framed request, answers one framed reply.
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        string? received = null;
        var device = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            var stream = client.GetStream();
            var header = new byte[4];
            await stream.ReadExactlyAsync(header);
            var payload = new byte[BinaryPrimitives.ReadInt32BigEndian(header)];
            await stream.ReadExactlyAsync(payload);
            received = KasaCipher.Decrypt(payload);
            await stream.WriteAsync(KasaCipher.Encrypt("""{"system":{"get_sysinfo":{"relay_state":1,"alias":"Lamp"}},"emeter":{"err_code":-1}}"""));
        });

        var driver = new KasaDriver("Lamp", "127.0.0.1", () => throw new InvalidOperationException("should not use KLAP"), port);
        var state = await driver.GetStateAsync(CancellationToken.None);
        await device;

        Assert.Equal(new PlugState(true, null), state);
        Assert.Contains("get_sysinfo", received);
        Assert.Contains("get_realtime", received);
    }

    [Fact]
    public async Task Falls_back_to_klap_when_port_9999_is_closed()
    {
        // A port that refuses: bind, note it, release.
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int closedPort = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        var device = new FakeKlapDevice("me@example.com", "pw", version: 1, request =>
            request.Contains("set_relay_state") ? """{"system":{"set_relay_state":{"err_code":0}}}"""
                : """{"system":{"get_sysinfo":{"relay_state":0}},"emeter":{"get_realtime":{"power_mw":2500}}}""");
        var http = device.Client();
        var driver = new KasaDriver("Fan", "127.0.0.1", () => new KlapTransport(http, "127.0.0.1", "me@example.com", "pw"), closedPort);

        var state = await driver.GetStateAsync(CancellationToken.None);
        Assert.Equal(new PlugState(false, 2.5), state);
        Assert.True(await driver.ToggleAsync(CancellationToken.None));
        Assert.Contains(device.Requests, r => r.Contains("\"state\":1"));
    }
}
