using Pucksley.Sdk;

namespace Pucksley.Plugins.Speech;

/// <summary>
/// Base for the speech plugins. They're enabled by default because they do nothing until picked in the hub's
/// Voice settings. The status says what's set up or what's missing; a rejected key, an empty account or an
/// unreachable server reported by a real call stays in the status until a call succeeds again.
/// </summary>
public abstract class SpeechPlugin<TSettings> : PuckPlugin<TSettings> where TSettings : class, new()
{
    int _callProblem;  // 1 while the status shows a failed call instead of the setup

    public override bool EnabledByDefault => true;
    public override string Icon => "tool";

    /// <summary>The status when no call has failed: "Ready: …", or what's missing ("Add an API key").</summary>
    protected abstract PluginStatus DescribeSetup();

    public override Task StartAsync(CancellationToken cancel)
    {
        ShowSetup();
        return Task.CompletedTask;
    }

    /// <summary>Shows <see cref="DescribeSetup"/>, forgetting any earlier call failure.</summary>
    protected void ShowSetup()
    {
        Volatile.Write(ref _callProblem, 0);
        SetStatus(DescribeSetup());
    }

    /// <summary>
    /// <see cref="SpeechHttp.SendAsync"/> through the shared client, keeping the status honest: problems the person
    /// must fix (key, quota, address) and unreachable servers show until a call works again.
    /// </summary>
    internal async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, string service, HttpCompletionOption completion, CancellationToken cancel, string? keyHint = null)
    {
        try
        {
            var response = await SpeechHttp.SendAsync(Context.Http, request, service, completion, cancel, keyHint).ConfigureAwait(false);
            if (Interlocked.Exchange(ref _callProblem, 0) == 1) SetStatus(DescribeSetup());
            return response;
        }
        catch (PluginException e) when (e.InnerException is ServiceError { Kind: var kind } && ShowsInStatus(kind))
        {
            Interlocked.Exchange(ref _callProblem, 1);
            SetStatus(kind == ServiceErrorKind.Network ? StatusLevel.Warning : StatusLevel.Error, e.Message);
            throw;
        }
    }

    static bool ShowsInStatus(ServiceErrorKind kind) =>
        kind is ServiceErrorKind.Auth or ServiceErrorKind.Quota or ServiceErrorKind.NotFound or ServiceErrorKind.Network;

    /// <summary>The trimmed value, or <paramref name="fallback"/> when it's blank.</summary>
    internal static string Or(string? value, string fallback) => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    internal static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
