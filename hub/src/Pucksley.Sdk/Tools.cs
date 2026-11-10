using System.Text.Json.Nodes;

namespace Pucksley.Sdk;

/// <summary>
/// Makes a method a tool Claude can call. The JSON schema is built from the parameters.
/// </summary>
/// <remarks>
/// <para>Supported parameter types: <c>string</c>, <c>int</c>, <c>long</c>, <c>double</c>, <c>float</c>,
/// <c>bool</c>, enums (sent as their names), <c>string[]</c>/<c>List&lt;string&gt;</c>,
/// <c>int[]</c>/<c>List&lt;int&gt;</c>, and <c>JsonObject</c>/<c>JsonNode</c> (free-form). A parameter is
/// optional when it is nullable or has a default value. Describe each with <see cref="ParamAttribute"/>.</para>
/// <para>Two parameter types are filled in by the hub instead of Claude: <see cref="CancellationToken"/>
/// and <see cref="ToolCaller"/> (which puck/room is asking).</para>
/// <para>Return <c>string</c>, <see cref="ToolResult"/>, any object (sent as JSON), or a <c>Task</c> of
/// those. Throw <see cref="PluginException"/> with a message for the person ("Spotify isn't
/// connected…"); any other exception is reported as a tool error too.</para>
/// <example><code>
/// [Tool("sonos_set_volume", "Set a Sonos room's volume (0-100) or change it by a relative amount.")]
/// public async Task&lt;string&gt; SetVolume(
///     [Param("Room name, e.g. Kitchen")] string room,
///     [Param("Absolute volume 0-100")] int? volume = null,
///     [Param("Relative change, e.g. -10")] int? change = null,
///     CancellationToken cancel = default) { … }
/// </code></example>
/// </remarks>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class ToolAttribute(string name, string description) : Attribute
{
    /// <summary>Unique across all plugins: <c>^[a-z0-9_]{1,64}$</c>, prefixed with the plugin, e.g. <c>sonos_play</c>.</summary>
    public string Name { get; } = name;

    /// <summary>What it does and when to use it — this is what Claude reads to decide. Be specific and brief.</summary>
    public string Description { get; } = description;

    /// <summary>Ask the person on the puck before running (shutdown, purchases, deleting things).</summary>
    public bool Confirm { get; init; }

    /// <summary>Overrides the plugin's activity icon for this tool.</summary>
    public string? Icon { get; init; }
}

/// <summary>Describes a tool parameter for Claude.</summary>
[AttributeUsage(AttributeTargets.Parameter, AllowMultiple = false)]
public sealed class ParamAttribute(string description) : Attribute
{
    public string Description { get; } = description;

    /// <summary>Allowed values for a string parameter (an enum parameter gets its names automatically).</summary>
    public string[]? Values { get; init; }

    /// <summary>Inclusive minimum for a number (NaN = none).</summary>
    public double Min { get; init; } = double.NaN;

    /// <summary>Inclusive maximum for a number (NaN = none).</summary>
    public double Max { get; init; } = double.NaN;
}

/// <summary>A tool: what Claude sees (name, description, JSON schema) and the code that runs it.</summary>
public sealed class ToolDefinition
{
    /// <summary><c>^[a-z0-9_]{1,64}$</c>, unique across all plugins.</summary>
    public required string Name { get; init; }

    public required string Description { get; init; }

    /// <summary>A JSON schema object: <c>{"type":"object","properties":{…},"required":[…]}</c>.</summary>
    public required JsonObject InputSchema { get; init; }

    public required Func<ToolInvocation, Task<ToolResult>> Handler { get; init; }

    /// <summary>Ask the person on the puck before running.</summary>
    public bool Confirm { get; init; }

    /// <summary>Activity icon (see <see cref="IPlugin.Icon"/>).</summary>
    public string? Icon { get; init; }

    /// <summary>Set by the hub: the plugin that owns this tool.</summary>
    public string? PluginId { get; set; }
}

/// <summary>One call of a tool.</summary>
public sealed class ToolInvocation
{
    /// <summary>The arguments Claude sent (already parsed; may be missing optional fields).</summary>
    public required JsonObject Arguments { get; init; }

    public required ToolCaller Caller { get; init; }

    public CancellationToken Cancellation { get; init; }
}

/// <summary>
/// Who is asking: which puck, and the room it's in (set per puck in the hub). Plugins use the room
/// as the default target ("turn it down" from the kitchen puck means the kitchen speaker).
/// </summary>
public sealed record ToolCaller(string? PuckId, string? PuckName, string? Room)
{
    /// <summary>A request typed in the hub's web page (no puck).</summary>
    public static ToolCaller Web { get; } = new(null, "web page", null);
}

/// <summary>What a tool hands back to Claude.</summary>
public sealed class ToolResult
{
    /// <summary>Text (or JSON) for Claude. Keep it compact: it costs tokens and time.</summary>
    public string Text { get; init; } = "";

    public bool IsError { get; init; }

    /// <summary>An image for Claude to look at (PNG or JPEG bytes), e.g. a screenshot.</summary>
    public byte[]? Image { get; init; }

    /// <summary><c>image/png</c> or <c>image/jpeg</c>.</summary>
    public string? ImageMediaType { get; init; }

    /// <summary>Short text for the puck's activity line ("Kitchen 30%"); null = the hub makes one up.</summary>
    public string? Activity { get; init; }

    public static ToolResult Ok(string text, string? activity = null) => new() { Text = text, Activity = activity };

    public static ToolResult Error(string text) => new() { Text = text, IsError = true };

    public static implicit operator ToolResult(string text) => Ok(text);
}

/// <summary>
/// A failure with a message meant for the person (Claude will relay it), e.g. "Spotify isn't
/// connected. Connect it on the hub's Plugins page."
/// </summary>
public sealed class PluginException(string message, Exception? inner = null) : Exception(message, inner);
