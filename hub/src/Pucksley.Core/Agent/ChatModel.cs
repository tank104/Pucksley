using System.Text.Json.Nodes;
using Pucksley.Sdk;

namespace Pucksley.Core.Agent;

/// <summary>
/// A language model the assistant can run on. Claude (Anthropic) is the one built in; another
/// provider plugs in behind this interface without touching the voice pipeline or the plugins.
/// </summary>
public interface IChatModel
{
    string Id { get; }

    /// <summary>A new, empty conversation.</summary>
    IChatSession CreateSession();
}

/// <summary>One conversation: keeps the provider's own history (append-only).</summary>
public interface IChatSession
{
    /// <summary>Completed user turns so far.</summary>
    int Turns { get; }

    /// <summary>
    /// Runs one user turn to the end: streams the answer, calls tools through
    /// <paramref name="callbacks"/>, and loops until the model is done.
    /// </summary>
    Task<TurnResult> RunTurnAsync(TurnRequest request, ITurnCallbacks callbacks, CancellationToken cancel);
}

/// <summary>Everything one turn needs.</summary>
public sealed record TurnRequest
{
    public required string SystemPrompt { get; init; }
    public required string UserText { get; init; }
    public required IReadOnlyList<ToolDefinition> Tools { get; init; }
    public required string ApiKey { get; init; }
    public required string Model { get; init; }
    public string Effort { get; init; } = "low";
    public int MaxTokens { get; init; } = 16000;
    public int MaxToolRounds { get; init; } = 10;
    public bool WebSearch { get; init; } = true;
    /// <summary>The plain web search (at most two), not the slower one that filters results with code: for answers someone is waiting to hear.</summary>
    public bool QuickWebSearch { get; init; }
    /// <summary>For web search results near home.</summary>
    public string? City { get; init; }
    public string? Country { get; init; }
    public string? TimeZone { get; init; }
}

/// <summary>How a turn reports progress and runs tools.</summary>
public interface ITurnCallbacks
{
    /// <summary>A piece of the answer (streamed). Text written before a tool call arrives here too.</summary>
    void OnText(string delta);

    /// <summary>A tool call is about to run (or a server tool such as web search started).</summary>
    void OnToolStarted(string toolName, JsonObject? input);

    /// <summary>Runs a client tool. Never throws for tool failures (returns an error result).</summary>
    Task<ToolResult> ExecuteToolAsync(string toolName, JsonObject input, CancellationToken cancel);
}

public sealed record TurnResult(string Text, bool Failed = false)
{
    public static TurnResult Error(string message) => new(message, true);
}
