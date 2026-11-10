using System.Text.Json.Nodes;
using Pucksley.Plugins.Tv;
using Pucksley.Sdk;
using Pucksley.TestSupport;

namespace Pucksley.Plugins.Home.Tests;

public class TvTests
{
    // ---------------------------------------------------------------- which TV

    static readonly string[] Tvs = ["Lounge", "Bedroom TV", "Downstairs Samsung"];

    [Theory]
    [InlineData("lounge", null, "Lounge")]
    [InlineData("the bedroom tv", "Lounge", "Bedroom TV")]
    [InlineData("downstairs", null, "Downstairs Samsung")]
    [InlineData(null, "Bedroom", "Bedroom TV")]
    [InlineData(null, "Lounge", "Lounge")]
    [InlineData("the TV", "Bedroom", "Bedroom TV")]
    [InlineData(null, "Kitchen", "Lounge")]
    [InlineData(null, null, "Lounge")]
    public void Picks_the_named_tv_else_the_pucks_room_else_the_first(string? spoken, string? room, string expected) =>
        Assert.Equal(expected, TvPicker.Pick(Tvs, t => t, spoken, room));

    [Fact]
    public void Unknown_tv_lists_the_real_ones()
    {
        var error = Assert.Throws<PluginException>(() => TvPicker.Pick(Tvs, t => t, "garage", null));
        Assert.Contains("Lounge, Bedroom TV and Downstairs Samsung", error.Message);
        Assert.Throws<PluginException>(() => TvPicker.Pick(Array.Empty<string>(), t => t, null, null));
    }

    // ---------------------------------------------------------------- keys, inputs and apps

    [Theory]
    [InlineData("HDMI 2", "KEY_HDMI2")]
    [InlineData("hdmi2", "KEY_HDMI2")]
    [InlineData("HDMI two", "KEY_HDMI2")]
    [InlineData("hdmi 4", "KEY_HDMI4")]
    [InlineData("live TV", "KEY_TV")]
    [InlineData("Freeview", "KEY_TV")]
    [InlineData("hdmi", "KEY_HDMI")]
    [InlineData("the PlayStation", "KEY_SOURCE")]
    public void Samsung_input_keys(string spoken, string key) => Assert.Equal(key, TvKeys.SamsungInputKey(spoken));

    [Theory]
    [InlineData("HDMI 1", "InputHDMI1")]
    [InlineData("hdmi three", "InputHDMI3")]
    [InlineData("antenna", "InputTuner")]
    [InlineData("AV", "InputAV1")]
    [InlineData("xbox", null)]
    public void Roku_input_keys(string spoken, string? key) => Assert.Equal(key, TvKeys.RokuInputKey(spoken));

    [Theory]
    [InlineData("play", "KEY_PLAY", "Play")]
    [InlineData("pause", "KEY_PAUSE", "Play")]
    [InlineData("stop", "KEY_STOP", "Back")]
    [InlineData("home", "KEY_HOME", "Home")]
    [InlineData("back", "KEY_RETURN", "Back")]
    [InlineData("resume", "KEY_PLAY", "Play")]
    public void Media_keys(string action, string samsung, string roku)
    {
        Assert.Equal(samsung, TvKeys.SamsungMediaKey(action));
        Assert.Equal(roku, TvKeys.RokuMediaKey(action));
    }

    [Fact]
    public void Unknown_media_action_is_refused() => Assert.Throws<PluginException>(() => TvKeys.SamsungMediaKey("rewind"));

    [Theory]
    [InlineData("Netflix", "3201907018807")]
    [InlineData("youtube", "111299001912")]
    [InlineData("Prime Video", "3201910019365")]
    [InlineData("amazon prime", "3201910019365")]
    [InlineData("Disney plus", "3201901017640")]
    [InlineData("Disney+", "3201901017640")]
    [InlineData("plex", "3201512006963")]
    [InlineData("TVNZ+", null)]
    public void Samsung_app_ids(string spoken, string? id) => Assert.Equal(id, TvKeys.SamsungAppId(spoken));

    // ---------------------------------------------------------------- Samsung

    /// <summary>The shape a real Samsung TU7000 (2020) returns from GET :8001/api/v2/ (probed read-only on the LAN).</summary>
    const string SamsungInfo = """
        {"device":{"FrameTVSupport":"false","GamePadSupport":"true","ImeSyncedSupport":"true","Language":"en_GB","OS":"Tizen","PowerState":"on",
        "TokenAuthSupport":"true","VoiceSupport":"false","countryCode":"NZ","description":"Samsung DTV RCR","id":"uuid:f526954f-9cca-4839-9640-8d3529ec12d2",
        "ip":"192.168.1.18","model":"20_KANTSU2_UHD_BASIC","modelName":"UA43TU7000SXNZ","name":"[TV] Downstairs Samsung","networkType":"wireless",
        "resolution":"3840x2160","type":"Samsung SmartTV","wifiMac":"BC:7E:8B:1E:18:C4"},
        "id":"uuid:f526954f-9cca-4839-9640-8d3529ec12d2","name":"[TV] Downstairs Samsung","remote":"1.0","type":"Samsung SmartTV","uri":"http://192.168.1.18:8001/api/v2/","version":"2.0.25"}
        """;

