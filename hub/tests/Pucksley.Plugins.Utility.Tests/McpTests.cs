using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using Pucksley.Plugins.Mcp;
using Pucksley.Sdk;
using Pucksley.TestSupport;

namespace Pucksley.Plugins.Tests;

public class McpToolMappingTests
{
    [Fact]
    public void Server_keys_are_short_slugs_and_unique()
    {
        var used = new HashSet<string>();
        Assert.Equal("my_github", McpToolMapping.ServerKey("My GitHub", used));
        Assert.Equal("my_github2", McpToolMapping.ServerKey("my-github", used));
        Assert.Equal("server", McpToolMapping.ServerKey("  ", used));
        Assert.True(McpToolMapping.ServerKey("a really quite long server name indeed", used).Length <= 24);
    }

    [Fact]
    public void Tool_names_are_sanitized_capped_and_unique()
    {
        var used = new HashSet<string>();
        Assert.Equal("mcp_github_create_issue", McpToolMapping.ToolName("github", "create_issue", used));
        Assert.Equal("mcp_github_create_issue_2", McpToolMapping.ToolName("github", "Create-Issue!", used));

        var a = McpToolMapping.ToolName("github", "list_all_the_pull_request_review_comments_for_a_repository_please", used);
        var b = McpToolMapping.ToolName("github", "list_all_the_pull_request_review_comments_for_a_repository_now", used);
        Assert.InRange(a.Length, 56, 64);
        Assert.Matches("^[a-z0-9_]{1,64}$", b);
        Assert.NotEqual(a, b);
        Assert.Equal(a, McpToolMapping.ToolName("github", "list_all_the_pull_request_review_comments_for_a_repository_please", new HashSet<string>())); // stable
    }

