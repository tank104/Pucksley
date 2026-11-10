using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text.Json.Nodes;
using Pucksley.Plugins.Shared;
using Pucksley.Sdk;

namespace Pucksley.Plugins.Tv;

/// <summary>LG webOS TVs over SSAP. Needs pairing once (a prompt on the TV); power-on is Wake-on-LAN.</summary>
internal sealed class LgDriver(TvDevice device, PairingStore keys) : ITvDriver
{
    const string Type = "lg";
    static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    static readonly TimeSpan PairWait = TimeSpan.FromSeconds(30);

    readonly SemaphoreSlim _connectGate = new(1, 1);
    WebOsClient? _client;

    public TvDevice Device => device;
    public string Label => $"{device.Name.Trim()} TV";
    string Host => device.Host.Trim();
    public bool NeedsPairing => keys.Get(Type, Host) is null;

    public async Task<TvPower> GetPowerAsync(CancellationToken cancel)
    {
        if (NeedsPairing) return await PortOpenAsync(cancel).ConfigureAwait(false) ? TvPower.On : TvPower.Off;
        try
        {
            var state = await RequestAsync("ssap://com.webos.service.tvpower/power/getPowerState", null, cancel).ConfigureAwait(false);
            return ParsePowerState(Json.Str(state, "state"));
        }
        catch (PluginException)
        {
            // Older sets don't have getPowerState: answering at all means it's on
            return _client?.IsOpen == true ? TvPower.On : TvPower.Off;
        }
    }

    /// <summary>webOS power states: "Active" = on; screen off / standby variants; anything else off.</summary>
    internal static TvPower ParsePowerState(string? state) => state switch
    {
        null => TvPower.Unknown,
        "Active" => TvPower.On,
        "Active Standby" or "Screen Off" or "Screen Saver" => TvPower.Standby,
        _ => TvPower.Off,
    };

    public async Task<string> PowerAsync(bool on, CancellationToken cancel)
    {
        if (on)
        {
            if (string.IsNullOrWhiteSpace(device.Mac))
                throw new PluginException($"To turn on the {Label} I need its MAC address (Wake-on-LAN). Add it in the TV plugin's settings.");
            await LanTools.WakeOnLanAsync(device.Mac, cancel).ConfigureAwait(false);
            return $"Turning on the {Label}.";
        }
        await RequestAsync("ssap://system/turnOff", null, cancel).ConfigureAwait(false);
        await DropClientAsync().ConfigureAwait(false);
        return $"Turned off the {Label}.";
    }

    public async Task<string> VolumeAsync(int? volume, int? change, bool? mute, CancellationToken cancel)
    {
        var parts = new List<string>();
        if (mute is bool m)
        {
            await RequestAsync("ssap://audio/setMute", new JsonObject { ["mute"] = m }, cancel).ConfigureAwait(false);
            parts.Add(m ? "muted" : "unmuted");
        }
        int? target = volume;
        if (target is null && change is int delta)
        {
            var (current, _) = ParseVolume(await RequestAsync("ssap://audio/getVolume", null, cancel).ConfigureAwait(false));
            if (current is int c) target = c + delta;
            else
            {
                // No reading: step instead
                for (int i = 0; i < Math.Min(Math.Abs(delta), 25); i++)
                    await RequestAsync(delta > 0 ? "ssap://audio/volumeUp" : "ssap://audio/volumeDown", null, cancel).ConfigureAwait(false);
                parts.Add($"volume {(delta > 0 ? "up" : "down")} {Math.Abs(delta)}");
            }
        }
        if (target is int t)
        {
            t = Math.Clamp(t, 0, 100);
            await RequestAsync("ssap://audio/setVolume", new JsonObject { ["volume"] = t }, cancel).ConfigureAwait(false);
            parts.Add($"volume {t}");
        }
        if (parts.Count == 0)
        {
            var (current, muted) = ParseVolume(await RequestAsync("ssap://audio/getVolume", null, cancel).ConfigureAwait(false));
            return $"The {Label} volume is {current?.ToString() ?? "unknown"}{(muted == true ? " (muted)" : "")}.";
        }
        return $"{Text.Capitalize(Label)}: {string.Join(", ", parts)}.";
    }

