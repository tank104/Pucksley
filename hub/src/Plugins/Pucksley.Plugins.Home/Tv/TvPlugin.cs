using Microsoft.Extensions.Logging;
using Pucksley.Plugins.Shared;
using Pucksley.Sdk;

namespace Pucksley.Plugins.Tv;

/// <summary>Picks which TV a request means.</summary>
internal static class TvPicker
{
    /// <summary>
    /// The TV named (fuzzy), else the TV named after the puck's room, else the only/first TV.
    /// Throws, listing the TVs, when a name was given that matches none.
    /// </summary>
    public static T Pick<T>(IReadOnlyList<T> tvs, Func<T, string> nameOf, string? spoken, string? room) where T : class
    {
        if (tvs.Count == 0) throw new PluginException("No TVs are set up. Add them on the TVs plugin in the hub's web page.");
        if (!string.IsNullOrWhiteSpace(spoken) && NameMatcher.Normalize(spoken) is not ("tv" or "the tv" or "television"))
        {
            return Names.Best(spoken, tvs, nameOf)
                   ?? throw new PluginException($"I can't find a TV called '{spoken}'. TVs: {Text.List(tvs.Select(nameOf))}.");
        }
        if (!string.IsNullOrWhiteSpace(room) && Names.Best(room, tvs, nameOf, 0.7) is { } inRoom) return inRoom;
        return tvs[0];
    }
}

/// <summary>LG webOS, Samsung Tizen and Roku TVs: power, volume, inputs, apps, playback, channels, toasts.</summary>
public sealed class TvPlugin : PuckPlugin<TvSettings>
{
    volatile IReadOnlyList<ITvDriver> _tvs = [];
    PairingStore? _keys;

    public override string Id => "tv";
    public override string Name => "TVs";
    public override string Description => "Turn LG, Samsung and Roku TVs on and off, change volume, inputs and apps.";
    public override string Icon => "tv";
    public override bool EnabledByDefault => false;

    public override Task StartAsync(CancellationToken cancel)
    {
        _keys = new PairingStore(Context.DataDirectory);
        var tvs = new List<ITvDriver>();
        foreach (var device in Settings.Tvs)
        {
            if (string.IsNullOrWhiteSpace(device.Name) || string.IsNullOrWhiteSpace(device.Host)) continue;
            ITvDriver? driver = device.Type.Trim().ToLowerInvariant() switch
            {
                "lg" => new LgDriver(device, _keys),
                "samsung" => new SamsungDriver(device, _keys, Context.Http),
                "roku" => new RokuDriver(device, Context.Http),
                _ => null,
            };
            if (driver is null) Log.LogWarning("TV {Name}: unknown type '{Type}'", device.Name, device.Type);
            else tvs.Add(driver);
        }
        _tvs = tvs;
        UpdateStatus();
        return Task.CompletedTask;
    }

    public override async Task StopAsync()
    {
        var tvs = _tvs;
        _tvs = [];
        foreach (var tv in tvs) await tv.DisposeAsync().ConfigureAwait(false);
    }

    void UpdateStatus()
    {
        var tvs = _tvs;
        if (tvs.Count == 0)
        {
            SetStatus(StatusLevel.Warning, "Add your TVs below (Find TVs looks for them)");
            return;
        }
        var unpaired = tvs.Where(t => t.NeedsPairing).Select(t => t.Device.Name.Trim()).ToList();
        if (unpaired.Count > 0) SetStatus(StatusLevel.Warning, $"{Text.List(unpaired)} need{(unpaired.Count == 1 ? "s" : "")} pairing: click Pair");
        else SetStatus(StatusLevel.Ok, $"{Text.Count(tvs.Count, "TV")} ready");
    }

    ITvDriver Pick(string? tv, ToolCaller? caller) => TvPicker.Pick(_tvs, t => t.Device.Name, tv, caller?.Room);

    // ---------------------------------------------------------------- tools

    [Tool("tv_list", "List the TVs: name, kind, whether each is on, and whether it still needs pairing.")]
    public async Task<object> List(CancellationToken cancel = default)
    {
        var tvs = _tvs;
        if (tvs.Count == 0) throw new PluginException("No TVs are set up. Add them on the TVs plugin in the hub's web page.");
        var power = await Task.WhenAll(tvs.Select(t => t.GetPowerAsync(cancel))).ConfigureAwait(false);
        return tvs.Select((t, i) => new
        {
            name = t.Device.Name.Trim(),
            type = t.Device.Type.Trim().ToLowerInvariant(),
            power = power[i].ToString().ToLowerInvariant(),
            needs_pairing = t.NeedsPairing ? true : (bool?)null,
        }).ToList();
    }

    [Tool("tv_power", "Turn a TV on or off. The TV defaults to the one in the puck's room.")]
    public async Task<ToolResult> Power(
        [Param("true = on, false = off")] bool on,
        [Param("TV name; omit for the one in the puck's room")] string? tv = null,
        ToolCaller? caller = null, CancellationToken cancel = default)
    {
        var target = Pick(tv, caller);
        return ToolResult.Ok(await target.PowerAsync(on, cancel).ConfigureAwait(false), Text.Activity($"{target.Device.Name.Trim()} TV {(on ? "on" : "off")}"));
    }

