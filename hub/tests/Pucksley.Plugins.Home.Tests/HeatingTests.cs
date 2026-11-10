using System.Net;
using System.Text.Json.Nodes;
using Pucksley.Plugins.Heating;
using Pucksley.Sdk;
using Pucksley.TestSupport;

namespace Pucksley.Plugins.Home.Tests;

public class HeatingTests
{
    /// <summary>The sample from the API's documentation.</summary>
    const string SampleState = """
        {"deviceId":"ufh-controller-001","lastReportedAt":"2026-05-26T07:30:00Z","systemMode":"heating","setpoint_c":21.0,"isStaleConfig":false,
         "floors":{"ground":{"reading_c":21.4,"setpoint_effective_c":21.0,"call":"none","mode":"heating","in_safe_state":false,"active_faults":[]},
                   "middle":{"reading_c":20.8,"setpoint_effective_c":21.0,"call":"heat","mode":"heating","in_safe_state":false,"active_faults":[]},
                   "top":{"reading_c":20.0,"setpoint_effective_c":20.0,"call":"none","mode":"heating","in_safe_state":false,"active_faults":[]}},
         "groups":{"ground":{"state":"closed","since_ms":1716708120000,"offset_c":0.0,"active_override":null},
                   "middle_sun":{"state":"open","since_ms":1716708180000,"offset_c":0.5,"active_override":null},
                   "middle_shade":{"state":"open","offset_c":0.0,"active_override":null},
                   "top_sun":{"state":"closed","offset_c":0.5,"active_override":{"kind":"force_closed","expires_at":"2026-05-26T09:30:00Z"}},
                   "top_shade":{"state":"closed","offset_c":0.0,"active_override":null}},
         "heat_pump":{"link_health":"alive","current_mode":"heating","operating_mode":"comfort","current_flow_temp_c":36.2,"hc1_setpoint_c":21.0,"fault_bitmap":0,"dewpoint_alarm":false,"summer_mode":false},
         "active_faults":[]}
        """;

    static readonly TimeZoneInfo Nz = TimeZoneInfo.CreateCustomTimeZone("NZST", TimeSpan.FromHours(12), "NZST", "NZST");
    static readonly DateTimeOffset JustAfterReport = DateTimeOffset.Parse("2026-05-26T07:31:00Z");

    [Fact]
    public void Parses_the_documented_state()
    {
        var s = HeatingState.Parse(SampleState);
        Assert.Equal(("heating", 21.0, false), (s.SystemMode, s.SetpointC!.Value, s.IsStaleConfig));
        Assert.Equal(["ground", "middle", "top"], s.Floors.Select(f => f.Name));
        Assert.True(s.Floors[1].CallingForHeat);
        Assert.Equal(["ground", "middle_sun", "middle_shade", "top_sun", "top_shade"], s.Groups.Select(g => g.Name));
        Assert.Equal("force_closed", s.Groups[3].Override!.Kind);
        Assert.Equal(36.2, s.HeatPump!.FlowTempC);
        Assert.Empty(s.Faults);
    }

    [Fact]
    public void Context_line_matches_the_spec()
    {
        var s = HeatingState.Parse(SampleState);
        Assert.Equal("Heating: 21.0 °C set, heating; ground 21.4, middle 20.8 (heating), top 20.0.", HeatingSummary.Context(s, JustAfterReport));
    }

    [Fact]
    public void Context_mentions_faults_and_a_stale_controller()
    {
        var json = JsonNode.Parse(SampleState)!;
        json["active_faults"] = new JsonArray("top sensor timeout");
        var s = HeatingState.Parse(json.ToJsonString());
        var text = HeatingSummary.Context(s, JustAfterReport.AddHours(3));
        Assert.EndsWith("top 20.0. Faults: top sensor timeout. (Last report 3 hours ago.)", text);
    }

    [Fact]
    public void Status_is_a_spoken_summary()
    {
        var text = HeatingSummary.Status(HeatingState.Parse(SampleState), JustAfterReport, Nz);
        Assert.Equal(
            "Heating, set to 21 °C. Ground 21.4 °C; middle 20.8 °C, heating; top 20.0 °C (target 20). " +
            "Heat pump comfort mode, flow 36.2 °C. Override: upstairs sunny side forced closed until 9:30 pm. " +
            "Offsets: middle sunny side +0.5, upstairs sunny side +0.5. No faults.",
            text);
    }

