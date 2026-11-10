using System.Net.Http.Headers;
using System.Text;
using Pucksley.Sdk;

namespace Pucksley.Plugins.Actions;

public sealed class CustomActionsSettings
{
    [Setting("HTTP actions", Help = "Each becomes a tool named action_<name> that calls a URL. {{param}} is replaced by the value Claude gives: URL-encoded in the URL, JSON-escaped in a JSON body.", Order = 1)]
    public List<HttpActionDef> HttpActions { get; set; } = [];

    [Setting("Script actions", Help = "Each becomes a tool named action_<name> that runs a command. {{param}} is replaced by the value, safely quoted for the shell; values are also in environment variables PUCK_<NAME>.", Order = 2)]
    public List<ScriptActionDef> ScriptActions { get; set; } = [];
}

public sealed class HttpActionDef
{
    [Setting("Name", Placeholder = "garage_door", Order = 1)]
    public string Name { get; set; } = "";

    [Setting("Description", Help = "What it does and when to use it: this is what Claude reads to decide.", Placeholder = "Open or close the garage door.", Order = 2)]
    public string Description { get; set; } = "";

    [Setting("Parameters", Multiline = true, Help = "One per line: name:type:description (type string, number, integer or boolean; end the name with ? if optional, e.g. level?:integer:0-100).", Placeholder = "action:string:open or close", Order = 3)]
    public string Parameters { get; set; } = "";

    [Setting("Method", Options = ["GET", "POST", "PUT", "PATCH", "DELETE"], Order = 4)]
    public string Method { get; set; } = "POST";

    [Setting("URL", Placeholder = "http://192.168.1.40/api/door/{{action}}", Order = 5)]
    public string Url { get; set; } = "";

    [Setting("Headers", Multiline = true, Secret = true, Help = "One per line: Name: value.", Placeholder = "Authorization: Bearer …", Order = 6)]
    public string Headers { get; set; } = "";

    [Setting("Body", Multiline = true, Placeholder = "{\"state\": \"{{action}}\"}", Order = 7)]
    public string Body { get; set; } = "";

    [Setting("Ask first", Help = "Confirm on the puck before running it.", Order = 8)]
    public bool Confirm { get; set; }
}

public sealed class ScriptActionDef
{
    [Setting("Name", Placeholder = "lock_pc", Order = 1)]
    public string Name { get; set; } = "";

    [Setting("Description", Help = "What it does and when to use it: this is what Claude reads to decide.", Placeholder = "Lock this PC's screen.", Order = 2)]
    public string Description { get; set; } = "";

    [Setting("Parameters", Multiline = true, Help = "One per line: name:type:description (type string, number, integer or boolean; end the name with ? if optional).", Order = 3)]
    public string Parameters { get; set; } = "";

    [Setting("Shell", Options = ["powershell", "pwsh", "cmd", "bash", "sh"], Order = 4)]
    public string Shell { get; set; } = OperatingSystem.IsWindows() ? "powershell" : "sh";

    [Setting("Command", Multiline = true, Help = "Write {{param}} where a value goes, without adding quotes of your own (they're added for you).", Placeholder = "rundll32.exe user32.dll,LockWorkStation", Order = 5)]
    public string Command { get; set; } = "";

    [Setting("Ask first", Help = "Confirm on the puck before running it.", Order = 6)]
    public bool Confirm { get; set; }

    [Setting("Timeout (seconds)", Order = 7, Advanced = true)]
    public int TimeoutSeconds { get; set; } = 30;
}

/// <summary>
/// No-code tools from settings: HTTP calls (webhooks, Home Assistant, Node-RED, IFTTT) and shell
/// commands. Each action becomes a tool <c>action_&lt;name&gt;</c> with a JSON schema built from its parameter lines.
/// </summary>
public sealed class CustomActionsPlugin : PuckPlugin<CustomActionsSettings>
{
    static readonly TimeSpan HttpTimeout = TimeSpan.FromSeconds(20);
    const int MaxResponseBytes = 64 * 1024;

