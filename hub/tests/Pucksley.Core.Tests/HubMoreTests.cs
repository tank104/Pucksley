using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Pucksley.Core.Hosting;
using Pucksley.Core.Pucks;
using Pucksley.Core.Tests.Support;
using static Pucksley.Core.Tests.Support.Claude;

namespace Pucksley.Core.Tests;

/// <summary>A puck link the test drives directly: stands in for the USB cable (no serial port needed).</summary>
public sealed class FakeLink(string kind = "usb", string description = "COM_FAKE") : PuckConnection
{
    readonly ConcurrentQueue<JsonObject> _sent = new();

    public override string Kind => kind;
    public override string Description => description;
    public override int QueuedMessages => 0;
    public ConcurrentQueue<short[]> AudioSent { get; } = new();
    public IReadOnlyList<JsonObject> Sent => _sent.ToList();

    public override void Send(JsonObject message)
    {
        if (IsClosed) return;
        PuckText.CleanStrings(message);
        _sent.Enqueue(message.DeepClone().AsObject());
    }

    public override void SendAudio(ReadOnlySpan<short> samples)
    {
        if (!IsClosed && samples.Length > 0) AudioSent.Enqueue(samples.ToArray());
    }

    protected override void CloseTransport() { }

    public void FromPuck(JsonObject m) => OnMessage(m);
    public void FromPuck(string json) => OnMessage(JsonNode.Parse(json)!.AsObject());

    readonly HashSet<JsonObject> _taken = [];

    /// <summary>The first message of <paramref name="type"/> not taken before.</summary>
    public async Task<JsonObject> WaitForAsync(string type, Func<JsonObject, bool>? match = null, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));
        while (true)
        {
            var hit = _sent.FirstOrDefault(m => !_taken.Contains(m) && m["t"]?.GetValue<string>() == type && (match is null || match(m)));
            if (hit is not null)
            {
                _taken.Add(hit);
                return hit;
            }
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"No '{type}' on the fake link. Sent: {string.Join(" | ", _sent.Select(m => m.ToJsonString()).TakeLast(15))}");
            await Task.Delay(10);
        }
    }
}

public sealed class HubMoreTests(HubFixture hub) : HubTestBase(hub)
{
    const int Rate = 16000;

    async Task<List<PuckFrame>> AskAsync(PuckClient puck, short[] audio, Func<PuckFrame, bool>? until = null)
    {
        await puck.TalkAsync("start");
        await puck.WaitForAsync("ai", m => IsAi(m, "listening"));
        await puck.SendAudioAsync(audio);
        await puck.TalkAsync("stop");
        return await puck.CollectUntilAsync(until ?? (f => Is(f, "spk", "a", "end")));
    }

    [Fact]
    public async Task A_puck_on_USB_is_trusted_without_approval()
    {
        var usb = new FakeLink();
        Hub.Pucks.Attach(usb);
        var hello = await usb.WaitForAsync("hello");   // on USB the hub speaks first
        Assert.Equal(Hub.Settings.Current.HubId, S(hello, "hubId"));
        Assert.Equal(1, hello["v"]!.GetValue<int>());

        var id = PuckClient.NewId();
        usb.FromPuck(new JsonObject { ["t"] = "hello", ["v"] = 1, ["id"] = id, ["name"] = "Puck-USB", ["token"] = "" });
        var welcome = await usb.WaitForAsync("welcome");
        Assert.True(welcome["ok"]!.GetValue<bool>(), welcome.ToJsonString());
        Assert.Equal(Hub.Pucks.Record(id)!.Token, S(welcome, "token"));
        Assert.DoesNotContain(Hub.Pucks.Pending, p => p.Id == id);
        await usb.WaitForAsync("state");

        usb.FromPuck("""{"t":"talk","a":"start","via":"tap"}""");
        await usb.WaitForAsync("ai", m => S(m, "s") == "listening");
        usb.FromPuck("""{"t":"talk","a":"cancel"}""");
        await usb.WaitForAsync("ai", m => S(m, "s") == "idle");
        usb.Close("unplugged");
        await HubFixture.WaitUntilAsync(() => Hub.Pucks.Session(id) is null, "unplugged");
    }

