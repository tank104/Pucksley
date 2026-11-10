using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Pucksley.Plugins.Shared;
using Pucksley.Sdk;

namespace Pucksley.Plugins.HomeAssistant;

public sealed class HomeAssistantSettings
{
    [Setting("URL", Placeholder = "http://homeassistant.local:8123", Order = 0)]
    public string Url { get; set; } = "";

    [Setting("Long-lived access token", Secret = true, Order = 1,
        Help = "In Home Assistant: your profile (bottom left) → Security → Long-lived access tokens → Create token.")]
    public string Token { get; set; } = "";

    [Setting("Puck controls", Order = 2,
        Help = "Entity ids to put on the puck's Controls face, one per line: lights, switches, fans, thermostats, blinds, media players, scenes, scripts (e.g. light.lounge).")]
    public List<string> ControlEntities { get; set; } = [];

    [Setting("Live context", Order = 3,
        Help = "Entity ids whose state Claude always knows, one per line (e.g. sensor.outside_temperature, lock.front_door). Keep it short.")]
    public List<string> ContextEntities { get; set; } = [];

    [Setting("Language", Order = 4, Placeholder = "en", Advanced = true, Help = "Language for Home Assistant's Assist (ha_assist).")]
    public string Language { get; set; } = "en";
}

/// <summary>Home Assistant over its REST API: states cached and refreshed every minute, tools, puck dials and live context.</summary>
public sealed partial class HomeAssistantPlugin : PuckPlugin<HomeAssistantSettings>
{
    static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(60);

    /// <param name="AreasLoaded">Whether the area names were fetched (an install may simply have none).</param>
    sealed record Snapshot(IReadOnlyDictionary<string, HaEntity> States, IReadOnlyDictionary<string, string> Areas, DateTimeOffset At, bool AreasLoaded = false)
    {
        public static Snapshot Empty { get; } = new(new Dictionary<string, HaEntity>(), new Dictionary<string, string>(), DateTimeOffset.MinValue);

