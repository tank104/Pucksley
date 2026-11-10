using System.Net;
using System.Text.Json.Nodes;
using Pucksley.Plugins.Plugs;
using Pucksley.Sdk;
using Pucksley.TestSupport;

namespace Pucksley.Plugins.Home.Tests;

public class PlugsTests
{
    // Real answers from Shelly Gen3 relays on the LAN (read-only GETs), plus documented Gen2 / Gen1 shapes.
    const string Gen3Pm = """{"name":null,"id":"shelly1pmminig3-34b7da9015b8","mac":"34B7DA9015B8","slot":0,"model":"S3SW-001P8EU","gen":3,"fw_id":"20240625-122608/1.3.3-gbdfd9b3","ver":"1.3.3","app":"Mini1PMG3","auth_en":false,"auth_domain":null}""";
    const string Gen3Mini = """{"name":null,"id":"shelly1minig3-48f6ee878504","mac":"48F6EE878504","slot":0,"model":"S3SW-001X8EU","gen":3,"fw_id":"20260311-095839/1.7.5-g9979d16","ver":"1.7.5","app":"Mini1G3","auth_en":false,"auth_domain":null,"matter":false,"provision":"complete"}""";
    const string Gen2 = """{"name":"Heater","id":"shellyplus1pm-a8032ab12345","mac":"A8032AB12345","model":"SNSW-001P16EU","gen":2,"fw_id":"20231107-164738/1.0.8-g8c7bb8d","ver":"1.0.8","app":"Plus1PM","auth_en":true,"auth_domain":"shellyplus1pm-a8032ab12345"}""";
    const string Gen1 = """{"type":"SHSW-1","mac":"A4CF12F45678","auth":false,"fw":"20230913-112003/v1.14.0-gcb84623","longid":1}""";
    const string Gen3PmStatus = """{"id":0, "source":"init", "output":true, "apower":6.2, "voltage":232.5, "freq":49.9, "current":0.035, "aenergy":{"total":10250.513,"by_minute":[207.099,0.000,207.099],"minute_ts":1794210000}, "ret_aenergy":{"total":0.000,"by_minute":[0.000,0.000,0.000],"minute_ts":1794210000},"temperature":{"tC":67.3, "tF":153.1}}""";
    const string Gen3MiniStatus = """{"id":0, "source":"HTTP_in", "output":false,"temperature":{"tC":60.1, "tF":140.1}}""";
    const string Gen1Status = """{"wifi_sta":{"connected":true},"relays":[{"ison":true,"has_timer":false,"source":"http"}],"meters":[{"power":41.27,"is_valid":true}],"temperature":45.1}""";

    [Fact]
    public void Detects_shelly_generations()
    {
        Assert.Equal(new ShellyInfo(3, "S3SW-001P8EU", "shelly1pmminig3-34b7da9015b8", false), ShellyInfo.Parse(Gen3Pm));
        Assert.Equal(3, ShellyInfo.Parse(Gen3Mini)!.Gen);
        Assert.Equal(new ShellyInfo(2, "SNSW-001P16EU", "shellyplus1pm-a8032ab12345", true), ShellyInfo.Parse(Gen2));
        Assert.Equal(new ShellyInfo(1, "SHSW-1", "A4CF12F45678", false), ShellyInfo.Parse(Gen1));
        Assert.Null(ShellyInfo.Parse("<html>router login</html>"));
        Assert.Null(ShellyInfo.Parse("""{"hello":"world"}"""));
    }

    [Fact]
    public void Reads_shelly_status()
    {
        Assert.Equal(new PlugState(true, 6.2), ShellyDriver.ParseRpcStatus(Gen3PmStatus));
        Assert.Equal(new PlugState(false, null), ShellyDriver.ParseRpcStatus(Gen3MiniStatus));
        Assert.Equal(new PlugState(true, 41.27), ShellyDriver.ParseGen1Status(Gen1Status));
        Assert.Equal(new PlugState(true, null), ShellyDriver.ParseGen1Status(Gen1Status.Replace("\"is_valid\":true", "\"is_valid\":false")));
    }

