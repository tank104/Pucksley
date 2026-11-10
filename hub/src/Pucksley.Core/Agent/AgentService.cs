using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Pucksley.Core.Plugins;
using Pucksley.Core.Settings;
using Pucksley.Core.Web;
using Pucksley.Sdk;

namespace Pucksley.Core.Agent;

/// <summary>How a turn reports back to whoever asked (a puck's voice session, or the web page).</summary>
public sealed class AgentCallbacks
{
    /// <summary>Streamed answer text.</summary>
    public Action<string>? OnText { get; init; }

    /// <summary>A tool started / finished: icon, short text, state (run/ok/err).</summary>
    public Action<string, string, string>? OnActivity { get; init; }

    /// <summary>Ask the person to confirm something drastic. Null = allowed (typed in the web page).</summary>
    public Func<string, Task<bool>>? Confirm { get; init; }
}

/// <summary>
/// The assistant: builds the system prompt and each turn's context, keeps one conversation per
/// puck (or web page), and runs turns on the chat model with the plugins' tools.
/// </summary>
public sealed class AgentService
{
    readonly SettingsStore _settings;
    readonly PluginManager _plugins;
    readonly IChatModel _model;
    readonly HubEvents _events;
    readonly ILogger<AgentService> _log;
    readonly Func<IHubRuntime> _runtime;
    readonly ConcurrentDictionary<string, Conversation> _conversations = new();

    sealed class Conversation(IChatSession session, string fingerprint)
    {
        public IChatSession Session { get; } = session;
        public string Fingerprint { get; } = fingerprint;
        public DateTimeOffset LastUsed { get; set; } = DateTimeOffset.UtcNow;
    }

    public AgentService(SettingsStore settings, PluginManager plugins, IChatModel model, HubEvents events, ILogger<AgentService> log, Func<IHubRuntime> runtime)
    {
        _settings = settings;
        _plugins = plugins;
        _model = model;
        _events = events;
        _log = log;
        _runtime = runtime;
    }

    /// <summary>The API key to use: settings, else ANTHROPIC_API_KEY.</summary>
    public string? ApiKey
    {
        get
        {
            var k = _settings.Current.Assistant.AnthropicApiKey;
            return string.IsNullOrWhiteSpace(k) ? Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY") : k.Trim();
        }
    }

    /// <summary>Forget a conversation (e.g. the person said "never mind" or "start over").</summary>
    public void Reset(string sessionKey) => _conversations.TryRemove(sessionKey, out _);

    /// <summary>Runs one turn. Never throws for model or tool failures (they come back as a failed result).</summary>
    public async Task<TurnResult> AskAsync(string sessionKey, string userText, ToolCaller caller, AgentCallbacks callbacks, CancellationToken cancel)
    {
        var s = _settings.Current;
        var key = ApiKey;
        if (string.IsNullOrEmpty(key))
            return TurnResult.Error("I need a Claude API key first. Add one on the hub's Assistant page.");

        var system = BuildSystemPrompt(s);
        var tools = _plugins.Tools;
        var city = CityOf(s.Household.Location);
        var country = CountryCode(s.Household.Location);
        var timeZone = _runtime().TimeZone.Id.Contains('/') ? _runtime().TimeZone.Id : null;
        // A new conversation when the model, prompt or tools changed, or after a quiet spell. "Tools"
        // means everything sent in `tools` (descriptions and schemas too, and the web tools with their
        // location), not just the names: history replayed against a different system prompt or tool
        // list misses the cache and, on newer accounts, gets its thinking blocks rejected.
        var fingerprint = Fingerprint(s.Assistant.Model.Trim(), system, tools, s.Assistant.WebSearch ? $"web|{city}|{country}|{timeZone}" : "no-web");
        var timeout = TimeSpan.FromMinutes(Math.Max(1, s.Assistant.ConversationTimeoutMinutes));
        var conv = _conversations.AddOrUpdate(sessionKey,
            _ => new Conversation(_model.CreateSession(), fingerprint),
            (_, existing) => existing.Fingerprint != fingerprint || DateTimeOffset.UtcNow - existing.LastUsed > timeout || existing.Session.Turns >= 24
                ? new Conversation(_model.CreateSession(), fingerprint)
                : existing);
        conv.LastUsed = DateTimeOffset.UtcNow;

        var context = await BuildContextAsync(caller, cancel).ConfigureAwait(false);
        var user = $"[Context]\n{context}\n[/Context]\n\n{userText}";

        _events.Conversation(sessionKey, caller.PuckName ?? "web", "user", userText);
        var request = new TurnRequest
        {
            SystemPrompt = system,
            UserText = user,
            Tools = tools,
            ApiKey = key,
            Model = s.Assistant.Model.Trim(),
            Effort = s.Assistant.Effort,
            MaxTokens = Math.Clamp(s.Assistant.MaxTokens, 1024, 64000),
            MaxToolRounds = Math.Clamp(s.Assistant.MaxToolRounds, 1, 25),
            WebSearch = s.Assistant.WebSearch,
            City = city,
            Country = country,
            TimeZone = timeZone,
        };

        var started = DateTimeOffset.UtcNow;
        var result = await conv.Session.RunTurnAsync(request, new Callbacks(this, caller, callbacks, sessionKey), cancel).ConfigureAwait(false);
        conv.LastUsed = DateTimeOffset.UtcNow;
        _events.Conversation(sessionKey, caller.PuckName ?? "web", result.Failed ? "error" : "assistant", result.Text);
        _log.LogInformation("Turn for {Who} took {Ms} ms{Failed}", caller.PuckName ?? "web", (int)(DateTimeOffset.UtcNow - started).TotalMilliseconds, result.Failed ? " (failed)" : "");
        return result;
    }

