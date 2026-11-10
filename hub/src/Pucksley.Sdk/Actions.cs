namespace Pucksley.Sdk;

/// <summary>A button on the plugin's card in the web page, with optional input boxes.</summary>
public sealed record PluginAction(string Id, string Label, string? Description = null, IReadOnlyList<ActionInput>? Inputs = null);

/// <summary>An input box shown with an action.</summary>
public sealed record ActionInput(string Key, string Label, string? Placeholder = null, bool Secret = false);

/// <summary>What happened. <see cref="OpenUrl"/> opens in a new browser tab (OAuth sign-in pages).</summary>
public sealed record ActionResult(string Message, bool Ok = true, string? OpenUrl = null);

/// <summary>A request to <c>/plugins/{id}/{Path}</c>.</summary>
public sealed record PluginHttpRequest(
    string Method,
    string Path,
    IReadOnlyDictionary<string, string> Query,
    IReadOnlyDictionary<string, string> Headers,
    string? Body);

public sealed record PluginHttpResponse(int Status, string ContentType, string Body)
{
    public static PluginHttpResponse Html(string html, int status = 200) => new(status, "text/html; charset=utf-8", html);
    public static PluginHttpResponse Text(string text, int status = 200) => new(status, "text/plain; charset=utf-8", text);
}

public enum StatusLevel { Idle, Ok, Warning, Error }

/// <summary>A line for the web page: green (Ok), amber (Warning: works but needs attention), red (Error).</summary>
public sealed record PluginStatus(StatusLevel Level, string Message)
{
    public static PluginStatus Idle { get; } = new(StatusLevel.Idle, "Not started");
}
