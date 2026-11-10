using Microsoft.Extensions.Logging;
using Pucksley.Plugins.Shared;
using Pucksley.Sdk;

namespace Pucksley.Plugins.Heating;

public sealed class HeatingSettings
{
    [Setting("API base URL", Placeholder = "https://<your-functions-app>.azurewebsites.net/api/v1", Order = 0)]
    public string BaseUrl { get; set; } = "";

    [Setting("Token", Secret = true, Order = 1,
        Help = "A long-lived token. In Clerk, set the dashboard's JWT template lifetime long enough, then in the dashboard's browser console run " +
               "await Clerk.session.getToken({ template: '<template name>' }) and paste the result here. Or an API key, if your API accepts one (choose x-api-key below).")]
    public string Token { get; set; } = "";

    [Setting("Auth header", Options = ["Bearer", "x-api-key"], Order = 2, Help = "Bearer for a Clerk JWT; x-api-key for an API key.")]
    public string AuthHeader { get; set; } = "Bearer";
}

/// <summary>A home-built underfloor-heating controller (3 floors, heat pump) through its cloud API.</summary>
public sealed class HeatingPlugin : PuckPlugin<HeatingSettings>
{
    public const double MinSetpoint = 10, MaxSetpoint = 28;
    const string SetpointControl = "setpoint";
    const long MaxOverrideMs = 86_400_000;
    static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(60);

    volatile HeatingClient? _client;
    volatile HeatingState? _state;
    volatile bool _unauthorized;
    Poller? _poller;
    ControlDebouncer? _debouncer;

    public override string Id => "heating";
    public override string Name => "Underfloor heating";
    public override string Description => "The house's underfloor heating and heat pump: temperatures, setpoint, zone overrides and history.";
    public override string Icon => "heat";
    public override bool EnabledByDefault => false;

    public override string? Instructions =>
        "Underfloor heating has one house setpoint (10-28 °C) and zones: downstairs (ground), middle (first floor) and upstairs (top); middle and upstairs have sunny and shady sides.";

    public override async Task StartAsync(CancellationToken cancel)
    {
        _state = null;
        _unauthorized = false;
        if (string.IsNullOrWhiteSpace(Settings.BaseUrl) || string.IsNullOrWhiteSpace(Settings.Token))
        {
            _client = null;
            SetStatus(StatusLevel.Warning, "Set the API URL and token");
            return;
        }
        try
        {
            _client = new HeatingClient(Context.Http, Settings.BaseUrl, Settings.Token, Settings.AuthHeader, OnUnauthorized);
        }
        catch (UriFormatException)
        {
            _client = null;
            SetStatus(StatusLevel.Error, "That API URL doesn't look right");
            return;
        }
        _debouncer = new ControlDebouncer(TimeSpan.FromMilliseconds(800));
        await RefreshAsync(cancel).ConfigureAwait(false);
        Context.ControlsChanged();
        if (!_unauthorized) _poller = new Poller(PollInterval, RefreshAsync, Log, "Heating");
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
        SetStatus(StatusLevel.Error, "Token rejected or expired");
    }

    async Task RefreshAsync(CancellationToken cancel)
    {
        if (_client is not { } client || _unauthorized) return;
        try
        {
            Store(await client.GetStateAsync(cancel).ConfigureAwait(false));
        }
        catch (Exception e) when (e is PluginException or FormatException && !cancel.IsCancellationRequested)
        {
            if (!_unauthorized) SetStatus(StatusLevel.Error, $"Can't reach the heating API ({e.Message.TrimEnd('.')})");
        }
    }

    /// <summary>Caches a fresh state, updates the dial and the status line.</summary>
    void Store(HeatingState state)
    {
        var old = _state;
        _state = state;
        if (state.SetpointC is double sp && old?.SetpointC != sp) Context.ControlValueChanged(SetpointControl, sp);
        var now = Context.Now;
        if (state.LastReportedAt is DateTimeOffset at && now - at > TimeSpan.FromMinutes(15))
            SetStatus(StatusLevel.Warning, $"Controller last reported {(int)(now - at).TotalMinutes} min ago");
        else
        {
            var faults = state.Faults.Count > 0 ? $", {Text.Count(state.Faults.Count, "fault")}" : "";
            var set = state.SetpointC is double s ? $"{Text.Fixed(s)} °C set" : "Connected";
            SetStatus(state.Faults.Count > 0 ? StatusLevel.Warning : StatusLevel.Ok, $"{set}{(state.SystemMode is { } m ? ", " + m.Replace('_', ' ') : "")}{faults}");
        }
    }

    HeatingClient RequireClient()
    {
        if (_client is not { } client) throw new PluginException("The heating isn't set up. Add the API URL and a token on the hub's Plugins page.");
        if (_unauthorized) throw new PluginException("The heating API rejected the token (it may have expired). Paste a new one on the hub's Plugins page.");
        return client;
    }

