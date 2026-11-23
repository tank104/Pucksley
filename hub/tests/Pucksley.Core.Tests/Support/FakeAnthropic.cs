using System.Collections.Concurrent;
using System.Net;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Pucksley.Core.Tests.Support;

/// <summary>One request the hub sent to the fake Messages API, as it arrived on the wire.</summary>
public sealed class ClaudeRequest
{
    public required JsonObject Body { get; init; }
    public required IReadOnlyDictionary<string, string> Headers { get; init; }
    public required string PathAndQuery { get; init; }

    /// <summary>Completes when the hub hangs up on this request (cancelled turn, puck gone…).</summary>
    public TaskCompletionSource Aborted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public JsonArray Messages => Body["messages"]!.AsArray();
    public JsonArray Tools => Body["tools"] as JsonArray ?? [];
    public IReadOnlyList<string> ToolNames => Tools.Select(t => t!["name"]?.GetValue<string>() ?? "").ToList();
    public string? Header(string name) => Headers.TryGetValue(name, out var v) ? v : null;

    /// <summary>The user text of the last user message (string content, or its text blocks joined).</summary>
    public string LastUserText()
    {
        var last = Messages.Last(m => m!["role"]!.GetValue<string>() == "user")!;
        var content = last["content"]!;
        if (content is JsonValue v) return v.GetValue<string>();
        return string.Join("\n", content.AsArray().Where(b => b!["type"]?.GetValue<string>() == "text").Select(b => b!["text"]!.GetValue<string>()));
    }
}

/// <summary>Writes the fake API's answer to one request.</summary>
public delegate Task Responder(ClaudeRequest request, HttpContext ctx);

/// <summary>What the fake API answers for one API key: a queue of responders, then <see cref="Fallback"/>.</summary>
public sealed class ClaudeScript(string apiKey)
{
    readonly ConcurrentQueue<Responder> _queue = new();
    readonly List<ClaudeRequest> _requests = [];

    public string ApiKey { get; } = apiKey;

    /// <summary>Answers requests once the queue is empty.</summary>
    public Responder Fallback { get; set; } = Claude.Text("OK.");

    public IReadOnlyList<ClaudeRequest> Requests { get { lock (_requests) return _requests.ToList(); } }

    public ClaudeScript Then(Responder r)
    {
        _queue.Enqueue(r);
        return this;
    }

    public void Reset(Responder? fallback = null)
    {
        _queue.Clear();
        lock (_requests) _requests.Clear();
        Fallback = fallback ?? Claude.Text("OK.");
    }

    internal Responder Next(ClaudeRequest r)
    {
        lock (_requests) _requests.Add(r);
        return _queue.TryDequeue(out var next) ? next : Fallback;
    }

    /// <summary>Waits until at least <paramref name="count"/> requests arrived; returns the last one.</summary>
    public async Task<ClaudeRequest> WaitForRequestsAsync(int count, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        while (true)
        {
            var all = Requests;
            if (all.Count >= count) return all[count - 1];
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"Expected {count} requests to the fake Claude API, got {all.Count}.");
            await Task.Delay(10);
        }
    }
}

/// <summary>
/// A local stand-in for <c>POST /v1/messages</c> (streamed). One server for the whole test run; each
/// test registers its own API key, so tests running in parallel never see each other's requests.
/// Unknown keys get a 401 like the real API.
/// </summary>
public sealed class FakeAnthropic
{
    static readonly Lazy<FakeAnthropic> s_shared = new(() =>
    {
        var f = new FakeAnthropic();
        f.Start();
        // Everything that builds an AnthropicChatModel from now on (the hub included) talks to this
        Environment.SetEnvironmentVariable("PUCKSLEY_ANTHROPIC_BASE_URL", f.Url);
        return f;
    });

    public static FakeAnthropic Shared => s_shared.Value;

    readonly ConcurrentDictionary<string, ClaudeScript> _scripts = new();
    WebApplication? _app;

    public string Url { get; private set; } = "";

    public ClaudeScript Script(string? apiKey = null)
    {
        var s = new ClaudeScript(apiKey ?? "sk-ant-test-" + Guid.NewGuid().ToString("n"));
        _scripts[s.ApiKey] = s;
        return s;
    }

