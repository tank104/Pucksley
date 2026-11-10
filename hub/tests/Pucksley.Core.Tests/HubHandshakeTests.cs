using System.Net;
using System.Text.Json.Nodes;
using Pucksley.Core.Pucks;
using Pucksley.Core.Settings;
using Pucksley.Core.Tests.Support;

namespace Pucksley.Core.Tests;

/// <summary>Shared setup for the end-to-end tests: one hub, one test at a time, shared pieces reset.</summary>
[Collection(HubCollection.Name)]
public abstract class HubTestBase(HubFixture hub) : IAsyncLifetime
{
    protected HubFixture Hub { get; } = hub;

    public virtual Task InitializeAsync()
    {
        Hub.ResetForTest();
        return Task.CompletedTask;
    }

    public virtual Task DisposeAsync() => Task.CompletedTask;

    protected static string? S(JsonObject m, string key) => m[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
    protected static bool IsAi(JsonObject m, string state) => S(m, "t") == "ai" && S(m, "s") == state;
    protected static bool Is(PuckFrame f, string type, string? key = null, string? value = null) =>
        f.Type == type && (key is null || S(f.Json!, key) == value);
}

/// <summary>Hello, approval, tokens, keepalive and the hub's web view of pucks.</summary>
public sealed class HubHandshakeTests(HubFixture hub) : HubTestBase(hub)
{
    [Fact]
    public async Task A_new_puck_waits_for_approval_then_gets_a_token_and_its_initial_state()
    {
        await using var puck = await Hub.ConnectAsync();
        await puck.HelloAsync(name: "Puck-1D4D");
        var pending = await puck.WaitForAsync("welcome");
        Assert.False(pending["ok"]!.GetValue<bool>());
        Assert.True(pending["pending"]!.GetValue<bool>());
        var code = S(pending, "code")!;
        Assert.Matches("^[0-9]{3}$", code);

        // The page shows it with the same code
        var status = await Hub.GetJsonAsync("api/status");
        var listed = status["pending"]!.AsArray().Single(p => S(p!.AsObject(), "id") == puck.Id)!.AsObject();
        Assert.Equal(code, S(listed, "code"));
        Assert.Equal("Puck-1D4D", S(listed, "name"));

        // Until it's welcomed, nothing but hello (and a few status messages) counts
        await puck.TalkAsync("start");

        // Approving needs the page's header (other sites' pages can't send it)
        var forbidden = await Hub.PostJsonAsync($"api/pucks/{puck.Id}/approve", null, ui: false);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        (await Hub.PostJsonAsync($"api/pucks/{puck.Id}/approve", new JsonObject { ["name"] = "Kitchen", ["room"] = "Kitchen" })).EnsureSuccessStatusCode();

        var frames = await puck.CollectUntilAsync(f => f.Type == "state");
        Assert.Equal(["welcome", "time", "cfg", "ctls", "ctl", "ctl", "ctl", "direct", "state"], frames.Select(f => f.Type));
        // no engine a puck could call itself: direct mode is off (and any old keys get wiped)
        Assert.Equal("off", S(frames[7].Json!, "a"));

        var welcome = frames[0].Json!;
        Assert.True(welcome["ok"]!.GetValue<bool>());
        Assert.Equal("TEST-HUB", S(welcome, "hub"));
        Assert.Equal(Hub.Settings.Current.HubId, S(welcome, "hubId"));
        Assert.Equal("Kitchen", S(welcome, "name"));
        var token = S(welcome, "token")!;
        Assert.Matches("^[0-9a-f]{32}$", token);
        Assert.DoesNotContain(token, File.ReadAllText(HubPaths.SettingsFile));   // stored encrypted

        var time = frames[1].Json!;
        Assert.InRange(time["epoch"]!.GetValue<long>(), DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 60, DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 60);
        Assert.NotNull(time["tz"]);

        var cfg = frames[2].Json!;
        Assert.Equal(80, cfg["bright"]!.GetValue<int>());
        Assert.Equal(70, cfg["spk"]!.GetValue<int>());
        Assert.Equal(30, cfg["mic"]!.GetValue<int>());
        Assert.True(cfg["lift"]!.GetValue<bool>());
        Assert.Equal("Kitchen", S(cfg, "name"));

        Assert.Equal(3, frames[3].Json!["n"]!.GetValue<int>());
        var vol = frames[4].Json!;
        Assert.Equal((0, "c0", "Hub tools", "Kitchen", "num"), (vol["i"]!.GetValue<int>(), S(vol, "id"), S(vol, "g"), S(vol, "n"), S(vol, "k")));
        Assert.Equal((25.0, 0.0, 100.0, 1.0, "%"), (vol["v"]!.GetValue<double>(), vol["min"]!.GetValue<double>(), vol["max"]!.GetValue<double>(), vol["step"]!.GetValue<double>(), S(vol, "u")));
        Assert.Equal(("c1", "bool", true), (S(frames[5].Json!, "id"), S(frames[5].Json!, "k"), frames[5].Json!["on"]!.GetValue<bool>()));
        Assert.Equal(("c2", "act"), (S(frames[6].Json!, "id"), S(frames[6].Json!, "k")));

        // What's playing, folded into the puck's font (curly apostrophe, macron)
        var state = frames[8].Json!;
        Assert.Equal(("Don't Stop", "Tamaki Five", "Kitchen"), (S(state, "title"), S(state, "artist"), S(state, "src")));
        Assert.True(state["play"]!.GetValue<bool>());
        Assert.Equal(25, state["vol"]!.GetValue<int>());

        // The record has the name and room from the approval
        var record = Hub.Pucks.Record(puck.Id)!;
        Assert.Equal(("Kitchen", "Kitchen", "Puck-1D4D"), (record.Name, record.Room, record.DeviceName));
    }

