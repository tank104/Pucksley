using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Helpers;
using Anthropic.Models.Beta.Messages;
using Microsoft.Extensions.Logging;
using Pucksley.Sdk;

namespace Pucksley.Core.Agent;

/// <summary>Claude through the official Anthropic SDK (beta Messages endpoint, streamed).</summary>
public sealed class AnthropicChatModel(ILogger<AnthropicChatModel> log) : IChatModel
{
    /// <summary>Always explicit: the SDK otherwise honours ANTHROPIC_BASE_URL, which Claude Code setups
    /// often point at a gateway, and the hub's key shouldn't follow it there.</summary>
    const string ProductionUrl = "https://api.anthropic.com";

    readonly object _gate = new();
    AnthropicClient? _client;
    string? _clientKey;

    /// <summary>Another endpoint (tests point this at a local stub). PUCKSLEY_ANTHROPIC_BASE_URL sets it too.</summary>
    public string? BaseUrl { get; init; } = Environment.GetEnvironmentVariable("PUCKSLEY_ANTHROPIC_BASE_URL") is { Length: > 0 } u ? u : null;

    public string Id => "anthropic";

    public IChatSession CreateSession() => new Session(this, log);

    internal AnthropicClient Client(string apiKey)
    {
        lock (_gate)
        {
            if (_client is null || _clientKey != apiKey)
            {
                _client = new AnthropicClient { ApiKey = apiKey, BaseUrl = BaseUrl ?? ProductionUrl };
                _clientKey = apiKey;
            }
            return _client;
        }
    }

    // Models that take the server-side refusal fallback in its "default" form.
    static readonly string[] FallbackModels = ["claude-fable-5-1", "claude-opus-5-5", "claude-opus-5", "claude-sonnet-5-5"];
    // Models that take output_config.effort.
    static readonly string[] EffortPrefixes = ["claude-fable", "claude-mythos", "claude-opus-5", "claude-sonnet-5", "claude-haiku-5", "claude-opus-4-8", "claude-opus-4-7", "claude-opus-4-6", "claude-sonnet-4-6"];
    // Models that take the dynamic-filtering web tools (_20260209); older ones get the basic variants.
    static readonly string[] NewWebToolPrefixes = ["claude-fable", "claude-mythos", "claude-opus-5", "claude-sonnet-5", "claude-opus-4-8", "claude-opus-4-7", "claude-opus-4-6", "claude-sonnet-4-6"];

    static bool StartsWithAny(string model, string[] prefixes) => Array.Exists(prefixes, p => model.StartsWith(p, StringComparison.Ordinal));

    sealed class Session(AnthropicChatModel owner, ILogger log) : IChatSession
    {
        readonly List<BetaMessageParam> _history = [];
        readonly SemaphoreSlim _turnGate = new(1, 1);

        public int Turns { get; private set; }

