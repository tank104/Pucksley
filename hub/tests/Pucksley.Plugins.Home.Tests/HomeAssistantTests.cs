using System.Net;
using System.Text.Json.Nodes;
using Pucksley.Plugins.HomeAssistant;
using Pucksley.Sdk;
using Pucksley.TestSupport;

namespace Pucksley.Plugins.Home.Tests;

public class HomeAssistantTests
{
    const string StatesJson = """
        [
          {"entity_id":"light.kitchen_ceiling","state":"on","attributes":{"friendly_name":"Kitchen Ceiling","brightness":153}},
          {"entity_id":"light.kitchen_pendants","state":"off","attributes":{"friendly_name":"Kitchen Pendants"}},
          {"entity_id":"light.lounge_lamp","state":"off","attributes":{"friendly_name":"Lounge Lamp"}},
          {"entity_id":"light.lamp","state":"off","attributes":{"friendly_name":"Lamp"}},
          {"entity_id":"media_player.kitchen","state":"playing","attributes":{"friendly_name":"Kitchen","volume_level":0.25,"media_title":"Teardrop","media_artist":"Massive Attack"}},
          {"entity_id":"switch.coffee_machine","state":"off","attributes":{"friendly_name":"Coffee Machine"}},
          {"entity_id":"climate.hallway","state":"heat","attributes":{"friendly_name":"Hallway Heat Pump","temperature":21,"current_temperature":19.5,"min_temp":16,"max_temp":30,"target_temp_step":0.5,"hvac_modes":["off","heat","cool","auto"],"hvac_action":"heating"}},
          {"entity_id":"cover.garage_door","state":"closed","attributes":{"friendly_name":"Garage Door","device_class":"garage"}},
          {"entity_id":"cover.lounge_blind","state":"open","attributes":{"friendly_name":"Lounge Blind","current_position":40}},
          {"entity_id":"scene.movie_night","state":"2026-11-01T10:00:00+00:00","attributes":{"friendly_name":"Movie Night"}},
          {"entity_id":"script.goodnight","state":"off","attributes":{"friendly_name":"Goodnight"}},
          {"entity_id":"sensor.outside_temperature","state":"12.3456","attributes":{"friendly_name":"Outside Temperature","unit_of_measurement":"°C"}},
          {"entity_id":"lock.front_door","state":"locked","attributes":{"friendly_name":"Front Door"}},
          {"entity_id":"binary_sensor.back_door","state":"on","attributes":{"friendly_name":"Back Door","device_class":"door"}}
        ]
        """;

    const string AreasText = """
        light.kitchen_ceiling|Kitchen
        light.kitchen_pendants|Kitchen
        light.lounge_lamp|Lounge
        light.lamp|Bedroom
        media_player.kitchen|Kitchen
        switch.coffee_machine|Kitchen
        climate.hallway|Hallway
        cover.garage_door|Garage
        cover.lounge_blind|Lounge
        scene.movie_night|
        sensor.outside_temperature|
        lock.front_door|Hallway

        """;

    static List<HaEntity> Entities()
    {
        var areas = HaClient.ParseAreas(AreasText);
        return JsonNode.Parse(StatesJson)!.AsArray().Select(n => HaEntity.FromJson(n)!).Select(e => e with { Area = areas.GetValueOrDefault(e.EntityId) }).ToList();
    }

    static IReadOnlyList<string> Resolve(string spoken, Want want = Want.Many, string? room = null, IReadOnlyCollection<HaEntity>? pool = null) =>
        EntityResolver.Resolve(spoken, pool ?? Entities(), want, room).Select(e => e.EntityId).ToList();

    // ---------------------------------------------------------------- matching

