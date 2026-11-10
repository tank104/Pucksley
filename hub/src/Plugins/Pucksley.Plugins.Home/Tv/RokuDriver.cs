using System.Net;
using System.Xml.Linq;
using Pucksley.Plugins.Shared;
using Pucksley.Sdk;

namespace Pucksley.Plugins.Tv;

/// <summary>
/// Roku TVs and players over External Control (ECP, <c>http://host:8060</c>): key presses, app
/// launches and device info. No pairing; Roku OS must allow "Control by mobile apps".
/// </summary>
internal sealed class RokuDriver(TvDevice device, HttpClient http) : ITvDriver
{
    const int MaxSteps = 25;
    static readonly TimeSpan Timeout = TimeSpan.FromSeconds(4);
    static readonly TimeSpan StepsTimeout = TimeSpan.FromSeconds(10);
    static readonly TimeSpan StepGap = TimeSpan.FromMilliseconds(100);

    public TvDevice Device => device;
    public string Label => $"{device.Name.Trim()} TV";
    public bool NeedsPairing => false;
    string Base => $"http://{device.Host.Trim()}:8060/";

    public async Task<TvPower> GetPowerAsync(CancellationToken cancel)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            cts.CancelAfter(TimeSpan.FromSeconds(2.5));
            return ParsePowerMode(await http.GetStringAsync(Base + "query/device-info", cts.Token).ConfigureAwait(false));
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException && !cancel.IsCancellationRequested)
        {
            return TvPower.Off;
        }
    }

    /// <summary><c>/query/device-info</c> → <c>power-mode</c>: PowerOn, or DisplayOff / Ready / Headless / Suspend in standby.</summary>
    internal static TvPower ParsePowerMode(string xml)
    {
        var mode = Element(xml, "power-mode");
        return mode switch
        {
            null => TvPower.On, // players without the field are on whenever they answer
            "PowerOn" => TvPower.On,
            "DisplayOff" or "Ready" or "Headless" or "Suspend" => TvPower.Standby,
            _ => TvPower.Unknown,
        };
    }

    /// <summary>The device's own name from <c>/query/device-info</c>.</summary>
    internal static string? DeviceName(string xml) =>
        Element(xml, "user-device-name") ?? Element(xml, "friendly-device-name") ?? Element(xml, "default-device-name") ?? Element(xml, "model-name");

    /// <summary><c>/query/apps</c>: <c>&lt;app id="12"&gt;Netflix&lt;/app&gt;</c>.</summary>
    internal static IReadOnlyList<TvItem> ParseApps(string xml)
    {
        try
        {
            return XDocument.Parse(xml).Root?.Elements("app")
                .Select(a => new TvItem((string?)a.Attribute("id") ?? "", a.Value.Trim()))
                .Where(a => a.Id.Length > 0 && a.Name.Length > 0)
                .ToList() ?? [];
        }
        catch (System.Xml.XmlException)
        {
            return [];
        }
    }

    public async Task<string> PowerAsync(bool on, CancellationToken cancel)
    {
        var power = await GetPowerAsync(cancel).ConfigureAwait(false);
        if (on)
        {
            if (power == TvPower.On) return $"The {Label} is already on.";
            if (power == TvPower.Off)
            {
                if (string.IsNullOrWhiteSpace(device.Mac))
                    throw new PluginException($"The {Label} isn't answering. To wake it I need its MAC address (Wake-on-LAN), or turn on Fast TV start on the Roku.");
                await LanTools.WakeOnLanAsync(device.Mac, cancel).ConfigureAwait(false);
                return $"Turning on the {Label}.";
            }
            await KeysAsync(["PowerOn"], Timeout, cancel).ConfigureAwait(false);
            return $"Turning on the {Label}.";
        }
        if (power is TvPower.Off or TvPower.Standby) return $"The {Label} is already off.";
        await KeysAsync(["PowerOff"], Timeout, cancel).ConfigureAwait(false);
        return $"Turned off the {Label}.";
    }

    public async Task<string> VolumeAsync(int? volume, int? change, bool? mute, CancellationToken cancel)
    {
        if (volume is not null && change is null && mute is null)
            throw new PluginException($"Roku TVs can't be set to an exact volume, only stepped up or down. Say e.g. 'turn the {Label} up 5'.");
        var keys = new List<string>();
        var parts = new List<string>();
        if (mute is not null)
        {
            keys.Add("VolumeMute");
            parts.Add("toggled mute");
        }
        if (change is int delta && delta != 0)
        {
            int steps = Math.Min(Math.Abs(delta), MaxSteps);
            keys.AddRange(Enumerable.Repeat(delta > 0 ? "VolumeUp" : "VolumeDown", steps));
            parts.Add($"volume {(delta > 0 ? "up" : "down")} {steps} step{(steps == 1 ? "" : "s")} (Roku only steps the volume)");
        }
        if (keys.Count == 0) throw new PluginException("Say how much to change the volume, e.g. up 5, or mute.");
        await KeysAsync(keys, StepsTimeout, cancel).ConfigureAwait(false);
        return $"{Text.Capitalize(Label)}: {string.Join(", ", parts)}.";
    }

    public async Task<string> InputAsync(string input, CancellationToken cancel)
    {
        var key = TvKeys.RokuInputKey(input) ?? throw new PluginException("Roku TVs switch to HDMI 1-4, live TV or AV.");
        await KeysAsync([key], Timeout, cancel).ConfigureAwait(false);
        return $"Switched the {Label} to {(key == "InputTuner" ? "live TV" : key.Replace("Input", "").Replace("HDMI", "HDMI "))}.";
    }

    public async Task<string> AppAsync(string app, CancellationToken cancel)
    {
        var apps = await DeviceCall.RunAsync($"the {Label}", Timeout, cancel, async c =>
            ParseApps(await http.GetStringAsync(Base + "query/apps", c).ConfigureAwait(false))).ConfigureAwait(false);
        var chosen = Names.Best(app, apps, a => a.Name)
                     ?? throw new PluginException($"The {Label} has no app called '{app}'. Apps include {Text.List(apps.Take(12).Select(a => a.Name))}.");
        await PostAsync("launch/" + Uri.EscapeDataString(chosen.Id), Timeout, cancel).ConfigureAwait(false);
        return $"Opening {chosen.Name} on the {Label}.";
    }

    public async Task<string> MediaAsync(string action, CancellationToken cancel)
    {
        var key = TvKeys.RokuMediaKey(action);
        await KeysAsync([key], Timeout, cancel).ConfigureAwait(false);
        var a = TvKeys.Action(action);
        return a is "play" or "pause" ? $"{Text.Capitalize(Label)}: play/pause (Roku has one key for both)." : $"{Text.Capitalize(Label)}: {a}.";
    }

    public async Task<string> ChannelAsync(bool up, CancellationToken cancel)
    {
        await KeysAsync([up ? "ChannelUp" : "ChannelDown"], Timeout, cancel).ConfigureAwait(false);
        return $"{Text.Capitalize(Label)}: channel {(up ? "up" : "down")}.";
    }

    public Task<string> MessageAsync(string text, CancellationToken cancel) =>
        throw new PluginException("Only LG TVs can show messages on screen.");

    public async Task<string> PairAsync(CancellationToken cancel)
    {
        var info = await DeviceCall.RunAsync($"the {Label}", Timeout, cancel, c => http.GetStringAsync(Base + "query/device-info", c)).ConfigureAwait(false);
        return $"Roku TVs don't need pairing: {DeviceName(info) ?? "it"} is reachable and ready.";
    }

    Task KeysAsync(IReadOnlyList<string> keys, TimeSpan timeout, CancellationToken cancel) =>
        DeviceCall.RunAsync($"the {Label}", timeout, cancel, async c =>
        {
            for (int i = 0; i < keys.Count; i++)
            {
                if (i > 0) await Task.Delay(StepGap, c).ConfigureAwait(false);
                await PostCoreAsync("keypress/" + keys[i], c).ConfigureAwait(false);
            }
        });

    Task PostAsync(string path, TimeSpan timeout, CancellationToken cancel) =>
        DeviceCall.RunAsync($"the {Label}", timeout, cancel, c => PostCoreAsync(path, c));

    async Task PostCoreAsync(string path, CancellationToken cancel)
    {
        using var response = await http.PostAsync(Base + path, null, cancel).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Forbidden)
            throw new PluginException($"The {Label} refused. On the Roku: Settings → System → Advanced system settings → Control by mobile apps → Network access: Default or Permissive.");
        if (!response.IsSuccessStatusCode) throw new PluginException($"The {Label} answered {(int)response.StatusCode}.");
    }

    static string? Element(string xml, string name)
    {
        try
        {
            var value = XDocument.Parse(xml).Root?.Element(name)?.Value.Trim();
            return string.IsNullOrEmpty(value) ? null : value;
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
