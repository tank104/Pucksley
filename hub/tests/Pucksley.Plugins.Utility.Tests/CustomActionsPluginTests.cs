using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Pucksley.Plugins.Actions;
using Pucksley.Sdk;
using Pucksley.TestSupport;

namespace Pucksley.Plugins.Tests;

public class CustomActionsPluginTests
{
    static Task<ToolResult> Call(IPlugin plugin, string tool, string json = "{}") => FakePluginContext.CallToolAsync(plugin, tool, json);

    [Fact]
    public void Builds_tools_from_settings_and_reports_what_it_skipped()
    {
        var ctx = new FakePluginContext();
        var plugin = new CustomActionsPlugin();
        plugin.Initialize(ctx);
        plugin.ApplySettings(new CustomActionsSettings
        {
            HttpActions =
            [
                new() { Name = "Garage Door!", Description = "Open or close the garage door.", Parameters = "action:string:open or close", Url = "http://door.local/{{action}}", Confirm = true },
                new() { Name = "garage door", Url = "http://door.local/x", Method = "get" },
                new() { Name = "broken", Url = "http://x/", Parameters = "bad name:string" },
                new() { Name = "", Url = "" }, // an empty row
                new() { Name = "ftp", Url = "ftp://x/" },
                new() { Name = "verb", Url = "http://x/", Method = "TRACE" },
            ],
            ScriptActions =
            [
                new() { Name = "Echo", Shell = "powershell", Command = "Write-Output {{text}}", Parameters = "text" },
                new() { Name = "escaped", Shell = "bash", Command = @"echo \{{text}}", Parameters = "text" },
            ],
        });

        var tools = plugin.GetTools();
        Assert.Equal(["action_garage_door", "action_garage_door_2", "action_echo"], tools.Select(t => t.Name));
        Assert.True(tools[0].Confirm);
        Assert.False(tools[1].Confirm);
        Assert.Equal("Open or close the garage door.", tools[0].Description);
        Assert.Equal("Runs the custom action \"garage door\".", tools[1].Description);
        Assert.Equal("""{"type":"object","properties":{"action":{"type":"string","description":"open or close"}},"required":["action"]}""", tools[0].InputSchema.ToJsonString());
        Assert.Equal("tool", tools[0].Icon);
        Assert.Equal(1, ctx.ToolChanges);

        Assert.Equal(StatusLevel.Warning, plugin.Status.Level);
        var status = plugin.Status.Message;
        Assert.StartsWith("3 actions; skipped 'broken': parameter line 1: 'bad name' isn't a valid name", status);
        Assert.Contains("'ftp': the URL must start with http:// or https://", status);
        Assert.Contains("'verb': method 'TRACE' isn't GET, POST, PUT, PATCH or DELETE", status);
        Assert.Contains("'escaped': {{text}} comes straight after an escape character", status);
    }

    [Fact]
    public void Tool_names_are_short_and_unique()
    {
        var used = new HashSet<string>();
        Assert.Equal("action_turn_on_the_lights", CustomActionsPlugin.ToolName("Turn on the LIGHTS", used));
        Assert.Equal("action_turn_on_the_lights_2", CustomActionsPlugin.ToolName("turn-on-the-lights", used));
        var longName = CustomActionsPlugin.ToolName(new string('a', 100), used);
        Assert.Equal(64, longName.Length);
        Assert.Equal(64, CustomActionsPlugin.ToolName(new string('a', 100), used).Length);
        Assert.Throws<FormatException>(() => CustomActionsPlugin.ToolName("!!!", used));
    }

