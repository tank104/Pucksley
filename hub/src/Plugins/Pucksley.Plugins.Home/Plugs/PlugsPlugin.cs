using Microsoft.Extensions.Logging;
using Pucksley.Plugins.Shared;
using Pucksley.Sdk;

namespace Pucksley.Plugins.Plugs;

/// <summary>Smart plugs and relays (Shelly, Kasa, Tapo, plain HTTP): tools, a toggle per plug on the puck, polled every 30 s.</summary>
public sealed class PlugsPlugin : PuckPlugin<PlugsSettings>
{
    static readonly TimeSpan DeviceTimeout = TimeSpan.FromSeconds(3);
    static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);
    static readonly HashSet<string> AllWords =
        ["all", "all plugs", "all the plugs", "all of them", "every plug", "everything", "all switches", "all the switches", "every switch"];

    /// <summary>One configured plug and what we last heard from it (fields guarded by <c>lock (plug)</c>).</summary>
    sealed class Plug(PlugDevice config, IPlugDriver driver, string controlId)
    {
        public PlugDevice Config { get; } = config;
        public IPlugDriver Driver { get; } = driver;
        public string ControlId { get; } = controlId;
        public string Name => Config.Name.Trim();
        public bool? On;
        public double? Watts;
        public bool Reachable = true;
        public string? Error;
    }

    volatile IReadOnlyList<Plug> _plugs = [];
    HttpClient? _klapHttp;
    Poller? _poller;

    public override string Id => "plugs";
    public override string Name => "Smart plugs";
    public override string Description => "Shelly, TP-Link Kasa and Tapo plugs and relays, or anything with on/off URLs.";
    public override string Icon => "plug";
    public override bool EnabledByDefault => false;

    public override async Task StartAsync(CancellationToken cancel)
    {
        // KLAP needs its own client: the session cookie is sent by hand, and the shared client may keep cookies.
        _klapHttp = new HttpClient(new SocketsHttpHandler { UseCookies = false, ConnectTimeout = DeviceTimeout, PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30) })
        {
            Timeout = TimeSpan.FromSeconds(10),
        };
        var plugs = new List<Plug>();
        var problems = new List<string>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var device in Settings.Devices)
        {
            if (string.IsNullOrWhiteSpace(device.Name)) continue;
            var driver = CreateDriver(device, out var problem);
            if (driver is null)
            {
                problems.Add(problem!);
                continue;
            }
            var id = Text.Slug(device.Name);
            for (int n = 2; !ids.Add(id); n++) id = $"{Text.Slug(device.Name)}-{n}";
            plugs.Add(new Plug(device, driver, id));
        }
        _plugs = plugs;
        if (plugs.Count == 0)
        {
            SetStatus(StatusLevel.Warning, problems.Count > 0 ? problems[0] : "Add your plugs below");
            return;
        }
        await RefreshAllAsync(cancel).ConfigureAwait(false);
        Context.ControlsChanged();
        UpdateStatus(problems);
        _poller = new Poller(PollInterval, async c =>
        {
            await RefreshAllAsync(c).ConfigureAwait(false);
            UpdateStatus(problems);
        }, Log, "Plugs");
    }

    public override async Task StopAsync()
    {
        if (_poller is not null) await _poller.DisposeAsync().ConfigureAwait(false);
        _poller = null;
        _plugs = [];
        _klapHttp?.Dispose();
        _klapHttp = null;
    }

    IPlugDriver? CreateDriver(PlugDevice d, out string? problem)
    {
        problem = null;
        var name = d.Name.Trim();
        var host = d.Host.Trim();
        var type = d.Type.Trim().ToLowerInvariant();
        if (type != "http" && host.Length == 0)
        {
            problem = $"{name}: set its host";
            return null;
        }
        switch (type)
        {
            case "shelly":
                return new ShellyDriver(Context.Http, name, host);
            case "kasa":
                return new KasaDriver(name, host, () => new KlapTransport(_klapHttp!, host, Settings.TpLinkEmail, Settings.TpLinkPassword));
            case "tapo":
                return new TapoDriver(name, new KlapTransport(_klapHttp!, host, Settings.TpLinkEmail, Settings.TpLinkPassword));
            case "http":
                if (string.IsNullOrWhiteSpace(d.OnUrl) && string.IsNullOrWhiteSpace(d.OffUrl))
                {
                    problem = $"{name}: set its On and Off URLs";
                    return null;
                }
                return new HttpPlugDriver(Context.Http, d);
            default:
                problem = $"{name}: unknown type '{d.Type}'";
                return null;
        }
    }

    // ---------------------------------------------------------------- polling

    Task RefreshAllAsync(CancellationToken cancel) => Task.WhenAll(_plugs.Select(p => RefreshAsync(p, cancel)));

    async Task RefreshAsync(Plug plug, CancellationToken cancel)
    {
        try
        {
            var state = await Call(plug, c => plug.Driver.GetStateAsync(c), cancel).ConfigureAwait(false);
            Record(plug, state.On, state.Watts, null);
        }
        catch (PluginException e)
        {
            Record(plug, null, null, e.Message);
        }
    }

    /// <summary>Stores what a plug reported and updates its dial when it changed. <paramref name="error"/> = unreachable.</summary>
    void Record(Plug plug, bool? on, double? watts, string? error)
    {
        bool changed;
        bool? shownOn;
        lock (plug)
        {
            bool reachable = error is null;
            changed = plug.Reachable != reachable || (reachable && on is not null && plug.On != on);
            plug.Reachable = reachable;
            plug.Error = error;
            if (reachable)
            {
                if (on is not null) plug.On = on;
                plug.Watts = watts;
            }
            shownOn = plug.On;
        }
        if (changed) Context.ControlValueChanged(plug.ControlId, on: shownOn ?? false, text: error is null ? null : "Offline");
    }

    void UpdateStatus(IReadOnlyList<string> problems)
    {
        var plugs = _plugs;
        int reachable = plugs.Count(p => p.Reachable);
        var message = $"{Text.Count(plugs.Count, "plug")}, {reachable} reachable";
        if (problems.Count > 0) message += $" ({problems[0]})";
        SetStatus(reachable == plugs.Count && problems.Count == 0 ? StatusLevel.Ok : StatusLevel.Warning, message);
    }

    static Task<T> Call<T>(Plug plug, Func<CancellationToken, Task<T>> call, CancellationToken cancel) =>
        DeviceCall.RunAsync(plug.Name, DeviceTimeout, cancel, async c =>
        {
            try
            {
                return await call(c).ConfigureAwait(false);
            }
            catch (TapoException e)
            {
                throw new PluginException(e.Message, e);
            }
            catch (InvalidDataException e)
            {
                throw new PluginException($"{plug.Name} sent something unexpected ({e.Message.TrimEnd('.')}).", e);
            }
        });

    // ---------------------------------------------------------------- tools

    [Tool("plugs_list", "List the smart plugs and relays: on or off, and power draw in watts where the plug measures it.")]
    public async Task<object> List(CancellationToken cancel = default)
    {
        var plugs = RequirePlugs();
        await RefreshAllAsync(cancel).ConfigureAwait(false);
        return plugs.Select(p =>
        {
            lock (p)
                return new
                {
                    name = p.Name,
                    on = p.Reachable ? p.On : null,
                    watts = p.Reachable && p.Watts is double w ? Math.Round(w, 1) : (double?)null,
                    error = p.Reachable ? null : p.Error,
                };
        }).ToList();
    }

    [Tool("plugs_set", "Turn a smart plug or relay on or off. The name can be 'all'.")]
    public async Task<ToolResult> Set(
        [Param("Plug name, e.g. 'lamp', or 'all'")] string name,
        [Param("true = on, false = off")] bool on,
        CancellationToken cancel = default)
    {
        var targets = Resolve(name);
        var results = await Task.WhenAll(targets.Select(async p =>
        {
            try
            {
                await Call(p, async c =>
                {
                    await p.Driver.SetAsync(on, c).ConfigureAwait(false);
                    return true;
                }, cancel).ConfigureAwait(false);
                Record(p, on, null, null);
                return (Plug: p, Error: (string?)null);
            }
            catch (PluginException e)
            {
                return (Plug: p, Error: e.Message);
            }
        })).ConfigureAwait(false);
        return Summarize(results, on ? "on" : "off");
    }

    [Tool("plugs_toggle", "Toggle a smart plug or relay (on becomes off and the other way). The name can be 'all'.")]
    public async Task<ToolResult> Toggle([Param("Plug name, e.g. 'lamp', or 'all'")] string name, CancellationToken cancel = default)
    {
        var targets = Resolve(name);
        var results = await Task.WhenAll(targets.Select(async p =>
        {
            try
            {
                bool now = await Call(p, c => p.Driver.ToggleAsync(c), cancel).ConfigureAwait(false);
                Record(p, now, null, null);
                return (Plug: p, Error: (string?)null);
            }
            catch (PluginException e)
            {
                return (Plug: p, Error: e.Message);
            }
        })).ConfigureAwait(false);
        return Summarize(results, null);
    }

    /// <summary>"Lamp is on." / "Turned off Lamp and Heater; Fan didn't answer within 3 s."</summary>
    static ToolResult Summarize(IReadOnlyList<(Plug Plug, string? Error)> results, string? state)
    {
        var ok = results.Where(r => r.Error is null).Select(r => r.Plug).ToList();
        var failed = results.Where(r => r.Error is not null).ToList();
        if (ok.Count == 0) throw new PluginException(string.Join(" ", failed.Select(f => f.Error)));

        string StateOf(Plug p)
        {
            lock (p) return p.On == true ? "on" : "off";
        }

        string text = ok.Count == 1
            ? $"{ok[0].Name} is {(state ?? StateOf(ok[0]))}."
            : state is null
                ? string.Join(" ", ok.Select(p => $"{p.Name} is {StateOf(p)}."))
                : $"Turned {state} {Text.List(ok.Select(p => p.Name))}.";
        if (failed.Count > 0) text += " " + string.Join(" ", failed.Select(f => f.Error));
        string activity = ok.Count == 1 ? $"{ok[0].Name} {state ?? StateOf(ok[0])}" : $"{ok.Count} plugs {state ?? "toggled"}";
        return ToolResult.Ok(text, Text.Activity(activity));
    }

    IReadOnlyList<Plug> RequirePlugs()
    {
        var plugs = _plugs;
        if (plugs.Count == 0) throw new PluginException("No plugs are set up. Add them on the Smart plugs settings in the hub's web page.");
        return plugs;
    }

    IReadOnlyList<Plug> Resolve(string spoken)
    {
        var plugs = RequirePlugs();
        if (AllWords.Contains(NameMatcher.Normalize(spoken ?? ""))) return plugs;
        var plug = Names.Best(spoken, plugs, p => p.Name)
                   ?? throw new PluginException($"I can't find a plug called '{spoken}'. Plugs: {Text.List(plugs.Select(p => p.Name))}.");
        return [plug];
    }

    // ---------------------------------------------------------------- controls

    public override IReadOnlyList<PuckControl> GetControls() => _plugs.Select(p =>
    {
        lock (p)
            return new PuckControl
            {
                Id = p.ControlId, Name = p.Name, Group = "Plugs", Kind = ControlKind.Toggle,
                On = p.On ?? false, Text = p.Reachable ? null : "Offline",
            };
    }).ToList();

    public override async Task OnControlAsync(string controlId, ControlInput input, CancellationToken cancel)
    {
        var plug = _plugs.FirstOrDefault(p => p.ControlId == controlId);
        if (plug is null || input.On is not bool on) return;
        try
        {
            await Call(plug, async c =>
            {
                await plug.Driver.SetAsync(on, c).ConfigureAwait(false);
                return true;
            }, cancel).ConfigureAwait(false);
            Record(plug, on, null, null);
            Context.ControlValueChanged(plug.ControlId, on: on);
        }
        catch (PluginException e)
        {
            Log.LogWarning("Plug {Name}: {Message}", plug.Name, e.Message);
            Record(plug, null, null, e.Message);
            bool? actual;
            lock (plug) actual = plug.On;
            // The puck already shows the new position: put it back to what's real
            Context.ControlValueChanged(plug.ControlId, on: actual ?? false, text: "Offline");
            await Context.NotifyAsync(e.Message, NotifyKind.Alert).ConfigureAwait(false);
        }
    }
}