    [Fact]
    public void Status_flags_problems()
    {
        var json = JsonNode.Parse(SampleState)!;
        json["heat_pump"]!["link_health"] = "lost";
        json["heat_pump"]!["dewpoint_alarm"] = true;
        json["floors"]!["top"]!["in_safe_state"] = true;
        json["active_faults"] = new JsonArray(new JsonObject { ["code"] = "E12", ["message"] = "flow sensor open circuit" });
        json["isStaleConfig"] = true;
        var text = HeatingSummary.Status(HeatingState.Parse(json.ToJsonString()), JustAfterReport.AddMinutes(40), Nz);
        Assert.Contains("top 20.0 °C (target 20), in safe mode", text);
        Assert.Contains("Heat pump link is lost.", text);
        Assert.Contains("Dew-point alarm!", text);
        Assert.Contains("Faults: flow sensor open circuit.", text);
        Assert.Contains("last reported 41 minutes ago", text);
        Assert.Contains("hasn't reached the controller", text);
    }

    [Theory]
    [InlineData("upstairs", "top_sun,top_shade")]
    [InlineData("top floor", "top_sun,top_shade")]
    [InlineData("upstairs sunny side", "top_sun")]
    [InlineData("the shady side upstairs", "top_shade")]
    [InlineData("downstairs", "ground")]
    [InlineData("ground floor", "ground")]
    [InlineData("downstairs sunny side", "ground")]
    [InlineData("middle", "middle_sun,middle_shade")]
    [InlineData("first floor", "middle_sun,middle_shade")]
    [InlineData("middle floor shade", "middle_shade")]
    [InlineData("second floor", "top_sun,top_shade")]
    [InlineData("sunny side", "middle_sun,top_sun")]
    [InlineData("top_sun", "top_sun")]
    [InlineData("middle shade", "middle_shade")]
    [InlineData("whole house", "ground,middle_sun,middle_shade,top_sun,top_shade")]
    [InlineData("everywhere", "ground,middle_sun,middle_shade,top_sun,top_shade")]
    public void Maps_spoken_zones_to_groups(string spoken, string expected) =>
        Assert.Equal(expected.Split(','), HeatingZones.ResolveGroups(spoken));

    [Theory]
    [InlineData("upstairs", "top")]
    [InlineData("downstairs", "ground")]
    [InlineData("ground floor", "ground")]
    [InlineData("first floor", "middle")]
    [InlineData("the middle", "middle")]
    public void Maps_spoken_floors(string spoken, string expected) => Assert.Equal(expected, HeatingZones.ResolveFloor(spoken));

    [Fact]
    public void Unknown_zones_list_the_real_ones()
    {
        var error = Assert.Throws<PluginException>(() => HeatingZones.ResolveGroups("garage"));
        Assert.Contains("upstairs", error.Message);
        Assert.Throws<PluginException>(() => HeatingZones.ResolveFloor("garage"));
    }

    [Theory]
    [InlineData("force_open", "force_open")]
    [InlineData("on", "force_open")]
    [InlineData("off", "force_closed")]
    [InlineData("hold", "hold_at_setpoint")]
    public void Reads_override_kinds(string spoken, string expected) => Assert.Equal(expected, HeatingZones.Kind(spoken));

    [Fact]
    public void Summarizes_history()
    {
        var rows = HeatingState.ParseHistory("""
            {"rows":[
              {"at":"2026-05-26T01:00:00Z","readingC":19.6,"setpointEffectiveC":20.0,"call":"heat"},
              {"at":"2026-05-26T03:00:00Z","readingC":19.9,"setpointEffectiveC":20.0,"call":"heat"},
              {"at":"2026-05-26T05:00:00Z","readingC":20.4,"setpointEffectiveC":20.0,"call":"none"},
              {"at":"2026-05-26T07:00:00Z","readingC":20.1,"setpointEffectiveC":20.0,"call":"none"}]}
            """);
        Assert.Equal("Upstairs, last 6 hours: 19.6–20.4 °C, now 20.1 °C, up 0.5 °C; calling for heat 50% of the time; target 20 °C.",
            HeatingSummary.History("top", 6, rows));
        Assert.Equal("No readings for the middle floor in the last 2 hours.", HeatingSummary.History("middle", 2, []));
    }

    // ---------------------------------------------------------------- the plugin against a fake API

