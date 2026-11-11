using System.Diagnostics;
using System.Text.Json.Nodes;
using Pucksley.Core.Tests.Support;
using static Pucksley.Core.Tests.Support.Claude;

namespace Pucksley.Core.Tests;

/// <summary>
/// The Claude agent (AnthropicChatModel + AgentService + PluginManager) against a fake Messages API
/// that streams real-shaped SSE. Every assertion about what the hub sends is made on the request
/// bodies the fake received.
/// </summary>
public sealed class AgentTests
{
    static string J(JsonNode? n) => n?.ToJsonString() ?? "null";

    static JsonArray Content(JsonNode message) => message["content"] as JsonArray ?? new JsonArray(JsonValue.Create(message["content"]!.GetValue<string>()));

    // ------------------------------------------------------------------ streaming and request shape

    [Fact]
    public async Task Streamed_text_reaches_the_callbacks_and_the_result()
    {
        var h = await AgentHarness.CreateAsync();
        h.Claude.Then(Text("Hello there. The kitchen is warm and the lights are off."));
        var cb = new RecordingCallbacks();

        var result = await h.AskAsync("how's the kitchen?", cb);

        Assert.False(result.Failed, result.Text);
        Assert.Equal("Hello there. The kitchen is warm and the lights are off.", result.Text);
        Assert.Equal(result.Text, cb.Streamed);
        Assert.True(cb.DeltaCount > 3, "text should arrive in pieces, not at the end");
    }

    [Fact]
    public async Task Request_has_cached_system_prompt_tools_effort_and_the_fallback_beta()
    {
        var h = await AgentHarness.CreateAsync(s =>
        {
            s.Household.Location = "Wellington, New Zealand";
            s.Household.Notes = "Mabel is the dog.";
            s.Assistant.ExtraInstructions = "Be cheeky.";
            s.Assistant.AssistantName = "Kiri";
        });
        h.Claude.Then(Text("Hi."));
        await h.AskAsync("hello");

        var r = Assert.Single(h.Claude.Requests);
        var b = r.Body;
        Assert.Equal("/v1/messages", r.PathAndQuery);   // the beta surface is signalled by the anthropic-beta header
        Assert.Equal("Bearer " + h.Claude.ApiKey, r.Header("authorization"));
        Assert.NotNull(r.Header("anthropic-version"));
        Assert.Equal("claude-opus-5-5", b["model"]!.GetValue<string>());
        Assert.True(b["stream"]!.GetValue<bool>());
        Assert.Equal(16000, b["max_tokens"]!.GetValue<int>());

        // System prompt: one text block with a cache breakpoint, plus top-level auto caching
        var system = b["system"]!.AsArray();
        var sys = Assert.Single(system)!;
        Assert.Equal("ephemeral", sys["cache_control"]!["type"]!.GetValue<string>());
        var prompt = sys["text"]!.GetValue<string>();
        Assert.Contains("You are Kiri", prompt);
        Assert.Contains("Wellington, New Zealand", prompt);
        Assert.Contains("Mabel is the dog.", prompt);
        Assert.Contains("Be cheeky.", prompt);
        Assert.Contains("[Lights] Rooms are Kitchen and Lounge.", prompt);   // plugin instructions
        Assert.Equal("ephemeral", b["cache_control"]!["type"]!.GetValue<string>());

        // Effort and the server-side refusal fallback for claude-opus-5-5
        Assert.Equal("low", b["output_config"]!["effort"]!.GetValue<string>());
        Assert.Equal("default", b["fallbacks"]!.GetValue<string>());
        Assert.Contains("server-side-fallback-2026-07-01", r.Header("anthropic-beta") ?? "");

        // Tools: the plugin's, with schemas built from the method parameters, streamed eagerly
        var tool = r.Tools.Single(t => t!["name"]!.GetValue<string>() == "agent_tools_set_level")!;
        Assert.Equal("Set a room's light level.", tool["description"]!.GetValue<string>());
        Assert.True(tool["eager_input_streaming"]!.GetValue<bool>());
        var schema = tool["input_schema"]!;
        Assert.Equal("object", schema["type"]!.GetValue<string>());
        Assert.Equal("string", schema["properties"]!["room"]!["type"]!.GetValue<string>());
        Assert.Equal("integer", schema["properties"]!["level"]!["type"]!.GetValue<string>());
        Assert.Equal(100, schema["properties"]!["level"]!["maximum"]!.GetValue<double>());
        Assert.Equal(["level", "room"], schema["required"]!.AsArray().Select(n => n!.GetValue<string>()).Order());
        Assert.DoesNotContain(r.Tools, t => t!["type"]?.GetValue<string>()?.StartsWith("web_") == true);   // web search off here

        // The user turn: the context block first (time, puck, room, plugin state), then the words
        var user = r.Messages.Single()!;
        Assert.Equal("user", user["role"]!.GetValue<string>());
        var text = r.LastUserText();
        Assert.StartsWith("[Context]\nNow: ", text);
        Assert.Contains("Speaking to: Kitchen puck, in the Kitchen", text);
        Assert.Contains("Kitchen lights: off", text);
        Assert.EndsWith("[/Context]\n\nhello", text);
    }