    [Theory]
    [InlineData("light.kitchen_ceiling", "light.kitchen_ceiling")]
    [InlineData("LIGHT.Kitchen_Ceiling", "light.kitchen_ceiling")]
    [InlineData("kitchen ceiling", "light.kitchen_ceiling")]
    [InlineData("the kitchen ceiling light", "light.kitchen_ceiling")]
    [InlineData("the lamp in the lounge", "light.lounge_lamp")]
    [InlineData("lamp", "light.lamp")]
    [InlineData("kitchen", "media_player.kitchen")]
    [InlineData("garage", "cover.garage_door")]
    [InlineData("heat pump", "climate.hallway")]
    [InlineData("coffee machine", "switch.coffee_machine")]
    [InlineData("coffee", "switch.coffee_machine")]
    [InlineData("outside temp", "sensor.outside_temperature")]
    public void Finds_one_entity(string spoken, string expected) => Assert.Equal([expected], Resolve(spoken));

    [Theory]
    [InlineData("kitchen lights")]
    [InlineData("all lights in the kitchen")]
    [InlineData("all the kitchen lights")]
    [InlineData("the lights in the kitchen")]
    public void Area_and_plural_means_all_of_them(string spoken) =>
        Assert.Equal(["light.kitchen_ceiling", "light.kitchen_pendants"], Resolve(spoken).Order());

    [Fact]
    public void All_lights_means_the_whole_house() => Assert.Equal(4, Resolve("all the lights").Count);

    [Fact]
    public void The_lights_means_the_pucks_room()
    {
        Assert.Equal(["light.lounge_lamp"], Resolve("the lights", room: "Lounge"));
        var error = Assert.Throws<PluginException>(() => Resolve("the lights"));
        Assert.Contains("Which room?", error.Message);
        Assert.Contains("Kitchen", error.Message);
    }

    [Fact]
    public void Everything_in_a_room_covers_lights_switches_and_players() =>
        Assert.Equal(["light.kitchen_ceiling", "light.kitchen_pendants", "media_player.kitchen", "switch.coffee_machine"],
            Resolve("everything in the kitchen").Order());

    [Fact]
    public void Singular_with_several_candidates_lists_them()
    {
        var error = Assert.Throws<PluginException>(() => Resolve("kitchen light"));
        Assert.Contains("light.kitchen_ceiling", error.Message);
        Assert.Contains("light.kitchen_pendants", error.Message);
        Assert.DoesNotContain("media_player.kitchen", error.Message); // "light" says what kind of thing
    }

    [Fact]
    public void The_pucks_room_breaks_ties()
    {
        var pool = Entities();
        pool.Add(new HaEntity { EntityId = "light.office_lamp", State = "off", Attributes = new JsonObject { ["friendly_name"] = "Lamp" }, Area = "Office" });
        Assert.Throws<PluginException>(() => Resolve("lamp", pool: pool));
        Assert.Equal(["light.office_lamp"], Resolve("lamp", room: "Office", pool: pool));
        Assert.Equal(["light.lamp"], Resolve("lamp", room: "Bedroom", pool: pool));
    }

    [Fact]
    public void Lists_of_ids_are_accepted() =>
        Assert.Equal(["light.lamp", "light.lounge_lamp"], Resolve("light.lamp, light.lounge_lamp"));

    [Fact]
    public void A_short_name_inside_a_longer_request_doesnt_win()
    {
        // "Kitchen" (the speaker) is inside "kitchen pendant", but the pendants are what's meant
        Assert.Equal(["light.kitchen_pendants"], Resolve("kitchen pendant", Want.One));
        // filler words are fine ("the kitchen pendant lamps thingy" still means the pendants)...
        Assert.Equal(["light.kitchen_pendants"], Resolve("kitchen pendant lamps thingy", Want.One));
        // ...but a device that isn't there doesn't fall back to the Kitchen speaker
        var error = Assert.Throws<PluginException>(() => Resolve("kitchen chandelier thingy", Want.One));
        Assert.Contains("Closest:", error.Message);
    }