    [Tool("tv_volume", "Set a TV's volume (0-100, LG only), change it by steps (e.g. +5 / -3), or mute/unmute. Samsung and Roku can only step. TV defaults to the puck's room.")]
    public async Task<ToolResult> Volume(
        [Param("Absolute volume 0-100 (LG only)", Min = 0, Max = 100)] int? volume = null,
        [Param("Relative change in steps, e.g. 5 or -5")] int? change = null,
        [Param("true = mute, false = unmute")] bool? mute = null,
        [Param("TV name; omit for the one in the puck's room")] string? tv = null,
        ToolCaller? caller = null, CancellationToken cancel = default)
    {
        if (volume is null && change is null && mute is null) throw new PluginException("Say a volume, a change, or mute.");
        var target = Pick(tv, caller);
        var result = await target.VolumeAsync(volume, change, mute, cancel).ConfigureAwait(false);
        var activity = mute is bool m ? (m ? "TV muted" : "TV unmuted") : volume is int v ? $"TV volume {v}" : $"TV volume {change:+0;-0}";
        return ToolResult.Ok(result, Text.Activity(activity));
    }

    [Tool("tv_input", "Switch a TV's input, e.g. 'HDMI 2', 'Xbox' (LG input names) or 'live TV'. TV defaults to the puck's room.")]
    public async Task<ToolResult> Input(
        [Param("Input, e.g. HDMI 1, HDMI 2, live TV, or the input's name on LG")] string input,
        [Param("TV name; omit for the one in the puck's room")] string? tv = null,
        ToolCaller? caller = null, CancellationToken cancel = default)
    {
        var target = Pick(tv, caller);
        return ToolResult.Ok(await target.InputAsync(input, cancel).ConfigureAwait(false), Text.Activity($"TV {input}"));
    }

    [Tool("tv_app", "Open an app on a TV, e.g. Netflix, YouTube, Prime Video, Disney+, Plex. TV defaults to the puck's room.")]
    public async Task<ToolResult> App(
        [Param("App name")] string app,
        [Param("TV name; omit for the one in the puck's room")] string? tv = null,
        ToolCaller? caller = null, CancellationToken cancel = default)
    {
        var target = Pick(tv, caller);
        return ToolResult.Ok(await target.AppAsync(app, cancel).ConfigureAwait(false), Text.Activity(app));
    }

    [Tool("tv_media", "Playback on a TV: play, pause or stop (Samsung and Roku also home and back). TV defaults to the puck's room.")]
    public async Task<ToolResult> Media(
        [Param("What to do", Values = ["play", "pause", "stop", "home", "back"])] string action,
        [Param("TV name; omit for the one in the puck's room")] string? tv = null,
        ToolCaller? caller = null, CancellationToken cancel = default)
    {
        var target = Pick(tv, caller);
        return ToolResult.Ok(await target.MediaAsync(action, cancel).ConfigureAwait(false), Text.Activity($"TV {TvKeys.Action(action)}"));
    }

    [Tool("tv_channel", "Change a TV's channel up or down. TV defaults to the puck's room.")]
    public async Task<ToolResult> Channel(
        [Param("up or down", Values = ["up", "down"])] string direction,
        [Param("TV name; omit for the one in the puck's room")] string? tv = null,
        ToolCaller? caller = null, CancellationToken cancel = default)
    {
        var target = Pick(tv, caller);
        bool up = NameMatcher.Normalize(direction) is "up" or "next" or "higher" or "plus";
        return ToolResult.Ok(await target.ChannelAsync(up, cancel).ConfigureAwait(false), Text.Activity($"TV channel {(up ? "up" : "down")}"));
    }

    [Tool("tv_message", "Show a short message on an LG TV's screen (a toast). TV defaults to the puck's room.")]
    public async Task<ToolResult> Message(
        [Param("The message")] string text,
        [Param("TV name; omit for the one in the puck's room")] string? tv = null,
        ToolCaller? caller = null, CancellationToken cancel = default)
    {
        var target = Pick(tv, caller);
        return ToolResult.Ok(await target.MessageAsync(text, cancel).ConfigureAwait(false), "TV message");
    }

    // ---------------------------------------------------------------- actions

    public override IReadOnlyList<PluginAction> GetActions() =>
    [
        new("pair", "Pair a TV", "Shows a prompt on the TV: accept it within 30 seconds (LG and Samsung).",
            [new ActionInput("tv", "TV name", _tvs.FirstOrDefault()?.Device.Name ?? "Lounge")]),
        new("discover", "Find TVs", "Looks for LG, Samsung and Roku TVs on the network."),
    ];

    public override async Task<ActionResult> RunActionAsync(string actionId, IReadOnlyDictionary<string, string> input, CancellationToken cancel)
    {
        switch (actionId)
        {
            case "pair":
                try
                {
                    var tv = TvPicker.Pick(_tvs, t => t.Device.Name, input.GetValueOrDefault("tv"), null);
                    var message = await tv.PairAsync(cancel).ConfigureAwait(false);
                    UpdateStatus();
                    return new ActionResult(message);
                }
                catch (PluginException e)
                {
                    return new ActionResult(e.Message, false);
                }

            case "discover":
                IReadOnlyList<FoundTv> found;
                try
                {
                    found = await TvDiscovery.FindAsync(Context.Http, cancel).ConfigureAwait(false);
                }
                catch (System.Net.Sockets.SocketException e)
                {
                    return new ActionResult($"Couldn't search the network ({e.SocketErrorCode}).", false);
                }
                if (found.Count == 0) return new ActionResult("No TVs answered. Check they're on and on the same network as the hub.", false);
                var lines = found.Select(f => $"{f.Name}: {f.Type}, {f.Host}{(f.Mac is null ? "" : ", MAC " + f.Mac)}");
                return new ActionResult($"Found {Text.Count(found.Count, "TV")}: {string.Join("; ", lines)}. Add them under TVs, save, then Pair.");

            default:
                return new ActionResult($"Unknown action '{actionId}'.", false);
        }
    }
}