    /// <summary><c>audio/getVolume</c>: older sets reply <c>{volume, muted}</c>, newer ones <c>{volumeStatus: {volume, muteStatus}}</c>.</summary>
    internal static (int? Volume, bool? Muted) ParseVolume(JsonObject payload)
    {
        var volume = Json.Num(payload, "volume") ?? Json.Num(payload, "volumeStatus.volume");
        var muted = Json.Bool(payload, "muted") ?? Json.Bool(payload, "volumeStatus.muteStatus");
        return (volume is double v ? (int)Math.Round(v) : null, muted);
    }

    public async Task<string> InputAsync(string input, CancellationToken cancel)
    {
        if (TvKeys.IsLiveTv(input))
        {
            await RequestAsync("ssap://system.launcher/launch", new JsonObject { ["id"] = "com.webos.app.livetv" }, cancel).ConfigureAwait(false);
            return $"The {Label} is on live TV.";
        }
        var list = await RequestAsync("ssap://tv/getExternalInputList", null, cancel).ConfigureAwait(false);
        var inputs = (list["devices"] as JsonArray ?? [])
            .Select(d => new TvItem(Json.Str(d, "id") ?? "", Json.Str(d, "label") ?? Json.Str(d, "id") ?? ""))
            .Where(d => d.Id.Length > 0)
            .ToList();
        var chosen = MatchInput(input, inputs)
                     ?? throw new PluginException($"The {Label} has no input '{input}'. Inputs: {Text.List(inputs.Select(i => i.Name))}.");
        await RequestAsync("ssap://tv/switchInput", new JsonObject { ["inputId"] = chosen.Id }, cancel).ConfigureAwait(false);
        return $"The {Label} is on {chosen.Name}.";
    }

    /// <summary>"HDMI 2" matches id HDMI_2 / label "HDMI 2"; otherwise by label ("Xbox"), then by id.</summary>
    internal static TvItem? MatchInput(string spoken, IReadOnlyList<TvItem> inputs)
    {
        if (TvKeys.HdmiNumber(spoken) is int n)
        {
            var byNumber = inputs.FirstOrDefault(i => TvKeys.HdmiNumber(i.Id.Replace('_', ' ')) == n || TvKeys.HdmiNumber(i.Name) == n);
            if (byNumber is not null) return byNumber;
        }
        return Names.Best(spoken, inputs, i => i.Name) ?? Names.Best(spoken, inputs, i => i.Id.Replace('_', ' '));
    }

    public async Task<string> AppAsync(string app, CancellationToken cancel)
    {
        var list = await RequestAsync("ssap://com.webos.applicationManager/listLaunchPoints", null, cancel).ConfigureAwait(false);
        var apps = (list["launchPoints"] as JsonArray ?? [])
            .Select(a => new TvItem(Json.Str(a, "id") ?? "", Json.Str(a, "title") ?? ""))
            .Where(a => a.Id.Length > 0 && a.Name.Length > 0)
            .ToList();
        var chosen = Names.Best(app, apps, a => a.Name)
                     ?? throw new PluginException($"The {Label} has no app called '{app}'. Apps include {Text.List(apps.Take(12).Select(a => a.Name))}.");
        await RequestAsync("ssap://system.launcher/launch", new JsonObject { ["id"] = chosen.Id }, cancel).ConfigureAwait(false);
        return $"Opening {chosen.Name} on the {Label}.";
    }

    public async Task<string> MediaAsync(string action, CancellationToken cancel)
    {
        var a = TvKeys.Action(action);
        if (a is not ("play" or "pause" or "stop"))
            throw new PluginException($"I can only play, pause or stop on the {Label}.");
        await RequestAsync($"ssap://media.controls/{a}", null, cancel).ConfigureAwait(false);
        return $"{Text.Capitalize(Label)}: {a}.";
    }

