using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Pucksley.Plugins.Shared;
using Pucksley.Sdk;

namespace Pucksley.Plugins.HomeAssistant;

/// <summary>The bits of Home Assistant's REST API the plugin uses. Thread-safe.</summary>
internal sealed class HaClient
{
    static readonly TimeSpan QuickTimeout = TimeSpan.FromSeconds(5);
    static readonly TimeSpan StatesTimeout = TimeSpan.FromSeconds(10);
    // Service calls block until the device acts (Zigbee, cloud integrations…), so allow longer.
    static readonly TimeSpan ServiceTimeout = TimeSpan.FromSeconds(10);
    // Assist may hand the sentence to an LLM conversation agent.
    static readonly TimeSpan AssistTimeout = TimeSpan.FromSeconds(20);

    readonly HttpClient _http;
    readonly string _token;
    readonly Action? _onUnauthorized;

    public HaClient(HttpClient http, string url, string token, Action? onUnauthorized = null)
    {
        _http = http;
        _token = token.Trim();
        _onUnauthorized = onUnauthorized;
        BaseUri = new Uri(NormalizeUrl(url) + "/");
    }

    public Uri BaseUri { get; }

    /// <summary>"homeassistant.local:8123/api/" → "http://homeassistant.local:8123".</summary>
    public static string NormalizeUrl(string url)
    {
        var u = url.Trim().TrimEnd('/');
        if (u.EndsWith("/api", StringComparison.OrdinalIgnoreCase)) u = u[..^4];
        if (!u.Contains("://")) u = "http://" + u;
        return u;
    }

    /// <summary><c>GET /api/</c>: checks the URL and token.</summary>
    public Task<string> PingAsync(CancellationToken cancel) => SendAsync(HttpMethod.Get, "api/", null, QuickTimeout, cancel);

    public async Task<IReadOnlyList<HaEntity>> GetStatesAsync(CancellationToken cancel)
    {
        var text = await SendAsync(HttpMethod.Get, "api/states", null, StatesTimeout, cancel).ConfigureAwait(false);
        return Json.Parse(text) is JsonArray arr ? arr.Select(n => HaEntity.FromJson(n)).OfType<HaEntity>().ToList() : [];
    }

    public async Task<HaEntity?> GetStateAsync(string entityId, CancellationToken cancel)
    {
        var text = await SendAsync(HttpMethod.Get, "api/states/" + Uri.EscapeDataString(entityId), null, QuickTimeout, cancel).ConfigureAwait(false);
        return HaEntity.FromJson(Json.Parse(text));
    }

    /// <summary><c>POST /api/template</c>: renders a Jinja template, returns the text.</summary>
    public Task<string> RenderTemplateAsync(string template, CancellationToken cancel) =>
        SendAsync(HttpMethod.Post, "api/template", new JsonObject { ["template"] = template }, StatesTimeout, cancel);

    /// <summary>Entity id → area name, for every entity that has one.</summary>
    public async Task<Dictionary<string, string>> GetAreasAsync(CancellationToken cancel)
    {
        const string template = "{% for s in states %}{{ s.entity_id }}|{{ area_name(s.entity_id) or '' }}\n{% endfor %}";
        var text = await RenderTemplateAsync(template, cancel).ConfigureAwait(false);
        return ParseAreas(text);
    }

    internal static Dictionary<string, string> ParseAreas(string text)
    {
        var areas = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in text.Split('\n'))
        {
            int bar = line.IndexOf('|');
            if (bar <= 0) continue;
            var area = line[(bar + 1)..].Trim();
            if (area.Length > 0 && area != "None") areas[line[..bar].Trim()] = area;
        }
        return areas;
    }

    /// <summary><c>POST /api/services/{domain}/{service}</c>; returns the states that changed.</summary>
    public async Task<IReadOnlyList<HaEntity>> CallServiceAsync(string domain, string service, JsonObject data, CancellationToken cancel)
    {
        var text = await SendAsync(HttpMethod.Post, $"api/services/{Uri.EscapeDataString(domain)}/{Uri.EscapeDataString(service)}", data, ServiceTimeout, cancel)
            .ConfigureAwait(false);
        // A list of changed states; newer versions wrap it as {"changed_states": [...]} when a response is asked for.
        var node = Json.Parse(text);
        var list = node as JsonArray ?? node?["changed_states"] as JsonArray;
        return list is null ? [] : list.Select(n => HaEntity.FromJson(n)).OfType<HaEntity>().ToList();
    }

    /// <summary><c>POST /api/conversation/process</c>: Assist's spoken answer and whether it was an error.</summary>
    public async Task<(string Speech, bool IsError)> AssistAsync(string text, string language, CancellationToken cancel)
    {
        var body = new JsonObject { ["text"] = text, ["language"] = string.IsNullOrWhiteSpace(language) ? "en" : language.Trim() };
        var result = Json.Parse(await SendAsync(HttpMethod.Post, "api/conversation/process", body, AssistTimeout, cancel).ConfigureAwait(false));
        var speech = Json.Str(result, "response.speech.plain.speech");
        bool error = Json.Str(result, "response.response_type") == "error";
        return (string.IsNullOrWhiteSpace(speech) ? (error ? "Home Assistant couldn't do that." : "Done.") : speech.Trim(), error);
    }

    async Task<string> SendAsync(HttpMethod method, string path, JsonNode? body, TimeSpan timeout, CancellationToken cancel) =>
        await DeviceCall.RunAsync("Home Assistant", timeout, cancel, async c =>
        {
            using var request = new HttpRequestMessage(method, new Uri(BaseUri, path));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
            if (body is not null) request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
            using var response = await _http.SendAsync(request, c).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync(c).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                _onUnauthorized?.Invoke();
                throw new PluginException("Home Assistant rejected the access token. Create a new long-lived token (profile → Security) and paste it on the hub's Plugins page.");
            }
            if (!response.IsSuccessStatusCode) throw new PluginException(ErrorText(response.StatusCode, text, path));
            return text;
        }).ConfigureAwait(false);

    static string ErrorText(HttpStatusCode status, string body, string path)
    {
        var message = Json.Str(Json.Parse(body), "message") ?? (body.Length > 200 ? body[..200] : body).Trim();
        if (status == HttpStatusCode.NotFound && path.StartsWith("api/services/", StringComparison.Ordinal))
            return $"Home Assistant has no service {path["api/services/".Length..].Replace('/', '.')}.";
        if (status == HttpStatusCode.NotFound && path.StartsWith("api/states/", StringComparison.Ordinal))
            return $"Home Assistant has no entity {Uri.UnescapeDataString(path["api/states/".Length..])}.";
        return string.IsNullOrEmpty(message)
            ? $"Home Assistant answered {(int)status} {status}."
            : $"Home Assistant said: {message} ({(int)status}).";
    }
}