    [Fact]
    public async Task Web_search_tools_and_options_follow_the_model()
    {
        var h = await AgentHarness.CreateAsync(s =>
        {
            s.Assistant.WebSearch = true;
            s.Assistant.Effort = "high";
            s.Household.Location = "Wellington, New Zealand";
        });
        h.Claude.Then(Text("One.")).Then(Text("Two.")).Then(Text("Three."));

        await h.AskAsync("q1", session: "a");
        h.Settings.Update(s => s.Assistant.Model = "claude-haiku-4-5");
        await h.AskAsync("q2", session: "b");
        h.Settings.Update(s => s.Assistant.Model = "claude-sonnet-5-5");
        await h.AskAsync("q3", session: "c");

        var (opus, haiku, sonnet) = (h.Claude.Requests[0], h.Claude.Requests[1], h.Claude.Requests[2]);

        var search = opus.Tools.Single(t => t!["name"]?.GetValue<string>() == "web_search")!;
        Assert.Equal("web_search_20260209", search["type"]!.GetValue<string>());
        Assert.Equal(3, search["max_uses"]!.GetValue<int>());
        Assert.Equal("Wellington", search["user_location"]!["city"]!.GetValue<string>());
        Assert.Equal("NZ", search["user_location"]!["country"]!.GetValue<string>());
        Assert.Contains(opus.Tools, t => t!["type"]?.GetValue<string>() == "web_fetch_20260209");
        Assert.Equal("high", opus.Body["output_config"]!["effort"]!.GetValue<string>());

        // An older model: basic web search, no fetch, no effort, no fallbacks
        Assert.Contains(haiku.Tools, t => t!["type"]?.GetValue<string>() == "web_search_20250305");
        Assert.DoesNotContain(haiku.Tools, t => t!["name"]?.GetValue<string>() == "web_fetch");
        Assert.Null(haiku.Body["output_config"]);
        Assert.Null(haiku.Body["fallbacks"]);
        Assert.DoesNotContain("server-side-fallback", haiku.Header("anthropic-beta") ?? "");

        Assert.Equal("default", sonnet.Body["fallbacks"]!.GetValue<string>());
        Assert.Contains("server-side-fallback-2026-07-01", sonnet.Header("anthropic-beta") ?? "");
        Assert.Contains(sonnet.Tools, t => t!["type"]?.GetValue<string>() == "web_search_20260209");
    }

    [Fact]
    public async Task Tool_schemas_reach_the_API_whole()
    {
        // An MCP tool's schema can carry $defs, $ref and additionalProperties; dropping them leaves a dangling $ref
        var h = await AgentHarness.CreateAsync();
        h.Claude.Then(Text("Hi."));
        await h.AskAsync("hello");

        var schema = h.Claude.Requests[0].Tools.Single(t => t!["name"]!.GetValue<string>() == "agent_tools_scene")!["input_schema"]!;
        Assert.Equal("#/$defs/Scene", schema["properties"]!["scene"]!["$ref"]!.GetValue<string>());
        Assert.NotNull(schema["$defs"]?["Scene"]);
        Assert.False(schema["additionalProperties"]!.GetValue<bool>());
        Assert.Equal("object", schema["type"]!.GetValue<string>());
    }