        public int AreaCount => States.Values.Select(e => e.Area).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).Count();
    }

    readonly object _lock = new();
    volatile Snapshot _snap = Snapshot.Empty;
    volatile HaClient? _client;
    volatile bool _unauthorized;
    Poller? _poller;
    ControlDebouncer? _debouncer;

    public override string Id => "homeassistant";
    public override string Name => "Home Assistant";
    public override string Description => "Lights, switches, climate, blinds, media players, scenes and scripts in Home Assistant.";
    public override string Icon => "home";
    public override bool EnabledByDefault => false;

    public override string? Instructions =>
        "Home Assistant tools take names, entity ids or groups like 'all lights in the kitchen'; 'the lights' on its own means the puck's room. " +
        "Use ha_assist for anything the other ha_ tools don't cover.";

    public override async Task StartAsync(CancellationToken cancel)
    {
        _snap = Snapshot.Empty;
        _unauthorized = false;
        if (string.IsNullOrWhiteSpace(Settings.Url) || string.IsNullOrWhiteSpace(Settings.Token))
        {
            _client = null;
            SetStatus(StatusLevel.Warning, "Set the URL and access token");
            return;
        }
        HaClient client;
        try
        {
            client = new HaClient(Context.Http, Settings.Url, Settings.Token, OnUnauthorized);
        }
        catch (UriFormatException)
        {
            _client = null;
            SetStatus(StatusLevel.Error, "That URL doesn't look right");
            return;
        }
        _client = client;
        _debouncer = new ControlDebouncer(TimeSpan.FromMilliseconds(250));
        try
        {
            await client.PingAsync(cancel).ConfigureAwait(false);
            await RefreshAsync(client, loadAreas: true, cancel).ConfigureAwait(false);
        }
        catch (PluginException e) when (!cancel.IsCancellationRequested)
        {
            if (_unauthorized) return; // nothing to retry until the token is changed
            SetStatus(StatusLevel.Error, $"Can't reach Home Assistant at {client.BaseUri.Authority}");
            Log.LogWarning("Home Assistant: {Message}", e.Message);
        }
        // Keep the cache fresh (and retry the connection if Home Assistant was down)
        _poller = new Poller(RefreshInterval, c => RefreshAsync(client, loadAreas: false, c), Log, "Home Assistant");
    }

    public override async Task StopAsync()
    {
        if (_poller is not null) await _poller.DisposeAsync().ConfigureAwait(false);
        _poller = null;
        _debouncer?.Dispose();
        _debouncer = null;
        _client = null;
    }

    void OnUnauthorized()
    {
        _unauthorized = true;
        SetStatus(StatusLevel.Error, "Token rejected");
    }

    /// <summary>Reloads every state (and the area names, the first time they load).</summary>
    async Task RefreshAsync(HaClient client, bool loadAreas, CancellationToken cancel)
    {
        if (_unauthorized) return;
        var states = await client.GetStatesAsync(cancel).ConfigureAwait(false);
        var areas = _snap.Areas;
        bool areasLoaded = _snap.AreasLoaded;
        if (loadAreas || !areasLoaded)
        {
            try
            {
                areas = await client.GetAreasAsync(cancel).ConfigureAwait(false);
                areasLoaded = true;
            }
            catch (PluginException e)
            {
                Log.LogInformation("Home Assistant: couldn't load area names ({Message})", e.Message);
            }
        }
        Snapshot old, now;
        lock (_lock)
        {
            old = _snap;
            now = new Snapshot(states.ToDictionary(e => e.EntityId, e => e with { Area = areas.GetValueOrDefault(e.EntityId) }, StringComparer.OrdinalIgnoreCase),
                areas, DateTimeOffset.UtcNow, areasLoaded);
            _snap = now;
        }
        if (old.States.Count == 0) Context.ControlsChanged();
        else ReportControlChanges(old, now, Settings.ControlEntities);
        SetStatus(StatusLevel.Ok, $"Connected: {Text.Count(now.States.Count, "entity", "entities")}, {Text.Count(now.AreaCount, "area")}");
    }

    /// <summary>Merges the states a service call changed into the cache and updates any dials showing them.</summary>
    void ApplyChanges(IReadOnlyList<HaEntity> changed)
    {
        if (changed.Count == 0) return;
        Snapshot old, now;
        lock (_lock)
        {
            old = _snap;
            var states = new Dictionary<string, HaEntity>(old.States, StringComparer.OrdinalIgnoreCase);
            foreach (var e in changed) states[e.EntityId] = e with { Area = old.Areas.GetValueOrDefault(e.EntityId) };
            now = old with { States = states };
            _snap = now;
        }
        ReportControlChanges(old, now, changed.Select(e => e.EntityId));
    }

    void ReportControlChanges(Snapshot old, Snapshot now, IEnumerable<string> ids)
    {
        var configured = Settings.ControlEntities.Select(s => s.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var id in ids.Select(s => s.Trim()).Where(configured.Contains).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var before = old.States.TryGetValue(id, out var b) ? HaControls.Build(b) : null;
            var after = now.States.TryGetValue(id, out var a) ? HaControls.Build(a) : null;
            if (before is null || after is null)
            {
                if (before is not null || after is not null) Context.ControlsChanged();
                continue;
            }
            if (before != after) Context.ControlValueChanged(after.Id, after.Value, after.On, after.Text);
        }
    }

    // ---------------------------------------------------------------- context and controls

    public override Task<string?> GetContextAsync(ToolCaller caller, CancellationToken cancel)
    {
        var snap = _snap;
        if (Settings.ContextEntities.Count == 0 || snap.States.Count == 0) return Task.FromResult<string?>(null);
        var lines = Settings.ContextEntities.Select(id => snap.States.GetValueOrDefault(id.Trim())).OfType<HaEntity>().Select(HaFormat.Line).ToList();
        if (lines.Count == 0) return Task.FromResult<string?>(null);
        var text = "Home Assistant: " + string.Join("; ", lines) + ".";
        return Task.FromResult<string?>(text.Length <= 400 ? text : text[..399] + "…");
    }

    public override IReadOnlyList<PuckControl> GetControls()
    {
        var snap = _snap;
        return Settings.ControlEntities
            .Select(id => snap.States.GetValueOrDefault(id.Trim()))
            .OfType<HaEntity>()
            .Select(HaControls.Build)
            .OfType<PuckControl>()
            .ToList();
    }

    public override async Task OnControlAsync(string controlId, ControlInput input, CancellationToken cancel)
    {
        var client = _client;
        if (client is null || !_snap.States.TryGetValue(controlId, out var e)) return;
        switch (e.Domain)
        {
            case "light" when input.Value is double v:
                Debounce(client, e, v <= 0 ? "turn_off" : "turn_on", v <= 0 ? null : new JsonObject { ["brightness_pct"] = (int)Math.Round(v) });
                break;
            case "climate" when input.Value is double t:
                Debounce(client, e, "set_temperature", new JsonObject { ["temperature"] = t });
                break;
            case "cover" when input.Value is double p:
                Debounce(client, e, "set_cover_position", new JsonObject { ["position"] = (int)Math.Round(p) });
                break;
            case "media_player" when input.Value is double vol:
                Debounce(client, e, "volume_set", new JsonObject { ["volume_level"] = Math.Clamp(vol / 100.0, 0, 1) });
                break;
            case "switch" or "fan" or "input_boolean" or "automation" or "siren" when input.On is bool on:
                await ControlCallAsync(client, e, on ? "turn_on" : "turn_off", cancel).ConfigureAwait(false);
                break;
            case "scene" or "script" when input.Pressed:
                await ControlCallAsync(client, e, "turn_on", cancel).ConfigureAwait(false);
                break;
            case "button" or "input_button" when input.Pressed:
                await ControlCallAsync(client, e, "press", cancel).ConfigureAwait(false);
                break;
        }
    }

    /// <summary>A tap on the puck: call the service; on failure say so and put the dial back to the real state.</summary>
    async Task ControlCallAsync(HaClient client, HaEntity e, string service, CancellationToken cancel)
    {
        try
        {
            ApplyChanges(await client.CallServiceAsync(e.Domain, service, Target([e]), cancel).ConfigureAwait(false));
        }
        catch (PluginException ex)
        {
            Log.LogWarning("Home Assistant control {Entity}: {Message}", e.EntityId, ex.Message);
            if (HaControls.Build(_snap.States.GetValueOrDefault(e.EntityId) ?? e) is { } actual)
                Context.ControlValueChanged(actual.Id, actual.Value, actual.On, actual.Text);
            await Context.NotifyAsync($"Home Assistant: {ex.Message}", NotifyKind.Alert).ConfigureAwait(false);
        }
    }

    /// <summary>Dial turns: send only the latest value once the dial settles.</summary>
    void Debounce(HaClient client, HaEntity e, string service, JsonObject? extra)
    {
        var data = Target([e]);
        if (extra is not null) foreach (var (key, value) in extra) data[key] = value?.DeepClone();
        _debouncer?.Post(e.EntityId,
            async c => ApplyChanges(await client.CallServiceAsync(e.Domain, service, data, c).ConfigureAwait(false)),
            ex =>
            {
                Log.LogWarning("Home Assistant control {Entity}: {Message}", e.EntityId, ex.Message);
                _ = Context.NotifyAsync($"Home Assistant: {ex.Message}", NotifyKind.Alert);
            });
    }

    // ---------------------------------------------------------------- tools

    [Tool("ha_find", "Search Home Assistant entities by name, area or entity id. Returns up to 10 matches (entity_id, name, area, state). Use it when unsure what something is called.")]
    public async Task<object> Find(
        [Param("What to look for, e.g. 'kitchen', 'heat pump', 'temperature' ('*' lists a domain)")] string query,
        [Param("Only this domain, e.g. light, switch, climate, cover, sensor, media_player")] string? domain = null,
        CancellationToken cancel = default)
    {
        var snap = await StatesAsync(cancel).ConfigureAwait(false);
        IEnumerable<HaEntity> pool = snap.States.Values;
        if (!string.IsNullOrWhiteSpace(domain)) pool = pool.Where(e => e.Domain.Equals(domain.Trim(), StringComparison.OrdinalIgnoreCase));
        var q = query?.Trim() ?? "";
        var found = q is "" or "*" or "all"
            ? pool.OrderBy(e => e.Area ?? "~", StringComparer.OrdinalIgnoreCase).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList()
            : EntityResolver.Rank(q, pool);
        if (found.Count == 0)
            throw new PluginException($"Nothing in Home Assistant matches '{q}'{(string.IsNullOrWhiteSpace(domain) ? "" : $" in {domain}")}.");
        return found.Take(10).Select(e => new { entity_id = e.EntityId, name = e.Name, area = e.Area, state = HaFormat.Describe(e) }).ToList();
    }

    [Tool("ha_state", "Current state of Home Assistant entities: a name, an entity id, or a group like 'kitchen lights'. Includes brightness, temperatures, media, positions.")]
    public async Task<string> State(
        [Param("Name or entity id, e.g. 'lounge lamp', 'sensor.outside_temperature', 'kitchen lights'")] string entity,
        ToolCaller? caller = null, CancellationToken cancel = default)
    {
        var client = RequireClient();
        var found = await ResolveAsync(entity, Want.Many, caller, _ => true, null, cancel).ConfigureAwait(false);
        if (found.Count == 1)
        {
            var e = found[0];
            if (await client.GetStateAsync(e.EntityId, cancel).ConfigureAwait(false) is { } fresh)
            {
                ApplyChanges([fresh]);
                e = fresh with { Area = e.Area };
            }
            return $"{e.Name} ({e.EntityId}{(e.Area is null ? "" : ", " + e.Area)}): {HaFormat.Describe(e)}.";
        }
        // Several: refresh the cache first unless it's very recent
        if (DateTimeOffset.UtcNow - _snap.At > TimeSpan.FromSeconds(5)) await RefreshAsync(client, false, cancel).ConfigureAwait(false);
        var snap = _snap;
        var lines = found.Select(e => snap.States.GetValueOrDefault(e.EntityId) ?? e).Take(20).Select(HaFormat.Line).ToList();
        if (found.Count > 20) lines.Add($"…and {found.Count - 20} more.");
        return string.Join("\n", lines);
    }

    [Tool("ha_turn_on", "Turn on Home Assistant things: a name, entity id, or e.g. 'all lights in the kitchen'. Lights also take brightness_pct, color ('red', 'warm white', '#ff8800') and color_temp_kelvin.")]
    public Task<ToolResult> TurnOn(
        [Param("What to turn on")] string entity,
        [Param("Light brightness, 0-100 %", Min = 0, Max = 100)] int? brightnessPct = null,
        [Param("Light colour: a name like 'blue' or 'warm white', or hex '#ff8800'")] string? color = null,
        [Param("Light colour temperature in kelvin, e.g. 2700 (warm) to 6500 (daylight)", Min = 1500, Max = 9000)] int? colorTempKelvin = null,
        ToolCaller? caller = null, CancellationToken cancel = default)
    {
        if (brightnessPct == 0) return SwitchAsync(entity, OnOff.Off, null, caller, cancel);
        var lightData = brightnessPct is null && string.IsNullOrWhiteSpace(color) && colorTempKelvin is null
            ? null
            : HaServices.LightData(brightnessPct, color, colorTempKelvin);
        return SwitchAsync(entity, OnOff.On, lightData, caller, cancel);
    }

    [Tool("ha_turn_off", "Turn off Home Assistant things: a name, entity id, or e.g. 'all lights in the kitchen'.")]
    public Task<ToolResult> TurnOff([Param("What to turn off")] string entity, ToolCaller? caller = null, CancellationToken cancel = default) =>
        SwitchAsync(entity, OnOff.Off, null, caller, cancel);

    [Tool("ha_toggle", "Toggle Home Assistant things on/off: a name, entity id, or e.g. 'all lights in the kitchen'.")]
    public Task<ToolResult> Toggle([Param("What to toggle")] string entity, ToolCaller? caller = null, CancellationToken cancel = default) =>
        SwitchAsync(entity, OnOff.Toggle, null, caller, cancel);

    [Tool("ha_set_climate", "Set a Home Assistant thermostat or heat pump: target temperature (°C) and/or HVAC mode (heat, cool, auto, dry, fan_only, off). Defaults to the one in the puck's room.", Icon = "heat")]
    public async Task<ToolResult> SetClimate(
        [Param("Thermostat or heat pump name/entity id; omit for the one in the puck's room")] string? entity = null,
        [Param("Target temperature in °C")] double? temperature = null,
        [Param("HVAC mode, e.g. heat, cool, auto, off")] string? hvacMode = null,
        ToolCaller? caller = null, CancellationToken cancel = default)
    {
        if (temperature is null && string.IsNullOrWhiteSpace(hvacMode)) throw new PluginException("Give a temperature, a mode, or both.");
        var client = RequireClient();
        var targets = await ResolveOrDefaultAsync(entity, "climate", "thermostats", Want.Many, caller, cancel).ConfigureAwait(false);
        // Check every target first, so a bad value doesn't leave some changed and some not
        var calls = targets.Select(e =>
        {
            var data = Target([e]);
            if (!string.IsNullOrWhiteSpace(hvacMode))
                data["hvac_mode"] = HaServices.HvacMode(hvacMode, e.Attributes["hvac_modes"] is JsonArray m ? m.Select(n => n?.ToString() ?? "").ToList() : []);
            if (temperature is double t)
            {
                double min = e.AttrNum("min_temp") ?? 5, max = e.AttrNum("max_temp") ?? 35;
                if (t < min || t > max) throw new PluginException($"{e.Name} takes {Text.Num(min)}–{Text.Num(max)} °C.");
                data["temperature"] = t;
            }
            return (Service: temperature is null ? "set_hvac_mode" : "set_temperature", Data: data);
        }).ToList();
        var changed = new List<HaEntity>();
        foreach (var (service, data) in calls)
            changed.AddRange(await client.CallServiceAsync("climate", service, data, cancel).ConfigureAwait(false));
        ApplyChanges(changed);
        var what = Text.List(new[] { hvacMode is null ? null : HaServices.HvacMode(hvacMode, []).Replace('_', ' '), temperature is double tt ? $"{Text.Num(tt)} °C" : null }.OfType<string>());
        return ToolResult.Ok($"{ListNames(targets)} set to {what}.", Text.Activity(temperature is double a ? $"{ShortOf(targets)} {Text.Num(a)}°" : $"{ShortOf(targets)} {hvacMode}"));
    }

    [Tool("ha_cover", "Open, close, stop or position Home Assistant blinds, curtains, garage doors and gates. Position is 0 (closed) to 100 (open).")]
    public async Task<ToolResult> Cover(
        [Param("Blind, curtain or door name/entity id, or e.g. 'all blinds in the lounge'")] string entity,
        [Param("What to do", Values = ["open", "close", "stop", "position"])] string action,
        [Param("For action 'position': 0 = closed, 100 = open", Min = 0, Max = 100)] int? position = null,
        ToolCaller? caller = null, CancellationToken cancel = default)
    {
        var client = RequireClient();
        var targets = await ResolveAsync(entity, Want.Many, caller, e => e.Domain == "cover", "blinds, curtains or doors", cancel).ConfigureAwait(false);
        var a = action.Trim().ToLowerInvariant();
        if (position is not null && a is not ("close" or "stop")) a = "position";
        var (service, verb) = a switch
        {
            "open" or "up" or "raise" => ("open_cover", "Opening"),
            "close" or "down" or "lower" or "shut" => ("close_cover", "Closing"),
            "stop" => ("stop_cover", "Stopped"),
            "position" or "set" => ("set_cover_position", "Moving"),
            _ => throw new PluginException("Action must be open, close, stop or position."),
        };
        var data = Target(targets);
        if (service == "set_cover_position")
        {
            if (position is not int p) throw new PluginException("Say what position, 0 (closed) to 100 (open).");
            data["position"] = Math.Clamp(p, 0, 100);
        }
        if (!await ConfirmIfNeededAsync("cover", service, targets, $"Open {ListNames(targets)}?", caller, cancel).ConfigureAwait(false))
            return ToolResult.Ok("Not done: it wasn't confirmed on the puck.");
        ApplyChanges(await client.CallServiceAsync("cover", service, data, cancel).ConfigureAwait(false));
        var tail = service == "set_cover_position" ? $" to {position}%" : "";
        return ToolResult.Ok($"{verb} {ListNames(targets)}{tail}.", Text.Activity($"{ShortOf(targets)} {a}"));
    }

    [Tool("ha_media", "Control a Home Assistant media player: play, pause, stop, next, previous, mute, unmute, or volume (0-100). Defaults to the player in the puck's room.", Icon = "music")]
    public async Task<ToolResult> Media(
        [Param("What to do", Values = ["play", "pause", "stop", "next", "previous", "volume", "mute", "unmute"])] string action,
        [Param("Media player name/entity id; omit for the one in the puck's room")] string? entity = null,
        [Param("For action 'volume': 0-100", Min = 0, Max = 100)] int? volume = null,
        ToolCaller? caller = null, CancellationToken cancel = default)
    {
        var client = RequireClient();
        var target = (await ResolveOrDefaultAsync(entity, "media_player", "media players", Want.One, caller, cancel).ConfigureAwait(false))[0];
        var a = action.Trim().ToLowerInvariant();
        if (volume is not null) a = "volume";
        var data = Target([target]);
        string service;
        switch (a)
        {
            case "play" or "resume": service = "media_play"; break;
            case "pause": service = "media_pause"; break;
            case "stop": service = "media_stop"; break;
            case "next" or "skip": service = "media_next_track"; break;
            case "previous" or "back": service = "media_previous_track"; break;
            case "mute" or "unmute":
                service = "volume_mute";
                data["is_volume_muted"] = a == "mute";
                break;
            case "volume":
                if (volume is not int v) throw new PluginException("Say what volume, 0-100.");
                service = "volume_set";
                data["volume_level"] = Math.Clamp(v, 0, 100) / 100.0;
                break;
            default:
                throw new PluginException("Action must be play, pause, stop, next, previous, mute, unmute or volume.");
        }
        ApplyChanges(await client.CallServiceAsync("media_player", service, data, cancel).ConfigureAwait(false));
        var result = a == "volume" ? $"{target.Name} volume is now {volume}." : $"{target.Name}: {a}.";
        return ToolResult.Ok(result, Text.Activity(a == "volume" ? $"{HaControls.ShortName(target)} {volume}%" : $"{HaControls.ShortName(target)} {a}"));
    }

    [Tool("ha_scene", "Activate a Home Assistant scene by name.")]
    public async Task<ToolResult> Scene([Param("Scene name, e.g. 'movie night'")] string name, ToolCaller? caller = null, CancellationToken cancel = default)
    {
        var client = RequireClient();
        var scene = (await ResolveAsync(name, Want.One, caller, e => e.Domain == "scene", "scenes", cancel).ConfigureAwait(false))[0];
        ApplyChanges(await client.CallServiceAsync("scene", "turn_on", Target([scene]), cancel).ConfigureAwait(false));
        return ToolResult.Ok($"Scene {scene.Name} is on.", Text.Activity(scene.Name));
    }

    [Tool("ha_script", "Run a Home Assistant script by name.")]
    public async Task<ToolResult> Script([Param("Script name, e.g. 'goodnight'")] string name, ToolCaller? caller = null, CancellationToken cancel = default)
    {
        var client = RequireClient();
        var script = (await ResolveAsync(name, Want.One, caller, e => e.Domain == "script", "scripts", cancel).ConfigureAwait(false))[0];
        ApplyChanges(await client.CallServiceAsync("script", "turn_on", Target([script]), cancel).ConfigureAwait(false));
        return ToolResult.Ok($"Started {script.Name}.", Text.Activity(script.Name));
    }

    [Tool("ha_call_service", "Call any Home Assistant service, e.g. domain 'vacuum', service 'start'. entity_id may be a name; data is extra service data as a JSON object. Use when no other ha_ tool fits.")]
    public async Task<ToolResult> CallService(
        [Param("Service domain, e.g. vacuum, lock, input_select, notify")] string domain,
        [Param("Service, e.g. start, lock, select_option")] string service,
        [Param("Target entity id(s) or name; omit for services without a target")] string? entityId = null,
        [Param("Extra service data, e.g. {\"option\": \"Away\"}")] JsonObject? data = null,
        ToolCaller? caller = null, CancellationToken cancel = default)
    {
        var client = RequireClient();
        domain = domain.Trim().ToLowerInvariant();
        service = service.Trim().ToLowerInvariant();
        var body = data?.DeepClone() as JsonObject ?? new JsonObject();
        IReadOnlyList<HaEntity> targets = [];
        if (!string.IsNullOrWhiteSpace(entityId))
        {
            targets = await ResolveAsync(entityId, Want.Many, caller, _ => true, null, cancel).ConfigureAwait(false);
            body["entity_id"] = Target(targets)["entity_id"]!.DeepClone();
        }
        if (!await ConfirmIfNeededAsync(domain, service, targets, $"Run {domain}.{service}{(targets.Count > 0 ? " on " + ListNames(targets) : "")}?", caller, cancel)
                .ConfigureAwait(false))
            return ToolResult.Ok("Not done: it wasn't confirmed on the puck.");
        var changed = await client.CallServiceAsync(domain, service, body, cancel).ConfigureAwait(false);
        ApplyChanges(changed);
        var text = $"Called {domain}.{service}{(targets.Count > 0 ? " on " + ListNames(targets) : "")}.";
        if (changed.Count > 0) text += " Now: " + string.Join("; ", changed.Take(5).Select(HaFormat.Line)) + ".";
        return ToolResult.Ok(text, Text.Activity($"{domain}.{service}"));
    }

    [GeneratedRegex(@"\b(unlock|disarm)\b|\bopen\b.*\b(garage|gate)\b", RegexOptions.IgnoreCase)]
    private static partial Regex RiskyAssist();

    [Tool("ha_assist", "Send a sentence to Home Assistant's Assist, e.g. 'what's the humidity in the bathroom'; returns its spoken answer. For anything the other ha_ tools don't cover.")]
    public async Task<ToolResult> Assist([Param("The request, in plain words")] string text, ToolCaller? caller = null, CancellationToken cancel = default)
    {
        var client = RequireClient();
        if (RiskyAssist().IsMatch(text) && !await Context.ConfirmAsync($"Ask Home Assistant to {text.TrimEnd('.', '?', '!')}?", caller ?? ToolCaller.Web, cancel: cancel)
                .ConfigureAwait(false))
            return ToolResult.Ok("Not done: it wasn't confirmed on the puck.");
        var (speech, isError) = await client.AssistAsync(text, Settings.Language, cancel).ConfigureAwait(false);
        return isError ? ToolResult.Error($"Home Assistant: {speech}") : ToolResult.Ok($"Home Assistant: {speech}");
    }

    // ---------------------------------------------------------------- helpers

    async Task<ToolResult> SwitchAsync(string spoken, OnOff action, JsonObject? lightData, ToolCaller? caller, CancellationToken cancel)
    {
        var client = RequireClient();
        var targets = await ResolveAsync(spoken, Want.Many, caller, e => HaServices.IsSwitchable(e.Domain), null, cancel).ConfigureAwait(false);
        var calls = targets.GroupBy(e => HaServices.For(e, action)).ToList();
        foreach (var call in calls)
        {
            if (!await ConfirmIfNeededAsync(call.Key.Domain, call.Key.Service, call, $"Open {ListNames(call.ToList())}?", caller, cancel).ConfigureAwait(false))
                return ToolResult.Ok("Not done: it wasn't confirmed on the puck.");
        }
        var changed = new List<HaEntity>();
        foreach (var call in calls)
        {
            var data = Target(call.ToList());
            if (lightData is not null && call.Key == ("light", "turn_on"))
                foreach (var (key, value) in lightData) data[key] = value?.DeepClone();
            changed.AddRange(await client.CallServiceAsync(call.Key.Domain, call.Key.Service, data, cancel).ConfigureAwait(false));
        }
        ApplyChanges(changed);
        return SwitchResult(targets, action, lightData, changed);
    }

    static ToolResult SwitchResult(IReadOnlyList<HaEntity> targets, OnOff action, JsonObject? lightData, IReadOnlyList<HaEntity> changed)
    {
        string names = ListNames(targets);
        string state = action switch { OnOff.On => "on", OnOff.Off => "off", _ => "toggled" };
        if (action == OnOff.Toggle && targets.Count == 1)
        {
            // Report where it ended up, when Home Assistant told us
            var now = changed.FirstOrDefault(c => c.EntityId == targets[0].EntityId);
            if (now is not null) state = now.IsActive ? "on" : "off";
            return ToolResult.Ok(now is null ? $"Toggled {names}." : $"{names} is now {state}.", Text.Activity($"{ShortOf(targets)} {state}"));
        }
        string extra = "";
        if (lightData is not null)
        {
            var bits = new List<string>();
            if (Json.Num(lightData, "brightness_pct") is double b) bits.Add($"{b:0}%");
            if (Json.Str(lightData, "color_name") is { } c) bits.Add(c);
            if (lightData["rgb_color"] is JsonArray rgb) bits.Add($"RGB {string.Join(",", rgb.Select(n => n?.ToString()))}");
            if (Json.Num(lightData, "color_temp_kelvin") is double k) bits.Add($"{k:0} K");
            if (bits.Count > 0) extra = " at " + string.Join(", ", bits);
        }
        string verb = action switch { OnOff.On => "Turned on", OnOff.Off => "Turned off", _ => "Toggled" };
        string activity = lightData is not null && Json.Num(lightData, "brightness_pct") is double pct
            ? $"{ShortOf(targets)} {pct:0}%"
            : $"{ShortOf(targets)} {state}";
        return ToolResult.Ok($"{verb} {names}{extra}.", Text.Activity(activity));
    }

    async Task<bool> ConfirmIfNeededAsync(string domain, string service, IEnumerable<HaEntity> targets, string question, ToolCaller? caller, CancellationToken cancel)
    {
        if (!HaServices.NeedsConfirmation(domain, service, targets)) return true;
        return await Context.ConfirmAsync(question, caller ?? ToolCaller.Web, cancel: cancel).ConfigureAwait(false);
    }

    HaClient RequireClient()
    {
        if (_client is not { } client)
            throw new PluginException("Home Assistant isn't set up. Add its URL and a long-lived access token on the hub's Plugins page.");
        if (_unauthorized)
            throw new PluginException("Home Assistant rejected the access token. Create a new long-lived token and paste it on the hub's Plugins page.");
        return client;
    }

    /// <summary>The cached states, loading them first if they never loaded (Home Assistant was down at start).</summary>
    async Task<Snapshot> StatesAsync(CancellationToken cancel)
    {
        var client = RequireClient();
        if (_snap.States.Count == 0) await RefreshAsync(client, loadAreas: true, cancel).ConfigureAwait(false);
        return _snap;
    }

    async Task<IReadOnlyList<HaEntity>> ResolveAsync(string spoken, Want want, ToolCaller? caller, Func<HaEntity, bool> filter, string? kind, CancellationToken cancel)
    {
        var snap = await StatesAsync(cancel).ConfigureAwait(false);
        var pool = snap.States.Values.Where(filter).ToList();
        if (pool.Count == 0) throw new PluginException($"Home Assistant has no {kind ?? "entities"}.");
        return EntityResolver.Resolve(spoken, pool, want, caller?.Room);
    }

    /// <summary>The named entity, or when none is named: the one in the puck's room, or the only one.</summary>
    async Task<IReadOnlyList<HaEntity>> ResolveOrDefaultAsync(string? spoken, string domain, string kind, Want want, ToolCaller? caller, CancellationToken cancel)
    {
        if (!string.IsNullOrWhiteSpace(spoken)) return await ResolveAsync(spoken, want, caller, e => e.Domain == domain, kind, cancel).ConfigureAwait(false);
        var snap = await StatesAsync(cancel).ConfigureAwait(false);
        var pool = snap.States.Values.Where(e => e.Domain == domain).ToList();
        if (pool.Count == 1) return pool;
        if (pool.Count == 0) throw new PluginException($"Home Assistant has no {kind}.");
        if (caller?.Room is { Length: > 0 } room)
        {
            var inRoom = pool.Where(e => e.Area is not null && NameMatcher.Score(room, e.Area) >= 0.85).ToList();
            if (inRoom.Count == 1) return inRoom;
        }
        throw new PluginException($"Which one? {Text.Capitalize(kind)}: {string.Join(", ", pool.Take(10).Select(EntityResolver.Label))}.");
    }

    /// <summary><c>{"entity_id": "light.a"}</c> or <c>{"entity_id": ["light.a", "light.b"]}</c>.</summary>
    static JsonObject Target(IReadOnlyList<HaEntity> entities) => new()
    {
        ["entity_id"] = entities.Count == 1 ? entities[0].EntityId : new JsonArray(entities.Select(e => (JsonNode)e.EntityId).ToArray()),
    };

    /// <summary>"Kitchen Ceiling and Kitchen Pendants", or "6 lights in the Kitchen" for many.</summary>
    static string ListNames(IReadOnlyList<HaEntity> entities)
    {
        if (entities.Count <= 4) return Text.List(entities.Select(e => e.Name));
        var area = CommonArea(entities);
        return $"{entities.Count} {KindOf(entities)}{(area is null ? "" : " in the " + area)}";
    }

    /// <summary>Short subject for the activity line: one name, "Kitchen lights", or "6 lights".</summary>
    static string ShortOf(IReadOnlyList<HaEntity> entities)
    {
        if (entities.Count == 1) return HaControls.ShortName(entities[0]) is var s && s.Length <= 14 ? s : entities[0].Name;
        var area = CommonArea(entities);
        return area is null ? $"{entities.Count} {KindOf(entities)}" : $"{area} {KindOf(entities)}";
    }

    static string? CommonArea(IReadOnlyList<HaEntity> entities)
    {
        var area = entities[0].Area;
        return area is not null && entities.All(e => string.Equals(e.Area, area, StringComparison.OrdinalIgnoreCase)) ? area : null;
    }

    static string KindOf(IReadOnlyList<HaEntity> entities)
    {
        var domains = entities.Select(e => e.Domain).Distinct().ToList();
        return domains.Count != 1 ? "devices" : domains[0] switch
        {
            "light" => "lights",
            "switch" => "switches",
            "fan" => "fans",
            "cover" => "blinds",
            "climate" => "heaters",
            "media_player" => "players",
            _ => "devices",
        };
    }
}