    [Fact]
    public async Task With_both_links_up_the_hub_uses_WiFi_and_falls_back_to_USB()
    {
        var wifi = await Hub.ConnectApprovedAsync();
        var usb = new FakeLink();
        Hub.Pucks.Attach(usb);
        usb.FromPuck(new JsonObject { ["t"] = "hello", ["v"] = 1, ["id"] = wifi.Id, ["token"] = wifi.Token });
        Assert.True((await usb.WaitForAsync("welcome"))["ok"]!.GetValue<bool>());
        Assert.Equal(2, Hub.Pucks.Session(wifi.Id)!.Connections.Count);   // different transports: both stay

        await Hub.Tools.NotifyAsync("via wifi", wifi.Id);
        await wifi.WaitForAsync("notify", m => S(m, "text") == "via wifi");
        Assert.DoesNotContain(usb.Sent, m => S(m, "t") == "notify");

        // Wi-Fi details go over USB even when Wi-Fi is up
        (await Hub.PostJsonAsync($"api/pucks/{wifi.Id}/wifi", new JsonObject { ["ssid"] = "Home", ["password"] = "pa55", ["hubUrl"] = "ws://192.168.1.10:8765/puck" })).EnsureSuccessStatusCode();
        var w = await usb.WaitForAsync("wifi");
        Assert.Equal(("Home", "pa55"), (S(w, "ssid"), S(w, "pass")));
        Assert.Equal("ws://192.168.1.10:8765/puck", S(await usb.WaitForAsync("hubaddr"), "url"));

        // Wi-Fi drops: still connected, over USB
        await wifi.CloseAsync();
        await HubFixture.WaitUntilAsync(() => Hub.Pucks.Session(wifi.Id)?.Connections.Count == 1, "Wi-Fi link gone");
        await Hub.Tools.NotifyAsync("via usb", wifi.Id);
        await usb.WaitForAsync("notify", m => S(m, "text") == "via usb");
        usb.Close("unplugged");
        await wifi.DisposeAsync();
    }

    [Fact]
    public async Task Two_pucks_talking_at_once_get_their_own_answers_and_conversations()
    {
        Hub.Speech.TranscriptFor = clip => clip.Samples.Length > 12000 ? "this is puck A" : "this is puck B";
        Hub.Claude.Fallback = (req, ctx) => Text(req.LastUserText().EndsWith("puck A") ? "Hello A, nice to hear from you." : "Hello B, nice to hear from you.", delayMs: 10)(req, ctx);
        await using var a = await Hub.ConnectApprovedAsync(name: "Puck-A");
        await using var b = await Hub.ConnectApprovedAsync(name: "Puck-B");

        var ta = AskAsync(a, Signals.Hiss(Rate));
        var tb = AskAsync(b, Signals.Hiss(Rate / 2));
        var fa = (await ta).Concat(await a.DrainAsync(TimeSpan.FromMilliseconds(100))).ToList();
        var fb = (await tb).Concat(await b.DrainAsync(TimeSpan.FromMilliseconds(100))).ToList();

        Assert.Contains(fa, f => Is(f, "reply", "text", "Hello A, nice to hear from you.") && f.Json!["done"]!.GetValue<bool>());
        Assert.Contains(fb, f => Is(f, "reply", "text", "Hello B, nice to hear from you.") && f.Json!["done"]!.GetValue<bool>());
        Assert.DoesNotContain(fa, f => f.Type == "reply" && S(f.Json!, "text")!.Contains('B'));
        Assert.Equal(2, Hub.Claude.Requests.Count);
        Assert.All(Hub.Claude.Requests, r => Assert.Single(r.Messages));   // one conversation each
        Assert.Contains(Hub.Claude.Requests, r => r.LastUserText().Contains("Speaking to: Puck-A"));
        Assert.Contains(Hub.Claude.Requests, r => r.LastUserText().Contains("Speaking to: Puck-B"));
    }