    [Fact]
    public async Task No_API_key_gives_a_friendly_answer_without_calling_Claude()
    {
        var h = await AgentHarness.CreateAsync(s => s.Assistant.AnthropicApiKey = "");
        var result = await h.AskAsync("hello");
        Assert.True(result.Failed);
        Assert.Contains("Claude API key", result.Text);
        Assert.Empty(h.Claude.Requests);
    }

    // ------------------------------------------------------------------ tools

    [Fact]
    public async Task A_tool_use_round_runs_the_tool_and_sends_the_result_back()
    {
        var h = await AgentHarness.CreateAsync();
        h.Claude
            .Then(Reply("tool_use", [Thinking("sig-1"), TextBlock("Let me check."), ToolUse("toolu_01A", "agent_tools_set_level", """{"room":"Kitchen","level":30}""")]))
            .Then(Text("Done, the kitchen is at thirty."));
        var cb = new RecordingCallbacks();

        var result = await h.AskAsync("kitchen lights to 30", cb);

        Assert.False(result.Failed, result.Text);
        Assert.Equal("Let me check. Done, the kitchen is at thirty.", result.Text);
        Assert.Equal("Let me check. Done, the kitchen is at thirty.", cb.Streamed);
        Assert.Equal(["set_level Kitchen 30"], h.Tools.Calls);
        Assert.Equal(AgentHarness.Kitchen, Assert.Single(h.Tools.Callers));
        Assert.Equal([("light", "Lights: Set level", "run"), ("light", "Kitchen 30%", "ok")], cb.Activities.ToArray());

        Assert.Equal(2, h.Claude.Requests.Count);
        var follow = h.Claude.Requests[1].Messages;
        Assert.Equal(3, follow.Count);
        // The assistant turn goes back whole: thinking (signature intact), text, tool_use
        var assistant = follow[1]!;
        Assert.Equal("assistant", assistant["role"]!.GetValue<string>());
        var blocks = Content(assistant);
        Assert.Equal(["thinking", "text", "tool_use"], blocks.Select(x => x!["type"]!.GetValue<string>()));
        Assert.Equal("sig-1", blocks[0]!["signature"]!.GetValue<string>());
        Assert.Equal("Let me check.", blocks[1]!["text"]!.GetValue<string>());
        Assert.Equal("toolu_01A", blocks[2]!["id"]!.GetValue<string>());
        Assert.Equal("agent_tools_set_level", blocks[2]!["name"]!.GetValue<string>());
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""{"room":"Kitchen","level":30}"""), blocks[2]!["input"]));
        // …followed by one user message with the tool_result for that id
        var results = Content(follow[2]!);
        Assert.Equal("user", follow[2]!["role"]!.GetValue<string>());
        var tr = Assert.Single(results)!;
        Assert.Equal("tool_result", tr["type"]!.GetValue<string>());
        Assert.Equal("toolu_01A", tr["tool_use_id"]!.GetValue<string>());
        Assert.Equal("Kitchen is at 30%.", tr["content"]!.GetValue<string>());
        Assert.NotEqual(true, tr["is_error"]?.GetValue<bool>());
    }

    [Fact]
    public async Task Parallel_tool_calls_come_back_as_one_message_with_every_result()
    {
        var h = await AgentHarness.CreateAsync();
        h.Claude
            .Then(Reply("tool_use", [
                ToolUse("toolu_A", "agent_tools_set_level", """{"room":"Kitchen","level":10}"""),
                ToolUse("toolu_B", "agent_tools_fail", "{}"),
                ToolUse("toolu_C", "no_such_tool", "{}"),
            ]))
            .Then(Text("Kitchen's done; the other lights are unplugged."));
        var cb = new RecordingCallbacks();

        var result = await h.AskAsync("all the lights", cb);

        Assert.False(result.Failed, result.Text);
        var results = Content(h.Claude.Requests[1].Messages[2]!);
        Assert.Equal(["toolu_A", "toolu_B", "toolu_C"], results.Select(r => r!["tool_use_id"]!.GetValue<string>()));
        Assert.NotEqual(true, results[0]!["is_error"]?.GetValue<bool>());
        Assert.True(results[1]!["is_error"]!.GetValue<bool>());
        Assert.Equal("The lights are unplugged.", results[1]!["content"]!.GetValue<string>());
        Assert.True(results[2]!["is_error"]!.GetValue<bool>());
        Assert.Contains("no tool called no_such_tool", results[2]!["content"]!.GetValue<string>());
        Assert.Contains(cb.Activities, a => a.State == "err");
    }

    [Fact]
    public async Task A_tool_that_wants_confirmation_asks_and_respects_the_answer()
    {
        var h = await AgentHarness.CreateAsync();
        h.Claude
            .Then(Reply("tool_use", [ToolUse("toolu_W1", "agent_tools_wipe", "{}")])).Then(Text("Okay, I left them."))
            .Then(Reply("tool_use", [ToolUse("toolu_W2", "agent_tools_wipe", "{}")])).Then(Text("Wiped."));

        var no = new RecordingCallbacks { ConfirmAnswer = false };
        await h.AskAsync("wipe the scenes", no);
        Assert.Equal("Wipe every light scene?", Assert.Single(no.ConfirmQuestions));
        Assert.Empty(h.Tools.Calls);
        var refused = Content(h.Claude.Requests[1].Messages.Last()!)[0]!;
        Assert.True(refused["is_error"]!.GetValue<bool>());
        Assert.Contains("said no", refused["content"]!.GetValue<string>());

        var yes = new RecordingCallbacks { ConfirmAnswer = true };
        await h.AskAsync("yes really wipe them", yes);
        Assert.Single(yes.ConfirmQuestions);
        Assert.Equal(["wipe"], h.Tools.Calls);
    }

    [Fact]
    public async Task A_tool_call_cut_off_by_max_tokens_is_never_run()
    {
        var h = await AgentHarness.CreateAsync();
        h.Claude.Then(Reply("max_tokens", [ToolUse("toolu_X", "agent_tools_set_level", """{"room":"Kit""")]));
        var result = await h.AskAsync("lights");
        Assert.True(result.Failed);
        Assert.Contains("too long", result.Text);
        Assert.Empty(h.Tools.Calls);
        await AssertRolledBackAsync(h);
    }

    [Fact]
    public async Task The_last_round_keeps_the_tools_but_forbids_using_them()
    {
        // Changing `tools` mid-conversation breaks the prompt cache and, on newer accounts, replayed
        // thinking blocks (the history-editing check covers tools). tool_choice none doesn't.
        var h = await AgentHarness.CreateAsync(s => s.Assistant.MaxToolRounds = 1);
        h.Claude
            .Then(Reply("tool_use", [ToolUse("toolu_1", "agent_tools_set_level", """{"room":"Lounge","level":5}""")]))
            .Then(Text("The lounge is at five."));

        var result = await h.AskAsync("lounge to 5");

        Assert.False(result.Failed, result.Text);
        Assert.Equal("The lounge is at five.", result.Text);
        var (first, last) = (h.Claude.Requests[0], h.Claude.Requests[1]);
        Assert.Equal(J(first.Body["tools"]), J(last.Body["tools"]));
        Assert.Equal("none", last.Body["tool_choice"]?["type"]?.GetValue<string>());
        Assert.Null(first.Body["tool_choice"]);
    }

    [Fact]
    public async Task Too_many_tool_rounds_stop_with_a_friendly_message()
    {
        var h = await AgentHarness.CreateAsync(s => s.Assistant.MaxToolRounds = 1);
        h.Claude.Fallback = Reply("tool_use", [ToolUse("toolu_L", "agent_tools_set_level", """{"room":"Lounge","level":5}""")]);
        var result = await h.AskAsync("loop forever");
        Assert.True(result.Failed);
        Assert.Contains("too many steps", result.Text);
        h.Claude.Fallback = Text("Fine.");
        await AssertRolledBackAsync(h);
    }

    [Fact]
    public async Task An_image_tool_result_goes_back_as_text_and_image()
    {
        var h = await AgentHarness.CreateAsync();
        h.Claude
            .Then(Reply("tool_use", [ToolUse("toolu_S", "agent_tools_snap", "{}")]))
            .Then(Text("It's tidy."));
        var result = await h.AskAsync("what does the kitchen look like?");
        Assert.False(result.Failed, result.Text);
        var tr = Content(h.Claude.Requests[1].Messages[2]!)[0]!;
        Assert.Equal("toolu_S", tr["tool_use_id"]!.GetValue<string>());
        var parts = tr["content"]!.AsArray();
        Assert.Equal("text", parts[0]!["type"]!.GetValue<string>());
        Assert.Equal("A picture of the kitchen.", parts[0]!["text"]!.GetValue<string>());
        Assert.Equal("image", parts[1]!["type"]!.GetValue<string>());
        Assert.Equal("base64", parts[1]!["source"]!["type"]!.GetValue<string>());
        Assert.Equal("image/png", parts[1]!["source"]!["media_type"]!.GetValue<string>());
        Assert.Equal(Convert.ToBase64String(AgentToolsPlugin.Png), parts[1]!["source"]!["data"]!.GetValue<string>());
    }

    // ------------------------------------------------------------------ history

    [Fact]
    public async Task History_is_append_only_across_turns()
    {
        var h = await AgentHarness.CreateAsync();
        h.Claude
            .Then(Reply("tool_use", [Thinking("sig-a"), ToolUse("toolu_H1", "agent_tools_set_level", """{"room":"Kitchen","level":40}""")]))
            .Then(Reply("end_turn", [Thinking("sig-b"), TextBlock("Kitchen's at forty.")]))
            .Then(Text("You're welcome."));

        await h.AskAsync("kitchen to 40");
        await h.AskAsync("thanks");

        var reqs = h.Claude.Requests;
        Assert.Equal(3, reqs.Count);
        var (r1, r2, r3) = (reqs[0], reqs[1], reqs[2]);
        // Each request starts with the previous one's messages, byte for byte
        Assert.Equal(J(r1.Messages[0]), J(r2.Messages[0]));
        for (int i = 0; i < r2.Messages.Count; i++) Assert.Equal(J(r2.Messages[i]), J(r3.Messages[i]));
        Assert.Equal(5, r3.Messages.Count);
        var final = Content(r3.Messages[3]!);
        Assert.Equal("assistant", r3.Messages[3]!["role"]!.GetValue<string>());
        Assert.Equal(["thinking", "text"], final.Select(b => b!["type"]!.GetValue<string>()));
        Assert.Equal("sig-b", final[0]!["signature"]!.GetValue<string>());
        Assert.EndsWith("thanks", r3.LastUserText());
        // …and the cached prefix (tools, system) doesn't move
        Assert.Equal(J(r1.Body["tools"]), J(r3.Body["tools"]));
        Assert.Equal(J(r1.Body["system"]), J(r3.Body["system"]));
    }

    [Fact]
    public async Task Changing_a_tool_definition_starts_a_new_conversation()
    {
        var h = await AgentHarness.CreateAsync();
        await h.AskAsync("one");
        await h.AskAsync("two");
        Assert.Equal(3, h.Claude.Requests[1].Messages.Count);   // same conversation

        // Same tool names, new description (e.g. a custom action edited): history from the old tool
        // set mustn't be replayed against the new one
        h.Tools.NoteDescription = "Write a note on the fridge.";
        h.Tools.RaiseToolsChanged();
        await h.AskAsync("three");
        Assert.Single(h.Claude.Requests[2].Messages);

        // Turning web search on changes the tool list too
        h.Settings.Update(s => s.Assistant.WebSearch = true);
        await h.AskAsync("four");
        Assert.Single(h.Claude.Requests[3].Messages);
    }

    [Fact]
    public async Task Reset_forgets_the_conversation()
    {
        var h = await AgentHarness.CreateAsync();
        await h.AskAsync("one");
        h.Agent.Reset("puck:test");
        await h.AskAsync("two");
        Assert.Single(h.Claude.Requests[1].Messages);
    }

    // ------------------------------------------------------------------ failures

    static async Task AssertRolledBackAsync(AgentHarness h, string session = "puck:test")
    {
        int before = h.Claude.Requests.Count;
        h.Claude.Then(Text("Fresh."));
        var ok = await h.AskAsync("are you there?", session: session);
        Assert.False(ok.Failed, ok.Text);
        var next = h.Claude.Requests[before];
        Assert.Single(next.Messages);   // nothing of the failed turn was kept
    }

    [Fact]
    public async Task A_refusal_becomes_a_friendly_failure_and_is_rolled_back()
    {
        var h = await AgentHarness.CreateAsync();
        h.Claude.Then(Reply("refusal", [TextBlock("I can")]));
        var result = await h.AskAsync("something it won't do");
        Assert.True(result.Failed);
        Assert.Equal("Sorry, I can't help with that one.", result.Text);
        await AssertRolledBackAsync(h);
    }

    [Fact]
    public async Task A_rejected_key_becomes_a_friendly_failure()
    {
        var h = await AgentHarness.CreateAsync();
        h.Claude.Then(Error(401, "authentication_error", "invalid x-api-key"));
        var result = await h.AskAsync("hello");
        Assert.True(result.Failed);
        Assert.Contains("API key was rejected", result.Text);
        Assert.Single(h.Claude.Requests);   // not retried
        await AssertRolledBackAsync(h);
    }

    [Fact]
    public async Task Rate_limiting_becomes_a_friendly_failure()
    {
        var h = await AgentHarness.CreateAsync();
        h.Claude.Fallback = Error(429, "rate_limit_error", "Number of request tokens has exceeded your per-minute rate limit", ("retry-after-ms", "1"));
        var result = await h.AskAsync("hello");
        Assert.True(result.Failed);
        Assert.Contains("busy", result.Text);
        Assert.True(h.Claude.Requests.Count >= 2, "the SDK retries 429s");
        h.Claude.Fallback = Text("OK.");
        await AssertRolledBackAsync(h);
    }

    [Fact]
    public async Task A_bad_request_is_reported_with_the_API_message_not_raw_JSON()
    {
        var h = await AgentHarness.CreateAsync();
        h.Claude.Then(Error(400, "invalid_request_error", "max_tokens: too big"));
        var result = await h.AskAsync("hello");
        Assert.True(result.Failed);
        Assert.Contains("max_tokens: too big", result.Text);
        Assert.DoesNotContain("{", result.Text);   // it's spoken aloud on the puck
        Assert.DoesNotContain("Status Code", result.Text);
    }

    [Fact]
    public async Task A_network_failure_becomes_a_friendly_failure()
    {
        var h = await AgentHarness.CreateAsync();
        h.Claude.Fallback = Abort();
        var result = await h.AskAsync("hello");
        Assert.True(result.Failed);
        Assert.Contains("couldn't reach Claude", result.Text);
        h.Claude.Fallback = Text("OK.");
        await AssertRolledBackAsync(h);
    }

    [Fact]
    public async Task A_connection_dropped_mid_stream_becomes_a_friendly_failure()
    {
        var h = await AgentHarness.CreateAsync();
        h.Claude.Then(Reply("end_turn", [TextBlock("The kitchen is")], abortAfterBlocks: true));
        var result = await h.AskAsync("hello");
        Assert.True(result.Failed, result.Text);
        await AssertRolledBackAsync(h);
    }

    [Fact]
    public async Task An_overloaded_error_mid_stream_becomes_a_friendly_failure()
    {
        // The API can send `event: error` (overloaded_error) after the stream started
        var h = await AgentHarness.CreateAsync();
        h.Claude.Then(Reply("end_turn", [TextBlock("Let me")], errorAfterBlocks: new JsonObject { ["type"] = "overloaded_error", ["message"] = "Overloaded" }));
        var result = await h.AskAsync("hello");
        Assert.True(result.Failed, result.Text);
        Assert.Contains("busy", result.Text);
        await AssertRolledBackAsync(h);
    }

    [Fact]
    public async Task A_stream_that_ends_before_message_stop_becomes_a_friendly_failure()
    {
        var h = await AgentHarness.CreateAsync();
        h.Claude.Then(Reply("end_turn", [TextBlock("Half an ans")], messageStop: false));
        var result = await h.AskAsync("hello");
        Assert.True(result.Failed, result.Text);
        await AssertRolledBackAsync(h);
    }

    [Fact]
    public async Task A_cancelled_turn_throws_and_is_rolled_back()
    {
        var h = await AgentHarness.CreateAsync();
        h.Claude.Then(Hang());
        using var cts = new CancellationTokenSource();
        var turn = h.AskAsync("hello", cancel: cts.Token);
        var req = await h.Claude.WaitForRequestsAsync(1);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => turn);
        Assert.True(await Task.WhenAny(req.Aborted.Task, Task.Delay(5000)) == req.Aborted.Task, "the HTTP request should be cancelled too");
        await AssertRolledBackAsync(h);
    }

    // ------------------------------------------------------------------ server tools, pause_turn, fallbacks

    [Fact]
    public async Task Pause_turn_sends_the_paused_turn_back_as_is()
    {
        var h = await AgentHarness.CreateAsync(s => s.Assistant.WebSearch = true);
        h.Claude
            .Then(Reply("pause_turn", [Thinking("sig-p"), ServerToolUse("srvtoolu_01", "web_search", """{"query":"wellington weather"}""")]))
            .Then(Reply("end_turn", [WebSearchResult("srvtoolu_01"), TextBlock("It's sunny in Wellington.")]));
        var cb = new RecordingCallbacks();

        var result = await h.AskAsync("weather?", cb);

        Assert.False(result.Failed, result.Text);
        Assert.Equal("It's sunny in Wellington.", result.Text);
        Assert.Equal(2, h.Claude.Requests.Count);
        var resumed = h.Claude.Requests[1].Messages;
        Assert.Equal(2, resumed.Count);   // no "continue" user message: the trailing server_tool_use resumes it
        var last = resumed[1]!;
        Assert.Equal("assistant", last["role"]!.GetValue<string>());
        var blocks = Content(last);
        Assert.Equal(["thinking", "server_tool_use"], blocks.Select(b => b!["type"]!.GetValue<string>()));
        Assert.Equal("srvtoolu_01", blocks[1]!["id"]!.GetValue<string>());
        Assert.Equal("wellington weather", blocks[1]!["input"]!["query"]!.GetValue<string>());
        // The puck shows a search activity, not the tool's JSON-quoted name
        Assert.Contains(cb.Activities, a => a.Icon == "search" && a.Text == "Searching the web" && a.State == "run");
    }

    [Fact]
    public async Task Server_tool_blocks_round_trip_into_the_next_turn()
    {
        var h = await AgentHarness.CreateAsync(s => s.Assistant.WebSearch = true);
        h.Claude
            .Then(Reply("end_turn", [ServerToolUse("srvtoolu_02", "web_search", """{"query":"news"}"""), WebSearchResult("srvtoolu_02"), TextBlock("Here's the news.")]))
            .Then(Text("Sure."));
        await h.AskAsync("news?");
        await h.AskAsync("thanks");
        var echoed = Content(h.Claude.Requests[1].Messages[1]!);
        Assert.Equal(["server_tool_use", "web_search_tool_result", "text"], echoed.Select(b => b!["type"]!.GetValue<string>()));
        Assert.Equal("srvtoolu_02", echoed[1]!["tool_use_id"]!.GetValue<string>());
        Assert.Equal("https://example.com/weather", echoed[1]!["content"]![0]!["url"]!.GetValue<string>());
    }

    [Fact]
    public async Task After_a_mid_stream_fallback_the_declined_models_tool_calls_are_dropped()
    {
        // docs: after a mid-output fallback, omit thinking / redacted_thinking / tool_use blocks that
        // come before the final `fallback` block; text and everything after it echo normally
        var h = await AgentHarness.CreateAsync();
        h.Claude
            .Then(Reply("tool_use", [
                Thinking("sig-declined"),
                TextBlock("Sure, "),
                ToolUse("toolu_old", "agent_tools_set_level", """{"room":"Lounge","level":10}"""),
                Fallback("claude-opus-5-5", "claude-opus-5"),
                ToolUse("toolu_new", "agent_tools_set_level", """{"room":"Kitchen","level":30}"""),
            ], model: "claude-opus-5"))
            .Then(Text("Kitchen's at thirty."));

        var result = await h.AskAsync("kitchen to 30");

        Assert.False(result.Failed, result.Text);
        Assert.Equal(["set_level Kitchen 30"], h.Tools.Calls);   // the declined model's call never runs
        var follow = h.Claude.Requests[1].Messages;
        var assistant = Content(follow[1]!);
        Assert.Equal(["text", "tool_use"], assistant.Select(b => b!["type"]!.GetValue<string>()));
        Assert.Equal("toolu_new", assistant[1]!["id"]!.GetValue<string>());
        Assert.Equal(["toolu_new"], Content(follow[2]!).Select(b => b!["tool_use_id"]!.GetValue<string>()));
    }

    // ------------------------------------------------------------------ context

    [Fact]
    public async Task A_slow_plugin_context_doesnt_hold_up_the_turn()
    {
        var h = await AgentHarness.CreateAsync();
        h.Tools.ContextDelay = TimeSpan.FromSeconds(30);
        var sw = Stopwatch.StartNew();
        var result = await h.AskAsync("hello");
        Assert.False(result.Failed, result.Text);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"took {sw.Elapsed}");
        Assert.DoesNotContain("Kitchen lights", h.Claude.Requests[0].LastUserText());
    }

    [Fact]
    public async Task A_harder_question_is_answered_by_the_bigger_model_word_for_word()
    {
        var h = await AgentHarness.CreateAsync(s =>
        {
            s.Assistant.Model = "claude-haiku-5-5";
            s.Assistant.DeepModel = "claude-opus-5-5";
        });
        h.Claude
            .Then(Reply("tool_use", [ToolUse("toolu_T", "think_harder", """{"question":"Should we fix the mortgage for two years or five?"}""")]))
            .Then(Text("It depends on where you think rates are heading. Five years buys certainty."))
            .Then(Text("Anything the everyday model adds is not spoken."));
        var cb = new RecordingCallbacks();

        var result = await h.AskAsync("should we fix the mortgage for two years or five?", cb);

        Assert.False(result.Failed, result.Text);
        Assert.Equal("It depends on where you think rates are heading. Five years buys certainty.", result.Text);
        Assert.Equal(result.Text, cb.Streamed);
        var (everyday, bigger, closing) = (h.Claude.Requests[0], h.Claude.Requests[1], h.Claude.Requests[2]);
        Assert.Equal("claude-haiku-5-5", everyday.Body["model"]!.GetValue<string>());
        Assert.Contains("think_harder", everyday.ToolNames);
        Assert.Equal("claude-opus-5-5", bigger.Body["model"]!.GetValue<string>());
        Assert.DoesNotContain("think_harder", bigger.ToolNames);
        Assert.Contains("Should we fix the mortgage for two years or five?", bigger.LastUserText());
        // The everyday model is told what was said, so follow-ups make sense
        Assert.Contains("Five years buys certainty", closing.Messages.ToJsonString());
        Assert.Contains(cb.Activities, a => a.Text == "Thinking it through");
    }

    [Fact]
    public async Task Without_a_model_for_harder_questions_there_is_no_hand_over()
    {
        var h = await AgentHarness.CreateAsync(s =>
        {
            s.Assistant.Model = "claude-haiku-5-5";
            s.Assistant.DeepModel = "";
        });
        h.Claude.Then(Text("Hi."));
        await h.AskAsync("hello");
        Assert.DoesNotContain("think_harder", h.Claude.Requests[0].ToolNames);
    }

    [Fact]
    public async Task Text_split_by_citations_reads_as_one_passage()
    {
        var h = await AgentHarness.CreateAsync(s => s.Assistant.WebSearch = true);
        // How a cited answer streams: each cited fact is its own text block, mid-sentence
        h.Claude.Then(Reply("end_turn", [
            ServerToolUse("srvtoolu_c1", "web_search", """{"query":"mortgage rates"}"""), WebSearchResult("srvtoolu_c1"),
            TextBlock("Two-year rates are about 5.29%"), TextBlock(", and five-year rates are about 5.75%"), TextBlock(". "),
            TextBlock("So it's close")]));
        var cb = new RecordingCallbacks();

        var result = await h.AskAsync("rates?", cb);

        Assert.Equal("Two-year rates are about 5.29%, and five-year rates are about 5.75%. So it's close", result.Text);
        Assert.Equal(result.Text, cb.Streamed);
    }
}