    [Fact]
    public void A_miss_says_so_and_points_to_search()
    {
        var error = Assert.Throws<PluginException>(() => Resolve("zzzz qqqq", Want.One));
        Assert.StartsWith("I can't find 'zzzz qqqq'", error.Message);
        Assert.Contains("ha_find", error.Message);
        Assert.Equal(["light.kitchen_ceiling", "light.kitchen_pendants"],
            EntityResolver.Rank("kitchen", Entities().Where(e => e.Domain == "light")).Select(e => e.EntityId).Order());
    }

    [Fact]
    public void Unknown_area_for_a_group_lists_areas()
    {
        var error = Assert.Throws<PluginException>(() => Resolve("all lights in the basement"));
        Assert.Contains("basement", error.Message);
        Assert.Contains("Kitchen", error.Message);
    }

    // ---------------------------------------------------------------- services

    static HaEntity E(string id, string state = "off", string? deviceClass = null) => new()
    {
        EntityId = id, State = state, Attributes = deviceClass is null ? new JsonObject() : new JsonObject { ["device_class"] = deviceClass },
    };

    [Theory]
    [InlineData("light.a", "off", "On", "light", "turn_on")]
    [InlineData("light.a", "on", "Toggle", "light", "toggle")]
    [InlineData("switch.a", "on", "Off", "switch", "turn_off")]
    [InlineData("fan.a", "off", "On", "fan", "turn_on")]
    [InlineData("input_boolean.guest_mode", "off", "On", "input_boolean", "turn_on")]
    [InlineData("media_player.tv", "on", "Off", "media_player", "turn_off")]
    [InlineData("climate.hall", "heat", "On", "climate", "turn_on")]
    [InlineData("climate.hall", "heat", "Toggle", "climate", "turn_off")]
    [InlineData("climate.hall", "off", "Toggle", "climate", "turn_on")]
    [InlineData("cover.blind", "closed", "On", "cover", "open_cover")]
    [InlineData("cover.blind", "open", "Off", "cover", "close_cover")]
    [InlineData("cover.blind", "open", "Toggle", "cover", "toggle")]
    [InlineData("scene.movie", "x", "On", "scene", "turn_on")]
    [InlineData("script.goodnight", "off", "On", "script", "turn_on")]
    [InlineData("button.doorbell", "x", "On", "button", "press")]
    [InlineData("vacuum.robot", "docked", "On", "vacuum", "start")]
    [InlineData("vacuum.robot", "cleaning", "Toggle", "vacuum", "return_to_base")]
    [InlineData("group.downstairs", "on", "Off", "homeassistant", "turn_off")]
    [InlineData("input_select.mode", "a", "On", "homeassistant", "turn_on")]
    public void Picks_the_service_for_the_domain(string entityId, string state, string action, string domain, string service) =>
        Assert.Equal((domain, service), HaServices.For(E(entityId, state), Enum.Parse<OnOff>(action)));

    [Theory]
    [InlineData("lock.front_door")]
    [InlineData("sensor.temperature")]
    [InlineData("alarm_control_panel.home")]
    public void Refuses_things_that_cant_be_switched(string entityId) =>
        Assert.Throws<PluginException>(() => HaServices.For(E(entityId), OnOff.On));

    [Fact]
    public void Scenes_cant_be_turned_off() => Assert.Throws<PluginException>(() => HaServices.For(E("scene.movie"), OnOff.Off));

    [Fact]
    public void Reads_colours()
    {
        Assert.Equal(2700, (int)HaServices.LightData(null, "warm white", null)["color_temp_kelvin"]!);
        Assert.Equal(6500, (int)HaServices.LightData(null, "daylight", null)["color_temp_kelvin"]!);
        Assert.Equal("[255,136,0]", HaServices.LightData(null, "#ff8800", null)["rgb_color"]!.ToJsonString());
        Assert.Equal("[10,20,30]", HaServices.LightData(null, "10, 20, 30", null)["rgb_color"]!.ToJsonString());
        Assert.Equal("lightblue", (string)HaServices.LightData(null, "Light Blue", null)["color_name"]!);
        var data = HaServices.LightData(150, null, 1000);
        Assert.Equal(100, (int)data["brightness_pct"]!);
        Assert.Equal(1500, (int)data["color_temp_kelvin"]!);
    }

