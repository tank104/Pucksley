using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Pucksley.Plugins.Plugs;
using Pucksley.Sdk;
using static Pucksley.Plugins.Home.Tests.TestPlugins;

namespace Pucksley.Plugins.Home.Tests;

public class KlapTests
{
    // Reference vectors computed independently in Python (hashlib + cryptography, python-kasa's algorithms)
    // for email "test@example.com", password "secret", local seed 00..0f, remote seed 10..1f.
    const string Email = "test@example.com", Password = "secret";
    static readonly byte[] Local = Enumerable.Range(0, 16).Select(i => (byte)i).ToArray();
    static readonly byte[] Remote = Enumerable.Range(16, 16).Select(i => (byte)i).ToArray();

    [Fact]
    public void Auth_hashes_match_reference()
    {
        Assert.Equal("954015608ff4182f10441368d2ec3bc882762457223dabe86015725e5eda9627", Hex(KlapCrypto.AuthHashV2(Email, Password)));
        Assert.Equal("24514e5e45389621690d654dfa8f787b", Hex(KlapCrypto.AuthHashV1(Email, Password)));
    }

    [Fact]
    public void Handshake_hashes_match_reference()
    {
        var v2 = KlapCrypto.AuthHashV2(Email, Password);
        var v1 = KlapCrypto.AuthHashV1(Email, Password);
        Assert.Equal("f4088775415fb73bd54a9d0b0bcb68b170cae9a6cffa72afb8e65ca37db7f654", Hex(KlapCrypto.Handshake1Hash(2, Local, Remote, v2)));
        Assert.Equal("3dfa7474d89f111f520c8d0a882fd9a59968714b9abc966ead18067544d3955a", Hex(KlapCrypto.Handshake2Hash(2, Local, Remote, v2)));
        Assert.Equal("27cedab27a029950e15412f90336b4846cc62a7a30728c17a8ac125f13c6d473", Hex(KlapCrypto.Handshake1Hash(1, Local, Remote, v1)));
        Assert.Equal("b5071209b8a65051117c9bc9aa2782e513317b6eb9a7d0efa0f5060ac41b1dc7", Hex(KlapCrypto.Handshake2Hash(1, Local, Remote, v1)));
    }

    [Fact]
    public void Session_keys_match_reference()
    {
        var session = new KlapSession(Local, Remote, KlapCrypto.AuthHashV2(Email, Password));
        Assert.Equal("a09ea8611a1cdbcc17d4e2da709ba96c", Hex(session.Key));
        Assert.Equal("4399c1b9215fa4a3f452e843", Hex(session.Iv));
        Assert.Equal("58db2e962e7d592680393a9662f73acecb9c5aacfab23b62e56f9e49", Hex(session.Signature));
        Assert.Equal(-866903049, session.Seq); // signed: the top bit of ivseed[28] is set
    }

    [Fact]
    public void Signed_seq_from_another_seed_matches_reference()
    {
        var remote = Bytes("011112131415161718191a1b1c1d1e1f");
        Assert.Equal(-1044645132, new KlapSession(Local, remote, KlapCrypto.AuthHashV2(Email, Password)).Seq);
    }

    [Fact]
    public void Encrypts_first_request_exactly_like_reference()
    {
        var session = new KlapSession(Local, Remote, KlapCrypto.AuthHashV2(Email, Password));
        var (body, seq) = session.Encrypt("""{"method":"get_device_info"}"""u8.ToArray());
        Assert.Equal(-866903048, seq);
        Assert.Equal(
            "c902311882f4189cece0d54fc98c8680c2a320002e66ce5671721e634f886c05f4cfee3044cea4b57ad04333c818a316a13861fd74a9bb6190b53e36d3e4282f",
            Hex(body));
        Assert.Equal(seq + 1, session.Encrypt([1, 2, 3]).Seq);
    }

    [Fact]
    public void Decrypts_a_device_reply_and_checks_its_signature()
    {
        var auth = KlapCrypto.AuthHashV2(Email, Password);
        var session = new KlapSession(Local, Remote, auth);
        var (_, seq) = session.Encrypt("{}"u8.ToArray());

        // The device encrypts its reply with the request's seq (built here with raw AES, not KlapSession)
        var seqBytes = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(seqBytes, seq);
        using var aes = Aes.Create();
        aes.Key = session.Key;
        byte[] ivFull = session.Iv.Concat(seqBytes).ToArray();
        byte[] cipher = aes.EncryptCbc("""{"error_code":0,"result":{"device_on":true}}"""u8.ToArray(), ivFull, PaddingMode.PKCS7);
        byte[] signature = SHA256.HashData(session.Signature.Concat(seqBytes).Concat(cipher).ToArray());
        byte[] reply = signature.Concat(cipher).ToArray();

        Assert.Equal("""{"error_code":0,"result":{"device_on":true}}""", Encoding.UTF8.GetString(session.Decrypt(reply, seq)));

        reply[0] ^= 0xFF;
        Assert.Throws<InvalidDataException>(() => session.Decrypt(reply, seq));
        Assert.Throws<InvalidDataException>(() => session.Decrypt(new byte[20], seq));
    }