    sealed class Callbacks(AgentService owner, ToolCaller caller, AgentCallbacks cb, string sessionKey) : ITurnCallbacks
    {
        public void OnText(string delta) => cb.OnText?.Invoke(delta);

        public void OnToolStarted(string toolName, JsonObject? input)
        {
            var (icon, text) = owner.Describe(toolName, input);
            cb.OnActivity?.Invoke(icon, text, "run");
            owner._events.Conversation(sessionKey, caller.PuckName ?? "web", "tool", input is null ? toolName : $"{toolName} {input.ToJsonString()}");
        }

        public async Task<ToolResult> ExecuteToolAsync(string toolName, JsonObject input, CancellationToken cancel)
        {
            var result = await owner._plugins.ExecuteToolAsync(toolName, input, caller, cb.Confirm, cancel).ConfigureAwait(false);
            var (icon, text) = owner.Describe(toolName, input);
            var shown = !string.IsNullOrWhiteSpace(result.Activity) ? result.Activity! : result.IsError ? $"{text} failed" : text;
            cb.OnActivity?.Invoke(icon, shown, result.IsError ? "err" : "ok");
            owner._events.Conversation(sessionKey, caller.PuckName ?? "web", result.IsError ? "tool-error" : "tool-result",
                result.Text.Length > 500 ? result.Text[..500] + "…" : result.Text);
            return result;
        }
    }

    /// <summary>An icon and a few words for the puck's activity line while a tool runs.</summary>
    (string Icon, string Text) Describe(string toolName, JsonObject? input)
    {
        if (toolName is "web_search")
        {
            var q = input?["query"]?.GetValue<string>();
            return ("search", q is null ? "Searching the web" : $"Searching: {Shorten(q, 30)}");
        }
        if (toolName is "web_fetch") return ("search", "Reading a web page");
        var tool = _plugins.Tools.FirstOrDefault(t => t.Name == toolName);
        var plugin = tool?.PluginId is { } pid ? _plugins.Find(pid)?.Plugin : null;
        var icon = tool?.Icon ?? plugin?.Icon ?? "tool";
        // "sonos_set_volume" → "Set volume"
        var verb = toolName;
        if (plugin is not null && verb.StartsWith(plugin.Id.Replace('-', '_') + "_", StringComparison.Ordinal))
            verb = verb[(plugin.Id.Length + 1)..];
        verb = verb.Replace('_', ' ');
        verb = verb.Length > 0 ? char.ToUpperInvariant(verb[0]) + verb[1..] : verb;
        var name = plugin?.Name ?? "";
        var text = name.Length > 0 && name.Length + verb.Length < 26 ? $"{name}: {verb}" : verb;
        return (icon, Shorten(text, 40));
    }

