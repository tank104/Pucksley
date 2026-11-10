using System.Text.Json.Nodes;
using Pucksley.Plugins.Orca;

namespace Pucksley.Plugins.Tests;

public class OrcaTests
{
    static readonly DateTimeOffset T0 = DateTimeOffset.FromUnixTimeMilliseconds(1_794_290_000_000);

    // The shape of `orca worktree ps --json` (trimmed to the fields the plugin reads)
    static JsonNode Ps(params (string Repo, string Name, string Pane, string State, string? Conversation, bool Unread)[] rows)
    {
        var worktrees = new JsonArray();
        foreach (var r in rows)
        {
            worktrees.Add(new JsonObject
            {
                ["repo"] = r.Repo, ["displayName"] = r.Name, ["path"] = $"D:/git/{r.Repo}", ["isArchived"] = false,
                ["agents"] = new JsonArray(new JsonObject
                {
                    ["paneKey"] = r.Pane, ["state"] = r.State, ["agentType"] = "claude",
                    ["conversationName"] = r.Conversation, ["prompt"] = "Fix the   flaky\ntest please",
                    ["lastAssistantMessage"] = "Shall I delete the old migrations?",
                    ["stateStartedAt"] = T0.AddMinutes(-5).ToUnixTimeMilliseconds(),
                    ["activity"] = new JsonObject { ["unread"] = r.Unread },
                }),
            });
        }
        return new JsonObject { ["id"] = "x", ["ok"] = true, ["result"] = new JsonObject { ["worktrees"] = worktrees } };
    }

    [Theory]
    [InlineData("working", OrcaState.Working)]
    [InlineData("blocked", OrcaState.Waiting)]
    [InlineData("waiting", OrcaState.Waiting)]
    [InlineData("done", OrcaState.Done)]
    [InlineData("idle", OrcaState.Idle)]
    public void Maps_orca_states(string orca, OrcaState expected) => Assert.Equal(expected, OrcaSessions.StateOf(orca));

    [Fact]
    public void Reads_sessions_with_workspace_names_and_title_fallbacks()
    {
        var sessions = OrcaSessions.Parse(Ps(
            ("Puck-Ai", "Puck-Ai", "p1", "blocked", "Puck plugins", false),
            ("Dimpkin", "bench findings", "p2", "working", null, false),
            ("Odd", "Odd", "p3", "something-new", "Skipped", false)));

        Assert.Equal(2, sessions.Count);
        Assert.Equal(("Puck-Ai", "Puck plugins", OrcaState.Waiting), (sessions[0].Workspace, sessions[0].Title, sessions[0].State));
        // No conversation name: the first prompt, with its whitespace tidied
        Assert.Equal(("Dimpkin (bench findings)", "Fix the flaky test please"), (sessions[1].Workspace, sessions[1].Title));
        Assert.Equal(T0.AddMinutes(-5), sessions[0].Since);
    }

    // What Orca reported when this very session finished a reply with a background shell still running
    [Theory]
    [InlineData("working", "done", null, OrcaState.Done)]
    [InlineData("working", "done", "working", OrcaState.Working)]   // waiting on its own subagents: still running
    [InlineData("working", "done", "done", OrcaState.Done)]
    [InlineData("working", "blocked", "working", OrcaState.Waiting)] // a question beats everything else
    [InlineData("done", "done", null, OrcaState.Done)]
    [InlineData("working", "working", null, OrcaState.Working)]
    public void Reads_the_main_agent_not_just_the_overall_state(string top, string main, string? subagent, OrcaState expected)
    {
        var agent = new JsonObject
        {
            ["paneKey"] = "p1", ["state"] = top, ["mainAgent"] = new JsonObject { ["state"] = main },
            ["subagents"] = subagent is null ? new JsonArray() : new JsonArray(new JsonObject { ["state"] = subagent }),
        };
        var node = new JsonObject { ["ok"] = true, ["result"] = new JsonObject { ["worktrees"] = new JsonArray(new JsonObject { ["repo"] = "Puck-Ai", ["agents"] = new JsonArray(agent) }) } };
        Assert.Equal(expected, OrcaSessions.Parse(node).Single().State);
    }

    [Fact]
    public void An_error_answer_is_reported()
    {
        var node = new JsonObject { ["ok"] = false, ["error"] = new JsonObject { ["message"] = "Orca isn't running" } };
        Assert.Equal("Orca isn't running", Assert.Throws<FormatException>(() => OrcaSessions.Parse(node)).Message);
    }

