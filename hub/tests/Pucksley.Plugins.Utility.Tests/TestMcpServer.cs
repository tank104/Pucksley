using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Pucksley.Plugins.Tests;

/// <summary>A small stdio MCP server built with the SDK's server side, for end-to-end tests of the MCP plugin.</summary>
internal static class TestMcpServer
{
    // A 1×1 red PNG.
    static readonly byte[] RedDot = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8DwHwAFBQIAX8jx0gAAAABJRU5ErkJggg==");

    public static async Task RunAsync()
    {
        var tools = new McpServerPrimitiveCollection<McpServerTool>();
        tools.Add(McpServerTool.Create(
            ([Description("Text to echo")] string text) => $"echo: {text}",
            new McpServerToolCreateOptions { Name = "echo", Description = "Echoes the text back." }));
        tools.Add(McpServerTool.Create(
            (int a, int b) => (a + b).ToString(),
            new McpServerToolCreateOptions { Name = "add", Description = "Adds two whole numbers." }));
        tools.Add(McpServerTool.Create(
            () => new CallToolResult { IsError = true, Content = [new TextContentBlock { Text = "it broke" }] },
            new McpServerToolCreateOptions { Name = "fail", Description = "Always reports an error." }));
        tools.Add(McpServerTool.Create(
            () => new CallToolResult { Content = [new TextContentBlock { Text = "a red dot" }, ImageContentBlock.FromBytes(RedDot, "image/png")] },
            new McpServerToolCreateOptions { Name = "picture", Description = "Returns a tiny picture." }));
        tools.Add(McpServerTool.Create(
            () =>
            {
                tools.Add(McpServerTool.Create(() => "late!", new McpServerToolCreateOptions { Name = "late_tool", Description = "Added while running." }));
                return "added";
            },
            new McpServerToolCreateOptions { Name = "add_tool", Description = "Adds another tool, then tells the client the list changed." }));
        tools.Add(McpServerTool.Create(
            () =>
            {
                Environment.Exit(3);
                return "unreachable";
            },
            new McpServerToolCreateOptions { Name = "crash", Description = "Exits the server process." }));

        var options = new McpServerOptions
        {
            ServerInfo = new Implementation { Name = "puck-test", Version = "1.0" },
            ServerInstructions = "Use echo to test the connection.",
            Capabilities = new ServerCapabilities { Tools = new ToolsCapability { ListChanged = true } },
            ToolCollection = tools,
        };
        await using var server = McpServer.Create(new StdioServerTransport("puck-test", null), options);
        await server.RunAsync();
    }
}