    static MockHttp FakeApi(HttpStatusCode stateStatus = HttpStatusCode.OK)
    {
        var http = new MockHttp();
        http.On(HttpMethod.Get, "/api/v1/state", SampleState, stateStatus);
        http.On(HttpMethod.Post, "/api/v1/setpoint", "{}");
        foreach (var g in HeatingZones.Groups)
        {
            http.On(HttpMethod.Post, $"/api/v1/groups/{g}/override", "{}");
            http.On(HttpMethod.Delete, $"/api/v1/groups/{g}/override", "", HttpStatusCode.NoContent);
            http.On(HttpMethod.Post, $"/api/v1/groups/{g}/offset", "{}");
        }
        return http;
    }

    static Task<(HeatingPlugin Plugin, FakePluginContext Context)> StartAsync(MockHttp http, string auth = "Bearer") =>
        TestPlugins.StartAsync<HeatingPlugin>(new HeatingSettings { BaseUrl = "https://ufh.example.net/api/v1/", Token = "jwt-token", AuthHeader = auth }, http.Client());

    [Fact]
    public async Task Starts_polls_and_shows_the_setpoint_dial()
    {
        var http = FakeApi();
        var (plugin, _) = await StartAsync(http);

        Assert.Equal("Bearer jwt-token", http.To(HttpMethod.Get, "/api/v1/state").Single().Headers["Authorization"]);
        var dial = Assert.Single(plugin.GetControls());
        Assert.Equal(("setpoint", "House", 21d, 10d, 28d, 0.5, "°C"), (dial.Id, dial.Name, dial.Value, dial.Min, dial.Max, dial.Step, dial.Unit));
        Assert.StartsWith("Heating: 21.0 °C set", await plugin.GetContextAsync(ToolCaller.Web, CancellationToken.None));
        await plugin.StopAsync();
    }

    [Fact]
    public async Task Api_key_mode_sends_the_key_header()
    {
        var http = FakeApi();
        var (plugin, _) = await StartAsync(http, "x-api-key");
        var request = http.To(HttpMethod.Get, "/api/v1/state").Single();
        Assert.Equal("jwt-token", request.Headers["x-api-key"]);
        Assert.False(request.Headers.ContainsKey("Authorization"));
        await plugin.StopAsync();
    }

    [Fact]
    public async Task Rejected_token_shows_an_error()
    {
        var (plugin, _) = await StartAsync(FakeApi(HttpStatusCode.Unauthorized));
        Assert.Equal((StatusLevel.Error, "Token rejected or expired"), (plugin.Status.Level, plugin.Status.Message));
        var result = await FakePluginContext.CallToolAsync(plugin, "heating_status");
        Assert.True(result.IsError);
        Assert.Contains("rejected the token", result.Text);
        await plugin.StopAsync();
    }

    [Fact]
    public async Task Sets_the_temperature_within_range()
    {
        var http = FakeApi();
        var (plugin, _) = await StartAsync(http);

        var tooHot = await FakePluginContext.CallToolAsync(plugin, "heating_set_temperature", """{"setpoint_c":30}""");
        Assert.True(tooHot.IsError);
        Assert.Empty(http.To(HttpMethod.Post, "/api/v1/setpoint"));

        var ok = await FakePluginContext.CallToolAsync(plugin, "heating_set_temperature", """{"setpoint_c":21.5}""");
        Assert.Equal("The heating is now set to 21.5 °C.", ok.Text);
        Assert.Equal(21.5, (double)JsonNode.Parse(http.To(HttpMethod.Post, "/api/v1/setpoint").Single().Text)!["setpoint_c"]!);
        Assert.Equal(21.5, plugin.GetControls()[0].Value);
        await plugin.StopAsync();
    }

    [Fact]
    public async Task Changes_the_temperature_relatively_and_clamps()
    {
        var http = FakeApi();
        var (plugin, _) = await StartAsync(http);

        var warmer = await FakePluginContext.CallToolAsync(plugin, "heating_change_temperature", """{"change_c":0.5}""");
        Assert.Equal("The heating went from 21 to 21.5 °C.", warmer.Text);

        var way = await FakePluginContext.CallToolAsync(plugin, "heating_change_temperature", """{"change_c":10}""");
        Assert.Equal("The heating went from 21 to 28 °C. (31 is outside 10-28 °C.)", way.Text);
        Assert.Equal(28.0, (double)JsonNode.Parse(http.To(HttpMethod.Post, "/api/v1/setpoint").Last().Text)!["setpoint_c"]!);
        await plugin.StopAsync();
    }