    [Fact]
    public async Task The_first_sentence_is_spoken_before_the_answer_is_finished()
    {
        Hub.Claude.Then(Reply("end_turn", [TextBlock("Sure, the kitchen lights are now on. "), TextBlock("I also turned the hallway light down a little so it isn't too bright this evening.")], delayMs: 50));
        await using var puck = await Hub.ConnectApprovedAsync();
        var frames = await AskAsync(puck, Signals.Hiss(Rate / 2));
        int spkStart = frames.FindIndex(f => Is(f, "spk", "a", "start"));
        int done = frames.FindIndex(f => f.Type == "reply" && f.Json!["done"]!.GetValue<bool>());
        Assert.True(spkStart >= 0 && done > spkStart, $"spk start at {spkStart}, reply done at {done}");
        Assert.Equal(["Sure, the kitchen lights are now on.", "I also turned the hallway light down a little so it isn't too bright this evening."], Hub.Speech.Spoken);
    }

    [Fact]
    public async Task Without_a_voice_the_answer_is_shown_and_the_puck_goes_idle()
    {
        (await Hub.PutJsonAsync("api/settings/voice", new JsonObject { ["ttsEngine"] = "none" })).EnsureSuccessStatusCode();
        Hub.Claude.Then(Text("The kitchen is warm."));
        await using var puck = await Hub.ConnectApprovedAsync();
        var frames = await AskAsync(puck, Signals.Hiss(Rate / 2), f => f.Json is { } m && IsAi(m, "idle"));
        Assert.Contains(frames, f => Is(f, "reply", "text", "The kitchen is warm.") && f.Json!["done"]!.GetValue<bool>());
        Assert.DoesNotContain(frames, f => f.Type == "spk" || f.IsAudio);
    }

    [Fact]
    public async Task A_speech_recognition_failure_is_reported_and_Claude_isnt_asked()
    {
        Hub.Speech.TranscribeError = "The speech service is down.";
        await using var puck = await Hub.ConnectApprovedAsync();
        var frames = await AskAsync(puck, Signals.Hiss(Rate / 2), f => f.Json is { } m && IsAi(m, "error"));
        Assert.Equal("The speech service is down.", S(frames.Last().Json!, "text"));
        Assert.Empty(Hub.Claude.Requests);
    }

    [Fact]
    public async Task A_timer_set_on_the_puck_without_the_timers_plugin_explains_itself()
    {
        await using var puck = await Hub.ConnectApprovedAsync();
        await puck.SendAsync("""{"t":"timer","a":"start","sec":60,"label":"Eggs"}""");
        Assert.Contains("Timers plugin", S(await puck.WaitForAsync("notify"), "text"));
    }

    [Fact]
    public async Task The_page_can_make_a_puck_listen()
    {
        await using var puck = await Hub.ConnectApprovedAsync();
        (await Hub.PostJsonAsync($"api/pucks/{puck.Id}/listen")).EnsureSuccessStatusCode();
        await puck.WaitForAsync("listen");
        Assert.Equal(HttpStatusCode.NotFound, (await Hub.PostJsonAsync("api/pucks/0000000000000000/listen")).StatusCode);
    }

    [Fact]
    public async Task From_another_computer_the_api_needs_the_admin_key()
    {
        var lan = HubRuntime.LanAddress();
        if (lan == "127.0.0.1") return;   // no LAN address on this machine: nothing to test
        using var http = new HttpClient { BaseAddress = new Uri($"http://{lan}:{Hub.Port}/"), Timeout = TimeSpan.FromSeconds(5) };
        HttpResponseMessage denied;
        try { denied = await http.GetAsync("api/status"); }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException) { return; }   // can't reach our own LAN address here

        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        var key = Hub.Settings.Current.AdminToken;
        var ok = await http.GetAsync($"api/status?key={key}");
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var body = (await ok.Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.Null(body["hub"]!["adminUrl"]);   // the key link and data folder only show on the hub's own machine
        Assert.Null(body["hub"]!["dataDir"]);

        using var change = new HttpRequestMessage(HttpMethod.Post, "api/chat/reset");
        change.Headers.Add("X-Puck-Key", key);
        Assert.Equal(HttpStatusCode.Forbidden, (await http.SendAsync(change)).StatusCode);   // changes also need the page's header
        using var wrong = new HttpRequestMessage(HttpMethod.Get, "api/status");
        wrong.Headers.Add("X-Puck-Key", "nope");
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.SendAsync(wrong)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/")).StatusCode);   // the page itself loads without a key (it asks for one)
    }
}
