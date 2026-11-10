using Microsoft.Extensions.Logging;
using Pucksley.Sdk;

namespace Pucksley.Plugins.Mcp;

public sealed class McpServerDef
{
    [Setting("Name", Placeholder = "github", Help = "Short: it's part of the tool names (mcp_<name>_<tool>).", Order = 1)]
    public string Name { get; set; } = "";

    [Setting("Enabled", Order = 2)]
    public bool Enabled { get; set; } = true;

    [Setting("Transport", Options = ["stdio", "http"], Help = "stdio: the hub runs a command. http: connect to a URL (Streamable HTTP, or SSE).", Order = 3)]
    public string Transport { get; set; } = "stdio";

    [Setting("Command", Placeholder = "npx", Help = "stdio: the program to run.", Order = 4)]
    public string Command { get; set; } = "";

    [Setting("Arguments", Multiline = true, Help = "stdio: one per line.", Placeholder = "-y\n@modelcontextprotocol/server-everything", Order = 5)]
    public string Arguments { get; set; } = "";

    [Setting("Environment", Multiline = true, Secret = true, Help = "stdio: KEY=VALUE, one per line.", Placeholder = "GITHUB_TOKEN=…", Order = 6)]
    public string Environment { get; set; } = "";

    [Setting("URL", Placeholder = "https://example.com/mcp", Help = "http: the server's endpoint.", Order = 7)]
    public string Url { get; set; } = "";

    [Setting("Headers", Multiline = true, Secret = true, Help = "http: one per line, Name: value.", Placeholder = "Authorization: Bearer …", Order = 8)]
    public string Headers { get; set; } = "";
}

public sealed class McpSettings
{
    [Setting("Servers", Help = "Each server's tools appear to Claude as mcp_<server>_<tool>.")]
    public List<McpServerDef> Servers { get; set; } = [];
}

/// <summary>
/// Model Context Protocol servers as tools: each enabled server is connected on start (in parallel),
/// and each of its tools becomes a tool named <c>mcp_&lt;server&gt;_&lt;tool&gt;</c>.
/// </summary>
public sealed class McpPlugin : PuckPlugin<McpSettings>
{
    static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(20);

    /// <summary>The least important MCP SDK message passed on to the hub log (the SDK is chatty below Warning).</summary>
    internal static LogLevel SdkLogLevel { get; set; } = LogLevel.Warning;

    readonly object _gate = new();
    IReadOnlyList<McpServerConnection> _connections = [];
    volatile IReadOnlyList<ToolDefinition> _tools = [];
    volatile string? _instructions;
    ILoggerFactory? _loggerFactory;

    public override string Id => "mcp";
    public override string Name => "MCP servers";
    public override string Description => "Connect Model Context Protocol servers (a command to run, or an HTTP URL); their tools become tools Claude can use.";
    public override string Icon => "tool";
    public override bool EnabledByDefault => true;

    public override IReadOnlyList<ToolDefinition> GetTools() => _tools;

    /// <summary>What the connected servers say about using their tools.</summary>
    public override string? Instructions => _instructions;

    public override async Task StartAsync(CancellationToken cancel)
    {
        _loggerFactory ??= new ForwardingLoggerFactory(Log, SdkLogLevel);
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var connections = (Settings.Servers ?? [])
            .Where(s => s.Enabled && !IsBlank(s))
            .Select(s => new McpServerConnection(s, McpToolMapping.ServerKey(s.Name, keys), ConnectTimeout, _loggerFactory, Log, Rebuild))
            .ToList();
        lock (_gate) _connections = connections;

        if (connections.Count > 0)
        {
            SetStatus(StatusLevel.Ok, $"Connecting to {TextUtil.Plural(connections.Count, "server")}…");
            await Task.WhenAll(connections.Select(c => c.ConnectAsync(cancel))).ConfigureAwait(false);
        }
        Rebuild(toolsChanged: true);
    }

