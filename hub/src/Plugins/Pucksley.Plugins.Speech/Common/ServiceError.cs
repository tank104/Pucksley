namespace Pucksley.Plugins.Speech;

/// <summary>What went wrong talking to a speech service.</summary>
internal enum ServiceErrorKind
{
    /// <summary>The service didn't like the request (bad voice, model, audio…).</summary>
    BadRequest,

    /// <summary>The key was rejected or lacks permission.</summary>
    Auth,

    /// <summary>Out of credit / over quota.</summary>
    Quota,

    /// <summary>Wrong address, model or voice.</summary>
    NotFound,

    RateLimited,
    Timeout,
    Server,

    /// <summary>Couldn't connect, or the connection dropped.</summary>
    Network,

    /// <summary>The answer wasn't what the API promised.</summary>
    BadResponse,

    Other,
}

/// <summary>
/// The details behind a speech call's <see cref="Pucksley.Sdk.PluginException"/> (which is sealed, so this rides
/// along as its <see cref="Exception.InnerException"/>). Lets plugins tell a rejected key, which the person must
/// fix, from a hiccup worth retrying.
/// </summary>
internal sealed class ServiceError(ServiceErrorKind kind, int? statusCode, string? detail, Exception? inner = null)
    : Exception(detail ?? kind.ToString(), inner)
{
    public ServiceErrorKind Kind { get; } = kind;

    /// <summary>The HTTP status, when there was one.</summary>
    public int? StatusCode { get; } = statusCode;

    /// <summary>The service's own words, if it gave any.</summary>
    public string? Detail { get; } = detail;
}