    [Fact]
    public void Input_schemas_become_object_schemas()
    {
        var schema = McpToolMapping.InputSchema(Parse("""{"$schema":"http://json-schema.org/draft-07/schema#","properties":{"a":{"type":"string"}},"required":["a"]}"""));
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""{"properties":{"a":{"type":"string"}},"required":["a"],"type":"object"}"""), schema));
        Assert.Equal("""{"type":"object","properties":{}}""", McpToolMapping.InputSchema(default).ToJsonString());
        Assert.Equal("""{"type":"object","properties":{}}""", McpToolMapping.InputSchema(Parse("""{"type":"string"}""")).ToJsonString());
    }

    [Fact]
    public void Arguments_keep_their_json()
    {
        var args = McpToolMapping.ToArguments(JsonNode.Parse("""{"n":5,"s":"x","o":{"a":[1,2]},"z":null}""")!.AsObject());
        Assert.Equal(JsonValueKind.Number, args["n"].ValueKind);
        Assert.Equal("x", args["s"].GetString());
        Assert.Equal("""{"a":[1,2]}""", args["o"].GetRawText());
        Assert.Equal(JsonValueKind.Null, args["z"].ValueKind);
    }

    [Fact]
    public void Results_join_text_and_carry_the_first_image()
    {
        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47 };
        var result = McpToolMapping.ToToolResult(new CallToolResult
        {
            Content =
            [
                new TextContentBlock { Text = "first" },
                ImageContentBlock.FromBytes(png, "image/png"),
                ImageContentBlock.FromBytes(png, "image/gif"),
                new TextContentBlock { Text = "second" },
            ],
        });
        Assert.False(result.IsError);
        Assert.Equal("first\n[image (image/gif) not shown]\nsecond", result.Text);
        Assert.Equal(png, result.Image);
        Assert.Equal("image/png", result.ImageMediaType);

        var error = McpToolMapping.ToToolResult(new CallToolResult { IsError = true, Content = [new TextContentBlock { Text = "nope" }] });
        Assert.True(error.IsError);
        Assert.Equal("nope", error.Text);

        Assert.Equal("""{"a":1}""", McpToolMapping.ToToolResult(new CallToolResult { Content = [], StructuredContent = Parse("""{"a":1}""") }).Text);
        Assert.Equal("Done.", McpToolMapping.ToToolResult(new CallToolResult { Content = [] }).Text);
    }

    static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();
}

/// <summary>End to end: the plugin starts the test assembly as a stdio MCP server (see <see cref="TestMcpServer"/>).</summary>
public class McpPluginTests(Xunit.Abstractions.ITestOutputHelper output)
{
    static readonly string[] TestTools = ["mcp_test_add", "mcp_test_add_tool", "mcp_test_crash", "mcp_test_echo", "mcp_test_fail", "mcp_test_picture"];

    static McpSettings TestServer(string? command = null) => new()
    {
        Servers =
        [
            new McpServerDef { Name = "test", Transport = "stdio", Command = command ?? Program.DotNet, Arguments = $"{Program.AssemblyPath}\n--mcp-test-server" },
            new McpServerDef { Name = "off", Enabled = false, Command = "nothing-to-see-here" },
        ],
    };

    static Task<ToolResult> Call(IPlugin plugin, string tool, string json = "{}") => FakePluginContext.CallToolAsync(plugin, tool, json);

    [Fact]
    public async Task Lists_and_calls_tools_over_stdio()
    {
        var ctx = new FakePluginContext();
        var plugin = await TestUtil.StartAsync<McpPlugin>(ctx, TestServer());
        try
        {
            Assert.Equal((StatusLevel.Ok, "1 server, 6 tools"), (plugin.Status.Level, plugin.Status.Message));
            Assert.Equal(TestTools, plugin.GetTools().Select(t => t.Name).Order());

            var echo = plugin.GetTools().Single(t => t.Name == "mcp_test_echo");
            Assert.Equal("test: Echoes the text back.", echo.Description);
            Assert.Equal("object", (string)echo.InputSchema["type"]!);
            Assert.Equal("""["text"]""", echo.InputSchema["required"]!.ToJsonString());
            Assert.Equal("test (MCP server): Use echo to test the connection.", plugin.Instructions);
            Assert.True(ctx.ToolChanges >= 1);

            Assert.Equal("echo: hello", (await Call(plugin, "mcp_test_echo", """{"text":"hello"}""")).Text);
            Assert.Equal("5", (await Call(plugin, "mcp_test_add", """{"a":2,"b":3}""")).Text);

            var fail = await Call(plugin, "mcp_test_fail");
            Assert.True(fail.IsError);
            Assert.Equal("it broke", fail.Text);

            var picture = await Call(plugin, "mcp_test_picture");
            Assert.Equal("a red dot", picture.Text);
            Assert.Equal("image/png", picture.ImageMediaType);
            Assert.Equal(0x89, picture.Image![0]);
        }
        finally
        {
            await plugin.StopAsync();
        }
        Assert.Empty(plugin.GetTools());
    }

    [Fact]
    public async Task A_tools_changed_notification_refreshes_the_list()
    {
        var ctx = new FakePluginContext { Log = new TestOutputLogger(output) };
        var plugin = await TestUtil.StartAsync<McpPlugin>(ctx, TestServer());
        try
        {
            int before = ctx.ToolChanges;
            Assert.Equal("added", (await Call(plugin, "mcp_test_add_tool")).Text);
            await TestUtil.WaitUntil(() => plugin.GetTools().Any(t => t.Name == "mcp_test_late_tool"), TimeSpan.FromSeconds(10), "the new tool");
            Assert.True(ctx.ToolChanges > before);
            Assert.Equal("late!", (await Call(plugin, "mcp_test_late_tool")).Text);
            Assert.Equal("1 server, 7 tools", plugin.Status.Message);
        }
        finally
        {
            await plugin.StopAsync();
        }
    }

    [Fact]
    public async Task A_dropped_server_is_reconnected_on_the_next_call()
    {
        var plugin = await TestUtil.StartAsync<McpPlugin>(new FakePluginContext(), TestServer());
        try
        {
            var crash = await Call(plugin, "mcp_test_crash");
            Assert.True(crash.IsError);
            Assert.StartsWith("Lost the connection to the test MCP server", crash.Text);
            Assert.Equal(StatusLevel.Error, plugin.Status.Level);

            Assert.Equal("echo: again", (await Call(plugin, "mcp_test_echo", """{"text":"again"}""")).Text);
            Assert.Equal((StatusLevel.Ok, "1 server, 6 tools"), (plugin.Status.Level, plugin.Status.Message));
        }
        finally
        {
            await plugin.StopAsync();
        }
    }

    [Fact]
    public async Task Reconnect_button_reconnects()
    {
        var plugin = await TestUtil.StartAsync<McpPlugin>(new FakePluginContext(), TestServer());
        try
        {
            Assert.Equal("reconnect", Assert.Single(plugin.GetActions()).Id);
            var result = await plugin.RunActionAsync("reconnect", new Dictionary<string, string>(), default);
            Assert.Equal(new ActionResult("1 server, 6 tools", true), result);
            Assert.Equal("echo: still here", (await Call(plugin, "mcp_test_echo", """{"text":"still here"}""")).Text);
        }
        finally
        {
            await plugin.StopAsync();
        }
    }

    [Fact]
    public async Task A_server_that_cannot_start_says_why()
    {
        var plugin = await TestUtil.StartAsync<McpPlugin>(new FakePluginContext(), TestServer(command: "puck-no-such-command"));
        try
        {
            Assert.Equal(StatusLevel.Error, plugin.Status.Level);
            Assert.StartsWith("test: ", plugin.Status.Message);
            Assert.Contains("puck-no-such-command", plugin.Status.Message);
            Assert.Empty(plugin.GetTools());
        }
        finally
        {
            await plugin.StopAsync();
        }
    }

    [Fact]
    public async Task An_unreachable_http_server_is_an_error()
    {
        var settings = new McpSettings { Servers = [new McpServerDef { Name = "web", Transport = "http", Url = "http://127.0.0.1:1/mcp" }] };
        var plugin = await TestUtil.StartAsync<McpPlugin>(new FakePluginContext(), settings);
        try
        {
            Assert.Equal(StatusLevel.Error, plugin.Status.Level);
            Assert.StartsWith("web: ", plugin.Status.Message);
            Assert.Empty(plugin.GetTools());
        }
        finally
        {
            await plugin.StopAsync();
        }
    }

    [Fact]
    public async Task No_enabled_servers_means_no_tools()
    {
        var settings = new McpSettings { Servers = [new McpServerDef { Name = "off", Enabled = false, Command = "x" }, new McpServerDef()] };
        var plugin = await TestUtil.StartAsync<McpPlugin>(new FakePluginContext(), settings);
        Assert.Equal((StatusLevel.Ok, "No servers yet"), (plugin.Status.Level, plugin.Status.Message));
        Assert.Empty(plugin.GetTools());
        Assert.Null(plugin.Instructions);
        await plugin.StopAsync();
    }
}