    [Fact]
    public void Round_trips_many_requests()
    {
        var auth = KlapCrypto.AuthHashV2(Email, Password);
        var client = new KlapSession(Local, Remote, auth);
        var mirror = new KlapSession(Local, Remote, auth);
        for (int i = 0; i < 50; i++)
        {
            var plain = Encoding.UTF8.GetBytes(new string('x', i) + "{\"n\":" + i + "}");
            var (body, seq) = client.Encrypt(plain);
            Assert.Equal(plain, mirror.Decrypt(body, seq)); // a request is signed and padded the same way as a reply
        }
    }

    [Fact]
    public void Matches_whichever_credentials_the_device_knows()
    {
        var candidates = KlapCrypto.Candidates(Email, Password);

        var account = KlapCrypto.Match(Local, Remote, KlapCrypto.Handshake1Hash(2, Local, Remote, KlapCrypto.AuthHashV2(Email, Password)), candidates);
        Assert.Equal((2, "account"), (account!.Version, account.Label));

        var kasaV1 = KlapCrypto.Match(Local, Remote, KlapCrypto.Handshake1Hash(1, Local, Remote, KlapCrypto.AuthHashV1(Email, Password)), candidates);
        Assert.Equal((1, "account"), (kasaV1!.Version, kasaV1.Label));

        var blank = KlapCrypto.Match(Local, Remote, KlapCrypto.Handshake1Hash(2, Local, Remote, KlapCrypto.AuthHashV2("", "")), candidates);
        Assert.Equal("blank", blank!.Label);

        var tapoDefault = KlapCrypto.Match(Local, Remote, KlapCrypto.Handshake1Hash(2, Local, Remote, KlapCrypto.AuthHashV2("test@tp-link.net", "test")), candidates);
        Assert.Equal("Tapo default", tapoDefault!.Label);

        Assert.Null(KlapCrypto.Match(Local, Remote, new byte[32], candidates));
    }

    [Fact]
    public void Reads_session_cookies()
    {
        using var response = new HttpResponseMessage();
        response.Headers.Add("Set-Cookie", "TP_SESSIONID=0123ABCD;TIMEOUT=1440");
        Assert.Equal(("0123ABCD", 1440), KlapTransport.ReadCookies(response.Headers));
    }

    static FakeKlapDevice TapoP110(bool on = true, int energyError = 0) => new(Email, Password, 2, request =>
        request.Contains("get_device_info")
            ? $$$"""{"error_code":0,"result":{"device_on":{{{(on ? "true" : "false")}}},"nickname":"{{{Convert.ToBase64String("Heater"u8.ToArray())}}}","model":"P110"}}"""
            : request.Contains("get_energy_usage")
                ? energyError == 0 ? """{"error_code":0,"result":{"current_power":48250,"today_energy":120}}""" : $$$"""{"error_code":{{{energyError}}}}"""
                : """{"error_code":0}""");

    [Fact]
    public async Task Tapo_reads_state_and_power_over_klap()
    {
        var device = TapoP110();
        var driver = new TapoDriver("Heater", new KlapTransport(device.Client(), "192.168.1.70", Email, Password));

        var state = await driver.GetStateAsync(CancellationToken.None);

        Assert.True(state.On);
        Assert.Equal(48.25, state.Watts!.Value, 3);
        Assert.Equal(1, device.Handshakes);
        Assert.Contains("\"method\":\"get_device_info\"", device.Requests[0]);
        Assert.Contains("terminal_uuid", device.Requests[0]);
        Assert.Equal("Heater", TapoDriver.DecodeNickname(Convert.ToBase64String("Heater"u8.ToArray())));
    }

    [Fact]
    public async Task Tapo_switches_and_re_handshakes_when_the_session_expires()
    {
        var device = TapoP110(on: false);
        var transport = new KlapTransport(device.Client(), "192.168.1.70", Email, Password);
        var driver = new TapoDriver("Heater", transport);

        await driver.SetAsync(true, CancellationToken.None);
        device.ExpireNextRequest = true;
        await driver.SetAsync(false, CancellationToken.None);

        Assert.Equal(2, device.Handshakes);
        Assert.Contains(device.Requests, r => r.Contains("\"params\":{\"device_on\":true}"));
        Assert.Contains(device.Requests, r => r.Contains("\"params\":{\"device_on\":false}"));
        Assert.Equal("account", transport.AuthLabel);
    }

    [Fact]
    public async Task Tapo_without_energy_monitoring_stops_asking()
    {
        var device = TapoP110(energyError: -1002);
        var driver = new TapoDriver("Lamp", new KlapTransport(device.Client(), "192.168.1.71", Email, Password));

        Assert.Equal(new PlugState(true, null), await driver.GetStateAsync(CancellationToken.None));
        Assert.Equal(new PlugState(true, null), await driver.GetStateAsync(CancellationToken.None));
        Assert.Single(device.Requests, r => r.Contains("get_energy_usage"));
    }

    [Fact]
    public async Task Wrong_password_is_reported_plainly()
    {
        var device = TapoP110();
        var driver = new TapoDriver("Heater", new KlapTransport(device.Client(), "192.168.1.70", Email, "wrong"));
        var error = await Assert.ThrowsAsync<PluginException>(() => driver.GetStateAsync(CancellationToken.None));
        Assert.Contains("didn't accept the TP-Link account", error.Message);
    }
}