    void Start()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0));
        _app = builder.Build();
        _app.MapPost("/v1/messages", HandleAsync);
        _app.StartAsync().GetAwaiter().GetResult();
        Url = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First().TrimEnd('/');
    }

    async Task HandleAsync(HttpContext ctx)
    {
        string body;
        using (var reader = new StreamReader(ctx.Request.Body)) body = await reader.ReadToEndAsync();
        // The C# SDK sends the key as "Authorization: Bearer …"; other clients use x-api-key
        var key = ctx.Request.Headers["x-api-key"].ToString();
        var auth = ctx.Request.Headers.Authorization.ToString();
        if (key.Length == 0 && auth.StartsWith("Bearer ", StringComparison.Ordinal)) key = auth["Bearer ".Length..];
        if (!_scripts.TryGetValue(key, out var script))
        {
            await Claude.Error(401, "authentication_error", "invalid x-api-key")(null!, ctx);
            return;
        }
        var request = new ClaudeRequest
        {
            Body = JsonNode.Parse(body)!.AsObject(),
            Headers = ctx.Request.Headers.ToDictionary(h => h.Key.ToLowerInvariant(), h => h.Value.ToString()),
            PathAndQuery = ctx.Request.Path + ctx.Request.QueryString,
        };
        using var reg = ctx.RequestAborted.Register(() => request.Aborted.TrySetResult());
        try
        {
            await script.Next(request)(request, ctx);
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
    }
}

/// <summary>One content block of a streamed reply: its <c>content_block_start</c> and its deltas.</summary>
public sealed record SseBlock(JsonObject Start, IReadOnlyList<JsonObject> Deltas);

/// <summary>Builders for the fake API's answers, in the documented streaming wire format.</summary>
public static class Claude
{
    public const string Model = "claude-opus-5-5";

    /// <summary>A plain answer: one text block, streamed in small pieces, <c>end_turn</c>.</summary>
    public static Responder Text(string text, int delayMs = 0) => Reply("end_turn", [TextBlock(text)], delayMs);

    /// <summary>
    /// A streamed message: message_start, a ping, each block (start, deltas, stop), message_delta
    /// with the stop reason, message_stop. Options cut it short in the ways real streams fail.
    /// </summary>
    public static Responder Reply(string stopReason, IReadOnlyList<SseBlock> blocks, int delayMs = 0, bool messageStop = true,
        JsonObject? errorAfterBlocks = null, bool abortAfterBlocks = false, string model = Model, Func<Task>? beforeEnd = null) =>
        async (req, ctx) =>
        {
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "text/event-stream; charset=utf-8";
            ctx.Response.Headers.CacheControl = "no-cache";
            ctx.Response.Headers["request-id"] = "req_" + Guid.NewGuid().ToString("n");
            var ct = ctx.RequestAborted;

            async Task Send(string type, JsonObject data)
            {
                if (delayMs > 0) await Task.Delay(delayMs, ct);
                await ctx.Response.WriteAsync($"event: {type}\ndata: {data.ToJsonString()}\n\n", ct);
                await ctx.Response.Body.FlushAsync(ct);
            }

            await Send("message_start", new JsonObject
            {
                ["type"] = "message_start",
                ["message"] = new JsonObject
                {
                    ["id"] = "msg_" + Guid.NewGuid().ToString("n")[..24],
                    ["type"] = "message",
                    ["role"] = "assistant",
                    ["model"] = model,
                    ["content"] = new JsonArray(),
                    ["stop_reason"] = null,
                    ["stop_sequence"] = null,
                    ["usage"] = new JsonObject { ["input_tokens"] = 25, ["output_tokens"] = 1 },
                },
            });
            await Send("ping", new JsonObject { ["type"] = "ping" });
            for (int i = 0; i < blocks.Count; i++)
            {
                await Send("content_block_start", new JsonObject { ["type"] = "content_block_start", ["index"] = i, ["content_block"] = blocks[i].Start.DeepClone() });
                foreach (var d in blocks[i].Deltas)
                    await Send("content_block_delta", new JsonObject { ["type"] = "content_block_delta", ["index"] = i, ["delta"] = d.DeepClone() });
                await Send("content_block_stop", new JsonObject { ["type"] = "content_block_stop", ["index"] = i });
            }
            if (abortAfterBlocks)
            {
                // A reset throws away whatever the client hasn't read yet, headers included: on a fast
                // machine it could arrive before the client had even seen the reply start, which looks
                // like a plain network error (and gets retried). Let it read what was sent, as it would
                // have in real life, then drop the connection.
                await Task.Delay(200, ct);
                ctx.Abort();
                return;
            }
            if (errorAfterBlocks is not null)
            {
                await Send("error", new JsonObject { ["type"] = "error", ["error"] = errorAfterBlocks.DeepClone() });
                return;
            }
            if (beforeEnd is not null) await beforeEnd();
            await Send("message_delta", new JsonObject
            {
                ["type"] = "message_delta",
                ["delta"] = new JsonObject { ["stop_reason"] = stopReason, ["stop_sequence"] = null },
                ["usage"] = new JsonObject { ["output_tokens"] = 42 },
            });
            if (messageStop) await Send("message_stop", new JsonObject { ["type"] = "message_stop" });
        };

