using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using Pucksley.Plugins.Shared;
using Pucksley.Sdk;

namespace Pucksley.Plugins.Tv;

/// <summary>An installed app as the TV lists it; DEEP_LINK launches suit store apps (app_type 2).</summary>
internal sealed record SamsungApp(string Id, string Name, bool DeepLink);

/// <summary>
/// The Samsung Tizen remote-control channel: <c>wss://host:8002/api/v2/channels/samsung.remote.control</c>.
/// The first connection shows "allow Pucksley?" on the TV; the <c>ms.channel.connect</c> event then
/// carries a token that skips the prompt next time. Used for one operation at a time, then closed.
/// </summary>
internal sealed class SamsungRemote : IAsyncDisposable
{
    public const string ClientName = "Pucksley";

    /// <summary>Events the TV may send before the one that answers the connection.</summary>
    static readonly HashSet<string> StartupNoise =
        ["ed.edenTV.update", "ms.voiceApp.hide", "ms.remote.touchEnable", "ms.remote.touchDisable", "ms.channel.ready", "ms.channel.clientConnect", "ms.channel.clientDisconnect"];

    readonly ClientWebSocket _ws;

    SamsungRemote(ClientWebSocket ws, string? token)
    {
        _ws = ws;
        Token = token;
    }

    /// <summary>The token to keep (a new one when the TV issued one).</summary>
    public string? Token { get; }

    public static Uri Url(string host, string? token)
    {
        var name = Uri.EscapeDataString(Convert.ToBase64String(Encoding.UTF8.GetBytes(ClientName)));
        var url = $"wss://{host}:8002/api/v2/channels/samsung.remote.control?name={name}";
        if (!string.IsNullOrEmpty(token)) url += "&token=" + Uri.EscapeDataString(token);
        return new Uri(url);
    }

    /// <summary>
    /// Connects and waits for the TV to accept. Without a valid token the TV shows a prompt and this
    /// waits until someone answers or <paramref name="cancel"/> fires.
    /// </summary>
    public static async Task<SamsungRemote> ConnectAsync(string host, string? token, CancellationToken cancel)
    {
        var ws = await TvSocket.ConnectAsync(Url(host, token), cancel).ConfigureAwait(false);
        try
        {
            while (true)
            {
                var text = await TvSocket.ReceiveAsync(ws, cancel).ConfigureAwait(false)
                           ?? throw new WebSocketException("The TV closed the connection.");
                var message = Json.Parse(text);
                var ev = Json.Str(message, "event") ?? "";
                if (StartupNoise.Contains(ev)) continue;
                switch (ev)
                {
                    case "ms.channel.connect":
                        return new SamsungRemote(ws, Json.Str(message, "data.token") is { Length: > 0 } issued ? issued : token);
                    case "ms.channel.unauthorized":
                        throw new PluginException("The TV didn't allow Pucksley. Pair again and choose Allow on the TV (or check Settings → General → External Device Manager → Device Connection Manager).");
                    case "ms.channel.timeOut":
                        throw new PluginException("Nobody answered the prompt on the TV in time.");
                    case "ms.error":
                        throw new PluginException($"The TV said: {Json.Str(message, "data.message") ?? "error"}.");
                }
            }
        }
        catch
        {
            await TvSocket.CloseAsync(ws).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>A key press, e.g. KEY_VOLUP.</summary>
    public Task SendKeyAsync(string key, CancellationToken cancel) => SendAsync(KeyMessage(key), cancel);

    internal static JsonObject KeyMessage(string key) => new()
    {
        ["method"] = "ms.remote.control",
        ["params"] = new JsonObject { ["Cmd"] = "Click", ["DataOfCmd"] = key, ["Option"] = "false", ["TypeOfRemote"] = "SendRemoteKey" },
    };

    /// <summary>Launches an app through the channel (for when the REST call is refused). DEEP_LINK for store apps, NATIVE_LAUNCH for built-ins.</summary>
    public Task LaunchAsync(string appId, bool deepLink, CancellationToken cancel) => SendAsync(new JsonObject
    {
        ["method"] = "ms.channel.emit",
        ["params"] = new JsonObject
        {
            ["event"] = "ed.apps.launch",
            ["to"] = "host",
            ["data"] = new JsonObject { ["appId"] = appId, ["action_type"] = deepLink ? "DEEP_LINK" : "NATIVE_LAUNCH" },
        },
    }, cancel);

    /// <summary>The installed apps (id, name, and whether it's a store app).</summary>
    public async Task<IReadOnlyList<SamsungApp>> InstalledAppsAsync(CancellationToken cancel)
    {
        await SendAsync(new JsonObject { ["method"] = "ms.channel.emit", ["params"] = new JsonObject { ["event"] = "ed.installedApp.get", ["to"] = "host" } }, cancel)
            .ConfigureAwait(false);
        while (true)
        {
            var text = await TvSocket.ReceiveAsync(_ws, cancel).ConfigureAwait(false) ?? throw new WebSocketException("The TV closed the connection.");
            var message = Json.Parse(text);
            if (Json.Str(message, "event") != "ed.installedApp.get") continue;
            return (Json.At(message, "data.data") as JsonArray ?? [])
                .Select(a => new SamsungApp(Json.Str(a, "appId") ?? "", Json.Str(a, "name") ?? "", Json.Num(a, "app_type") == 2))
                .Where(a => a.Id.Length > 0 && a.Name.Length > 0)
                .ToList();
        }
    }

    Task SendAsync(JsonObject message, CancellationToken cancel) => TvSocket.SendAsync(_ws, message.ToJsonString(), cancel);

    public async ValueTask DisposeAsync()
    {
        // Give the TV a moment to act on the last key before the socket goes away
        try
        {
            await Task.Delay(150).ConfigureAwait(false);
        }
        finally
        {
            await TvSocket.CloseAsync(_ws).ConfigureAwait(false);
        }
    }
}