    [Fact]
    public async Task Http_action_fills_in_the_request()
    {
        var handler = new StubHttpHandler((_, _) => StubHttpHandler.Json("""{"ok":true,"level":40}"""));
        var plugin = await TestUtil.StartAsync<CustomActionsPlugin>(new FakePluginContext(http: new HttpClient(handler)), new CustomActionsSettings
        {
            HttpActions =
            [
                new()
                {
                    Name = "set_light",
                    Description = "Set a light's level.",
                    Parameters = "room:string:Which room\nlevel:integer:0-100\nnote?:string",
                    Method = "PUT",
                    Url = "http://lights.local/api/{{room}}?level={{level}}",
                    Headers = "Authorization: Bearer abc123\nX-Room: {{room}}",
                    Body = """{"room": "{{room}}", "level": {{level}}, "note": "{{note}}", "raw": {{note}}}""",
                },
            ],
        });
        Assert.Equal((StatusLevel.Ok, "1 action"), (plugin.Status.Level, plugin.Status.Message));

        var result = await Call(plugin, "action_set_light", """{"room":"living room","level":40.4,"note":"He said \"hi\"\n"}""");
        Assert.False(result.IsError, result.Text);
        Assert.Equal("HTTP 200 OK:\n{\n  \"ok\": true,\n  \"level\": 40\n}", result.Text);
        Assert.Equal("set light", result.Activity);

        var (request, body) = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Put, request.Method);
        Assert.Equal("http://lights.local/api/living%20room?level=40", request.RequestUri!.AbsoluteUri);
        Assert.Equal("Bearer abc123", request.Headers.Authorization!.ToString());
        Assert.Equal("living room", request.Headers.GetValues("X-Room").Single());
        Assert.Equal("application/json", request.Content!.Headers.ContentType!.MediaType);
        var json = JsonNode.Parse(body!)!;
        Assert.Equal("living room", (string)json["room"]!);
        Assert.Equal(40, (int)json["level"]!);
        Assert.Equal("He said \"hi\"\n", (string)json["note"]!);
        Assert.Equal("He said \"hi\"\n", (string)json["raw"]!); // an unquoted placeholder still can't break the JSON
    }

    [Fact]
    public async Task Http_action_failures_are_tool_errors()
    {
        var handler = new StubHttpHandler((request, _) => request.RequestUri!.AbsolutePath == "/boom"
            ? new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("boom") }
            : throw new HttpRequestException("No such host is known."));
        var plugin = await TestUtil.StartAsync<CustomActionsPlugin>(new FakePluginContext(http: new HttpClient(handler)), new CustomActionsSettings
        {
            HttpActions =
            [
                new() { Name = "boom", Method = "GET", Url = "http://hooks.local/boom" },
                new() { Name = "away", Method = "POST", Url = "http://nowhere.local/hook?key=SECRET", Parameters = "room" },
            ],
        });

        var boom = await Call(plugin, "action_boom");
        Assert.True(boom.IsError);
        Assert.Equal("HTTP 500 Internal Server Error:\nboom", boom.Text);
        Assert.Null(handler.Requests[0].Body); // GET without a body template sends no body

        Assert.Equal("Missing 'room'.", (await Call(plugin, "action_away")).Text);
        var away = await Call(plugin, "action_away", """{"room":"x"}""");
        Assert.True(away.IsError);
        Assert.Equal("Couldn't reach nowhere.local for 'away': No such host is known.", away.Text); // no key in the message
    }

    [WindowsFact]
    public async Task Script_action_passes_values_safely_and_in_the_environment()
    {
        var plugin = await TestUtil.StartAsync<CustomActionsPlugin>(new FakePluginContext(), new CustomActionsSettings
        {
            ScriptActions =
            [
                new() { Name = "say it", Shell = "powershell", Parameters = "msg:string\ntimes?:integer", Command = "Write-Output \"[{{msg}}]\"\nWrite-Output $env:PUCK_MSG\nWrite-Output \"times=$env:PUCK_TIMES\"" },
            ],
        });
        var result = await Call(plugin, "action_say_it", JsonSerializer.Serialize(new { msg = TestUtil.Nasty }));
        Assert.False(result.IsError, result.Text);
        Assert.Equal($"[{TestUtil.Nasty}]\n{TestUtil.Nasty}\ntimes=", result.Text.Replace("\r\n", "\n"));
        Assert.Equal("say it", result.Activity);
    }

    [WindowsFact]
    public async Task Script_action_failures_and_timeouts_are_reported()
    {
        var plugin = await TestUtil.StartAsync<CustomActionsPlugin>(new FakePluginContext(), new CustomActionsSettings
        {
            ScriptActions =
            [
                new() { Name = "fails", Shell = "cmd", Command = "echo nope 1>&2 & exit /b 3" },
                new() { Name = "slow", Shell = "powershell", Command = "Start-Sleep -Seconds 30", TimeoutSeconds = 2 },
                new() { Name = "quiet", Shell = "cmd", Command = "ver >nul" },
            ],
        });

        var fails = await Call(plugin, "action_fails");
        Assert.True(fails.IsError);
        Assert.Equal("'fails' failed (exit code 3): nope", fails.Text);

        var sw = Stopwatch.StartNew();
        var slow = await Call(plugin, "action_slow");
        Assert.True(slow.IsError);
        Assert.Equal("'slow' didn't finish within 2 s, so I stopped it.", slow.Text);
        Assert.InRange(sw.Elapsed, TimeSpan.FromSeconds(1.5), TimeSpan.FromSeconds(15));

        Assert.Equal("Done.", (await Call(plugin, "action_quiet")).Text);
    }
}