        public async Task<TurnResult> RunTurnAsync(TurnRequest req, ITurnCallbacks cb, CancellationToken cancel)
        {
            await _turnGate.WaitAsync(cancel).ConfigureAwait(false);
            int mark = _history.Count;
            try
            {
                var client = owner.Client(req.ApiKey);
                _history.Add(new BetaUserMessageParam { Content = req.UserText });
                var answer = new StringBuilder();

                for (int round = 0; round <= req.MaxToolRounds; round++)
                {
                    var parameters = BuildParams(req, round == req.MaxToolRounds);
                    var aggregator = new BetaMessageContentAggregator();
                    bool textBlockOpen = false, afterFallback = false;
                    // Text resuming after a tool or a search reads as a new sentence and needs a space;
                    // text blocks back to back are one passage split by citations ("about 5.29%" + ", and"),
                    // so nothing goes between them. Decided at the block's first words: never before punctuation.
                    bool previousWasText = false, spaceDue = false;

                    // CollectAsync hands every event back while the aggregator keeps the whole message
                    await foreach (var ev in client.Beta.Messages.CreateStreaming(parameters, cancel).CollectAsync(aggregator).ConfigureAwait(false))
                    {
                        if (ev.TryPickContentBlockStart(out var start))
                        {
                            bool isText = start.ContentBlock.TryPickBetaText(out _);
                            if (!isText) previousWasText = false;
                            if (isText)
                            {
                                // Text after a tool or search starts a new sentence; after a fallback marker the
                                // next model carries on the same text, and back-to-back blocks are one passage
                                spaceDue = answer.Length > 0 && !char.IsWhiteSpace(answer[^1]) && !afterFallback && !previousWasText;
                                textBlockOpen = true;
                                afterFallback = false;
                            }
                            else if (start.ContentBlock.TryPickBetaServerToolUse(out var stu))
                            {
                                // Raw(), not ToString(): an ApiEnum's ToString() is its JSON, quotes included
                                cb.OnToolStarted(RawOr(stu.Name.Raw, "web_search"), null);
                            }
                            else if (start.ContentBlock.TryPickBetaFallback(out _))
                            {
                                afterFallback = true;
                            }
                        }
                        else if (ev.TryPickContentBlockDelta(out var delta) && delta.Delta.TryPickText(out var text))
                        {
                            if (!textBlockOpen) continue;
                            if (spaceDue && text.Text.Length > 0)
                            {
                                spaceDue = false;
                                if (!char.IsWhiteSpace(text.Text[0]) && ",.;:!?)%'’\"".IndexOf(text.Text[0]) < 0)
                                {
                                    answer.Append(' ');
                                    cb.OnText(" ");
                                }
                            }
                            answer.Append(text.Text);
                            cb.OnText(text.Text);
                        }
                        else if (ev.TryPickContentBlockStop(out _))
                        {
                            if (textBlockOpen) previousWasText = true;
                            textBlockOpen = false;
                        }
                    }

                    BetaMessage message = aggregator.Message();
                    string stop = message.StopReason is { } sr ? RawOr(sr.Raw, "") : "";

                    if (stop == "refusal")
                    {
                        Rollback(mark);
                        return TurnResult.Error("Sorry, I can't help with that one.");
                    }

                    var content = AfterFallback(message.Content);
                    var blocks = ToParams(content);
                    var toolUses = content.Select(b => b.TryPickToolUse(out var tu) ? tu : null).Where(tu => tu is not null).ToList();

                    if (stop == "max_tokens" && toolUses.Count > 0)
                    {
                        // A tool call cut off mid-input: never run it
                        Rollback(mark);
                        return TurnResult.Error("My answer got too long. Try asking a narrower question.");
                    }

                    if (blocks.Count > 0) _history.Add(new BetaAssistantMessageParam { Content = blocks });

                    if (stop == "pause_turn")
                        continue; // a long server-tool turn: send it back as-is to let it carry on

                    if (stop == "tool_use" && toolUses.Count > 0)
                    {
                        var results = await Task.WhenAll(toolUses.Select(tu => RunTool(tu!, cb, cancel))).ConfigureAwait(false);
                        _history.Add(new BetaUserMessageParam { Content = results.ToList() });
                        continue;
                    }

                    Turns++;
                    var final = answer.ToString().Trim();
                    return final.Length > 0 ? new TurnResult(final) : new TurnResult("Done.");
                }

                Rollback(mark);
                return TurnResult.Error("That needed too many steps, so I stopped. Try breaking it up.");
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested)
            {
                Rollback(mark);
                throw;
            }
            catch (OperationCanceledException)
            {
                // Not ours: the SDK's own request timeout
                Rollback(mark);
                log.LogWarning("Claude didn't answer in time");
                return TurnResult.Error("Claude took too long to answer. Try again.");
            }
            catch (AnthropicUnauthorizedException)
            {
                Rollback(mark);
                return TurnResult.Error("The Claude API key was rejected. Check it on the hub's Assistant page.");
            }
            catch (AnthropicNotFoundException)
            {
                Rollback(mark);
                return TurnResult.Error($"Claude doesn't know the model \"{req.Model}\". Check the hub's Assistant page.");
            }
            catch (Exception e) when (e is AnthropicRateLimitException or AnthropicOverloadedException or AnthropicServiceUnavailableException)
            {
                Rollback(mark);
                return TurnResult.Error("Claude is busy right now. Try again in a moment.");
            }
            catch (AnthropicBadRequestException e)
            {
                Rollback(mark);
                log.LogWarning("Claude rejected the request: {Message}", e.Message);
                return TurnResult.Error($"Claude couldn't take that request ({Trim(e.ApiMessage ?? e.Message)}).");
            }
            catch (AnthropicApiException e)
            {
                Rollback(mark);
                log.LogWarning("Claude API error: {Message}", e.Message);
                return TurnResult.Error($"Claude couldn't answer ({Trim(e.ApiMessage ?? e.Message)}).");
            }
            catch (AnthropicSseException e)
            {
                // An `error` event after the stream started (e.g. overloaded_error): not an AnthropicApiException
                Rollback(mark);
                log.LogWarning("Claude's answer stopped with an error: {Message}", e.Message);
                var type = RawOr(() => e.Type?.Raw() ?? "", "");
                return TurnResult.Error(type is "overloaded_error" or "rate_limit_error"
                    ? "Claude is busy right now. Try again in a moment."
                    : $"Claude couldn't answer ({Trim(e.ApiMessage ?? type)}).");
            }
            catch (AnthropicIOException e)
            {
                Rollback(mark);
                log.LogWarning("Couldn't reach Claude: {Message}", e.Message);
                return TurnResult.Error("I couldn't reach Claude. Check the internet connection.");
            }
            catch (AnthropicException e)
            {
                // The stream ended early or held something unreadable (no message_stop, bad JSON)
                Rollback(mark);
                log.LogWarning(e, "Claude's answer was cut off");
                return TurnResult.Error("Claude's answer got cut off. Try again.");
            }
            catch (Exception e) when (e is HttpRequestException or IOException)
            {
                Rollback(mark);
                log.LogWarning(e, "Couldn't reach Claude");
                return TurnResult.Error("I couldn't reach Claude. Check the internet connection.");
            }
            finally
            {
                _turnGate.Release();
            }
        }