    [Fact]
    public void Samsung_power_state()
    {
        Assert.Equal(TvPower.On, SamsungDriver.ParsePowerState(SamsungInfo));
        Assert.Equal(TvPower.Standby, SamsungDriver.ParsePowerState(SamsungInfo.Replace("\"PowerState\":\"on\"", "\"PowerState\":\"standby\"")));
        Assert.Equal(TvPower.On, SamsungDriver.ParsePowerState("""{"device":{"name":"old model"}}"""));
        Assert.Equal("Downstairs Samsung", TvDiscovery.CleanName("[TV] Downstairs Samsung"));
    }

    [Fact]
    public void Samsung_remote_url_and_key_message()
    {
        Assert.Equal("wss://192.168.1.18:8002/api/v2/channels/samsung.remote.control?name=UHVja3NsZXk%3D&token=12345678",
            SamsungRemote.Url("192.168.1.18", "12345678").AbsoluteUri);
        Assert.Equal("wss://192.168.1.18:8002/api/v2/channels/samsung.remote.control?name=UHVja3NsZXk%3D",
            SamsungRemote.Url("192.168.1.18", null).AbsoluteUri);
        Assert.Equal("""{"method":"ms.remote.control","params":{"Cmd":"Click","DataOfCmd":"KEY_VOLUP","Option":"false","TypeOfRemote":"SendRemoteKey"}}""",
            SamsungRemote.KeyMessage("KEY_VOLUP").ToJsonString());
    }

    [Fact]
    public async Task Samsung_has_no_absolute_volume_and_says_so()
    {
        var driver = new SamsungDriver(new TvDevice { Name = "Downstairs", Type = "samsung", Host = "192.0.2.1" }, new PairingStore(TempDir()), new HttpClient());
        var error = await Assert.ThrowsAsync<PluginException>(() => driver.VolumeAsync(20, null, null, CancellationToken.None));
        Assert.Contains("only stepped", error.Message);
        Assert.True(driver.NeedsPairing);
    }

    [Fact]
    public async Task Unpaired_samsung_asks_for_pairing_instead_of_prompting()
    {
        var driver = new SamsungDriver(new TvDevice { Name = "Downstairs", Type = "samsung", Host = "192.0.2.1" }, new PairingStore(TempDir()), new HttpClient());
        var error = await Assert.ThrowsAsync<PluginException>(() => driver.ChannelAsync(true, CancellationToken.None));
        Assert.Contains("isn't paired", error.Message);
    }

    // ---------------------------------------------------------------- LG

    [Fact]
    public void Lg_registration_carries_the_signed_manifest_and_key()
    {
        var withKey = LgManifest.Registration("abc123");
        Assert.Equal("abc123", (string)withKey["client-key"]!);
        Assert.Equal("PROMPT", (string)withKey["pairingType"]!);
        var manifest = withKey["manifest"]!;
        Assert.StartsWith("eyJhbGdvcml0aG0iOiJSU0EtU0hBMjU2Iiwia2V5SWQiOiJ0ZXN0LXNpZ25pbmctY2VydCIsInNpZ25hdHVyZVZlcnNpb24iOjF9.hrVRgjCwXVvE2OOSpDZ58hR+59aF",
            (string)manifest["signatures"]![0]!["signature"]!);
        Assert.EndsWith("aCViZNCP8iC9aO0q9fQojoa7NQnAtw==", (string)manifest["signatures"]![0]!["signature"]!);
        Assert.Equal(52, manifest["permissions"]!.AsArray().Count);
        Assert.Equal(16, manifest["signed"]!["permissions"]!.AsArray().Count);
        Assert.Equal("com.lge.test", (string)manifest["signed"]!["appId"]!);
        Assert.Null(LgManifest.Registration(null)["client-key"]);
    }

    [Fact]
    public void Lg_volume_both_formats()
    {
        Assert.Equal((12, false), LgDriver.ParseVolume(JsonNode.Parse("""{"returnValue":true,"scenario":"mastervolume_tv_speaker","volume":12,"muted":false}""")!.AsObject()));
        Assert.Equal((30, true), LgDriver.ParseVolume(JsonNode.Parse("""{"returnValue":true,"volumeStatus":{"volume":30,"muteStatus":true,"soundOutput":"tv_speaker"}}""")!.AsObject()));
    }