    [Fact]
    public void Maps_spoken_hvac_modes()
    {
        Assert.Equal("heat", HaServices.HvacMode("heating", ["off", "heat"]));
        Assert.Equal("fan_only", HaServices.HvacMode("fan", []));
        Assert.Equal("heat_cool", HaServices.HvacMode("heat and cool", []));
        var error = Assert.Throws<PluginException>(() => HaServices.HvacMode("cooling", ["off", "heat"]));
        Assert.Contains("off, heat", error.Message);
    }

    [Fact]
    public void Unlocking_and_opening_the_garage_need_confirmation()
    {
        Assert.True(HaServices.NeedsConfirmation("lock", "unlock", []));
        Assert.False(HaServices.NeedsConfirmation("lock", "lock", []));
        Assert.True(HaServices.NeedsConfirmation("cover", "open_cover", [E("cover.garage_door", "closed", "garage")]));
        Assert.False(HaServices.NeedsConfirmation("cover", "open_cover", [E("cover.blind")]));
        Assert.False(HaServices.NeedsConfirmation("cover", "close_cover", [E("cover.garage_door", "open", "garage")]));
    }

    [Fact]
    public void Describes_states_for_speech()
    {
        var all = Entities().ToDictionary(e => e.EntityId);
        Assert.Equal("on, 60%", HaFormat.Describe(all["light.kitchen_ceiling"]));
        Assert.Equal("heat, set 21 °C, now 19.5 °C, heating", HaFormat.Describe(all["climate.hallway"]));
        Assert.Equal("playing, 'Teardrop' by Massive Attack, volume 25%", HaFormat.Describe(all["media_player.kitchen"]));
        Assert.Equal("12.35 °C", HaFormat.Describe(all["sensor.outside_temperature"]));
        Assert.Equal("open", HaFormat.Describe(all["binary_sensor.back_door"]));
        Assert.Equal("open, 40% open", HaFormat.Describe(all["cover.lounge_blind"]));
    }

    [Fact]
    public void Builds_controls_by_domain()
    {
        var all = Entities().ToDictionary(e => e.EntityId);
        var light = HaControls.Build(all["light.kitchen_ceiling"])!;
        Assert.Equal((ControlKind.Number, 60d, "%", "Ceiling", "Kitchen"), (light.Kind, light.Value, light.Unit, light.Name, light.Group));
        Assert.Equal("Off", HaControls.Build(all["light.kitchen_pendants"])!.Text);
        var sw = HaControls.Build(all["switch.coffee_machine"])!;
        Assert.Equal((ControlKind.Toggle, false), (sw.Kind, sw.On));
        var climate = HaControls.Build(all["climate.hallway"])!;
        Assert.Equal((21d, 16d, 30d, 0.5, "°C"), (climate.Value, climate.Min, climate.Max, climate.Step, climate.Unit));
        Assert.Equal(40d, HaControls.Build(all["cover.lounge_blind"])!.Value);
        Assert.Equal(ControlKind.Action, HaControls.Build(all["scene.movie_night"])!.Kind);
        Assert.Null(HaControls.Build(all["sensor.outside_temperature"]));
    }

    // ---------------------------------------------------------------- the plugin against a fake Home Assistant

