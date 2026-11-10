using System.Text;
using System.Text.Json.Nodes;

namespace Pucksley.Plugins.Orca;

/// <summary>Where an agent session stands, from the person's point of view.</summary>
public enum OrcaState
{
    /// <summary>Running (Orca: working).</summary>
    Working,

    /// <summary>Stopped on a question, a permission or a sign-in (Orca: blocked or waiting).</summary>
    Waiting,

    /// <summary>Finished its turn (Orca: done).</summary>
    Done,

    /// <summary>Finished a while ago, nothing new (Orca: idle).</summary>
    Idle,
}

/// <summary>One agent session in an Orca workspace.</summary>
/// <param name="Key">Orca's pane key: stable for the life of the session.</param>
/// <param name="Workspace">The repo, plus the worktree's name when it differs ("Dimpkin (bench findings)").</param>
/// <param name="Title">The conversation's name, else the task, else the first prompt.</param>
/// <param name="Agent">"claude", "codex"…</param>
/// <param name="Since">When it entered this state.</param>
/// <param name="LastMessage">The agent's last reply (what it's asking, when waiting).</param>
/// <param name="Unread">Orca hasn't shown its latest activity to anyone yet.</param>
public sealed record OrcaSession(
    string Key, string Workspace, string Title, string Agent, OrcaState State, DateTimeOffset? Since, string? LastMessage, bool Unread);

/// <summary>Reads <c>orca worktree ps --json</c> and describes the sessions in it.</summary>
public static class OrcaSessions
{
    /// <summary>Orca's agent states (working, blocked, waiting, done, idle); null for anything new.</summary>
    public static OrcaState? StateOf(string? orcaState) => orcaState switch
    {
        "working" => OrcaState.Working,
        "blocked" or "waiting" => OrcaState.Waiting,
        "done" => OrcaState.Done,
        "idle" => OrcaState.Idle,
        _ => null,
    };

    /// <summary>
    /// The sessions in the CLI's answer (<c>{"ok":true,"result":{"worktrees":[{…,"agents":[…]}]}}</c>).
    /// Throws <see cref="FormatException"/> when the answer says it failed or has no worktree list.
    /// </summary>
    public static IReadOnlyList<OrcaSession> Parse(JsonNode? root)
    {
        if (root?["ok"] is JsonValue ok && ok.TryGetValue<bool>(out var good) && !good)
            throw new FormatException(Str(root["error"]?["message"]) ?? Str(root["error"]) ?? "Orca reported an error");
        var worktrees = (root?["result"]?["worktrees"] ?? root?["worktrees"]) as JsonArray
                        ?? throw new FormatException("Orca's answer has no worktree list");
        var sessions = new List<OrcaSession>();
        foreach (var w in worktrees.OfType<JsonObject>())
        {
            if (w["isArchived"] is JsonValue arch && arch.TryGetValue<bool>(out var archived) && archived) continue;
            var workspace = WorkspaceName(Str(w["repo"]), Str(w["displayName"]), Str(w["path"]));
            if (w["agents"] is not JsonArray agents) continue;
            foreach (var a in agents.OfType<JsonObject>())
            {
                var key = Str(a["paneKey"]);
                if (key is null || Effective(a) is not { } state) continue;
                var title = Str(a["conversationName"]) ?? Str(a["taskTitle"]) ?? Str(a["displayName"]) ?? Shorten(Str(a["prompt"]), 60) ?? "a session";
                var unread = a["activity"]?["unread"] is JsonValue u && u.TryGetValue<bool>(out var isUnread) && isUnread;
                sessions.Add(new OrcaSession(key, workspace, title, Str(a["agentType"]) ?? "agent", state,
                    Millis(a["stateStartedAt"]) ?? Millis(a["updatedAt"]), Str(a["lastAssistantMessage"]), unread));
            }
        }
        return sessions;
    }

    /// <summary>
    /// The state as the person sees it. Orca keeps a session "working" while anything in it runs,
    /// a background shell included, even after the agent itself has finished its reply
    /// (<c>mainAgent.state</c> = done). Finished with no subagents still at work counts as done; a
    /// main agent stopped on a question counts as waiting whatever else is running.
    /// </summary>
    static OrcaState? Effective(JsonObject agent)
    {
        var top = StateOf(Str(agent["state"]));
        var main = StateOf(Str(agent["mainAgent"]?["state"]));
        if (main == OrcaState.Waiting || top == OrcaState.Waiting) return OrcaState.Waiting;
        if (top == OrcaState.Working && main is OrcaState.Done or OrcaState.Idle && !SubagentsWorking(agent)) return OrcaState.Done;
        return top;

        static bool SubagentsWorking(JsonObject a) =>
            a["subagents"] is JsonArray subs && subs.OfType<JsonObject>().Any(s => Str(s["state"]) == "working");
    }

    static string WorkspaceName(string? repo, string? name, string? path)
    {
        repo ??= path is null ? null : Path.GetFileName(path.TrimEnd('/', '\\'));
        if (repo is null) return name ?? "a workspace";
        if (name is null || string.Equals(name, repo, StringComparison.OrdinalIgnoreCase)) return repo;
        return $"{repo} ({name})";
    }