    [Fact]
    public async Task Drives_a_gen3_shelly_over_rpc()
    {
        var http = new MockHttp()
            .On(HttpMethod.Get, "/shelly", Gen3Pm)
            .On(HttpMethod.Get, "/rpc/Switch.GetStatus", Gen3PmStatus)
            .On(HttpMethod.Get, "/rpc/Switch.Set", """{"was_on":true}""")
            .On(HttpMethod.Get, "/rpc/Switch.Toggle", """{"was_on":true}""");
        var driver = new ShellyDriver(http.Client(), "Lamp", "192.168.1.33");

        Assert.Equal(new PlugState(true, 6.2), await driver.GetStateAsync(CancellationToken.None));
        await driver.SetAsync(false, CancellationToken.None);
        Assert.False(await driver.ToggleAsync(CancellationToken.None));

        Assert.Single(http.To(HttpMethod.Get, "/shelly")); // generation detected once
        Assert.Equal("http://192.168.1.33/rpc/Switch.GetStatus?id=0", http.To(HttpMethod.Get, "/rpc/Switch.GetStatus").Single().Uri.ToString());
        Assert.Equal("/rpc/Switch.Set?id=0&on=false", http.To(HttpMethod.Get, "/rpc/Switch.Set").Single().PathAndQuery);
    }

    [Fact]
    public async Task Drives_a_gen1_shelly_over_rest()
    {
        var http = new MockHttp()
            .On(HttpMethod.Get, "/shelly", Gen1)
            .On(HttpMethod.Get, "/status", Gen1Status)
            .On(HttpMethod.Get, "/relay/0", """{"ison":false,"has_timer":false}""");
        var driver = new ShellyDriver(http.Client(), "Pump", "192.168.1.40");

        Assert.Equal(new PlugState(true, 41.27), await driver.GetStateAsync(CancellationToken.None));
        await driver.SetAsync(true, CancellationToken.None);
        Assert.False(await driver.ToggleAsync(CancellationToken.None));
        Assert.Equal(["/relay/0?turn=on", "/relay/0?turn=toggle"], http.To(HttpMethod.Get, "/relay/0").Select(r => r.PathAndQuery));
    }

    [Fact]
    public async Task A_password_protected_shelly_says_how_to_fix_it()
    {
        var http = new MockHttp().On(HttpMethod.Get, "/shelly", Gen2).On(HttpMethod.Get, "/rpc/Switch.GetStatus", "", HttpStatusCode.Unauthorized);
        var error = await Assert.ThrowsAsync<PluginException>(() => new ShellyDriver(http.Client(), "Heater", "192.168.1.50").GetStateAsync(CancellationToken.None));
        Assert.Contains("restricted login", error.Message);
    }

    [Theory]
    [InlineData("on", true)]
    [InlineData("OFF", false)]
    [InlineData("1", true)]
    [InlineData("0", false)]
    [InlineData("true", true)]
    [InlineData("""{"POWER":"ON"}""", true)]
    [InlineData("""{"POWER1":"OFF"}""", false)]
    [InlineData("""{"id":1,"state":"off"}""", false)]
    [InlineData("""{"ison":true}""", true)]
    [InlineData("""{"output":0}""", false)]
    [InlineData("The relay is on.", true)]
    [InlineData("status: off (since 10:02)", false)]
    [InlineData("hello", null)]
    public void Reads_http_status_bodies(string body, bool? expected) => Assert.Equal(expected, HttpPlugDriver.ParseStatus(body));

    // ---------------------------------------------------------------- the plugin with http plugs (no devices needed)

    static MockHttp FakeWebhooks()
    {
        var http = new MockHttp();
        foreach (var name in new[] { "lamp", "fan" })
        {
            http.On(HttpMethod.Get, $"/{name}/on", "ok");
            http.On(HttpMethod.Get, $"/{name}/off", "ok");
        }
        http.On(HttpMethod.Get, "/lamp/status", """{"POWER":"OFF"}""");
        return http;
    }