    static MockHttp FakeHomeAssistant()
    {
        var http = new MockHttp();
        http.On(HttpMethod.Get, "/api/", """{"message":"API running."}""");
        http.On(HttpMethod.Get, "/api/states", StatesJson);
        http.On(HttpMethod.Post, "/api/template", AreasText, contentType: "text/plain");
        http.On(HttpMethod.Get, "/api/states/sensor.outside_temperature",
            """{"entity_id":"sensor.outside_temperature","state":"13.1","attributes":{"friendly_name":"Outside Temperature","unit_of_measurement":"°C"}}""");
        foreach (var path in new[] { "/api/services/light/turn_on", "/api/services/light/turn_off", "/api/services/switch/turn_on", "/api/services/media_player/turn_on",
                     "/api/services/cover/open_cover", "/api/services/cover/close_cover", "/api/services/climate/set_temperature", "/api/services/scene/turn_on" })
            http.On(HttpMethod.Post, path, "[]");
        http.On(HttpMethod.Post, "/api/services/switch/toggle",
            """[{"entity_id":"switch.coffee_machine","state":"on","attributes":{"friendly_name":"Coffee Machine"}}]""");
        http.On(HttpMethod.Post, "/api/conversation/process",
            """{"response":{"speech":{"plain":{"speech":"The bathroom humidity is 62%.","extra_data":null}},"response_type":"query_answer","language":"en"}}""");
        return http;
    }

    static Task<(HomeAssistantPlugin Plugin, FakePluginContext Context)> StartAsync(MockHttp http, Action<HomeAssistantSettings>? configure = null)
    {
        var settings = new HomeAssistantSettings { Url = "http://ha.test:8123/", Token = "secret-token" };
        configure?.Invoke(settings);
        return TestPlugins.StartAsync<HomeAssistantPlugin>(settings, http.Client());
    }

    [Fact]
    public async Task Starts_by_checking_the_token_and_loading_states_and_areas()
    {
        var http = FakeHomeAssistant();
        var (plugin, _) = await StartAsync(http);

        Assert.Equal(StatusLevel.Ok, plugin.Status.Level);
        Assert.Equal("Connected: 14 entities, 5 areas", plugin.Status.Message);
        Assert.Equal("Bearer secret-token", http.To(HttpMethod.Get, "/api/").Single().Headers["Authorization"]);
        Assert.Contains("area_name(s.entity_id)", http.To(HttpMethod.Post, "/api/template").Single().Text);
        await plugin.StopAsync();
    }

    [Fact]
    public async Task Rejected_token_shows_an_error()
    {
        var http = new MockHttp().On(HttpMethod.Get, "/api/", "401: Unauthorized", HttpStatusCode.Unauthorized, "text/plain");
        var (plugin, _) = await StartAsync(http);
        Assert.Equal((StatusLevel.Error, "Token rejected"), (plugin.Status.Level, plugin.Status.Message));
        var result = await FakePluginContext.CallToolAsync(plugin, "ha_turn_on", """{"entity":"lamp"}""");
        Assert.True(result.IsError);
        Assert.Contains("rejected the access token", result.Text);
        await plugin.StopAsync();
    }

    [Fact]
    public async Task Missing_settings_ask_for_them()
    {
        var (plugin, _) = await TestPlugins.StartAsync<HomeAssistantPlugin>(new HomeAssistantSettings());
        Assert.Equal(StatusLevel.Warning, plugin.Status.Level);
    }

    [Fact]
    public async Task Turns_on_all_kitchen_lights_with_brightness_in_one_call()
    {
        var http = FakeHomeAssistant();
        var (plugin, _) = await StartAsync(http);

        var result = await FakePluginContext.CallToolAsync(plugin, "ha_turn_on", """{"entity":"kitchen lights","brightness_pct":40}""");

        Assert.False(result.IsError, result.Text);
        Assert.Equal("Turned on Kitchen Ceiling and Kitchen Pendants at 40%.", result.Text);
        Assert.Equal("Kitchen lights 40%", result.Activity);
        var body = JsonNode.Parse(http.To(HttpMethod.Post, "/api/services/light/turn_on").Single().Text)!;
        Assert.Equal("""["light.kitchen_ceiling","light.kitchen_pendants"]""", body["entity_id"]!.ToJsonString());
        Assert.Equal(40, (int)body["brightness_pct"]!);
        await plugin.StopAsync();
    }