    [Fact]
    public void Describes_waiting_first_with_what_it_asked()
    {
        var sessions = OrcaSessions.Parse(Ps(
            ("Dimpkin", "Dimpkin", "p2", "working", "Bench", false),
            ("Puck-Ai", "Puck-Ai", "p1", "waiting", "Puck plugins", false),
            ("Connect51", "Connect51", "p3", "done", "Pricing", true),
            ("Old", "Old", "p4", "idle", "Old work", false)));

        var text = OrcaSessions.Describe(sessions, T0);
        Assert.StartsWith("Waiting on you (1):\n- Puck-Ai: \"Puck plugins\" (claude), 5 min. Last message: Shall I delete the old migrations?", text.Replace("\r", ""));
        Assert.Contains("In progress (1):", text);
        Assert.Contains("\"Pricing\" (claude), 5 min, not looked at yet", text);
        Assert.Contains("Idle (finished earlier): 1 more session.", text);
        Assert.Equal("In progress: none.", OrcaSessions.Describe(OrcaSessions.Parse(Ps(("A", "A", "p", "done", "x", false))), T0, OrcaState.Working));
    }

    [Fact]
    public void The_tile_and_the_spoken_summary_lead_with_what_needs_the_person()
    {
        var sessions = OrcaSessions.Parse(Ps(
            ("Puck-Ai", "Puck-Ai", "p1", "blocked", "a", false),
            ("Dimpkin", "Dimpkin", "p2", "waiting", "b", false),
            ("Connect51", "Connect51", "p3", "working", "c", false)));
        Assert.Equal("2 need you", OrcaSessions.Glance(sessions));
        Assert.Equal("Puck-Ai and Dimpkin need you. One session running.", OrcaSessions.Spoken(sessions));
        Assert.Equal("All quiet", OrcaSessions.Glance([]));
        Assert.Equal("Nothing's running or waiting on you in Orca.", OrcaSessions.Spoken([]));
    }

    [Fact]
    public void Tracker_learns_first_then_reports_waiting_and_finishing()
    {
        var tracker = new OrcaTracker();
        // First look: everything is just learned, nothing announced
        Assert.Empty(tracker.Update(OrcaSessions.Parse(Ps(("A", "A", "p1", "working", "a", false), ("B", "B", "p2", "waiting", "b", false))), T0));

        // p1 starts waiting on the person
        var changes = tracker.Update(OrcaSessions.Parse(Ps(("A", "A", "p1", "blocked", "a", false), ("B", "B", "p2", "waiting", "b", false))), T0.AddSeconds(4));
        Assert.Equal(("p1", OrcaState.Waiting, OrcaState.Working), (changes.Single().Session.Key, changes.Single().Session.State, changes.Single().Before));

        // p1 goes back to work, then finishes: done is news after running
        Assert.Empty(tracker.Update(OrcaSessions.Parse(Ps(("A", "A", "p1", "working", "a", false))), T0.AddSeconds(8)));
        var done = tracker.Update(OrcaSessions.Parse(Ps(("A", "A", "p1", "done", "a", true))), T0.AddSeconds(12)).Single();
        Assert.Equal(OrcaState.Done, done.Session.State);

        // done → idle is not news; a new session that's already waiting is
        Assert.Empty(tracker.Update(OrcaSessions.Parse(Ps(("A", "A", "p1", "idle", "a", false))), T0.AddSeconds(16)));
        Assert.Equal("p9", tracker.Update(OrcaSessions.Parse(Ps(("A", "A", "p1", "idle", "a", false), ("C", "C", "p9", "waiting", "c", false))), T0.AddSeconds(20)).Single().Session.Key);
    }

    [Fact]
    public void Tracker_does_not_repeat_news_for_a_flickering_session()
    {
        var tracker = new OrcaTracker { RepeatAfter = TimeSpan.FromMinutes(1) };
        tracker.Update(OrcaSessions.Parse(Ps(("A", "A", "p1", "working", "a", false))), T0);
        Assert.Single(tracker.Update(OrcaSessions.Parse(Ps(("A", "A", "p1", "waiting", "a", false))), T0.AddSeconds(4)));
        tracker.Update(OrcaSessions.Parse(Ps(("A", "A", "p1", "working", "a", false))), T0.AddSeconds(8));
        Assert.Empty(tracker.Update(OrcaSessions.Parse(Ps(("A", "A", "p1", "waiting", "a", false))), T0.AddSeconds(12)));
        tracker.Update(OrcaSessions.Parse(Ps(("A", "A", "p1", "working", "a", false))), T0.AddSeconds(70));
        Assert.Single(tracker.Update(OrcaSessions.Parse(Ps(("A", "A", "p1", "waiting", "a", false))), T0.AddSeconds(74)));
    }

    [Fact]
    public void Real_orca_cli_answers_parse_when_orca_is_installed()
    {
        var exe = OrcaCli.Find(null);
        if (exe is null) return; // not on this machine (CI, the NAS)
        JsonNode? node;
        try { node = OrcaCli.RunJsonAsync(exe, ["worktree", "ps", "--json"], TimeSpan.FromSeconds(20), CancellationToken.None).GetAwaiter().GetResult(); }
        catch (InvalidOperationException) { return; } // Orca installed but not running
        var sessions = OrcaSessions.Parse(node);
        Assert.All(sessions, s => Assert.False(string.IsNullOrWhiteSpace(s.Workspace)));
    }
}