        static string Trim(string s) => s.Length > 160 ? s[..157] + "…" : s;

        /// <summary>An ApiEnum's raw string (its ToString() is JSON, quotes and all); <paramref name="fallback"/> if it isn't one.</summary>
        static string RawOr(Func<string> raw, string fallback)
        {
            try { return raw(); }
            catch (AnthropicInvalidDataException) { return fallback; }
        }

        /// <summary>
        /// After a mid-stream server-side fallback, what goes back to the API (and gets run): text
        /// and paired server-tool blocks from before the last <c>fallback</c> marker, and everything
        /// after it. The declining model's thinking and tool calls are dropped (the documented echo
        /// rule), so its tool calls never run; the marker itself is only an audit note.
        /// </summary>
        static IReadOnlyList<BetaContentBlock> AfterFallback(IReadOnlyList<BetaContentBlock> content)
        {
            int boundary = -1;
            for (int i = 0; i < content.Count; i++)
                if (content[i].TryPickFallback(out _)) boundary = i;
            if (boundary < 0) return content;

            var shapes = content.Select(b => JsonSerializer.SerializeToElement(b)).ToList();
            string? Str(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            var before = shapes.Take(boundary).ToList();
            var resultIds = before.Select(e => Str(e, "tool_use_id")).Where(id => id is not null).ToHashSet();
            var useIds = before.Where(e => Str(e, "type") == "server_tool_use").Select(e => Str(e, "id")).ToHashSet();

            var kept = new List<BetaContentBlock>(content.Count);
            for (int i = 0; i < content.Count; i++)
            {
                if (i > boundary) { kept.Add(content[i]); continue; }
                if (i == boundary) continue;
                var type = Str(shapes[i], "type");
                bool keep = type == "text"
                            || (type == "server_tool_use" && resultIds.Contains(Str(shapes[i], "id")))
                            || (type is not null && type.EndsWith("_tool_result", StringComparison.Ordinal) && useIds.Contains(Str(shapes[i], "tool_use_id")));
                if (keep) kept.Add(content[i]);
            }
            return kept;
        }

        /// <summary>Drops an unfinished exchange so the history stays whole user/assistant turns.</summary>
        void Rollback(int mark)
        {
            if (_history.Count > mark) _history.RemoveRange(mark, _history.Count - mark);
        }

        MessageCreateParams BuildParams(TurnRequest req, bool lastRound)
        {
            // The same tools every round, even the last: a different `tools` array mid-conversation
            // misses the prompt cache and, on newer accounts, invalidates replayed thinking blocks.
            // The last round says tool_choice none instead.
            var tools = new List<BetaToolUnion>();
            foreach (var t in req.Tools)
            {
                // The whole schema ($defs, additionalProperties, enum… as MCP servers send them), not just properties/required
                var schema = new Dictionary<string, JsonElement>();
                foreach (var kv in t.InputSchema) schema[kv.Key] = JsonSerializer.SerializeToElement(kv.Value);
                tools.Add(new BetaTool
                {
                    Name = t.Name,
                    Description = t.Description,
                    InputSchema = new InputSchema(schema),
                    EagerInputStreaming = true,
                });
            }
            if (req.WebSearch)
            {
                bool latest = StartsWithAny(req.Model, NewWebToolPrefixes) && !req.QuickWebSearch;
                var location = new BetaUserLocation { City = req.City, Country = req.Country, Timezone = req.TimeZone };
                if (latest)
                {
                    tools.Add(new BetaWebSearchTool20260209 { MaxUses = 3, UserLocation = location });
                    tools.Add(new BetaWebFetchTool20260209 { MaxUses = 3 });
                }
                else
                {
                    tools.Add(new BetaWebSearchTool20250305 { MaxUses = req.QuickWebSearch ? 2 : 3, UserLocation = location });
                }
            }

            var p = new MessageCreateParams
            {
                Model = req.Model,
                MaxTokens = req.MaxTokens,
                System = new List<BetaTextBlockParam> { new() { Text = req.SystemPrompt, CacheControl = new BetaCacheControlEphemeral() } },
                Messages = [.. _history],
                Tools = tools,
                CacheControl = new BetaCacheControlEphemeral(),
            };
            if (lastRound && tools.Count > 0) p = p with { ToolChoice = new BetaToolChoiceNone() };
            if (StartsWithAny(req.Model, EffortPrefixes))
            {
                var effort = req.Effort switch { "medium" => Effort.Medium, "high" => Effort.High, _ => Effort.Low };
                p = p with { OutputConfig = new BetaOutputConfig { Effort = effort } };
            }
            if (Array.IndexOf(FallbackModels, req.Model) >= 0)
                p = p with { Betas = ["server-side-fallback-2026-07-01"], Fallbacks = new Default() };
            return p;
        }

        async Task<BetaContentBlockParam> RunTool(BetaToolUseBlock tu, ITurnCallbacks cb, CancellationToken cancel)
        {
            var input = new JsonObject();
            foreach (var kv in tu.Input) input[kv.Key] = JsonNode.Parse(kv.Value.GetRawText());
            cb.OnToolStarted(tu.Name, input);
            ToolResult result;
            try
            {
                result = await cb.ExecuteToolAsync(tu.Name, input, cancel).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception e)
            {
                result = ToolResult.Error($"The tool failed: {e.Message}");
            }

            var text = string.IsNullOrEmpty(result.Text) ? (result.IsError ? "Failed." : "Done.") : result.Text;
            if (result.Image is { Length: > 0 } image)
            {
                var content = new List<Block>
                {
                    new BetaTextBlockParam { Text = text },
                    new BetaImageBlockParam
                    {
                        Source = new BetaBase64ImageSource
                        {
                            Data = Convert.ToBase64String(image),
                            MediaType = result.ImageMediaType == "image/png" ? MediaType.ImagePng : MediaType.ImageJpeg,
                        },
                    },
                };
                return new BetaToolResultBlockParam { ToolUseID = tu.ID, Content = content, IsError = result.IsError };
            }
            return new BetaToolResultBlockParam { ToolUseID = tu.ID, Content = text, IsError = result.IsError };
        }

        /// <summary>
        /// The assistant turn as params for the next request: every block kept unchanged (thinking
        /// blocks with their signatures, server-tool blocks as they came).
        /// </summary>
        static List<BetaContentBlockParam> ToParams(IReadOnlyList<BetaContentBlock> content)
        {
            var list = new List<BetaContentBlockParam>(content.Count);
            foreach (var block in content)
            {
                if (block.TryPickText(out var t))
                {
                    if (t.Text.Length > 0) list.Add(new BetaTextBlockParam { Text = t.Text });
                }
                else if (block.TryPickThinking(out var th))
                {
                    list.Add(new BetaThinkingBlockParam { Thinking = th.Thinking, Signature = th.Signature });
                }
                else if (block.TryPickRedactedThinking(out var rt))
                {
                    list.Add(new BetaRedactedThinkingBlockParam { Data = rt.Data });
                }
                else if (block.TryPickToolUse(out var tu))
                {
                    list.Add(new BetaToolUseBlockParam { ID = tu.ID, Name = tu.Name, Input = tu.Input });
                }
                else
                {
                    // Server tool use and results (web search / fetch) and anything newer: round-trip
                    // their JSON, which is how the API expects them back.
                    var json = JsonSerializer.Serialize(block);
                    var param = JsonSerializer.Deserialize<BetaContentBlockParam>(json);
                    if (param is not null) list.Add(param);
                }
            }
            return list;
        }
    }
}