    [Fact]
    public async Task Splits_a_mixed_group_into_one_call_per_service()
    {
        var http = FakeHomeAssistant();
        var (plugin, _) = await StartAsync(http);

        var result = await FakePluginContext.CallToolAsync(plugin, "ha_turn_on", """{"entity":"everything in the kitchen","color":"red"}""");

        Assert.False(result.IsError, result.Text);
        var light = JsonNode.Parse(http.To(HttpMethod.Post, "/api/services/light/turn_on").Single().Text)!;
        Assert.Equal("red", (string)light["color_name"]!);
        var sw = JsonNode.Parse(http.To(HttpMethod.Post, "/api/services/switch/turn_on").Single().Text)!;
        Assert.Equal("switch.coffee_machine", (string)sw["entity_id"]!);
        Assert.Null(sw["color_name"]); // light data only goes to lights
        Assert.Single(http.To(HttpMethod.Post, "/api/services/media_player/turn_on"));
        await plugin.StopAsync();
    }

    [Fact]
    public async Task Toggle_reports_the_new_state()
    {
        var (plugin, _) = await StartAsync(FakeHomeAssistant());
        var result = await FakePluginContext.CallToolAsync(plugin, "ha_toggle", """{"entity":"coffee machine"}""");
        Assert.Equal("Coffee Machine is now on.", result.Text);
        await plugin.StopAsync();
    }

    [Fact]
    public async Task Brightness_zero_turns_the_light_off()
    {
        var http = FakeHomeAssistant();
        var (plugin, _) = await StartAsync(http);
        await FakePluginContext.CallToolAsync(plugin, "ha_turn_on", """{"entity":"lounge lamp","brightness_pct":0}""");
        Assert.Single(http.To(HttpMethod.Post, "/api/services/light/turn_off"));
        await plugin.StopAsync();
    }

    [Fact]
    public async Task Opening_the_garage_asks_first()
    {
        var http = FakeHomeAssistant();
        var (plugin, context) = await StartAsync(http);

        context.ConfirmAnswer = false;
        var refused = await FakePluginContext.CallToolAsync(plugin, "ha_cover", """{"entity":"garage door","action":"open"}""");
        Assert.Contains("wasn't confirmed", refused.Text);
        Assert.Empty(http.To(HttpMethod.Post, "/api/services/cover/open_cover"));

        context.ConfirmAnswer = true;
        var opened = await FakePluginContext.CallToolAsync(plugin, "ha_cover", """{"entity":"garage door","action":"open"}""");
        Assert.Equal("Opening Garage Door.", opened.Text);
        Assert.Equal(2, context.ConfirmQuestions.Count);

        // Closing doesn't ask
        await FakePluginContext.CallToolAsync(plugin, "ha_cover", """{"entity":"garage door","action":"close"}""");
        Assert.Equal(2, context.ConfirmQuestions.Count);
        await plugin.StopAsync();
    }

    [Fact]
    public async Task Climate_defaults_to_the_only_thermostat_and_checks_its_range()
    {
        var http = FakeHomeAssistant();
        var (plugin, _) = await StartAsync(http);

        var ok = await FakePluginContext.CallToolAsync(plugin, "ha_set_climate", """{"temperature":22.5,"hvac_mode":"heating"}""");
        Assert.Equal("Hallway Heat Pump set to heat and 22.5 °C.", ok.Text);
        var body = JsonNode.Parse(http.To(HttpMethod.Post, "/api/services/climate/set_temperature").Single().Text)!;
        Assert.Equal((22.5, "heat"), ((double)body["temperature"]!, (string)body["hvac_mode"]!));

        var tooHot = await FakePluginContext.CallToolAsync(plugin, "ha_set_climate", """{"temperature":35}""");
        Assert.True(tooHot.IsError);
        Assert.Contains("16–30", tooHot.Text);
        await plugin.StopAsync();
    }