    static string Shorten(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    static string Fingerprint(string model, string system, IReadOnlyList<ToolDefinition> tools, string web)
    {
        var sb = new StringBuilder(model).Append('\u001e').Append(system).Append('\u001e').Append(web);
        foreach (var t in tools) sb.Append('\u001e').Append(t.Name).Append('\u001f').Append(t.Description).Append('\u001f').Append(t.InputSchema.ToJsonString());
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    /// <summary>The stable part of the prompt (cached by the API): persona, style, household, plugin guidance, memory.</summary>
    internal string BuildSystemPrompt(HubSettings s)
    {
        var a = s.Assistant;
        var name = string.IsNullOrWhiteSpace(a.AssistantName) ? "Claude" : a.AssistantName.Trim();
        var sb = new StringBuilder();
        sb.AppendLine($"You are {name}, a voice assistant that lives in the household's \"puck\": a small round device with a microphone, a little speaker and a round 1.75-inch screen. People talk to you out loud and hear your answer through the puck's speaker while the screen shows your words.");
        sb.AppendLine();
        sb.AppendLine("How to answer:");
        sb.AppendLine("- Talk like a warm, capable person in a real conversation. Keep it short: usually one to three sentences, unless someone asks for more detail, a story or step-by-step help.");
        sb.AppendLine("- Everything you write is spoken aloud. Use plain sentences only: no markdown, bullet points, headings, tables, emoji or code (unless code is asked for). Don't read out URLs; name the site instead. Write numbers, times and units the way you'd say them.");
        sb.AppendLine("- What you hear comes from speech recognition, so words can be wrong or missing. Work out what was meant; if it's genuinely unclear, ask one short question.");
        sb.AppendLine("- For news, weather, prices, scores, opening hours or anything else that changes, look it up (tools or web search) instead of guessing, and say briefly where it came from if it matters.");
        sb.AppendLine("- When you've done something with a tool, confirm it in a few words (\"Done, the kitchen's at 30.\"). Don't describe the tool or repeat raw data.");
        sb.AppendLine("- If something fails, say so plainly and offer one next step.");
        sb.AppendLine("- Ask before anything drastic or hard to undo unless the person clearly asked for it.");
        sb.AppendLine("- If the person says thanks or goodbye, reply briefly and don't ask a follow-up question.");
        sb.AppendLine();
        sb.AppendLine("You can control the home and the hub's computer with tools. Each message from the person begins with a [Context] block written by the system, not the person: the time, which puck and room they're speaking to, and live state such as what's playing. Use it to resolve \"here\", \"this room\", \"it\" and \"this song\", and to answer questions about the home without calling a tool when the context already has the answer.");

        var h = s.Household;
        if (!string.IsNullOrWhiteSpace(h.Location)) sb.AppendLine().AppendLine($"The household is in {h.Location.Trim()}.");
        if (!string.IsNullOrWhiteSpace(h.Notes)) sb.AppendLine().AppendLine("About the household:").AppendLine(h.Notes.Trim());
        if (!string.IsNullOrWhiteSpace(a.ExtraInstructions)) sb.AppendLine().AppendLine("Extra instructions from the household:").AppendLine(a.ExtraInstructions.Trim());

        var guidance = _plugins.Instructions;
        if (guidance.Count > 0)
        {
            sb.AppendLine().AppendLine("Notes on your tools:");
            foreach (var (pluginName, text) in guidance) sb.AppendLine($"[{pluginName}] {text}");
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>The per-turn context: time, place, the puck, and the plugins' live state.</summary>
    async Task<string> BuildContextAsync(ToolCaller caller, CancellationToken cancel)
    {
        var tz = _runtime().TimeZone;
        var now = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, tz);
        var culture = CultureInfo.GetCultureInfo("en-NZ");
        var sb = new StringBuilder();
        sb.Append("Now: ").Append(now.ToString("dddd d MMMM yyyy, h:mm tt", culture)).Append(" (").Append(tz.Id).AppendLine(")");
        if (caller.PuckId is null) sb.AppendLine("Typed in the hub's web page (no puck; the answer is shown, not spoken).");
        else
        {
            sb.Append("Speaking to: ").Append(caller.PuckName ?? "a puck");
            if (!string.IsNullOrWhiteSpace(caller.Room)) sb.Append(", in the ").Append(caller.Room);
            sb.AppendLine();
        }
        try
        {
            var parts = await _plugins.GatherContextAsync(caller, TimeSpan.FromMilliseconds(900), cancel).ConfigureAwait(false);
            foreach (var p in parts) sb.AppendLine(p);
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested) { }
        return sb.ToString().TrimEnd();
    }

    internal static string? CityOf(string location)
    {
        if (string.IsNullOrWhiteSpace(location)) return null;
        return location.Split(',')[0].Trim();
    }

    internal static string? CountryCode(string location)
    {
        var l = location.ToLowerInvariant();
        if (l.Contains("new zealand") || l.EndsWith(" nz") || l.EndsWith(",nz")) return "NZ";
        if (l.Contains("australia")) return "AU";
        if (l.Contains("united kingdom") || l.EndsWith(" uk")) return "GB";
        if (l.Contains("united states") || l.EndsWith(" usa") || l.EndsWith(" us")) return "US";
        if (l.Contains("canada")) return "CA";
        return null;
    }
}
