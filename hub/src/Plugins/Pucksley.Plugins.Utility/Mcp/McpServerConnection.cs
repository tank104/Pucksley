using System.ComponentModel;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Pucksley.Sdk;

namespace Pucksley.Plugins.Mcp;

/// <summary>
/// One configured MCP server: connects (stdio or HTTP), lists its tools, calls them, follows its
/// tools-changed notifications, and reconnects once on the next call after it dropped.
/// </summary>
internal sealed class McpServerConnection : IAsyncDisposable
{
    const int StderrLinesKept = 5;

    readonly McpServerDef _def;
    readonly TimeSpan _connectTimeout;
    readonly ILoggerFactory _loggerFactory;
    readonly ILogger _log;
    readonly Action<bool> _changed; // true when the tool list changed, false for status only
    readonly SemaphoreSlim _connectLock = new(1, 1);
    readonly Queue<string> _stderr = new();
    McpClient? _client;
    IAsyncDisposable? _transport;
    CancellationTokenSource? _listening; // ends the subscriptions/listen stream
    volatile IReadOnlyList<Tool> _tools = [];
    volatile bool _broken;
    volatile bool _disposed;

    public McpServerConnection(McpServerDef def, string key, TimeSpan connectTimeout, ILoggerFactory loggerFactory, ILogger log, Action<bool> changed)
    {
        _def = def;
        Key = key;
        _connectTimeout = connectTimeout;
        _loggerFactory = loggerFactory;
        _log = log;
        _changed = changed;
    }

    /// <summary>The server's part of tool names: <c>mcp_{Key}_{tool}</c>.</summary>
    public string Key { get; }

    public string Name => string.IsNullOrWhiteSpace(_def.Name) ? Key : _def.Name.Trim();

    /// <summary>The last tool list the server gave (kept after a drop, so a call can trigger the reconnect).</summary>
    public IReadOnlyList<Tool> Tools => _tools;

    /// <summary>Why it isn't connected, for the status line.</summary>
    public string? Error { get; private set; }

    public bool Connected => _client is { } c && !c.Completion.IsCompleted && !_broken;

    public string? Instructions => Connected ? _client?.ServerInstructions : null;

    public async Task ConnectAsync(CancellationToken cancel)
    {
        await _connectLock.WaitAsync(cancel).ConfigureAwait(false);
        try { await ConnectCoreAsync(cancel).ConfigureAwait(false); }
        finally { _connectLock.Release(); }
    }

    public async Task<ToolResult> CallAsync(string toolName, JsonObject arguments, CancellationToken cancel)
    {
        McpClient? client;
        try
        {
            client = await ReadyClientAsync(cancel).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return ToolResult.Error("Cancelled.");
        }
        if (client is null) return ToolResult.Error($"The {Name} MCP server isn't connected ({Error ?? "not started"}).");

        try
        {
            var request = new CallToolRequestParams { Name = toolName, Arguments = McpToolMapping.ToArguments(arguments) };
            var result = await client.CallToolAsync(request, cancel).ConfigureAwait(false);
            return McpToolMapping.ToToolResult(result);
        }
        catch (McpProtocolException e) // the server answered with a JSON-RPC error: the connection is fine
        {
            return ToolResult.Error($"{Name}: {e.Message}");
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            return ToolResult.Error("Cancelled.");
        }
        catch (Exception e)
        {
            // The connection broke mid-call. The call may already have run, so don't repeat it; the next call reconnects.
            _broken = true;
            Error = Describe(e) + StderrHint();
            _log.LogWarning(e, "MCP server {Server} failed during {Tool}.", Name, toolName);
            _changed(false);
            return ToolResult.Error($"Lost the connection to the {Name} MCP server ({Error}). Try again.");
        }
    }

