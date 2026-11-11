using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Pucksley.Core.Hosting;
using Pucksley.Core.Plugins;
using Pucksley.Core.Pucks;
using Pucksley.Core.Settings;
using Pucksley.Core.Web;

namespace Pucksley.Core.Tests.Support;

/// <summary>The end-to-end tests share one hub and run one at a time (the hub is process-wide state).</summary>
[CollectionDefinition(Name)]
public sealed class HubCollection : ICollectionFixture<HubFixture>
{
    public const string Name = "hub";
}

/// <summary>
/// A real hub (<see cref="HubHost.Build"/>) on a free port, data in the run's temp folder, with this
/// assembly's fake speech and tools plugins switched on and Claude pointed at the fake API.
/// </summary>
public sealed class HubFixture : IAsyncLifetime
{
    public const string ApiKey = "sk-ant-hub-e2e";
    public const string Transcript = "turn the kitchen lights on";

    public WebApplication App { get; private set; } = null!;
    public int Port { get; private set; }
    public HttpClient Http { get; private set; } = null!;
    public ClaudeScript Claude { get; private set; } = null!;

    public PluginManager Plugins => App.Services.GetRequiredService<PluginManager>();
    public PuckManager Pucks => App.Services.GetRequiredService<PuckManager>();
    public SettingsStore Settings => App.Services.GetRequiredService<SettingsStore>();
    public HubEvents Events => App.Services.GetRequiredService<HubEvents>();
    public FakeSpeechPlugin Speech => (FakeSpeechPlugin)Plugins.Find("fake-speech")!.Plugin;
    public FakeLiveSpeechPlugin LiveSpeech => (FakeLiveSpeechPlugin)Plugins.Find("fake-live-speech")!.Plugin;
    public HubToolsPlugin Tools => (HubToolsPlugin)Plugins.Find("hub-tools")!.Plugin;

    public async Task InitializeAsync()
    {
        var fake = FakeAnthropic.Shared;   // points PUCKSLEY_ANTHROPIC_BASE_URL at the fake before the hub makes its model
        Claude = fake.Script(ApiKey);
        Port = TestEnvironment.FreeTcpPort();
        Environment.SetEnvironmentVariable("PUCKSLEY_PORT", Port.ToString());

        // The hub loads settings.json from the (temp) data folder: switch the test plugins on first
        new SettingsStore().Update(s =>
        {
            s.HubName = "TEST-HUB";
            s.Plugins["fake-speech"] = new PluginState { Enabled = true };
            s.Plugins["fake-live-speech"] = new PluginState { Enabled = true };
            s.Plugins["hub-tools"] = new PluginState { Enabled = true };
        });

        App = HubHost.Build([], [typeof(HubFixture).Assembly]);
        await App.StartAsync();
        Http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{Port}/"), Timeout = TimeSpan.FromSeconds(20) };
        await WaitUntilAsync(() => Plugins.Find("fake-speech")?.Running == true && Plugins.Find("hub-tools")?.Running == true, "test plugins started");

        // Configure through the web API, as the page would
        (await PutJsonAsync("api/settings/assistant", new JsonObject { ["anthropicApiKey"] = ApiKey, ["model"] = "claude-opus-5-5" })).EnsureSuccessStatusCode();
        (await PutJsonAsync("api/settings/voice", new JsonObject { ["sttEngine"] = "fake-stt", ["ttsEngine"] = "fake-tts" })).EnsureSuccessStatusCode();
    }

    public async Task DisposeAsync()
    {
        Http?.Dispose();
        if (App is not null)
        {
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await App.StopAsync(stop.Token);
            await App.DisposeAsync();
        }
    }

    /// <summary>Puts every shared piece back the way the tests expect it.</summary>
    public void ResetForTest()
    {
        Claude.Reset();
        Speech.Reset();
        LiveSpeech.Reset();
        Tools.Reset();
        Settings.Update(s =>
        {
            s.Assistant.FollowUpListening = true;
            s.Assistant.FollowUpOnlyAfterQuestions = false;
            s.Assistant.FollowUpSeconds = 6;
            s.Voice.LiveRecognition = true;
            s.Voice.NoSpeechTimeoutSeconds = 7;
            s.Voice.AutoEndOfSpeech = true;
            s.Voice.SttEngine = "fake-stt";
            s.Voice.TtsEngine = "fake-tts";
        });
    }

    // ---------------------------------------------------------------- web API

    public Task<HttpResponseMessage> PutJsonAsync(string path, JsonNode body, bool ui = true) => SendJsonAsync(HttpMethod.Put, path, body, ui);
    public Task<HttpResponseMessage> PostJsonAsync(string path, JsonNode? body = null, bool ui = true) => SendJsonAsync(HttpMethod.Post, path, body, ui);

    public async Task<HttpResponseMessage> SendJsonAsync(HttpMethod method, string path, JsonNode? body, bool ui = true)
    {
        using var req = new HttpRequestMessage(method, path);
        if (body is not null) req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        if (ui) req.Headers.Add("X-Puck-Ui", "1");
        return await Http.SendAsync(req);
    }

    public async Task<JsonObject> GetJsonAsync(string path)
    {
        var res = await Http.GetAsync(path);
        res.EnsureSuccessStatusCode();
        return (await res.Content.ReadFromJsonAsync<JsonObject>())!;
    }

    // ---------------------------------------------------------------- pucks

    public Task<PuckClient> ConnectAsync(string? id = null) => PuckClient.ConnectAsync(Port, id);

    /// <summary>A new puck, approved through the web API, past its initial state messages.</summary>
    public async Task<PuckClient> ConnectApprovedAsync(string? id = null, string name = "Puck-TEST")
    {
        var puck = await ConnectAsync(id);
        await puck.HelloAsync(name: name);
        var first = await puck.WaitForAsync("welcome");
        if (first["ok"]?.GetValue<bool>() != true)
        {
            Assert.True(first["pending"]?.GetValue<bool>(), first.ToJsonString());
            (await PostJsonAsync($"api/pucks/{puck.Id}/approve")).EnsureSuccessStatusCode();
            first = await puck.WaitForAsync("welcome");
        }
        Assert.True(first["ok"]!.GetValue<bool>(), first.ToJsonString());
        puck.Token = first["token"]!.GetValue<string>();
        await puck.WaitForAsync("state");   // the last of the initial messages
        return puck;
    }

    public string LogTail(int n = 40) =>
        string.Join("\n", Events.RecentLogs.TakeLast(n).Select(l => $"{l["level"]} {l["category"]}: {l["message"]}"));

    public static async Task WaitUntilAsync(Func<bool> condition, string what, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"Timed out waiting for: {what}");
            await Task.Delay(20);
        }
    }
}