    [Fact]
    public async Task A_known_puck_is_welcomed_straight_away_and_a_wrong_token_isnt()
    {
        string id, token;
        await using (var first = await Hub.ConnectApprovedAsync())
        {
            id = first.Id;
            token = first.Token!;
        }

        await using var again = await Hub.ConnectAsync(id);
        await again.HelloAsync(token);
        var w = await again.WaitForAsync("welcome");
        Assert.True(w["ok"]!.GetValue<bool>(), w.ToJsonString());
        Assert.Equal(token, S(w, "token"));
        await again.WaitForAsync("state");

        await using var stranger = await Hub.ConnectAsync(id);
        await stranger.HelloAsync(new string('0', 32));
        var p = await stranger.WaitForAsync("welcome");
        Assert.True(p["pending"]?.GetValue<bool>(), p.ToJsonString());
    }

    [Fact]
    public async Task A_bad_puck_id_is_refused()
    {
        await using var puck = await Hub.ConnectAsync("not-hex!");
        await puck.HelloAsync();
        var w = await puck.WaitForAsync("welcome");
        Assert.False(w["ok"]!.GetValue<bool>());
        Assert.Equal("Bad puck id", S(w, "error"));
    }

    [Fact]
    public async Task A_rejected_puck_is_told_before_it_is_disconnected()
    {
        await using var puck = await Hub.ConnectAsync();
        await puck.HelloAsync();
        await puck.WaitForAsync("welcome");
        (await Hub.PostJsonAsync($"api/pucks/{puck.Id}/reject")).EnsureSuccessStatusCode();

        // The puck must see why (it shows the error and waits 30 s instead of reconnecting at once)
        var w = await puck.WaitForAsync("welcome", timeout: TimeSpan.FromSeconds(5));
        Assert.False(w["ok"]!.GetValue<bool>());
        Assert.Equal("Not approved", S(w, "error"));
        Assert.True(await puck.WaitClosedAsync(TimeSpan.FromSeconds(5)), "the hub closes the link");
        Assert.DoesNotContain(Hub.Pucks.Pending, p => p.Id == puck.Id);
    }