    public override async Task StopAsync()
    {
        IReadOnlyList<McpServerConnection> connections;
        lock (_gate)
        {
            connections = _connections;
            _connections = [];
        }
        _tools = [];
        _instructions = null;
        await Task.WhenAll(connections.Select(c => c.DisposeAsync().AsTask())).ConfigureAwait(false);
        Context.ToolsChanged();
        SetStatus(PluginStatus.Idle);
    }

    public override IReadOnlyList<PluginAction> GetActions() =>
        [new PluginAction("reconnect", "Reconnect", "Reconnect to every enabled server and refresh its tools.")];

    public override async Task<ActionResult> RunActionAsync(string actionId, IReadOnlyDictionary<string, string> input, CancellationToken cancel)
    {
        if (actionId != "reconnect") return await base.RunActionAsync(actionId, input, cancel);
        IReadOnlyList<McpServerConnection> connections;
        lock (_gate) connections = _connections;
        if (connections.Count == 0) return new ActionResult("No MCP servers are enabled.", false);
        await Task.WhenAll(connections.Select(c => c.ConnectAsync(cancel))).ConfigureAwait(false);
        Rebuild(toolsChanged: true);
        return new ActionResult(Status.Message, Status.Level != StatusLevel.Error);
    }

    /// <summary>Rebuilds the tool list and status from the servers (after start, reconnects and tools-changed notifications).</summary>
    void Rebuild(bool toolsChanged)
    {
        lock (_gate)
        {
            var used = new HashSet<string>(StringComparer.Ordinal);
            var tools = new List<ToolDefinition>();
            var instructions = new List<string>();
            foreach (var connection in _connections)
            {
                foreach (var tool in connection.Tools)
                {
                    var (c, toolName) = (connection, tool.Name);
                    tools.Add(new ToolDefinition
                    {
                        Name = McpToolMapping.ToolName(c.Key, toolName, used),
                        Description = McpToolMapping.Description(c.Name, tool),
                        InputSchema = McpToolMapping.InputSchema(tool.InputSchema),
                        // The server says this tool is destructive: ask on the puck first.
                        Confirm = tool.Annotations is { DestructiveHint: true, ReadOnlyHint: not true },
                        Icon = Icon,
                        Handler = inv => c.CallAsync(toolName, inv.Arguments, inv.Cancellation),
                    });
                }
                if (connection.Instructions is { } text && !string.IsNullOrWhiteSpace(text))
                    instructions.Add($"{connection.Name} (MCP server): {TextUtil.Truncate(text.Trim(), 600)}");
            }
            if (toolsChanged) _tools = tools;
            _instructions = instructions.Count == 0 ? null : string.Join("\n", instructions);
            UpdateStatus(_connections, _tools.Count);
        }
        if (toolsChanged) Context.ToolsChanged();
    }

    void UpdateStatus(IReadOnlyList<McpServerConnection> connections, int toolCount)
    {
        if (connections.Count == 0)
        {
            SetStatus(StatusLevel.Ok, "No servers yet");
            return;
        }
        var failed = connections.Where(c => !c.Connected).ToList();
        var problems = string.Join("; ", failed.Select(c => $"{c.Name}: {c.Error ?? "not connected"}"));
        var summary = $"{TextUtil.Plural(connections.Count - failed.Count, "server")}, {TextUtil.Plural(toolCount, "tool")}";
        if (failed.Count == 0) SetStatus(StatusLevel.Ok, summary);
        else if (failed.Count == connections.Count) SetStatus(StatusLevel.Error, problems);
        else SetStatus(StatusLevel.Warning, $"{summary}; {problems}");
    }

    internal static bool IsHttp(McpServerDef def) =>
        (def.Transport ?? "").Trim().ToLowerInvariant() switch
        {
            "http" or "https" or "sse" or "streamable-http" or "streamable http" => true,
            "stdio" => false,
            _ => string.IsNullOrWhiteSpace(def.Command) && !string.IsNullOrWhiteSpace(def.Url),
        };

    static bool IsBlank(McpServerDef def) =>
        string.IsNullOrWhiteSpace(def.Name) && string.IsNullOrWhiteSpace(def.Command) && string.IsNullOrWhiteSpace(def.Url);
}
