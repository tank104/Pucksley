using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Pucksley.Core.Settings;
using Pucksley.Core.Tests.Support;
using Pucksley.Sdk;
using static Pucksley.Core.Tests.Support.Claude;

namespace Pucksley.Core.Tests;

/// <summary>Controls, notifications, media and the web API, end to end.</summary>
public sealed class HubFeatureTests(HubFixture hub) : HubTestBase(hub)
{
    [Fact]
    public async Task Turning_a_control_on_the_puck_reaches_the_plugin_with_its_own_id()
    {
        await using var puck = await Hub.ConnectApprovedAsync();
        await puck.SendAsync("""{"t":"ctl","id":"c0","v":27}""");
        await puck.SendAsync("""{"t":"ctl","id":"c1","on":false}""");
        await puck.SendAsync("""{"t":"ctl","id":"c2","a":"press"}""");
        await puck.SendAsync("""{"t":"ctl","id":"c9","v":1}""");   // no such control: ignored
        await HubFixture.WaitUntilAsync(() => Hub.Tools.ControlInputs.Count >= 3, "three control inputs");
        await Task.Delay(100);

        var inputs = Hub.Tools.ControlInputs.ToArray();
        Assert.Equal(3, inputs.Length);
        Assert.Equal("kitchen.vol", inputs[0].Id);
        Assert.Equal(27, inputs[0].Input.Value);
        Assert.Equal("lamp", inputs[1].Id);
        Assert.False(inputs[1].Input.On);
        Assert.Equal("tv.off", inputs[2].Id);
        Assert.True(inputs[2].Input.Pressed);
    }

    [Fact]
    public async Task A_plugin_changing_a_value_updates_the_puck_by_short_id()
    {
        await using var puck = await Hub.ConnectApprovedAsync();
        Hub.Tools.SetControlValue("kitchen.vol", 42);
        var v = await puck.WaitForAsync("ctlv");
        Assert.Equal("c0", S(v, "id"));
        Assert.Equal(42, v["v"]!.GetValue<double>());
    }