    public static SseBlock TextBlock(string text, int piece = 9)
    {
        var deltas = new List<JsonObject>();
        for (int i = 0; i < text.Length; i += piece)
            deltas.Add(new JsonObject { ["type"] = "text_delta", ["text"] = text.Substring(i, Math.Min(piece, text.Length - i)) });
        return new SseBlock(new JsonObject { ["type"] = "text", ["text"] = "" }, deltas);
    }

    /// <summary>A client tool call; the input JSON arrives in <c>input_json_delta</c> fragments (first one empty, as the API does).</summary>
    public static SseBlock ToolUse(string id, string name, string inputJson, int piece = 7)
    {
        var deltas = new List<JsonObject> { new() { ["type"] = "input_json_delta", ["partial_json"] = "" } };
        for (int i = 0; i < inputJson.Length; i += piece)
            deltas.Add(new JsonObject { ["type"] = "input_json_delta", ["partial_json"] = inputJson.Substring(i, Math.Min(piece, inputJson.Length - i)) });
        return new SseBlock(new JsonObject { ["type"] = "tool_use", ["id"] = id, ["name"] = name, ["input"] = new JsonObject() }, deltas);
    }

    /// <summary>An adaptive-thinking block with the thinking omitted (the default display), only a signature.</summary>
    public static SseBlock Thinking(string signature) =>
        new(new JsonObject { ["type"] = "thinking", ["thinking"] = "", ["signature"] = "" },
            [new JsonObject { ["type"] = "signature_delta", ["signature"] = signature }]);

    public static SseBlock ServerToolUse(string id, string name, string inputJson) =>
        new(new JsonObject { ["type"] = "server_tool_use", ["id"] = id, ["name"] = name, ["input"] = new JsonObject() },
            [new JsonObject { ["type"] = "input_json_delta", ["partial_json"] = inputJson }]);

    public static SseBlock WebSearchResult(string toolUseId) =>
        new(new JsonObject
        {
            ["type"] = "web_search_tool_result",
            ["tool_use_id"] = toolUseId,
            ["content"] = new JsonArray(new JsonObject
            {
                ["type"] = "web_search_result",
                ["title"] = "Wellington weather",
                ["url"] = "https://example.com/weather",
                ["encrypted_content"] = "EqgfCioIARgBIiQ3YTAwMjY1Mi1mZjM5LTQ1NGUtODgxNC1kNjNjNTk1ZWI3Y2MSDGp4",
                ["page_age"] = null,
            }),
        }, []);

    /// <summary>The marker a server-side fallback puts where one model's output gives way to the next.</summary>
    public static SseBlock Fallback(string from, string to) =>
        new(new JsonObject
        {
            ["type"] = "fallback",
            ["from"] = new JsonObject { ["model"] = from },
            ["to"] = new JsonObject { ["model"] = to },
            ["trigger"] = new JsonObject { ["type"] = "refusal", ["category"] = "cyber" },
        }, []);

    /// <summary>An HTTP error with the documented error body.</summary>
    public static Responder Error(int status, string type, string message, params (string Name, string Value)[] headers) =>
        async (req, ctx) =>
        {
            ctx.Response.StatusCode = status;
            foreach (var (n, v) in headers) ctx.Response.Headers[n] = v;
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsync(new JsonObject
            {
                ["type"] = "error",
                ["error"] = new JsonObject { ["type"] = type, ["message"] = message },
                ["request_id"] = "req_" + Guid.NewGuid().ToString("n"),
            }.ToJsonString());
        };

    /// <summary>Drops the connection without answering (a network failure).</summary>
    public static Responder Abort() => (req, ctx) =>
    {
        ctx.Abort();
        return Task.CompletedTask;
    };

    /// <summary>Never answers; returns once the hub gives up on the request.</summary>
    public static Responder Hang() => async (req, ctx) =>
    {
        try { await Task.Delay(Timeout.Infinite, ctx.RequestAborted); }
        catch (OperationCanceledException) { }
    };
}