    static Task<(PlugsPlugin Plugin, FakePluginContext Context)> StartAsync(MockHttp http) =>
        TestPlugins.StartAsync<PlugsPlugin>(new PlugsSettings
        {
            Devices =
            [
                new PlugDevice { Name = "Lamp", Type = "http", OnUrl = "http://hooks.test/lamp/on", OffUrl = "http://hooks.test/lamp/off", StatusUrl = "http://hooks.test/lamp/status" },
                new PlugDevice { Name = "Fan", Type = "http", OnUrl = "http://hooks.test/fan/on", OffUrl = "http://hooks.test/fan/off" },
                new PlugDevice { Name = "", Type = "shelly", Host = "192.168.1.99" }, // ignored: no name
            ],
        }, http.Client());

    [Fact]
    public async Task Lists_switches_and_toggles_plugs()
    {
        var http = FakeWebhooks();
        var (plugin, _) = await StartAsync(http);
        Assert.Equal((StatusLevel.Ok, "2 plugs, 2 reachable"), (plugin.Status.Level, plugin.Status.Message));

        var list = JsonNode.Parse((await FakePluginContext.CallToolAsync(plugin, "plugs_list")).Text)!.AsArray();
        Assert.Equal(("Lamp", false), ((string)list[0]!["name"]!, (bool)list[0]!["on"]!));
        Assert.Null(list[1]!["on"]); // no status URL and nothing done yet: unknown

        var on = await FakePluginContext.CallToolAsync(plugin, "plugs_set", """{"name":"the lamp","on":true}""");
        Assert.Equal(("Lamp is on.", "Lamp on"), (on.Text, on.Activity));
        Assert.Single(http.To(HttpMethod.Get, "/lamp/on"));

        var all = await FakePluginContext.CallToolAsync(plugin, "plugs_set", """{"name":"all","on":false}""");
        Assert.Equal("Turned off Lamp and Fan.", all.Text);

        var toggled = await FakePluginContext.CallToolAsync(plugin, "plugs_toggle", """{"name":"fan"}""");
        Assert.Equal("Fan is on.", toggled.Text); // it was just turned off
        await plugin.StopAsync();
    }

    [Fact]
    public async Task Unknown_plug_lists_the_real_ones()
    {
        var (plugin, _) = await StartAsync(FakeWebhooks());
        var result = await FakePluginContext.CallToolAsync(plugin, "plugs_set", """{"name":"kettle","on":true}""");
        Assert.True(result.IsError);
        Assert.Equal("I can't find a plug called 'kettle'. Plugs: Lamp and Fan.", result.Text);
        await plugin.StopAsync();
    }

    [Fact]
    public async Task Each_plug_is_a_toggle_on_the_puck()
    {
        var http = FakeWebhooks();
        var (plugin, _) = await StartAsync(http);

        var controls = plugin.GetControls();
        Assert.Equal(["lamp", "fan"], controls.Select(c => c.Id));
        Assert.All(controls, c => Assert.Equal((ControlKind.Toggle, "Plugs"), (c.Kind, c.Group)));

        await plugin.OnControlAsync("lamp", new ControlInput { On = true }, CancellationToken.None);
        Assert.Single(http.To(HttpMethod.Get, "/lamp/on"));
        Assert.True(plugin.GetControls()[0].On);
        await plugin.StopAsync();
    }

    [Fact]
    public async Task Unreachable_plugs_show_in_the_status()
    {
        var http = FakeWebhooks().On(HttpMethod.Get, "/lamp/status", "", HttpStatusCode.InternalServerError);
        var (plugin, _) = await StartAsync(http);
        Assert.Equal((StatusLevel.Warning, "2 plugs, 1 reachable"), (plugin.Status.Level, plugin.Status.Message));
        Assert.Equal("Offline", plugin.GetControls()[0].Text);
        await plugin.StopAsync();
    }

    [Fact]
    public async Task No_plugs_asks_for_some()
    {
        var (plugin, _) = await TestPlugins.StartAsync<PlugsPlugin>(new PlugsSettings());
        Assert.Equal(StatusLevel.Warning, plugin.Status.Level);
        var result = await FakePluginContext.CallToolAsync(plugin, "plugs_list");
        Assert.True(result.IsError);
    }
}
