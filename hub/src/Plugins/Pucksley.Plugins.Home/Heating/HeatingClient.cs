using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Pucksley.Plugins.Shared;
using Pucksley.Sdk;

namespace Pucksley.Plugins.Heating;

/// <summary>The UFH controller's cloud REST API (Azure Functions, Clerk JWT or API key). Thread-safe.</summary>
internal sealed class HeatingClient
{
    // A cloud function may be cold-starting
    static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    readonly HttpClient _http;
    readonly string _token;
    readonly bool _apiKey;
    readonly Action? _onUnauthorized;

    public HeatingClient(HttpClient http, string baseUrl, string token, string authHeader, Action? onUnauthorized = null)
    {
        _http = http;
        _token = token.Trim();
        _apiKey = authHeader.Trim().Equals("x-api-key", StringComparison.OrdinalIgnoreCase);
        _onUnauthorized = onUnauthorized;
        var url = baseUrl.Trim().TrimEnd('/');
        if (!url.Contains("://")) url = "https://" + url;
        BaseUri = new Uri(url + "/");
    }

    public Uri BaseUri { get; }

    public async Task<HeatingState> GetStateAsync(CancellationToken cancel) =>
        HeatingState.Parse(await SendAsync(HttpMethod.Get, "state", null, cancel).ConfigureAwait(false));

    public Task SetSetpointAsync(double setpointC, CancellationToken cancel) =>
        SendAsync(HttpMethod.Post, "setpoint", new JsonObject { ["setpoint_c"] = Math.Round(setpointC, 1) }, cancel);

    public Task SetOffsetAsync(string group, double offsetC, CancellationToken cancel) =>
        SendAsync(HttpMethod.Post, $"groups/{Uri.EscapeDataString(group)}/offset", new JsonObject { ["offset_c"] = Math.Round(offsetC, 1) }, cancel);

    public Task SetOverrideAsync(string group, string kind, long durationMs, CancellationToken cancel) =>
        SendAsync(HttpMethod.Post, $"groups/{Uri.EscapeDataString(group)}/override", new JsonObject { ["kind"] = kind, ["duration_ms"] = durationMs }, cancel);

    public Task ClearOverrideAsync(string group, CancellationToken cancel) =>
        SendAsync(HttpMethod.Delete, $"groups/{Uri.EscapeDataString(group)}/override", null, cancel);

    public async Task<IReadOnlyList<HistoryRow>> GetHistoryAsync(string floor, DateTimeOffset from, DateTimeOffset to, int maxPoints, CancellationToken cancel)
    {
        var query = $"from={Uri.EscapeDataString(from.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture))}" +
                    $"&to={Uri.EscapeDataString(to.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture))}&maxPoints={maxPoints}";
        return HeatingState.ParseHistory(await SendAsync(HttpMethod.Get, $"history/floor/{Uri.EscapeDataString(floor)}?{query}", null, cancel).ConfigureAwait(false));
    }

    async Task<string> SendAsync(HttpMethod method, string path, JsonNode? body, CancellationToken cancel) =>
        await DeviceCall.RunAsync("the heating API", Timeout, cancel, async c =>
        {
            using var request = new HttpRequestMessage(method, new Uri(BaseUri, path));
            if (_apiKey) request.Headers.Add("x-api-key", _token);
            else request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
            if (body is not null) request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
            using var response = await _http.SendAsync(request, c).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync(c).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                _onUnauthorized?.Invoke();
                throw new PluginException("The heating API rejected the token (it may have expired). Paste a new one on the hub's Plugins page.");
            }
            if (!response.IsSuccessStatusCode)
            {
                var message = Message(text);
                throw new PluginException((int)response.StatusCode == 422
                    ? $"The heating controller refused that{(message is null ? "" : $": {message}")}."
                    : $"The heating API answered {(int)response.StatusCode}{(message is null ? "" : $": {message}")}.");
            }
            return text;
        }).ConfigureAwait(false);

    static string? Message(string body)
    {
        var node = Json.Parse(body);
        var message = Json.Str(node, "error") ?? Json.Str(node, "message") ?? Json.Str(node, "detail") ?? Json.Str(node, "title");
        if (message is null && node is null && body.Length > 0) message = body.Length > 160 ? body[..160] : body;
        return message?.Trim().TrimEnd('.');
    }
}