    [Fact]
    public async Task Overrides_both_sides_of_a_floor_unless_one_is_named()
    {
        var http = FakeApi();
        var (plugin, _) = await StartAsync(http);

        var result = await FakePluginContext.CallToolAsync(plugin, "heating_zone_override", """{"zone":"upstairs","kind":"force_closed","hours":3}""");
        Assert.Equal("Upstairs sunny side and upstairs shady side are forced closed for 3 hours.", result.Text);
        foreach (var g in new[] { "top_sun", "top_shade" })
        {
            var body = JsonNode.Parse(http.To(HttpMethod.Post, $"/api/v1/groups/{g}/override").Single().Text)!;
            Assert.Equal(("force_closed", 10_800_000L), ((string)body["kind"]!, (long)body["duration_ms"]!));
        }

        await FakePluginContext.CallToolAsync(plugin, "heating_zone_override", """{"zone":"middle sunny side","kind":"force_open","hours":1}""");
        Assert.Single(http.To(HttpMethod.Post, "/api/v1/groups/middle_sun/override"));
        Assert.Empty(http.To(HttpMethod.Post, "/api/v1/groups/middle_shade/override"));

        var tooLong = await FakePluginContext.CallToolAsync(plugin, "heating_zone_override", """{"zone":"downstairs","kind":"hold_at_setpoint","hours":30}""");
        Assert.True(tooLong.IsError);
        await plugin.StopAsync();
    }

    [Fact]
    public async Task Clears_overrides_and_sets_offsets()
    {
        var http = FakeApi();
        var (plugin, _) = await StartAsync(http);

        var cleared = await FakePluginContext.CallToolAsync(plugin, "heating_clear_override", """{"zone":"downstairs"}""");
        Assert.Equal("Cleared the override on downstairs.", cleared.Text);
        Assert.Single(http.To(HttpMethod.Delete, "/api/v1/groups/ground/override"));

        var offset = await FakePluginContext.CallToolAsync(plugin, "heating_zone_offset", """{"zone":"upstairs shady side","offset_c":-0.5}""");
        Assert.Equal("Upstairs shady side offset set to -0.5 °C.", offset.Text);
        Assert.Equal(-0.5, (double)JsonNode.Parse(http.To(HttpMethod.Post, "/api/v1/groups/top_shade/offset").Single().Text)!["offset_c"]!);
        await plugin.StopAsync();
    }

    [Fact]
    public async Task History_asks_for_the_window()
    {
        var http = FakeApi();
        http.On(HttpMethod.Get, "/api/v1/history/floor/middle", """{"rows":[{"at":"2026-05-26T05:00:00Z","readingC":20.5,"setpointEffectiveC":21.0,"call":"heat"}]}""");
        var (plugin, _) = await StartAsync(http);

        var result = await FakePluginContext.CallToolAsync(plugin, "heating_history", """{"floor":"first floor","hours":4}""");
        Assert.StartsWith("Middle floor, last 4 hours: 20.5–20.5 °C", result.Text);
        var query = http.To(HttpMethod.Get, "/api/v1/history/floor/middle").Single().Uri.Query;
        Assert.Contains("maxPoints=24", query);
        Assert.Contains("from=", query);
        Assert.Contains("to=", query);
        await plugin.StopAsync();
    }

    [Fact]
    public async Task The_dial_sends_one_setpoint_after_turning_stops()
    {
        var http = FakeApi();
        var (plugin, _) = await StartAsync(http);
        foreach (var v in new[] { 21.5, 22, 22.5 })
            await plugin.OnControlAsync("setpoint", new ControlInput { Value = v }, CancellationToken.None);
        Assert.True(await TestPlugins.WaitUntilAsync(() => http.To(HttpMethod.Post, "/api/v1/setpoint").Count > 0));
        await Task.Delay(1000); // longer than the settle time: no second call should follow
        var sent = Assert.Single(http.To(HttpMethod.Post, "/api/v1/setpoint"));
        Assert.Equal(22.5, (double)JsonNode.Parse(sent.Text)!["setpoint_c"]!);
        await plugin.StopAsync();
    }

    [Fact]
    public async Task Not_configured_asks_for_settings()
    {
        var (plugin, _) = await TestPlugins.StartAsync<HeatingPlugin>(new HeatingSettings());
        Assert.Equal(StatusLevel.Warning, plugin.Status.Level);
        Assert.Empty(plugin.GetControls());
    }
}