    [Fact]
    public async Task A_plugin_notification_reaches_the_puck_in_its_font()
    {
        await using var puck = await Hub.ConnectApprovedAsync();
        await Hub.Tools.NotifyAsync("Tāmaki timer’s done — 5 min");
        var n = await puck.WaitForAsync("notify");
        Assert.Equal("Tamaki timer's done - 5 min", S(n, "text"));
        Assert.Equal("alert", S(n, "kind"));
        Assert.True(n["chime"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Media_keys_on_the_puck_reach_the_followed_source()
    {
        await using var puck = await Hub.ConnectApprovedAsync();
        await puck.SendAsync("""{"t":"media","a":"toggle"}""");
        await puck.SendAsync("""{"t":"media","a":"vol","v":40}""");
        await HubFixture.WaitUntilAsync(() => Hub.Tools.MediaCommands.Count >= 2, "two media commands");
        var cmds = Hub.Tools.MediaCommands.ToArray();
        Assert.Equal(("kitchen", MediaAction.Toggle), (cmds[0].Source, cmds[0].Command.Action));
        Assert.Equal(("kitchen", MediaAction.SetVolume, (int?)40), (cmds[1].Source, cmds[1].Command.Action, cmds[1].Command.Volume));
    }

    [Fact]
    public async Task Now_playing_is_pushed_when_it_changes_and_only_then()
    {
        await using var puck = await Hub.ConnectApprovedAsync();
        // Same song again (its title has a curly apostrophe the puck can't show): nothing new to send
        Hub.Tools.RaiseNowPlaying();
        var noise = await puck.DrainAsync(TimeSpan.FromMilliseconds(600));
        Assert.DoesNotContain(noise, f => f.Type == "state");

        Hub.Tools.Playing = Hub.Tools.Playing with { Title = "Royals", Artist = "Lorde", Volume = 30 };
        Hub.Tools.RaiseNowPlaying();
        var state = await puck.WaitForAsync("state", timeout: TimeSpan.FromSeconds(3));
        Assert.Equal(("Royals", "Lorde"), (S(state, "title"), S(state, "artist")));
        Assert.Equal(30, state["vol"]!.GetValue<int>());
    }

    [Fact]
    public async Task Chat_from_the_page_runs_tools_without_asking()
    {
        Hub.Claude
            .Then(Reply("tool_use", [ToolUse("toolu_w1", "hub_tools_lights", """{"room":"Lounge","level":50}"""), ToolUse("toolu_w2", "hub_tools_shutdown", "{}")]))
            .Then(Text("The lounge is at half, and the computer is shutting down."));
        var res = await Hub.PostJsonAsync("api/chat", new JsonObject { ["text"] = "lounge to half and shut down", ["session"] = "t1" });
        res.EnsureSuccessStatusCode();
        var body = (await res.Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.False(body["failed"]!.GetValue<bool>(), body.ToJsonString());
        Assert.Equal("The lounge is at half, and the computer is shutting down.", S(body, "text"));
        var states = body["activities"]!.AsArray().Select(a => S(a!.AsObject(), "state")).ToList();
        Assert.Equal(2, states.Count(s => s == "run"));
        Assert.Equal(2, states.Count(s => s == "ok"));
        // Typed on the hub's own page = deliberate: no confirmation, and no puck in the context
        Assert.Contains("shutdown ", Hub.Tools.Calls);
        Assert.Contains("Typed in the hub's web page", Hub.Claude.Requests[0].LastUserText());

        // Reset starts the conversation over
        (await Hub.PostJsonAsync("api/chat/reset", new JsonObject { ["session"] = "t1" })).EnsureSuccessStatusCode();
        await Hub.PostJsonAsync("api/chat", new JsonObject { ["text"] = "hi", ["session"] = "t1" });
        Assert.Single(Hub.Claude.Requests[2].Messages);
    }

    [Fact]
    public async Task Chat_failures_come_back_as_failed_answers()
    {
        Hub.Claude.Then(Error(401, "authentication_error", "invalid x-api-key"));
        var body = (await (await Hub.PostJsonAsync("api/chat", new JsonObject { ["text"] = "hi", ["session"] = "t2" })).Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.True(body["failed"]!.GetValue<bool>());
        Assert.Contains("API key", S(body, "text"));
    }

    [Fact]
    public async Task Settings_round_trip_through_the_page_keeps_the_key_secret()
    {
        var page = await Hub.GetJsonAsync("api/settings/assistant");
        var values = page["values"]!.AsObject();
        Assert.Equal(SettingsSchema.SecretMask, S(values, "anthropicApiKey"));
        Assert.DoesNotContain(HubFixture.ApiKey, page.ToJsonString());

        values["extraInstructions"] = "Be brief.";
        var saved = await Hub.PutJsonAsync("api/settings/assistant", values);
        saved.EnsureSuccessStatusCode();
        Assert.Equal(HubFixture.ApiKey, Hub.Settings.Current.Assistant.AnthropicApiKey);   // the mask didn't overwrite it
        Assert.Equal("Be brief.", Hub.Settings.Current.Assistant.ExtraInstructions);
        Assert.DoesNotContain(HubFixture.ApiKey, File.ReadAllText(HubPaths.SettingsFile));
        (await Hub.PutJsonAsync("api/settings/assistant", new JsonObject { ["extraInstructions"] = "" })).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Status_events_are_published_for_voice_plugin_and_settings_changes()
    {
        await using var puck = await Hub.ConnectApprovedAsync();
        var events = new ConcurrentQueue<JsonObject>();
        using var cts = new CancellationTokenSource();
        var reader = Task.Run(async () =>
        {
            try { await foreach (var e in Hub.Events.SubscribeAsync(cts.Token)) events.Enqueue(e); }
            catch (OperationCanceledException) { }
        });
        // Wait until subscribed (a probe comes through), and let earlier status events pass
        await HubFixture.WaitUntilAsync(() =>
        {
            Hub.Events.Publish("probe", new JsonObject());
            return events.Any(e => S(e, "type") == "probe");
        }, "subscribed");
        await Task.Delay(400);
        events.Clear();

        bool Saw(string what) => events.Any(e => S(e, "type") == "status" && S(e, "what") == what);

        await puck.TalkAsync("start");                       // the puck's voice state changes
        await puck.WaitForAsync("ai", m => IsAi(m, "listening"));
        await HubFixture.WaitUntilAsync(() => Saw("pucks"), "a pucks status event", TimeSpan.FromSeconds(2));
        await puck.TalkAsync("cancel");

        Hub.Tools.SetStatusForTest("Busy");                  // a plugin's status changes
        await HubFixture.WaitUntilAsync(() => Saw("plugins"), "a plugins status event", TimeSpan.FromSeconds(2));

        (await Hub.PutJsonAsync("api/settings/household", new JsonObject { ["notes"] = "The cat is Tui." })).EnsureSuccessStatusCode();
        await HubFixture.WaitUntilAsync(() => Saw("settings"), "a settings status event", TimeSpan.FromSeconds(2));

        // A chatty plugin can't flood the page
        await Task.Delay(400);
        events.Clear();
        for (int i = 0; i < 40; i++) Hub.Tools.SetStatusForTest($"Busy {i}");
        await Task.Delay(700);
        int n = events.Count(e => S(e, "type") == "status" && S(e, "what") == "plugins");
        Assert.InRange(n, 1, 3);
        cts.Cancel();
        await reader;
        (await Hub.PutJsonAsync("api/settings/household", new JsonObject { ["notes"] = "" })).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task The_voice_test_endpoints_use_the_chosen_engines()
    {
        var res = await Hub.PostJsonAsync("api/voice/speak", new JsonObject { ["text"] = "Testing, one two." });
        res.EnsureSuccessStatusCode();
        Assert.Equal("audio/wav", res.Content.Headers.ContentType!.MediaType);
        var wav = Wav.Decode(await res.Content.ReadAsByteArrayAsync());
        Assert.Equal(16000, wav.SampleRate);
        Assert.InRange(wav.Samples.Length, 3500, 4500);   // 0.25 s
    }
}