    [Fact]
    public void Lg_power_states()
    {
        Assert.Equal(TvPower.On, LgDriver.ParsePowerState("Active"));
        Assert.Equal(TvPower.Standby, LgDriver.ParsePowerState("Screen Off"));
        Assert.Equal(TvPower.Off, LgDriver.ParsePowerState("Suspend"));
    }

    [Fact]
    public void Lg_inputs_match_by_number_or_name()
    {
        TvItem[] inputs = [new("HDMI_1", "Xbox"), new("HDMI_2", "HDMI 2"), new("HDMI_3", "PlayStation")];
        Assert.Equal("HDMI_2", LgDriver.MatchInput("HDMI 2", inputs)!.Id);
        Assert.Equal("HDMI_1", LgDriver.MatchInput("hdmi one", inputs)!.Id);
        Assert.Equal("HDMI_1", LgDriver.MatchInput("the xbox", inputs)!.Id);
        Assert.Equal("HDMI_3", LgDriver.MatchInput("playstation", inputs)!.Id);
        Assert.Null(LgDriver.MatchInput("chromecast", inputs));
    }

    [Fact]
    public async Task Unpaired_lg_asks_for_pairing()
    {
        var driver = new LgDriver(new TvDevice { Name = "Lounge", Type = "lg", Host = "192.0.2.1" }, new PairingStore(TempDir()));
        var error = await Assert.ThrowsAsync<PluginException>(() => driver.ChannelAsync(true, CancellationToken.None));
        Assert.Contains("isn't paired", error.Message);
        var noMac = await Assert.ThrowsAsync<PluginException>(() => driver.PowerAsync(true, CancellationToken.None));
        Assert.Contains("MAC address", noMac.Message);
    }

    [Fact]
    public void Pairing_keys_persist()
    {
        var dir = TempDir();
        var store = new PairingStore(dir);
        store.Set("lg", "192.168.1.60", "client-key-1");
        store.Set("samsung", "192.168.1.18", "12345678");
        var reloaded = new PairingStore(dir);
        Assert.Equal("client-key-1", reloaded.Get("lg", "192.168.1.60"));
        Assert.Equal("12345678", reloaded.Get("SAMSUNG", "192.168.1.18"));
        Assert.Null(reloaded.Get("lg", "192.168.1.18"));
        reloaded.Remove("lg", "192.168.1.60");
        Assert.Null(new PairingStore(dir).Get("lg", "192.168.1.60"));
    }

    // ---------------------------------------------------------------- Roku over ECP (fake)

    const string RokuInfo = """
        <?xml version="1.0" encoding="UTF-8" ?>
        <device-info><udn>015e5108-9000-1046-8035-b0a737964dfb</udn><model-name>TCL 55R635</model-name>
        <user-device-name>Bedroom Roku</user-device-name><is-tv>true</is-tv><power-mode>PowerOn</power-mode></device-info>
        """;

    const string RokuApps = """
        <?xml version="1.0" encoding="UTF-8" ?>
        <apps><app id="tvinput.hdmi1" type="tvin" version="1.0.0">HDMI 1</app><app id="12" type="appl" version="5.1.0">Netflix</app>
        <app id="837" type="appl" version="2.21.9">YouTube</app><app id="291097" type="appl" version="1.32.2">Disney Plus</app></apps>
        """;

    [Fact]
    public void Parses_roku_xml()
    {
        Assert.Equal(TvPower.On, RokuDriver.ParsePowerMode(RokuInfo));
        Assert.Equal(TvPower.Standby, RokuDriver.ParsePowerMode(RokuInfo.Replace("PowerOn", "DisplayOff")));
        Assert.Equal("Bedroom Roku", RokuDriver.DeviceName(RokuInfo));
        var apps = RokuDriver.ParseApps(RokuApps);
        Assert.Equal(4, apps.Count);
        Assert.Equal(new TvItem("12", "Netflix"), apps[1]);
    }

    static MockHttp FakeRoku(string power = "PowerOn")
    {
        var http = new MockHttp();
        http.On(HttpMethod.Get, "/query/device-info", RokuInfo.Replace("PowerOn", power), contentType: "text/xml");
        http.On(HttpMethod.Get, "/query/apps", RokuApps, contentType: "text/xml");
        http.On(HttpMethod.Post, "/launch/12", "");
        foreach (var key in new[] { "VolumeUp", "VolumeDown", "VolumeMute", "PowerOff", "PowerOn", "InputHDMI1", "InputTuner", "Play", "ChannelUp" })
            http.On(HttpMethod.Post, "/keypress/" + key, "");
        return http;
    }