    [Fact]
    public async Task State_reads_fresh_from_home_assistant()
    {
        var (plugin, _) = await StartAsync(FakeHomeAssistant());
        var result = await FakePluginContext.CallToolAsync(plugin, "ha_state", """{"entity":"outside temperature"}""");
        Assert.Equal("Outside Temperature (sensor.outside_temperature): 13.1 °C.", result.Text);
        await plugin.StopAsync();
    }

    [Fact]
    public async Task Find_returns_compact_matches()
    {
        var (plugin, _) = await StartAsync(FakeHomeAssistant());
        var result = await FakePluginContext.CallToolAsync(plugin, "ha_find", """{"query":"kitchen","domain":"light"}""");
        var list = JsonNode.Parse(result.Text)!.AsArray();
        Assert.Equal(2, list.Count);
        Assert.All(list, item => Assert.Equal("Kitchen", (string)item!["area"]!));
        await plugin.StopAsync();
    }

    [Fact]
    public async Task Scene_and_assist()
    {
        var http = FakeHomeAssistant();
        var (plugin, _) = await StartAsync(http, s => s.Language = "en");

        var scene = await FakePluginContext.CallToolAsync(plugin, "ha_scene", """{"name":"movie night"}""");
        Assert.Equal("Scene Movie Night is on.", scene.Text);

        var assist = await FakePluginContext.CallToolAsync(plugin, "ha_assist", """{"text":"what's the humidity in the bathroom"}""");
        Assert.Equal("Home Assistant: The bathroom humidity is 62%.", assist.Text);
        var body = JsonNode.Parse(http.To(HttpMethod.Post, "/api/conversation/process").Single().Text)!;
        Assert.Equal(("what's the humidity in the bathroom", "en"), ((string)body["text"]!, (string)body["language"]!));
        await plugin.StopAsync();
    }

    [Fact]
    public async Task Context_and_controls_come_from_the_cache()
    {
        var (plugin, _) = await StartAsync(FakeHomeAssistant(), s =>
        {
            s.ContextEntities = ["sensor.outside_temperature", "lock.front_door", "light.nonexistent"];
            s.ControlEntities = ["light.kitchen_ceiling", "switch.coffee_machine", "climate.hallway", "scene.movie_night", "sensor.outside_temperature"];
        });

        var context = await plugin.GetContextAsync(ToolCaller.Web, CancellationToken.None);
        Assert.Equal("Home Assistant: Outside Temperature: 12.35 °C; Front Door: locked.", context);

        var controls = plugin.GetControls();
        Assert.Equal(["light.kitchen_ceiling", "switch.coffee_machine", "climate.hallway", "scene.movie_night"], controls.Select(c => c.Id));
        await plugin.StopAsync();
    }

    [Fact]
    public async Task A_dial_turn_sends_the_settled_value()
    {
        var http = FakeHomeAssistant();
        var (plugin, _) = await StartAsync(http, s => s.ControlEntities = ["light.kitchen_ceiling"]);

        foreach (var v in new[] { 65d, 70, 75, 80 })
            await plugin.OnControlAsync("light.kitchen_ceiling", new ControlInput { Value = v }, CancellationToken.None);
        Assert.True(await TestPlugins.WaitUntilAsync(() => http.To(HttpMethod.Post, "/api/services/light/turn_on").Count > 0));
        await Task.Delay(500); // longer than the settle time: no second call should follow

        var sent = http.To(HttpMethod.Post, "/api/services/light/turn_on");
        Assert.Single(sent); // coalesced
        Assert.Equal(80, (int)JsonNode.Parse(sent[0].Text)!["brightness_pct"]!);
        await plugin.StopAsync();
    }
}