    /// <summary>A fresh state, or the cached one (with a note) when the API can't be reached.</summary>
    async Task<(HeatingState State, string? Note)> CurrentAsync(CancellationToken cancel)
    {
        var client = RequireClient();
        try
        {
            var state = await client.GetStateAsync(cancel).ConfigureAwait(false);
            Store(state);
            return (state, null);
        }
        catch (PluginException) when (_state is { } cached && !_unauthorized)
        {
            return (cached, "(The API didn't answer just now: this is from the last reading.)");
        }
        catch (FormatException e)
        {
            throw new PluginException($"The heating API sent something unexpected ({e.Message.TrimEnd('.')}).");
        }
    }

    // ---------------------------------------------------------------- context and controls

    public override Task<string?> GetContextAsync(ToolCaller caller, CancellationToken cancel) =>
        Task.FromResult(_state is { } s ? HeatingSummary.Context(s, Context.Now) : null);

    public override IReadOnlyList<PuckControl> GetControls()
    {
        if (_client is null) return [];
        var sp = _state?.SetpointC;
        return
        [
            new PuckControl
            {
                Id = SetpointControl, Name = "House", Group = "Heating", Kind = ControlKind.Number,
                Value = sp ?? 20, Min = MinSetpoint, Max = MaxSetpoint, Step = 0.5, Unit = "°C",
                Text = sp is null ? "…" : null,
            },
        ];
    }

    public override Task OnControlAsync(string controlId, ControlInput input, CancellationToken cancel)
    {
        if (controlId != SetpointControl || input.Value is not double value || _client is null) return Task.CompletedTask;
        var target = Math.Clamp(Math.Round(value * 2) / 2, MinSetpoint, MaxSetpoint);
        _debouncer?.Post(SetpointControl, async c =>
        {
            await RequireClient().SetSetpointAsync(target, c).ConfigureAwait(false);
            UpdateCachedSetpoint(target);
        }, e =>
        {
            Log.LogWarning("Heating setpoint: {Message}", e.Message);
            _ = Context.NotifyAsync($"Couldn't set the heating: {e.Message}", NotifyKind.Alert);
            if (_state?.SetpointC is double actual) Context.ControlValueChanged(SetpointControl, actual);
        });
        return Task.CompletedTask;
    }

    void UpdateCachedSetpoint(double setpoint)
    {
        if (_state is { } s) _state = s with { SetpointC = setpoint };
        Context.ControlValueChanged(SetpointControl, setpoint);
    }

    // ---------------------------------------------------------------- tools

    [Tool("heating_status", "The underfloor heating now: mode, house setpoint, each floor's temperature and whether it's calling for heat, heat pump flow temperature, overrides and faults.")]
    public async Task<string> HeatingStatus(CancellationToken cancel = default)
    {
        var (state, note) = await CurrentAsync(cancel).ConfigureAwait(false);
        var text = HeatingSummary.Status(state, Context.Now, Context.TimeZone);
        return note is null ? text : $"{text} {note}";
    }

    [Tool("heating_set_temperature", "Set the house heating setpoint (one for the whole house), 10-28 °C.")]
    public async Task<ToolResult> SetTemperature(
        [Param("Setpoint in °C, 10-28", Min = MinSetpoint, Max = MaxSetpoint)] double setpointC,
        CancellationToken cancel = default)
    {
        if (setpointC < MinSetpoint || setpointC > MaxSetpoint)
            throw new PluginException($"The heating can be set from {MinSetpoint:0} to {MaxSetpoint:0} °C.");
        var target = Math.Round(setpointC, 1);
        await RequireClient().SetSetpointAsync(target, cancel).ConfigureAwait(false);
        UpdateCachedSetpoint(target);
        return ToolResult.Ok($"The heating is now set to {Text.Num(target)} °C.", Text.Activity($"Heating {Text.Num(target)}°C"));
    }

    [Tool("heating_change_temperature", "Raise or lower the house heating setpoint by a relative amount, e.g. 0.5 or -1 (°C).")]
    public async Task<ToolResult> ChangeTemperature([Param("Change in °C, e.g. 1 or -0.5")] double changeC, CancellationToken cancel = default)
    {
        var (state, _) = await CurrentAsync(cancel).ConfigureAwait(false);
        if (state.SetpointC is not double current) throw new PluginException("I don't know the current setpoint yet, so give me a temperature instead.");
        var wanted = Math.Round(current + changeC, 1);
        var target = Math.Clamp(wanted, MinSetpoint, MaxSetpoint);
        if (target == current) return ToolResult.Ok($"The heating is already at {Text.Num(current)} °C, the {(changeC > 0 ? "maximum" : "minimum")}.");
        await RequireClient().SetSetpointAsync(target, cancel).ConfigureAwait(false);
        UpdateCachedSetpoint(target);
        var note = target != wanted ? $" ({Text.Num(wanted)} is outside {MinSetpoint:0}-{MaxSetpoint:0} °C.)" : "";
        return ToolResult.Ok($"The heating went from {Text.Num(current)} to {Text.Num(target)} °C.{note}", Text.Activity($"Heating {Text.Num(target)}°C"));
    }

