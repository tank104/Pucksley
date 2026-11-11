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

    /// <summary>A tool is starting, by name (web_search, think_harder, sonos_play...), before it runs.</summary>
    public Action<string>? OnToolStarted { get; init; }
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

        /// <summary>The conversation so far in plain words, for the model that takes harder questions.</summary>
        public List<(string Person, string Answer)> Transcript { get; } = [];
    }

    /// <summary>The everyday model's way to hand a question to the model for harder questions.</summary>
    public const string ThinkHarder = "think_harder";

    static readonly ToolDefinition ThinkHarderTool = new()
    {
        Name = ThinkHarder,
        Description =
            "Hand this question to a more capable model, which answers the person in your place (its answer is spoken as it's written, so you write nothing). " +
            "Use it when the question needs real thought: multi-step maths or logic, planning, weighing up options, advice about health, money or the law, explaining how or why something works, writing something, code, or when the person asks you to think carefully. " +
            "Don't use it for controlling things, timers, weather, quick facts, simple lookups or small talk. Call it straight away, before writing anything.",
        InputSchema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["question"] = new JsonObject
                {
                    ["type"] = "string",
                    ["description"] = "The person's question in full, with whatever it depends on from earlier in the conversation.",
                },
            },
            ["required"] = new JsonArray("question"),
        },
        Handler = _ => Task.FromResult(ToolResult.Error("The hub runs this itself.")),
        Icon = "note",
    };

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
        var deep = s.Assistant.DeepModel?.Trim() ?? "";
        bool canEscalate = deep.Length > 0 && !deep.Equals(s.Assistant.Model.Trim(), StringComparison.OrdinalIgnoreCase);
        IReadOnlyList<ToolDefinition> tools = canEscalate ? [.. _plugins.Tools, ThinkHarderTool] : _plugins.Tools;
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
        var turnCallbacks = new Callbacks(this, caller, callbacks, sessionKey) { Conversation = conv, Context = context, DeepModel = canEscalate ? deep : null };
        var result = await conv.Session.RunTurnAsync(request, turnCallbacks, cancel).ConfigureAwait(false);
        // Answered by the model for harder questions: that's the answer (the everyday model was told to add nothing)
        if (turnCallbacks.Escalated is { } deepAnswer && !result.Failed) result = new TurnResult(deepAnswer);
        conv.LastUsed = DateTimeOffset.UtcNow;
        if (!result.Failed)
        {
            conv.Transcript.Add((userText, result.Text));
            if (conv.Transcript.Count > 12) conv.Transcript.RemoveAt(0);
        }
        _events.Conversation(sessionKey, caller.PuckName ?? "web", result.Failed ? "error" : "assistant", result.Text);
        _log.LogInformation("Turn for {Who} took {Ms} ms{Model}{Failed}", caller.PuckName ?? "web", (int)(DateTimeOffset.UtcNow - started).TotalMilliseconds,
            turnCallbacks.Escalated is not null ? $" (handed to {deep})" : "", result.Failed ? " (failed)" : "");
        return result;
    }

    sealed class Callbacks(AgentService owner, ToolCaller caller, AgentCallbacks cb, string sessionKey) : ITurnCallbacks
    {
        public Conversation? Conversation { get; init; }
        public string Context { get; init; } = "";
        public string? DeepModel { get; init; }

        /// <summary>The answer from the model for harder questions, once it has given one.</summary>
        public string? Escalated { get; private set; }

        // After a hand-over the everyday model's own words (if any) aren't spoken
        public void OnText(string delta)
        {
            if (Escalated is null) cb.OnText?.Invoke(delta);
        }

        public void OnToolStarted(string toolName, JsonObject? input)
        {
            cb.OnToolStarted?.Invoke(toolName);
            var (icon, text) = owner.Describe(toolName, input);
            cb.OnActivity?.Invoke(icon, text, "run");
            owner._events.Conversation(sessionKey, caller.PuckName ?? "web", "tool", input is null ? toolName : $"{toolName} {input.ToJsonString()}");
        }

        public async Task<ToolResult> ExecuteToolAsync(string toolName, JsonObject input, CancellationToken cancel)
        {
            if (toolName == ThinkHarder && DeepModel is not null && Conversation is not null && Escalated is null)
            {
                var question = input["question"]?.GetValue<string>() ?? "";
                var answer = await owner.AnswerHarderAsync(Conversation, Context, question, DeepModel, caller, cb, sessionKey, cancel).ConfigureAwait(false);
                if (answer is null)
                {
                    cb.OnActivity?.Invoke("note", "Thinking it through failed", "err");
                    return ToolResult.Error("The other model couldn't answer just now. Answer the person yourself.");
                }
                Escalated = answer;
                cb.OnActivity?.Invoke("note", "Thought it through", "ok");
                return ToolResult.Ok("The more capable model has answered, and the person has already heard it, word for word: \"" + answer +
                                     "\". Your turn is over: write nothing more. (For follow-ups, that answer is what was said.)");
            }
            var result = await owner._plugins.ExecuteToolAsync(toolName, input, caller, cb.Confirm, cancel).ConfigureAwait(false);
            var (icon, text) = owner.Describe(toolName, input);
            var shown = !string.IsNullOrWhiteSpace(result.Activity) ? result.Activity! : result.IsError ? $"{text} failed" : text;
            cb.OnActivity?.Invoke(icon, shown, result.IsError ? "err" : "ok");
            owner._events.Conversation(sessionKey, caller.PuckName ?? "web", result.IsError ? "tool-error" : "tool-result",
                result.Text.Length > 500 ? result.Text[..500] + "…" : result.Text);
            return result;
        }
    }

    /// <summary>
    /// A harder question, answered by the bigger model in a one-off conversation: the system
    /// prompt, the context, the conversation so far in plain words, and web search (not the home's
    /// tools). Its text streams straight to whoever asked. Null when it fails.
    /// </summary>
    async Task<string?> AnswerHarderAsync(Conversation conv, string context, string question, string model, ToolCaller caller, AgentCallbacks cb, string sessionKey, CancellationToken cancel)
    {
        var s = _settings.Current;
        var sb = new StringBuilder();
        sb.Append("[Context]\n").Append(context).Append("\n[/Context]\n\n");
        sb.AppendLine("(You were handed this question because it needs careful thought. You have web search here but not the home's other tools. Your answer is spoken aloud as you write it: plain sentences, no headings, lists or bold, and as short as the question allows; offer more detail rather than giving it all.)");
        if (conv.Transcript.Count > 0)
        {
            sb.AppendLine().AppendLine("The conversation so far:");
            foreach (var (person, answer) in conv.Transcript.TakeLast(6)) sb.Append("Person: ").AppendLine(person).Append("You: ").AppendLine(answer);
        }
        sb.AppendLine().Append(string.IsNullOrWhiteSpace(question) ? "(the person's last question)" : question.Trim());
        var request = new TurnRequest
        {
            SystemPrompt = BuildSystemPrompt(s),
            UserText = sb.ToString(),
            Tools = [],
            ApiKey = ApiKey ?? "",
            Model = model,
            Effort = s.Assistant.Effort,
            MaxTokens = Math.Clamp(s.Assistant.MaxTokens, 1024, 64000),
            MaxToolRounds = 3,
            WebSearch = s.Assistant.WebSearch,
            QuickWebSearch = true,
            City = CityOf(s.Household.Location),
            Country = CountryCode(s.Household.Location),
            TimeZone = _runtime().TimeZone.Id.Contains('/') ? _runtime().TimeZone.Id : null,
        };
        var started = DateTimeOffset.UtcNow;
        double firstWordsMs = -1;
        var timed = new AgentCallbacks
        {
            OnText = d =>
            {
                if (firstWordsMs < 0) firstWordsMs = (DateTimeOffset.UtcNow - started).TotalMilliseconds;
                cb.OnText?.Invoke(d);
            },
            OnActivity = cb.OnActivity,
            Confirm = cb.Confirm,
            OnToolStarted = cb.OnToolStarted,
        };
        var result = await _model.CreateSession().RunTurnAsync(request, new Callbacks(this, caller, timed, sessionKey), cancel).ConfigureAwait(false);
        _log.LogInformation("Harder question for {Who} answered by {Model}: first words after {First:0} ms, all in {Ms} ms{Failed}", caller.PuckName ?? "web", model,
            firstWordsMs, (int)(DateTimeOffset.UtcNow - started).TotalMilliseconds, result.Failed ? " (failed)" : "");
        return result.Failed || string.IsNullOrWhiteSpace(result.Text) ? null : result.Text.Trim();
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
        if (toolName is ThinkHarder) return ("note", "Thinking it through");
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
        sb.AppendLine("- When you need a tool or a web search, call it straight away without writing anything first: no notes to yourself and no \"let me check\" (the puck says that for you). Everything you write is spoken.");
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