    /// <summary>The client, after reconnecting once if the server dropped since the last call.</summary>
    async Task<McpClient?> ReadyClientAsync(CancellationToken cancel)
    {
        if (Connected) return _client;
        await _connectLock.WaitAsync(cancel).ConfigureAwait(false);
        try
        {
            if (!Connected && !_disposed)
            {
                _log.LogInformation("Reconnecting to MCP server {Server}.", Name);
                await ConnectCoreAsync(cancel).ConfigureAwait(false);
                _changed(Connected); // its tools may differ after a restart
            }
            return Connected ? _client : null;
        }
        finally
        {
            _connectLock.Release();
        }
    }

    async Task ConnectCoreAsync(CancellationToken cancel)
    {
        if (_disposed) return;
        await CloseAsync().ConfigureAwait(false);
        lock (_stderr) _stderr.Clear();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        cts.CancelAfter(_connectTimeout);
        McpClient? client = null;
        try
        {
            var transport = CreateTransport();
            _transport = transport as IAsyncDisposable;
            var options = new McpClientOptions
            {
                ClientInfo = new Implementation { Name = "Pucksley", Version = "1.0" },
                InitializationTimeout = _connectTimeout,
                Handlers = new McpClientHandlers
                {
                    NotificationHandlers = [new(NotificationMethods.ToolListChangedNotification, OnToolListChanged)],
                },
            };
            client = await McpClient.CreateAsync(transport, options, _loggerFactory, cts.Token).ConfigureAwait(false);
            List<Tool> tools = client.ServerCapabilities?.Tools is null
                ? []
                : (await client.ListToolsAsync(cancellationToken: cts.Token).ConfigureAwait(false)).Select(t => t.ProtocolTool).ToList();
            (_client, client) = (client, null); // ours now
            _tools = tools;
            _broken = false;
            Error = null;
            ListenForToolChanges(_client);
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
        {
            Error = $"no answer within {_connectTimeout.TotalSeconds:0} s" + StderrHint();
            _log.LogWarning("MCP server {Server} didn't answer within {Seconds} s.", Name, _connectTimeout.TotalSeconds);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Error = Describe(e) + StderrHint();
            _log.LogWarning(e, "Couldn't connect to MCP server {Server}.", Name);
        }
        finally
        {
            if (client is not null) await DisposeQuietlyAsync(client).ConfigureAwait(false);
            if (_client is null) await CloseAsync().ConfigureAwait(false);
        }
    }

    IClientTransport CreateTransport()
    {
        if (McpPlugin.IsHttp(_def))
        {
            if (!Uri.TryCreate(_def.Url?.Trim(), UriKind.Absolute, out var endpoint) || endpoint.Scheme is not ("http" or "https"))
                throw new PluginException("set its URL (http:// or https://)");
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (key, value) in TextUtil.ParseHeaders(_def.Headers)) headers[key] = value;
            return new HttpClientTransport(new HttpClientTransportOptions
            {
                Endpoint = endpoint,
                Name = Name,
                TransportMode = HttpTransportMode.AutoDetect, // Streamable HTTP, falling back to SSE
                ConnectionTimeout = _connectTimeout,
                AdditionalHeaders = headers,
            }, _loggerFactory);
        }

        if (string.IsNullOrWhiteSpace(_def.Command)) throw new PluginException("set the command to run (e.g. npx)");
        var environment = TextUtil.ParseKeyValues(_def.Environment);
        return new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = Name,
            Command = _def.Command.Trim(),
            Arguments = TextUtil.Lines(_def.Arguments),
            EnvironmentVariables = environment.ToDictionary(kv => kv.Key, kv => (string?)kv.Value, environment.Comparer),
            StandardErrorLines = OnStderrLine,
            ShutdownTimeout = TimeSpan.FromSeconds(3),
        }, _loggerFactory);
    }

    /// <summary>
    /// Servers on protocol 2026-07-28 or later only send tools/list_changed to a client holding a
    /// subscriptions/listen request open (earlier revisions broadcast it to every session). The SDK has no
    /// helper for it yet, so open one and leave it running until the connection closes.
    /// </summary>
    void ListenForToolChanges(McpClient client)
    {
        if (client.ServerCapabilities?.Tools?.ListChanged != true) return;
        if (string.CompareOrdinal(client.NegotiatedProtocolVersion, "2026-07-28") < 0) return; // dates compare as strings
        _listening = new CancellationTokenSource();
        var stop = _listening.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await client.SendRequestAsync<SubscriptionsListenRequestParams, EmptyResult>(
                    RequestMethods.SubscriptionsListen,
                    new SubscriptionsListenRequestParams { Notifications = new SubscriptionsListenNotifications { ToolsListChanged = true } },
                    McpJsonUtilities.DefaultOptions,
                    cancellationToken: stop).ConfigureAwait(false);
            }
            catch (Exception e) when (!stop.IsCancellationRequested)
            {
                // The server ended the stream or doesn't support it: tools are still refreshed on reconnect.
                _log.LogDebug(e, "MCP server {Server} closed its tools-changed subscription.", Name);
            }
            catch (Exception)
            {
                // closed on purpose
            }
        }, CancellationToken.None);
    }

    ValueTask OnToolListChanged(JsonRpcNotification notification, CancellationToken cancel)
    {
        // Don't call back into the server from inside its own notification: refresh on the side.
        _ = Task.Run(RefreshToolsAsync, CancellationToken.None);
        return ValueTask.CompletedTask;
    }

    async Task RefreshToolsAsync()
    {
        var client = _client;
        if (client is null || _disposed) return;
        try
        {
            using var cts = new CancellationTokenSource(_connectTimeout);
            var tools = await client.ListToolsAsync(cancellationToken: cts.Token).ConfigureAwait(false);
            _tools = tools.Select(t => t.ProtocolTool).ToList();
            _log.LogInformation("MCP server {Server} now has {Count} tools.", Name, _tools.Count);
            _changed(true);
        }
        catch (Exception e)
        {
            _log.LogWarning(e, "Couldn't refresh the tools of MCP server {Server}.", Name);
        }
    }

    void OnStderrLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        _log.LogDebug("MCP {Server} stderr: {Line}", Name, line);
        lock (_stderr)
        {
            _stderr.Enqueue(line.Trim());
            while (_stderr.Count > StderrLinesKept) _stderr.Dequeue();
        }
    }

    /// <summary>" — npx: command not found": the server's last stderr lines, which usually say what went wrong.</summary>
    string StderrHint()
    {
        lock (_stderr)
            return _stderr.Count == 0 ? "" : " — " + TextUtil.Truncate(string.Join(" ", _stderr.TakeLast(2)), 240);
    }

    string Describe(Exception e) => e switch
    {
        PluginException p => p.Message,
        ClientTransportClosedException { Details: StdioClientCompletionDetails { ExitCode: int code } } => $"the server exited (code {code})",
        ClientTransportClosedException => "the server closed the connection",
        Win32Exception w => $"couldn't start '{_def.Command?.Trim()}': {w.Message}",
        HttpRequestException h => $"couldn't reach it: {h.Message}",
        _ when e.InnerException is Win32Exception w => $"couldn't start '{_def.Command?.Trim()}': {w.Message}",
        _ => e.Message,
    };

    async Task CloseAsync()
    {
        var (client, transport, listening) = (_client, _transport, _listening);
        (_client, _transport, _listening) = (null, null, null);
        if (listening is not null)
        {
            listening.Cancel();
            listening.Dispose();
        }
        if (client is not null) await DisposeQuietlyAsync(client).ConfigureAwait(false);
        if (transport is not null) await DisposeQuietlyAsync(transport).ConfigureAwait(false);
    }

    async Task DisposeQuietlyAsync(IAsyncDisposable disposable)
    {
        try { await disposable.DisposeAsync().ConfigureAwait(false); }
        catch (Exception e) { _log.LogDebug(e, "Closing MCP server {Server}.", Name); }
    }

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        await _connectLock.WaitAsync().ConfigureAwait(false);
        try { await CloseAsync().ConfigureAwait(false); }
        finally { _connectLock.Release(); }
    }
}