    public async Task<string> ChannelAsync(bool up, CancellationToken cancel)
    {
        await RequestAsync(up ? "ssap://tv/channelUp" : "ssap://tv/channelDown", null, cancel).ConfigureAwait(false);
        return $"{Text.Capitalize(Label)}: channel {(up ? "up" : "down")}.";
    }

    public async Task<string> MessageAsync(string text, CancellationToken cancel)
    {
        await RequestAsync("ssap://system.notifications/createToast", new JsonObject { ["message"] = text }, cancel).ConfigureAwait(false);
        return $"Showed the message on the {Label}.";
    }

    public async Task<string> PairAsync(CancellationToken cancel)
    {
        await DropClientAsync().ConfigureAwait(false);
        var client = await DeviceCall.RunAsync($"the {Label}", Timeout, cancel, c => WebOsClient.ConnectAsync(Host, c)).ConfigureAwait(false);
        try
        {
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            wait.CancelAfter(PairWait);
            var key = await client.RegisterAsync(keys.Get(Type, Host), wait.Token).ConfigureAwait(false);
            keys.Set(Type, Host, key);
            return $"Paired with the {Label}.";
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
        {
            throw new PluginException($"Nobody accepted the prompt on the {Label} within {PairWait.TotalSeconds:0} seconds. Click Pair again, then accept it on the TV.");
        }
        catch (WebSocketException e)
        {
            throw new PluginException($"The {Label} closed the connection while pairing ({e.Message.TrimEnd('.')}).", e);
        }
        finally
        {
            await client.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>A request on the (re)used connection, retried once on a fresh connection if the old one went stale.</summary>
    async Task<JsonObject> RequestAsync(string uri, JsonObject? payload, CancellationToken cancel) =>
        await DeviceCall.RunAsync($"the {Label}", Timeout, cancel, async c =>
        {
            for (int attempt = 0; ; attempt++)
            {
                var client = await ClientAsync(c).ConfigureAwait(false);
                try
                {
                    return await client.RequestAsync(uri, payload?.DeepClone() as JsonObject, c).ConfigureAwait(false);
                }
                catch (WebSocketException) when (attempt == 0)
                {
                    await DropClientAsync().ConfigureAwait(false);
                }
            }
        }).ConfigureAwait(false);

    async Task<WebOsClient> ClientAsync(CancellationToken cancel)
    {
        await _connectGate.WaitAsync(cancel).ConfigureAwait(false);
        try
        {
            if (_client is { IsOpen: true } open) return open;
            if (_client is not null) await _client.DisposeAsync().ConfigureAwait(false);
            _client = null;
            var key = keys.Get(Type, Host)
                      ?? throw new PluginException($"The {Label} isn't paired yet. On the hub's Plugins page click Pair on the TVs card, then accept the prompt on the TV.");
            var client = await WebOsClient.ConnectAsync(Host, cancel).ConfigureAwait(false);
            try
            {
                var newKey = await client.RegisterAsync(key, cancel).ConfigureAwait(false);
                if (newKey != key) keys.Set(Type, Host, newKey);
            }
            catch
            {
                await client.DisposeAsync().ConfigureAwait(false);
                throw;
            }
            return _client = client;
        }
        finally
        {
            _connectGate.Release();
        }
    }

    async Task DropClientAsync()
    {
        await _connectGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_client is not null) await _client.DisposeAsync().ConfigureAwait(false);
            _client = null;
        }
        finally
        {
            _connectGate.Release();
        }
    }

    async Task<bool> PortOpenAsync(CancellationToken cancel)
    {
        foreach (var port in new[] { 3001, 3000 })
        {
            using var tcp = new TcpClient();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            cts.CancelAfter(TimeSpan.FromSeconds(1.5));
            try
            {
                await tcp.ConnectAsync(Host, port, cts.Token).ConfigureAwait(false);
                return true;
            }
            catch (Exception e) when (e is SocketException or OperationCanceledException)
            {
            }
        }
        return false;
    }

    public async ValueTask DisposeAsync() => await DropClientAsync().ConfigureAwait(false);
}
