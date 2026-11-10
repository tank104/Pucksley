using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Authentication;
using Pucksley.Plugins.Shared;
using Pucksley.Sdk;

namespace Pucksley.Plugins.Tv;

/// <summary>
/// Samsung Tizen TVs (2016+): remote keys over the paired WebSocket channel, apps and power state
/// over the REST API on port 8001, power-on by Wake-on-LAN. There's no absolute volume: only steps.
/// </summary>
internal sealed class SamsungDriver(TvDevice device, PairingStore keys, HttpClient http) : ITvDriver
{
    const string Type = "samsung";
    const int MaxSteps = 25;
    static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    static readonly TimeSpan StepsTimeout = TimeSpan.FromSeconds(10);
    static readonly TimeSpan PairWait = TimeSpan.FromSeconds(30);
    static readonly TimeSpan StepGap = TimeSpan.FromMilliseconds(120);

    public TvDevice Device => device;
    public string Label => $"{device.Name.Trim()} TV";
    string Host => device.Host.Trim();
    public bool NeedsPairing => keys.Get(Type, Host) is null;

    public async Task<TvPower> GetPowerAsync(CancellationToken cancel)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            cts.CancelAfter(TimeSpan.FromSeconds(2.5));
            var body = await http.GetStringAsync($"http://{Host}:8001/api/v2/", cts.Token).ConfigureAwait(false);
            return ParsePowerState(body);
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException && !cancel.IsCancellationRequested)
        {
            return TvPower.Off; // not answering at all: off (or off the network)
        }
    }

    /// <summary><c>GET :8001/api/v2/</c> → <c>device.PowerState</c> ("on" / "standby"); older models leave it out (answering = on).</summary>
    internal static TvPower ParsePowerState(string json)
    {
        var state = Json.Str(Json.Parse(json), "device.PowerState");
        return state?.ToLowerInvariant() switch
        {
            null => TvPower.On,
            "on" => TvPower.On,
            "standby" => TvPower.Standby,
            _ => TvPower.Unknown,
        };
    }

    public async Task<string> PowerAsync(bool on, CancellationToken cancel)
    {
        var power = await GetPowerAsync(cancel).ConfigureAwait(false);
        if (on)
        {
            if (power == TvPower.On) return $"The {Label} is already on.";
            if (power == TvPower.Standby && !NeedsPairing)
            {
                // Standby still runs the remote channel: the power key wakes it
                await KeysAsync(["KEY_POWER"], Timeout, cancel).ConfigureAwait(false);
                return $"Turning on the {Label}.";
            }
            if (string.IsNullOrWhiteSpace(device.Mac))
                throw new PluginException($"To turn on the {Label} I need its MAC address (Wake-on-LAN). Add it in the TV plugin's settings.");
            await LanTools.WakeOnLanAsync(device.Mac, cancel).ConfigureAwait(false);
            return $"Turning on the {Label}.";
        }
        if (power is TvPower.Off or TvPower.Standby) return $"The {Label} is already off.";
        // KEY_POWER toggles, so it's only sent when the TV says it's on
        await KeysAsync(["KEY_POWER"], Timeout, cancel).ConfigureAwait(false);
        return $"Turned off the {Label}.";
    }

    public async Task<string> VolumeAsync(int? volume, int? change, bool? mute, CancellationToken cancel)
    {
        if (volume is not null && change is null && mute is null)
            throw new PluginException($"Samsung TVs can't be set to an exact volume, only stepped up or down. Say e.g. 'turn the {Label} up 5'.");
        var keysToSend = new List<string>();
        var parts = new List<string>();
        if (mute is not null)
        {
            keysToSend.Add("KEY_MUTE");
            parts.Add("toggled mute (the TV doesn't say whether it's muted)");
        }
        if (change is int delta && delta != 0)
        {
            int steps = Math.Min(Math.Abs(delta), MaxSteps);
            keysToSend.AddRange(Enumerable.Repeat(delta > 0 ? "KEY_VOLUP" : "KEY_VOLDOWN", steps));
            parts.Add($"volume {(delta > 0 ? "up" : "down")} {steps} step{(steps == 1 ? "" : "s")} (Samsung TVs only step the volume)");
        }
        if (keysToSend.Count == 0) throw new PluginException("Say how much to change the volume, e.g. up 5, or mute.");
        await KeysAsync(keysToSend, StepsTimeout, cancel).ConfigureAwait(false);
        return $"{Text.Capitalize(Label)}: {string.Join(", ", parts)}.";
    }

    public async Task<string> InputAsync(string input, CancellationToken cancel)
    {
        var key = TvKeys.SamsungInputKey(input);
        await KeysAsync([key], Timeout, cancel).ConfigureAwait(false);
        return key switch
        {
            "KEY_SOURCE" => $"Opened the source menu on the {Label} (it has no direct key for '{input}').",
            "KEY_HDMI" => $"Switched the {Label} to the next HDMI input.",
            "KEY_TV" => $"The {Label} is on live TV.",
            _ => $"Switched the {Label} to HDMI {key[^1]}.",
        };
    }

    public async Task<string> AppAsync(string app, CancellationToken cancel)
    {
        if (TvKeys.SamsungAppId(app) is string id)
        {
            var name = TvKeys.SamsungApps.First(kv => kv.Value == id).Key;
            if (await RestLaunchAsync(id, cancel).ConfigureAwait(false)) return $"Opening {name} on the {Label}.";
            await WithRemoteAsync(Timeout, cancel, (r, c) => r.LaunchAsync(id, deepLink: true, c)).ConfigureAwait(false);
            return $"Opening {name} on the {Label}.";
        }
        // Not one we know: ask the TV what's installed
        TvItem? chosen = null;
        await WithRemoteAsync(Timeout, cancel, async (r, c) =>
        {
            var apps = await r.InstalledAppsAsync(c).ConfigureAwait(false);
            var match = Names.Best(app, apps, a => a.Name)
                        ?? throw new PluginException($"The {Label} has no app called '{app}'. Apps include {Text.List(apps.Take(12).Select(a => a.Name))}.");
            chosen = new TvItem(match.Id, match.Name);
            await r.LaunchAsync(match.Id, match.DeepLink, c).ConfigureAwait(false);
        }).ConfigureAwait(false);
        return $"Opening {chosen!.Name} on the {Label}.";
    }

    public async Task<string> MediaAsync(string action, CancellationToken cancel)
    {
        var key = TvKeys.SamsungMediaKey(action);
        await KeysAsync([key], Timeout, cancel).ConfigureAwait(false);
        return $"{Text.Capitalize(Label)}: {TvKeys.Action(action)}.";
    }

    public async Task<string> ChannelAsync(bool up, CancellationToken cancel)
    {
        await KeysAsync([up ? "KEY_CHUP" : "KEY_CHDOWN"], Timeout, cancel).ConfigureAwait(false);
        return $"{Text.Capitalize(Label)}: channel {(up ? "up" : "down")}.";
    }

    public Task<string> MessageAsync(string text, CancellationToken cancel) =>
        throw new PluginException("Only LG TVs can show messages on screen.");

    public async Task<string> PairAsync(CancellationToken cancel)
    {
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        wait.CancelAfter(PairWait);
        try
        {
            // The TV shows "Allow Pucksley?"; the connection is answered once someone chooses
            await using var remote = await SamsungRemote.ConnectAsync(Host, keys.Get(Type, Host), wait.Token).ConfigureAwait(false);
            if (string.IsNullOrEmpty(remote.Token))
                return $"Connected to the {Label}, but it didn't issue a token; it may not need pairing.";
            keys.Set(Type, Host, remote.Token);
            return $"Paired with the {Label}.";
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
        {
            throw new PluginException($"Nobody accepted the prompt on the {Label} within {PairWait.TotalSeconds:0} seconds. Click Pair again, then choose Allow on the TV.");
        }
        catch (Exception e) when (e is WebSocketException or HttpRequestException or IOException or AuthenticationException or SocketException)
        {
            throw new PluginException($"Couldn't reach the {Label} ({e.Message.TrimEnd('.')}). Is it on?", e);
        }
    }

    /// <summary>Sends keys on one connection, a short gap apart (the TV drops keys sent back to back).</summary>
    Task KeysAsync(IReadOnlyList<string> keyNames, TimeSpan timeout, CancellationToken cancel) =>
        WithRemoteAsync(timeout, cancel, async (remote, c) =>
        {
            for (int i = 0; i < keyNames.Count; i++)
            {
                if (i > 0) await Task.Delay(StepGap, c).ConfigureAwait(false);
                await remote.SendKeyAsync(keyNames[i], c).ConfigureAwait(false);
            }
        });

    Task WithRemoteAsync(TimeSpan timeout, CancellationToken cancel, Func<SamsungRemote, CancellationToken, Task> action) =>
        DeviceCall.RunAsync($"the {Label}", timeout, cancel, async c =>
        {
            var token = keys.Get(Type, Host)
                        ?? throw new PluginException($"The {Label} isn't paired yet. On the hub's Plugins page click Pair on the TVs card, then choose Allow on the TV.");
            await using var remote = await SamsungRemote.ConnectAsync(Host, token, c).ConfigureAwait(false);
            if (remote.Token is { } issued && issued != token) keys.Set(Type, Host, issued);
            await action(remote, c).ConfigureAwait(false);
        });

    async Task<bool> RestLaunchAsync(string appId, CancellationToken cancel)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            cts.CancelAfter(Timeout);
            using var response = await http.PostAsync($"http://{Host}:8001/api/v2/applications/{appId}", null, cts.Token).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException && !cancel.IsCancellationRequested)
        {
            return false;
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
