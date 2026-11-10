using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using Pucksley.Sdk;

namespace Pucksley.Plugins.Speech;

/// <summary>
/// HTTP plumbing shared by the cloud engines: sending with clear errors, multipart forms and JSON answers.
/// </summary>
internal static class SpeechHttp
{
    // Error codes services use for "out of credit" (OpenAI 429, ElevenLabs 401…), which deserve a different message than a bad key.
    static readonly HashSet<string> QuotaCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        "insufficient_quota", "quota_exceeded", "billing_hard_limit_reached", "payment_required",
    };

    /// <summary>
    /// Sends <paramref name="request"/>. Every failure becomes a <see cref="PluginException"/> a person can act on
    /// (with a <see cref="ServiceError"/> inside); cancellation by <paramref name="cancel"/> stays an
    /// <see cref="OperationCanceledException"/>. Use <see cref="HttpCompletionOption.ResponseHeadersRead"/> to stream
    /// the body (the shared client's timeout then covers only the wait for headers).
    /// </summary>
    /// <param name="keyHint">What to check when the key is rejected, if the service has a usual suspect (Azure: the region).</param>
    public static async Task<HttpResponseMessage> SendAsync(
        HttpClient http, HttpRequestMessage request, string service, HttpCompletionOption completion, CancellationToken cancel, string? keyHint = null)
    {
        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, completion, cancel).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException e)
        {
            // Not ours, so it's HttpClient.Timeout.
            throw Fail(ServiceErrorKind.Timeout, null, null, $"{service} didn't answer in time. Try again.", e);
        }
        catch (HttpRequestException e)
        {
            throw Fail(ServiceErrorKind.Network, null, e.Message, $"Couldn't reach {service} ({Reason(e)}).", e);
        }

        if (response.IsSuccessStatusCode) return response;
        using (response)
        {
            var (detail, code) = await ReadErrorAsync(response, cancel).ConfigureAwait(false);
            throw ErrorFor(service, response.StatusCode, detail, code, keyHint);
        }
    }

    /// <summary>The exception for an HTTP error status, worded for a person.</summary>
    public static PluginException ErrorFor(string service, HttpStatusCode status, string? detail, string? code, string? keyHint = null)
    {
        int s = (int)status;
        string about = string.IsNullOrEmpty(detail) ? "" : $" ({detail})";
        bool quota = code is not null && QuotaCodes.Contains(code);
        var (kind, message) = s switch
        {
            401 or 402 or 403 or 429 when quota => (ServiceErrorKind.Quota, $"{service}: out of credit or over quota{about}. Check the account's plan and billing."),
            401 => (ServiceErrorKind.Auth, $"{service}: API key rejected. {keyHint ?? "Check it on the hub's Plugins page."}"),
            402 => (ServiceErrorKind.Quota, $"{service}: payment required{about}. Check the account's plan and billing."),
            403 => (ServiceErrorKind.Auth, $"{service}: access denied{about}. Check the API key and its permissions."),
            404 => (ServiceErrorKind.NotFound, $"{service}: not found{about}. Check the address, model and voice in its settings."),
            408 or 504 => (ServiceErrorKind.Timeout, $"{service} took too long to answer. Try again."),
            413 => (ServiceErrorKind.BadRequest, $"{service}: the recording was too large."),
            429 => (ServiceErrorKind.RateLimited, $"{service}: rate limited, too many requests. Wait a moment and try again."),
            >= 500 => (ServiceErrorKind.Server, $"{service} is having trouble ({s}). Try again shortly."),
            400 or 415 or 422 => (ServiceErrorKind.BadRequest, $"{service} rejected the request{about}."),
            _ => (ServiceErrorKind.Other, $"{service} returned an error ({s}){about}."),
        };
        return new PluginException(message, new ServiceError(kind, s, detail));
    }

    public static PluginException Fail(ServiceErrorKind kind, int? status, string? detail, string message, Exception? cause = null) =>
        new(message, new ServiceError(kind, status, detail, cause));

    /// <summary>Reads a JSON answer, turning a dropped connection or garbage into a clear error.</summary>
    public static async Task<JsonNode> ReadJsonAsync(HttpResponseMessage response, string service, CancellationToken cancel)
    {
        try
        {
            await using var body = await response.Content.ReadAsStreamAsync(cancel).ConfigureAwait(false);
            return await JsonNode.ParseAsync(body, cancellationToken: cancel).ConfigureAwait(false)
                   ?? throw new JsonException("The answer was empty.");
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            throw;
        }
        catch (JsonException e)
        {
            throw Fail(ServiceErrorKind.BadResponse, null, e.Message, $"{service} sent an answer that isn't valid JSON.", e);
        }
        catch (Exception e) when (e is IOException or HttpRequestException)
        {
            throw Fail(ServiceErrorKind.Network, null, e.Message, $"The connection to {service} dropped ({e.Message}).", e);
        }
    }

    /// <summary>A string property of a JSON object, or null.</summary>
    public static string? GetString(JsonNode? node, string property) =>
        node is JsonObject o && o[property] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    /// <summary>A file part, with quoted names (some servers reject .NET's unquoted <c>name=file</c>).</summary>
    public static void AddFile(this MultipartFormDataContent form, string name, byte[] data, string fileName, string mediaType)
    {
        var part = new ByteArrayContent(data);
        part.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
        part.Headers.ContentDisposition = new ContentDispositionHeaderValue("form-data") { Name = Quote(name), FileName = Quote(fileName) };
        form.Add(part);
    }

    /// <summary>A plain form field, sent the way browsers do (no Content-Type of its own).</summary>
    public static void AddField(this MultipartFormDataContent form, string name, string value)
    {
        var part = new StringContent(value);
        part.Headers.ContentType = null;
        part.Headers.ContentDisposition = new ContentDispositionHeaderValue("form-data") { Name = Quote(name) };
        form.Add(part);
    }

    static string Quote(string s) => $"\"{s}\"";

    static string Reason(HttpRequestException e) => e.HttpRequestError switch
    {
        HttpRequestError.NameResolutionError => "its address wasn't found",
        HttpRequestError.ConnectionError => "the connection failed",
        HttpRequestError.SecureConnectionError => "the secure connection failed",
        _ => e.InnerException?.Message ?? e.Message,
    };

    static async Task<(string? Detail, string? Code)> ReadErrorAsync(HttpResponseMessage response, CancellationToken cancel)
    {
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            limit.CancelAfter(TimeSpan.FromSeconds(5));
            return ParseError(await response.Content.ReadAsStringAsync(limit.Token).ConfigureAwait(false));
        }
        catch (Exception) when (!cancel.IsCancellationRequested)
        {
            return (null, null);
        }
    }

    /// <summary>
    /// Pulls the message and code out of an error body, wherever the service puts them: <c>{"error":{"message","code"}}</c>
    /// (OpenAI, Groq, Azure), <c>{"detail":{"status","message"}}</c> (ElevenLabs), <c>{"err_code","err_msg"}</c> (Deepgram), <c>{"detail":"…"}</c> or
    /// <c>{"detail":[{"msg"}]}</c> (FastAPI servers), <c>{"message":"…"}</c>, or plain text.
    /// </summary>
    internal static (string? Detail, string? Code) ParseError(string? body)
    {
        body = body?.Trim();
        if (string.IsNullOrEmpty(body)) return (null, null);
        if (body[0] is '{' or '[')
        {
            try
            {
                var (message, code) = FindMessage(JsonNode.Parse(body));
                if (message is not null || code is not null) return (Shorten(message), code);
            }
            catch (JsonException)
            {
            }
        }
        // An HTML error page says nothing useful.
        return body[0] == '<' ? (null, null) : (Shorten(body), null);
    }

    static (string? Message, string? Code) FindMessage(JsonNode? node)
    {
        switch (node)
        {
            case JsonValue v when v.TryGetValue<string>(out var text):
                return (text, null);
            case JsonArray { Count: > 0 } a:
                return FindMessage(a[0]);
            case JsonObject o:
                foreach (var key in (string[])["error", "detail"])
                {
                    if (o[key] is not { } inner) continue;
                    var found = FindMessage(inner);
                    if (found.Message is not null || found.Code is not null) return found;
                }
                return (Text(o["message"]) ?? Text(o["msg"]) ?? Text(o["err_msg"]) ?? Text(o["error_description"]),
                        Text(o["code"]) ?? Text(o["err_code"]) ?? Text(o["status"]) ?? Text(o["type"]));
            default:
                return (null, null);
        }
    }

    static string? Text(JsonNode? node) => node is JsonValue v
        ? v.TryGetValue<string>(out var s) ? s : v.ToJsonString()
        : null;

    static string? Shorten(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = string.Join(' ', s.Split((char[])['\r', '\n', '\t', ' '], StringSplitOptions.RemoveEmptyEntries)).TrimEnd('.');
        return s.Length <= 200 ? s : s[..197] + "...";
    }
}