    /// <summary>
    /// For the assistant: the sessions grouped by state. <paramref name="only"/> limits it to one
    /// state; idle sessions are only counted unless asked for.
    /// </summary>
    public static string Describe(IReadOnlyList<OrcaSession> sessions, DateTimeOffset now, OrcaState? only = null)
    {
        if (sessions.Count == 0) return "Orca has no agent sessions open.";
        var sb = new StringBuilder();
        foreach (var (state, heading) in new[]
                 {
                     (OrcaState.Waiting, "Waiting on you"), (OrcaState.Working, "In progress"),
                     (OrcaState.Done, "Done"), (OrcaState.Idle, "Idle (finished earlier)"),
                 })
        {
            if (only is not null && only != state) continue;
            var group = sessions.Where(s => s.State == state).OrderBy(s => s.Since ?? now).ToList();
            if (group.Count == 0)
            {
                if (only is not null) sb.AppendLine($"{heading}: none.");
                continue;
            }
            if (state == OrcaState.Idle && only is null)
            {
                sb.AppendLine($"{heading}: {group.Count} more session{(group.Count == 1 ? "" : "s")}.");
                continue;
            }
            sb.AppendLine($"{heading} ({group.Count}):");
            foreach (var s in group)
            {
                sb.Append($"- {s.Workspace}: \"{s.Title}\" ({s.Agent})");
                if (s.Since is { } since) sb.Append($", {Ago(now - since)}");
                if (s.Unread && state == OrcaState.Done) sb.Append(", not looked at yet");
                if (state is OrcaState.Waiting or OrcaState.Done && Shorten(s.LastMessage, 240) is { } last)
                    sb.Append($". Last message: {last}");
                sb.AppendLine();
            }
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>The status tile on the puck: what needs attention first.</summary>
    public static string Glance(IReadOnlyList<OrcaSession> sessions)
    {
        int waiting = sessions.Count(s => s.State == OrcaState.Waiting);
        int working = sessions.Count(s => s.State == OrcaState.Working);
        int done = sessions.Count(s => s.State == OrcaState.Done && s.Unread);
        if (waiting > 0) return $"{waiting} need{(waiting == 1 ? "s" : "")} you";
        if (working > 0) return $"{working} running";
        if (done > 0) return $"{done} done";
        return "All quiet";
    }

    /// <summary>A sentence or two to say out loud: who needs the person, then how many are running and finished.</summary>
    public static string Spoken(IReadOnlyList<OrcaSession> sessions)
    {
        var parts = new List<string>();
        var waiting = sessions.Where(s => s.State == OrcaState.Waiting).Select(s => s.Workspace).Distinct().ToList();
        if (waiting.Count > 0) parts.Add($"{JoinNames(waiting)} need{(waiting.Count == 1 ? "s" : "")} you.");
        int working = sessions.Count(s => s.State == OrcaState.Working);
        if (working > 0) parts.Add($"{Count(working)} running.");
        int done = sessions.Count(s => s.State == OrcaState.Done && s.Unread);
        if (done > 0) parts.Add($"{Count(done)} finished and not looked at yet.");
        return parts.Count == 0 ? "Nothing's running or waiting on you in Orca." : string.Join(' ', parts);

        static string Count(int n) => n == 1 ? "One session" : $"{n} sessions";
        static string JoinNames(List<string> names) =>
            names.Count == 1 ? names[0] : string.Join(", ", names.Take(names.Count - 1)) + " and " + names[^1];
    }

    static string Ago(TimeSpan t) => t.TotalMinutes < 1 ? "just now"
        : t.TotalMinutes < 90 ? $"{(int)t.TotalMinutes} min"
        : t.TotalHours < 36 ? $"{(int)Math.Round(t.TotalHours)} h"
        : $"{(int)Math.Round(t.TotalDays)} days";

    internal static string? Shorten(string? text, int max)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var flat = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return flat.Length <= max ? flat : flat[..(max - 1)].TrimEnd() + "…";
    }

    static string? Str(JsonNode? n) =>
        n is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s.Trim() : null;

    static DateTimeOffset? Millis(JsonNode? n)
    {
        if (n is not JsonValue v) return null;
        long ms = v.TryGetValue<long>(out var l) ? l : v.TryGetValue<double>(out var d) ? (long)d : 0;
        return ms > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : null;
    }
}

/// <summary>A session that needs a heads-up on the puck.</summary>
public sealed record OrcaChange(OrcaSession Session, OrcaState? Before);

/// <summary>
/// Remembers each session's last state and reports the changes worth telling someone about: a
/// session starting to wait on them, and one finishing. The first look only learns the states.
/// </summary>
public sealed class OrcaTracker
{
    readonly Dictionary<string, OrcaState> _states = new(StringComparer.Ordinal);
    readonly Dictionary<string, (OrcaState State, DateTimeOffset At)> _told = new(StringComparer.Ordinal);
    bool _primed;

    /// <summary>The same news about the same session isn't repeated within this long (a session flickering between states).</summary>
    public TimeSpan RepeatAfter { get; init; } = TimeSpan.FromSeconds(10);

    public IReadOnlyList<OrcaChange> Update(IReadOnlyList<OrcaSession> sessions, DateTimeOffset now)
    {
        var changes = new List<OrcaChange>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var s in sessions)
        {
            seen.Add(s.Key);
            _states.TryGetValue(s.Key, out var before);
            bool known = _states.ContainsKey(s.Key);
            _states[s.Key] = s.State;
            if (!_primed || (known && before == s.State)) continue;
            // Waiting on the person is news however it got there; done is news after running.
            bool news = s.State == OrcaState.Waiting || (s.State == OrcaState.Done && known && before == OrcaState.Working);
            if (!news) continue;
            if (_told.TryGetValue(s.Key, out var last) && last.State == s.State && now - last.At < RepeatAfter) continue;
            _told[s.Key] = (s.State, now);
            changes.Add(new OrcaChange(s, known ? before : null));
        }
        foreach (var gone in _states.Keys.Where(k => !seen.Contains(k)).ToList())
        {
            _states.Remove(gone);
            _told.Remove(gone);
        }
        _primed = true;
        return changes;
    }
}