    [Fact]
    public async Task A_forgotten_puck_is_told_to_forget_its_token_and_must_be_approved_again()
    {
        var puck = await Hub.ConnectApprovedAsync();
        await using (puck)
        {
            (await Hub.SendJsonAsync(HttpMethod.Delete, $"api/pucks/{puck.Id}", null)).EnsureSuccessStatusCode();
            await puck.WaitForAsync("forget", timeout: TimeSpan.FromSeconds(5));
            Assert.True(await puck.WaitClosedAsync(TimeSpan.FromSeconds(5)));
        }
        Assert.Null(Hub.Pucks.Record(puck.Id));

        await using var back = await Hub.ConnectAsync(puck.Id);
        await back.HelloAsync(puck.Token);
        Assert.True((await back.WaitForAsync("welcome"))["pending"]?.GetValue<bool>());
    }

    [Fact]
    public async Task The_hub_pings_and_a_puck_that_answers_stays_connected()
    {
        await using var puck = await Hub.ConnectApprovedAsync();
        await FixtureWait(() => puck.PingsReceived > 0, "a ping");
        Assert.False(puck.IsClosed);
    }

    [Fact]
    public async Task Status_lists_the_connected_puck_with_what_it_reported()
    {
        await using var puck = await Hub.ConnectApprovedAsync(name: "Puck-ABCD");
        await puck.SendAsync(new JsonObject { ["t"] = "bat", ["p"] = 55, ["chg"] = true, ["usb"] = false });
        await puck.SendAsync(new JsonObject { ["t"] = "net", ["ip"] = "192.168.1.77", ["rssi"] = -61, ["ssid"] = "Home" });
        await puck.SendAsync(new JsonObject { ["t"] = "face", ["f"] = "home" });
        await FixtureWait(() => Hub.Pucks.Session(puck.Id)?.Face == "home", "face update");

        var status = await Hub.GetJsonAsync("api/status");
        var p = status["pucks"]!.AsArray().Single(x => S(x!.AsObject(), "id") == puck.Id)!.AsObject();
        Assert.True(p["connected"]!.GetValue<bool>());
        Assert.Equal(["wifi"], p["transports"]!.AsArray().Select(t => t!.GetValue<string>()));
        Assert.Equal(("Puck-ABCD", "0.1.0", "idle", "home"), (S(p, "name"), S(p, "firmware"), S(p, "voice"), S(p, "face")));
        Assert.Equal((55, true, "192.168.1.77", -61), (p["battery"]!.GetValue<int>(), p["charging"]!.GetValue<bool>(), S(p, "ip"), p["rssi"]!.GetValue<int>()));
        Assert.Equal("Kitchen", S(p, "mediaSource"));

        Assert.True(status["assistant"]!["ready"]!.GetValue<bool>());
        Assert.Equal("fake-stt", S(status["voice"]!["stt"]!.AsObject(), "id"));
        Assert.Equal("fake-tts", S(status["voice"]!["tts"]!.AsObject(), "id"));

        await puck.CloseAsync();
        await FixtureWait(() => Hub.Pucks.Session(puck.Id) is null, "disconnect noticed");
        var after = await Hub.GetJsonAsync("api/status");
        Assert.False(after["pucks"]!.AsArray().Single(x => S(x!.AsObject(), "id") == puck.Id)!["connected"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Junk_and_unknown_messages_are_ignored()
    {
        await using var puck = await Hub.ConnectApprovedAsync();
        await puck.SendAsync("not json at all");
        await puck.SendAsync("""{"t":"something-new","x":1}""");
        await puck.SendAsync("""{"no":"type"}""");
        await puck.SendAsync("""{"t":42}""");
        await puck.SendAudioAsync(Signals.Hiss(640));   // mic audio while not listening
        await puck.TalkAsync("start");
        await puck.WaitForAsync("ai", m => IsAi(m, "listening"));
        await puck.TalkAsync("cancel");
        await puck.WaitForAsync("ai", m => IsAi(m, "idle"));
        Assert.False(puck.IsClosed);
    }

    [Fact]
    public async Task The_puck_changing_its_own_volume_is_remembered()
    {
        await using var puck = await Hub.ConnectApprovedAsync();
        await puck.SendAsync(new JsonObject { ["t"] = "spkvol", ["v"] = 33 });
        await FixtureWait(() => Hub.Pucks.Record(puck.Id)?.SpeakerVolume == 33, "speaker volume saved");
    }

    [Fact]
    public async Task Editing_a_puck_on_the_page_sends_it_new_settings()
    {
        await using var puck = await Hub.ConnectApprovedAsync();
        var page = await Hub.GetJsonAsync($"api/pucks/{puck.Id}");
        Assert.False(page["values"]!.AsObject().ContainsKey("token"));   // the token stays on the hub
        var values = page["values"]!.AsObject();
        values["brightness"] = 55;
        values["room"] = "Lounge";
        (await Hub.PutJsonAsync($"api/pucks/{puck.Id}", values)).EnsureSuccessStatusCode();
        var cfg = await puck.WaitForAsync("cfg");
        Assert.Equal(55, cfg["bright"]!.GetValue<int>());
        var record = Hub.Pucks.Record(puck.Id)!;
        Assert.Equal("Lounge", record.Room);
        Assert.Equal(puck.Token, record.Token);   // untouched by the form
    }

    // ---------------------------------------------------------------- waiting for approval

    [Fact]
    public async Task A_puck_approved_after_a_long_wait_stays_connected()
    {
        // Pending links must be pinged (the puck says nothing while it shows its code), or the
        // silence timeout drops it the moment it's approved. Timings shortened for the test.
        var (ping, silence) = (PuckSession.PingInterval, PuckSession.SilenceTimeout);
        PuckSession.PingInterval = TimeSpan.FromMilliseconds(250);
        PuckSession.SilenceTimeout = TimeSpan.FromSeconds(1.5);
        try
        {
            await using var puck = await Hub.ConnectAsync();
            await puck.HelloAsync();
            await puck.WaitForAsync("welcome");
            await Task.Delay(2500);
            Assert.True(puck.PingsReceived > 0, "a pending puck is pinged too");
            (await Hub.PostJsonAsync($"api/pucks/{puck.Id}/approve")).EnsureSuccessStatusCode();
            Assert.True((await puck.WaitForAsync("welcome"))["ok"]!.GetValue<bool>());
            await Task.Delay(2000);
            Assert.False(puck.IsClosed, "dropped after approval");
            await Hub.Tools.NotifyAsync("still here", puck.Id);
            await puck.WaitForAsync("notify");
        }
        finally
        {
            (PuckSession.PingInterval, PuckSession.SilenceTimeout) = (ping, silence);
        }
    }

    [Fact]
    public async Task Approval_restarts_the_links_silence_clock()
    {
        // Even a puck that didn't answer pings while waiting gets a full timeout after it's accepted
        var (ping, silence) = (PuckSession.PingInterval, PuckSession.SilenceTimeout);
        PuckSession.PingInterval = TimeSpan.FromMilliseconds(250);
        PuckSession.SilenceTimeout = TimeSpan.FromSeconds(1.5);
        try
        {
            await using var puck = await Hub.ConnectAsync();
            puck.AutoPong = false;
            await puck.HelloAsync();
            await puck.WaitForAsync("welcome");
            await Task.Delay(2000);
            puck.AutoPong = true;
            (await Hub.PostJsonAsync($"api/pucks/{puck.Id}/approve")).EnsureSuccessStatusCode();
            Assert.True((await puck.WaitForAsync("welcome"))["ok"]!.GetValue<bool>());
            await Task.Delay(1500);
            Assert.False(puck.IsClosed, "dropped right after approval");
        }
        finally
        {
            (PuckSession.PingInterval, PuckSession.SilenceTimeout) = (ping, silence);
        }
    }

    static Task FixtureWait(Func<bool> condition, string what) => HubFixture.WaitUntilAsync(condition, what);
}