    volatile IReadOnlyList<ToolDefinition> _tools = [];
    volatile IReadOnlyList<string> _problems = [];

    public override string Id => "actions";
    public override string Name => "Custom actions";
    public override string Description => "Your own tools, no code needed: call a URL (webhooks, Home Assistant, Node-RED, IFTTT) or run a PowerShell or shell command.";
    public override string Icon => "tool";
    public override bool EnabledByDefault => true;

    public override IReadOnlyList<ToolDefinition> GetTools() => _tools;

    protected override void OnSettingsApplied()
    {
        var (tools, problems) = BuildTools(Settings);
        _tools = tools;
        _problems = problems;
        if (Context is not null)
        {
            UpdateStatus();
            Context.ToolsChanged();
        }
    }

    public override Task StartAsync(CancellationToken cancel)
    {
        UpdateStatus();
        return Task.CompletedTask;
    }

    void UpdateStatus()
    {
        var count = _tools.Count;
        var summary = count == 0 ? "No actions yet" : TextUtil.Plural(count, "action");
        if (_problems.Count > 0) SetStatus(StatusLevel.Warning, $"{summary}; skipped {string.Join("; ", _problems)}");
        else SetStatus(StatusLevel.Ok, summary);
    }

    // ------------------------------------------------------------------ building tools