    static Task<(TvPlugin Plugin, FakePluginContext Context)> StartRoku(MockHttp http) =>
        TestPlugins.StartAsync<TvPlugin>(new TvSettings { Tvs = [new TvDevice { Name = "Bedroom", Type = "roku", Host = "192.168.1.90" }] }, http.Client());

    [Fact]
    public async Task Roku_steps_volume_launches_apps_and_switches_inputs()
    {
        var http = FakeRoku();
        var (plugin, _) = await StartRoku(http);
        Assert.Equal((StatusLevel.Ok, "1 TV ready"), (plugin.Status.Level, plugin.Status.Message));
        var bedroom = new ToolCaller("puck-1", "Bedroom puck", "Bedroom");

        var volume = await FakePluginContext.CallToolAsync(plugin, "tv_volume", """{"change":2}""", bedroom);
        Assert.Equal("Bedroom TV: volume up 2 steps (Roku only steps the volume).", volume.Text);
        Assert.Equal(2, http.To(HttpMethod.Post, "/keypress/VolumeUp").Count);
        Assert.Equal("http://192.168.1.90:8060/keypress/VolumeUp", http.To(HttpMethod.Post, "/keypress/VolumeUp")[0].Uri.ToString());

        var app = await FakePluginContext.CallToolAsync(plugin, "tv_app", """{"app":"netflix"}""", bedroom);
        Assert.Equal("Opening Netflix on the Bedroom TV.", app.Text);
        Assert.Single(http.To(HttpMethod.Post, "/launch/12"));

        var input = await FakePluginContext.CallToolAsync(plugin, "tv_input", """{"input":"HDMI 1"}""", bedroom);
        Assert.Equal("Switched the Bedroom TV to HDMI 1.", input.Text);

        var absolute = await FakePluginContext.CallToolAsync(plugin, "tv_volume", """{"volume":20}""", bedroom);
        Assert.True(absolute.IsError);
        Assert.Contains("only stepped", absolute.Text);
        await plugin.StopAsync();
    }

    [Fact]
    public async Task Roku_power_checks_state_first()
    {
        var http = FakeRoku();
        var (plugin, _) = await StartRoku(http);

        var off = await FakePluginContext.CallToolAsync(plugin, "tv_power", """{"on":false}""");
        Assert.Equal("Turned off the Bedroom TV.", off.Text);
        Assert.Single(http.To(HttpMethod.Post, "/keypress/PowerOff"));

        var on = await FakePluginContext.CallToolAsync(plugin, "tv_power", """{"on":true}""");
        Assert.Equal("The Bedroom TV is already on.", on.Text);
        Assert.Empty(http.To(HttpMethod.Post, "/keypress/PowerOn"));

        var list = JsonNode.Parse((await FakePluginContext.CallToolAsync(plugin, "tv_list")).Text)!.AsArray();
        Assert.Equal(("Bedroom", "roku", "on"), ((string)list[0]!["name"]!, (string)list[0]!["type"]!, (string)list[0]!["power"]!));
        await plugin.StopAsync();
    }

    [Fact]
    public async Task Roku_in_standby_wakes_with_power_on()
    {
        var http = FakeRoku(power: "DisplayOff");
        var (plugin, _) = await StartRoku(http);
        var on = await FakePluginContext.CallToolAsync(plugin, "tv_power", """{"on":true}""");
        Assert.Equal("Turning on the Bedroom TV.", on.Text);
        Assert.Single(http.To(HttpMethod.Post, "/keypress/PowerOn"));
        await plugin.StopAsync();
    }

    [Fact]
    public async Task Unpaired_tvs_show_in_the_status_and_messages_are_lg_only()
    {
        var (plugin, _) = await TestPlugins.StartAsync<TvPlugin>(new TvSettings
        {
            Tvs = [new TvDevice { Name = "Lounge", Type = "lg", Host = "192.0.2.10" }, new TvDevice { Name = "Bedroom", Type = "roku", Host = "192.0.2.11" }],
        });
        Assert.Equal((StatusLevel.Warning, "Lounge needs pairing: click Pair"), (plugin.Status.Level, plugin.Status.Message));

        var roku = await FakePluginContext.CallToolAsync(plugin, "tv_message", """{"text":"Dinner!","tv":"bedroom"}""");
        Assert.True(roku.IsError);
        Assert.Contains("Only LG", roku.Text);
        Assert.Equal(["pair", "discover"], plugin.GetActions().Select(a => a.Id));
        await plugin.StopAsync();
    }

    static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pucksley-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        return dir;
    }
}