    [Tool("heating_zone_override", "Override a heating zone for some hours: force_open (heat it regardless), force_closed (no heat) or hold_at_setpoint. Zones: downstairs, middle, upstairs; middle and upstairs have sunny/shady sides (both unless one is named).")]
    public async Task<ToolResult> ZoneOverride(
        [Param("Zone, e.g. 'upstairs', 'middle sunny side', 'downstairs'")] string zone,
        [Param("What to do", Values = ["force_open", "force_closed", "hold_at_setpoint"])] string kind,
        [Param("How long, in hours (max 24)", Min = 0.1, Max = 24)] double hours = 2,
        CancellationToken cancel = default)
    {
        var groups = HeatingZones.ResolveGroups(zone);
        var k = HeatingZones.Kind(kind);
        if (hours <= 0 || hours > 24) throw new PluginException("An override lasts from a few minutes up to 24 hours.");
        long ms = Math.Min((long)Math.Round(hours * 3_600_000), MaxOverrideMs);
        var client = RequireClient();
        foreach (var group in groups) await client.SetOverrideAsync(group, k, ms, cancel).ConfigureAwait(false);
        var what = k switch { "force_open" => "forced open", "force_closed" => "forced closed", _ => "held at the setpoint" };
        var names = Text.List(groups.Select(HeatingZones.Describe));
        return ToolResult.Ok($"{Text.Capitalize(names)} {(groups.Count > 1 ? "are" : "is")} {what} for {Text.Num(hours)} hour{(hours == 1 ? "" : "s")}.",
            Text.Activity($"{HeatingZones.Describe(groups[0])} {(k == "force_open" ? "on" : k == "force_closed" ? "off" : "hold")}"));
    }

    [Tool("heating_clear_override", "Clear a heating zone's override so it follows the schedule again. Zone as for heating_zone_override, or 'whole house'.")]
    public async Task<ToolResult> ClearOverride([Param("Zone, e.g. 'upstairs' or 'whole house'")] string zone, CancellationToken cancel = default)
    {
        var groups = HeatingZones.ResolveGroups(zone);
        var client = RequireClient();
        foreach (var group in groups) await client.ClearOverrideAsync(group, cancel).ConfigureAwait(false);
        return ToolResult.Ok($"Cleared the override on {Text.List(groups.Select(HeatingZones.Describe))}.", Text.Activity("Override cleared"));
    }

    [Tool("heating_zone_offset", "Make a heating zone run warmer or cooler than the house setpoint by an offset in °C (e.g. 0.5, -1, 0 to reset). Zones as for heating_zone_override.")]
    public async Task<ToolResult> ZoneOffset(
        [Param("Zone, e.g. 'upstairs sunny side'")] string zone,
        [Param("Offset in °C relative to the house setpoint, e.g. 0.5 or -1", Min = -5, Max = 5)] double offsetC,
        CancellationToken cancel = default)
    {
        if (Math.Abs(offsetC) > 5) throw new PluginException("Offsets go from -5 to +5 °C.");
        var groups = HeatingZones.ResolveGroups(zone);
        var client = RequireClient();
        foreach (var group in groups) await client.SetOffsetAsync(group, offsetC, cancel).ConfigureAwait(false);
        var sign = offsetC.ToString("+0.#;-0.#;0", System.Globalization.CultureInfo.InvariantCulture);
        return ToolResult.Ok($"{Text.Capitalize(Text.List(groups.Select(HeatingZones.Describe)))} offset set to {sign} °C.", Text.Activity($"Offset {sign}°C"));
    }

    [Tool("heating_history", "A floor's temperature over the last hours: lowest, highest, latest, trend and how often it called for heat.")]
    public async Task<string> History(
        [Param("Floor: downstairs (ground), middle, upstairs (top)")] string floor,
        [Param("How many hours back (default 12, max 168)", Min = 1, Max = 168)] double hours = 12,
        CancellationToken cancel = default)
    {
        var f = HeatingZones.ResolveFloor(floor);
        hours = Math.Clamp(hours, 1, 168);
        var to = DateTimeOffset.UtcNow;
        var rows = await RequireClient().GetHistoryAsync(f, to.AddHours(-hours), to, 24, cancel).ConfigureAwait(false);
        return HeatingSummary.History(f, hours, rows);
    }
}
