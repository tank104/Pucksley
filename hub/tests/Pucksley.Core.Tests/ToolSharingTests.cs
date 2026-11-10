using System.Net;
using System.Net.WebSockets;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Pucksley.Core.Settings;
using Pucksley.Core.Tests.Support;

namespace Pucksley.Core.Tests;

/// <summary>
/// One hub lending its tools to another over MCP (the PC's hub to the NAS's), and a hub that
/// doesn't take pucks.
/// </summary>
public sealed class ToolSharingTests(HubFixture hub) : HubTestBase(hub)
{
    string Endpoint => $"http://127.0.0.1:{Hub.Port}/mcp";

    async Task<McpClient> ConnectAsync(string key) =>
        await McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(Endpoint),
            TransportMode = HttpTransportMode.StreamableHttp,
            AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = $"Bearer {key}" },
        }));

    static StringContent InitializeBody() => new(
        """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"test","version":"1"}}}""",
        System.Text.Encoding.UTF8, "application/json");

    [Fact]
    public async Task Off_by_default_then_needs_the_key()
    {
        Hub.Settings.Update(s => s.Sharing.ShareTools = false);
        using var http = new HttpClient();
        var off = await http.PostAsync(Endpoint, InitializeBody());
        Assert.Equal(HttpStatusCode.NotFound, off.StatusCode);

        Hub.Settings.Update(s => s.Sharing.ShareTools = true);
        try
        {
            var noKey = await http.PostAsync(Endpoint, InitializeBody());
            Assert.Equal(HttpStatusCode.Unauthorized, noKey.StatusCode);
            using var wrong = new HttpRequestMessage(HttpMethod.Post, Endpoint) { Content = InitializeBody() };
            wrong.Headers.Add("Authorization", "Bearer puck_not_the_key");
            Assert.Equal(HttpStatusCode.Unauthorized, (await http.SendAsync(wrong)).StatusCode);
        }
        finally
        {
            Hub.Settings.Update(s => s.Sharing.ShareTools = false);
        }
    }

    [Fact]
    public async Task Another_hub_sees_the_tools_and_runs_them_with_drastic_ones_marked()
    {
        Hub.Settings.Update(s => s.Sharing.ShareTools = true);
        try
        {
            var page = await Hub.GetJsonAsync("api/sharing");
            var key = page["key"]!.GetValue<string>();
            Assert.StartsWith("puck_", key);
            Assert.EndsWith("/mcp", page["url"]!.GetValue<string>());
            Assert.Equal($"Authorization: Bearer {key}", page["header"]!.GetValue<string>());
            Assert.DoesNotContain(key, File.ReadAllText(HubPaths.SettingsFile));   // kept encrypted

            await using var client = await ConnectAsync(key);
            var tools = (await client.ListToolsAsync()).Select(t => t.ProtocolTool).ToList();
            var lights = tools.Single(t => t.Name == "hub_tools_lights");
            Assert.Equal("Set a room's lights (0-100).", lights.Description);
            Assert.Equal(false, lights.Annotations?.DestructiveHint);
            Assert.True(lights.InputSchema.GetProperty("properties").TryGetProperty("room", out _));
            // a tool that asks first on the puck: the other hub's MCP plugin asks its own person
            Assert.Equal(true, tools.Single(t => t.Name == "hub_tools_shutdown").Annotations?.DestructiveHint);

            var result = await client.CallToolAsync("hub_tools_lights", new Dictionary<string, object?> { ["room"] = "Study", ["level"] = 40 });
            Assert.NotEqual(true, result.IsError);
            Assert.Equal("Study lights are at 40%.", Assert.IsType<TextContentBlock>(result.Content[0]).Text);
            Assert.Contains("lights Study 40 ", Hub.Tools.Calls);

            // no confirmation asked here (nobody may be at this hub's pucks): it just runs
            var shutdown = await client.CallToolAsync("hub_tools_shutdown");
            Assert.NotEqual(true, shutdown.IsError);
            Assert.Contains(Hub.Tools.Calls, c => c.StartsWith("shutdown", StringComparison.Ordinal));

            var missing = await client.CallToolAsync("no_such_tool");
            Assert.True(missing.IsError);

            // only some plugins shared: the others' tools are neither listed nor callable
            Hub.Settings.Update(s => s.Sharing.SharedPlugins = ["agent-tools"]);
            var onlyThose = (await client.ListToolsAsync()).Select(t => t.ProtocolTool.Name).ToList();
            Assert.DoesNotContain("hub_tools_lights", onlyThose);
            Assert.True((await client.CallToolAsync("hub_tools_lights", new Dictionary<string, object?> { ["room"] = "Study", ["level"] = 1 })).IsError);
            Hub.Settings.Update(s => s.Sharing.SharedPlugins = []);

            // a new key: the old one stops working
            var renewed = await Hub.PostJsonAsync("api/sharing/new-key", new System.Text.Json.Nodes.JsonObject());
            renewed.EnsureSuccessStatusCode();
            using var http = new HttpClient();
            using var old = new HttpRequestMessage(HttpMethod.Post, Endpoint) { Content = InitializeBody() };
            old.Headers.Add("Authorization", $"Bearer {key}");
            Assert.Equal(HttpStatusCode.Unauthorized, (await http.SendAsync(old)).StatusCode);
        }
        finally
        {
            Hub.Settings.Update(s =>
            {
                s.Sharing.ShareTools = false;
                s.Sharing.SharedPlugins = [];
            });
        }
    }

    [Fact]
    public async Task A_hub_that_doesnt_take_pucks_turns_them_away()
    {
        Hub.Settings.Update(s => s.Sharing.AcceptPucks = false);
        try
        {
            await Assert.ThrowsAnyAsync<WebSocketException>(() => Hub.ConnectAsync());
        }
        finally
        {
            Hub.Settings.Update(s => s.Sharing.AcceptPucks = true);
        }
        await using var puck = await Hub.ConnectApprovedAsync();   // and back to normal
        Assert.NotNull(puck.Token);
    }
}