    (IReadOnlyList<ToolDefinition> Tools, IReadOnlyList<string> Problems) BuildTools(CustomActionsSettings settings)
    {
        var tools = new List<ToolDefinition>();
        var problems = new List<string>();
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var def in settings.HttpActions ?? [])
        {
            if (string.IsNullOrWhiteSpace(def.Name) && string.IsNullOrWhiteSpace(def.Url)) continue; // an empty row
            try { tools.Add(HttpTool(def, used)); }
            catch (FormatException e) { problems.Add($"'{Label(def.Name)}': {e.Message}"); }
        }
        foreach (var def in settings.ScriptActions ?? [])
        {
            if (string.IsNullOrWhiteSpace(def.Name) && string.IsNullOrWhiteSpace(def.Command)) continue;
            try { tools.Add(ScriptTool(def, used)); }
            catch (FormatException e) { problems.Add($"'{Label(def.Name)}': {e.Message}"); }
        }
        return (tools, problems);
    }

    ToolDefinition HttpTool(HttpActionDef def, HashSet<string> used)
    {
        var parameters = ActionParameters.Parse(def.Parameters);
        var method = string.IsNullOrWhiteSpace(def.Method) ? "POST" : def.Method.Trim().ToUpperInvariant();
        if (method is not ("GET" or "POST" or "PUT" or "PATCH" or "DELETE"))
            throw new FormatException($"method '{def.Method}' isn't GET, POST, PUT, PATCH or DELETE");
        if (string.IsNullOrWhiteSpace(def.Url)) throw new FormatException("needs a URL");
        if (!IsHttpUrl(ActionTemplates.Url(def.Url, SampleValues(parameters))))
            throw new FormatException("the URL must start with http:// or https://");
        var name = ToolName(def.Name, used);
        return new ToolDefinition
        {
            Name = name,
            Description = DescriptionFor(def.Description, def.Name),
            InputSchema = ActionParameters.Schema(parameters),
            Confirm = def.Confirm,
            Icon = Icon,
            Handler = inv => Guard(() => RunHttpAsync(def, method, parameters, inv)),
        };
    }

    ToolDefinition ScriptTool(ScriptActionDef def, HashSet<string> used)
    {
        var parameters = ActionParameters.Parse(def.Parameters);
        if (!ShellQuoting.TryParseShell(def.Shell, out var shell))
            throw new FormatException($"shell '{def.Shell}' isn't powershell, pwsh, cmd, bash or sh");
        if (string.IsNullOrWhiteSpace(def.Command)) throw new FormatException("needs a command");
        ShellQuoting.Render(def.Command, SampleValues(parameters), shell); // throws if a placeholder can't be quoted safely where it is
        var timeout = TimeSpan.FromSeconds(def.TimeoutSeconds <= 0 ? 30 : Math.Min(def.TimeoutSeconds, 600));
        var name = ToolName(def.Name, used);
        return new ToolDefinition
        {
            Name = name,
            Description = DescriptionFor(def.Description, def.Name),
            InputSchema = ActionParameters.Schema(parameters),
            Confirm = def.Confirm,
            Icon = Icon,
            Handler = inv => Guard(() => RunScriptAsync(def, shell, parameters, timeout, inv)),
        };
    }

    /// <summary><c>action_</c> + the name in <c>[a-z0-9_]</c>, at most 64 characters, unique.</summary>
    internal static string ToolName(string? name, ISet<string> used)
    {
        var slug = TextUtil.Slug(name);
        if (slug.Length == 0) throw new FormatException("needs a name made of letters or digits");
        var baseName = Cut("action_" + slug, 64);
        var candidate = baseName;
        for (int n = 2; !used.Add(candidate); n++)
        {
            var suffix = $"_{n}";
            candidate = Cut(baseName, 64 - suffix.Length) + suffix;
        }
        return candidate;

        static string Cut(string s, int max) => (s.Length <= max ? s : s[..max]).TrimEnd('_');
    }

    static string DescriptionFor(string? description, string? name) =>
        TextUtil.Truncate(string.IsNullOrWhiteSpace(description) ? $"Runs the custom action \"{name?.Trim()}\"." : description.Trim(), 1000);

    static Dictionary<string, string> SampleValues(IReadOnlyList<ActionParameter> parameters) =>
        parameters.ToDictionary(p => p.Name, p => p.Type == ParameterType.String ? "x" : "1", StringComparer.OrdinalIgnoreCase);

    static bool IsHttpUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";

    static string Label(string? name) => string.IsNullOrWhiteSpace(name) ? "(no name)" : name.Trim();

    static string Activity(string name) => TextUtil.Truncate(name.Trim().Replace('_', ' '), 24);

    /// <summary>Dynamic tools don't go through the SDK's exception handling, so turn failures into results here.</summary>
    static async Task<ToolResult> Guard(Func<Task<ToolResult>> run)
    {
        try
        {
            return await run().ConfigureAwait(false);
        }
        catch (PluginException e)
        {
            return ToolResult.Error(e.Message);
        }
        catch (OperationCanceledException)
        {
            return ToolResult.Error("Cancelled.");
        }
        catch (Exception e)
        {
            return ToolResult.Error($"{e.GetType().Name}: {e.Message}");
        }
    }

    // ------------------------------------------------------------------ HTTP actions

    async Task<ToolResult> RunHttpAsync(HttpActionDef def, string method, IReadOnlyList<ActionParameter> parameters, ToolInvocation inv)
    {
        var values = ActionParameters.ReadArguments(parameters, inv.Arguments);
        var url = ActionTemplates.Url(def.Url, values);
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new PluginException($"The URL for '{def.Name}' isn't valid after filling in the values.");

        var headers = ActionTemplates.Headers(def.Headers, values);
        var contentType = headers.LastOrDefault(h => h.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)).Value;
        using var request = new HttpRequestMessage(new HttpMethod(method), uri);
        if (!string.IsNullOrWhiteSpace(def.Body))
        {
            request.Content = new StringContent(ActionTemplates.Body(def.Body, values, contentType), Encoding.UTF8);
            var type = contentType ?? (ActionTemplates.LooksLikeJson(def.Body) ? "application/json; charset=utf-8" : "text/plain; charset=utf-8");
            if (MediaTypeHeaderValue.TryParse(type, out var parsed)) request.Content.Headers.ContentType = parsed;
            else request.Content.Headers.TryAddWithoutValidation("Content-Type", type);
        }
        foreach (var (key, value) in headers)
        {
            if (key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)) continue;
            if (!request.Headers.TryAddWithoutValidation(key, value)) request.Content?.Headers.TryAddWithoutValidation(key, value);
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(inv.Cancellation);
        cts.CancelAfter(HttpTimeout);
        try
        {
            using var response = await Context.Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
            var body = await ReadCappedAsync(response.Content, cts.Token).ConfigureAwait(false);
            var status = $"{(int)response.StatusCode} {response.ReasonPhrase}".TrimEnd();
            var shown = ActionTemplates.ResponseBody(body);
            var text = shown.Length == 0 ? $"HTTP {status}." : $"HTTP {status}:\n{shown}";
            return response.IsSuccessStatusCode ? ToolResult.Ok(text, Activity(def.Name)) : ToolResult.Error(text);
        }
        catch (OperationCanceledException) when (!inv.Cancellation.IsCancellationRequested)
        {
            return ToolResult.Error($"'{def.Name.Trim()}' got no answer from {uri.Host} within {HttpTimeout.TotalSeconds:0} s.");
        }
        catch (HttpRequestException e)
        {
            // Name only the host: the URL can carry keys.
            return ToolResult.Error($"Couldn't reach {uri.Host} for '{def.Name.Trim()}': {e.Message}");
        }
    }

    static async Task<string> ReadCappedAsync(HttpContent content, CancellationToken cancel)
    {
        await using var stream = await content.ReadAsStreamAsync(cancel).ConfigureAwait(false);
        var buffer = new byte[MaxResponseBytes];
        int total = 0, n;
        while (total < buffer.Length && (n = await stream.ReadAsync(buffer.AsMemory(total), cancel).ConfigureAwait(false)) > 0)
            total += n;
        Encoding encoding;
        try { encoding = content.Headers.ContentType?.CharSet is { Length: > 0 } cs ? Encoding.GetEncoding(cs.Trim('"')) : Encoding.UTF8; }
        catch (Exception e) when (e is ArgumentException or NotSupportedException) { encoding = Encoding.UTF8; }
        return encoding.GetString(buffer, 0, total);
    }

    // ------------------------------------------------------------------ script actions

    async Task<ToolResult> RunScriptAsync(ScriptActionDef def, ShellKind shell, IReadOnlyList<ActionParameter> parameters, TimeSpan timeout, ToolInvocation inv)
    {
        var values = ActionParameters.ReadArguments(parameters, inv.Arguments);
        string command;
        try { command = ShellQuoting.Render(def.Command, values, shell); }
        catch (FormatException e) { throw new PluginException($"The command for '{def.Name.Trim()}' can't be used: {e.Message}."); }
        var environment = parameters.ToDictionary(p => p.EnvironmentName, p => values[p.Name]);
        var launch = ScriptRunner.Prepare(shell, command, environment, Context.DataDirectory);
        var result = await ScriptRunner.RunAsync(launch, timeout, inv.Cancellation).ConfigureAwait(false);

        var output = result.Output.Trim();
        var error = result.Error.Trim();
        if (result.TimedOut)
            return ToolResult.Error($"'{def.Name.Trim()}' didn't finish within {timeout.TotalSeconds:0} s, so I stopped it." +
                                    (output.Length > 0 ? $" Output so far: {TextUtil.Truncate(output, 1000)}" : ""));
        if (result.ExitCode != 0)
            return ToolResult.Error($"'{def.Name.Trim()}' failed (exit code {result.ExitCode}): " +
                                    TextUtil.Truncate(error.Length > 0 ? error : output.Length > 0 ? output : "no output", 2000));
        var text = output.Length > 0 ? TextUtil.Truncate(output, 2000) : "Done.";
        if (error.Length > 0) text += $"\n(stderr: {TextUtil.Truncate(error, 500)})";
        return ToolResult.Ok(text, Activity(def.Name));
    }
}
