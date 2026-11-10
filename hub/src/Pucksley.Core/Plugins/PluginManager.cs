using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Pucksley.Core.Settings;
using Pucksley.Sdk;

namespace Pucksley.Core.Plugins;

/// <summary>One loaded plugin and its state.</summary>
public sealed class PluginHost
{
    internal PluginHost(IPlugin plugin, PluginContext context, string source)
    {
        Plugin = plugin;
        Context = context;
        Source = source;
    }

    public IPlugin Plugin { get; }
    internal PluginContext Context { get; }
    /// <summary>Built in, or the DLL it came from.</summary>
    public string Source { get; }
    public bool Enabled { get; internal set; }
    public bool Running { get; internal set; }
    public string? StartError { get; internal set; }
    internal SemaphoreSlim Gate { get; } = new(1, 1);
    public string Id => Plugin.Id;
}

/// <summary>A puck control with its plugin.</summary>
public sealed record ControlEntry(string GlobalId, PuckControl Control, PluginHost Host);

/// <summary>A media source with its plugin.</summary>
public sealed record MediaSourceEntry(string GlobalId, string Name, string LocalId, IMediaSourceProvider Provider, PluginHost Host);

/// <summary>
/// Finds the plugins, starts and stops them, keeps their settings, and gathers what they offer:
/// tools, context, controls, media sources and speech engines.
/// </summary>
public sealed partial class PluginManager : IAsyncDisposable
{
    static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(45);
    static readonly TimeSpan ToolTimeout = TimeSpan.FromSeconds(45);

    readonly SettingsStore _settings;
    readonly ILoggerFactory _loggers;
    readonly ILogger _log;
    readonly Func<IHubRuntime> _runtime;
    readonly List<PluginHost> _plugins = [];
    readonly object _cacheGate = new();
    IReadOnlyList<ToolDefinition>? _tools;
    IReadOnlyList<ControlEntry>? _controls;
    readonly CancellationTokenSource _shutdown = new();

    public PluginManager(SettingsStore settings, ILoggerFactory loggers, Func<IHubRuntime> runtime)
    {
        _settings = settings;
        _loggers = loggers;
        _log = loggers.CreateLogger("Plugins");
        _runtime = runtime;
    }

    internal IHubRuntime Runtime => _runtime();
    internal SettingsStore Settings => _settings;

    public IReadOnlyList<PluginHost> Plugins { get { lock (_plugins) return _plugins.ToList(); } }

    /// <summary>A plugin's tools / status / controls changed.</summary>
    public event Action? ToolsChanged;
    public event Action? ControlsChanged;
    public event Action<string, double?, bool?, string?>? ControlValueChanged;
    public event Action<string>? NowPlayingChanged;
    public event Action<PluginHost>? StatusChanged;

    // ------------------------------------------------------------------ discovery

    /// <summary>Loads plugins from the given assemblies and from DLLs in the plugins folders.</summary>
    public void Discover(IEnumerable<Assembly> builtIn)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var asm in builtIn) LoadFrom(asm, "built in", seen);

        foreach (var dir in new[] { HubPaths.PluginsDir, Path.Combine(AppContext.BaseDirectory, "plugins") }.Distinct())
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var dll in Directory.EnumerateFiles(dir, "*.dll", SearchOption.AllDirectories))
            {
                var name = Path.GetFileNameWithoutExtension(dll);
                if (name.Equals("Pucksley.Sdk", StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    var asm = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.GetFullPath(dll));
                    LoadFrom(asm, Path.GetFileName(dll), seen);
                }
                catch (Exception e) when (e is BadImageFormatException or FileLoadException or FileNotFoundException)
                {
                    // dependencies of a plugin sit in the same folder and aren't plugins themselves
                    _log.LogDebug("Skipped {Dll}: {Message}", dll, e.Message);
                }
            }
        }
        _log.LogInformation("Loaded {Count} plugins: {Ids}", _plugins.Count, string.Join(", ", _plugins.Select(p => p.Id)));
    }

    void LoadFrom(Assembly asm, string source, HashSet<string> seen)
    {
        Type[] types;
        try { types = asm.GetTypes(); }
        catch (ReflectionTypeLoadException e) { types = e.Types.Where(t => t is not null).ToArray()!; }
        foreach (var t in types)
        {
            if (t is null || t.IsAbstract || t.IsInterface || !typeof(IPlugin).IsAssignableFrom(t)) continue;
            if (t.GetConstructor(Type.EmptyTypes) is null) continue;
            try
            {
                var plugin = (IPlugin)Activator.CreateInstance(t)!;
                if (!IdPattern().IsMatch(plugin.Id) || !seen.Add(plugin.Id))
                {
                    _log.LogWarning("Plugin {Type} has a bad or duplicate id '{Id}'; skipped", t.FullName, plugin.Id);
                    continue;
                }
                var host = new PluginHost(plugin, null!, source);
                var context = new PluginContext(this, host, _loggers.CreateLogger($"Plugin.{plugin.Id}"));
                var real = new PluginHost(plugin, context, source);
                context.Bind(real);
                plugin.Initialize(context);
                var state = _settings.Current.Plugins.GetValueOrDefault(plugin.Id);
                real.Enabled = state?.Enabled ?? plugin.EnabledByDefault;
                lock (_plugins) _plugins.Add(real);
            }
            catch (Exception e)
            {
                _log.LogError(e, "Couldn't load plugin {Type}", t.FullName);
            }
        }
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,39}$")]
    private static partial Regex IdPattern();

    [GeneratedRegex("^[a-zA-Z0-9_-]{1,64}$")]
    private static partial Regex ToolNamePattern();

    public PluginHost? Find(string id) => Plugins.FirstOrDefault(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    // ------------------------------------------------------------------ lifecycle

    /// <summary>Starts every enabled plugin in the background (a slow one doesn't hold up the hub).</summary>
    public Task StartAllAsync()
    {
        var tasks = Plugins.Where(p => p.Enabled).Select(p => StartAsync(p)).ToArray();
        return Task.WhenAll(tasks);
    }

    async Task StartAsync(PluginHost h)
    {
        await h.Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (h.Running) return;
            var settings = _settings.PluginSettings(h.Id, h.Plugin.SettingsType);
            h.Plugin.ApplySettings(settings);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
            cts.CancelAfter(StartTimeout);
            await h.Plugin.StartAsync(cts.Token).ConfigureAwait(false);
            h.Running = true;
            h.StartError = null;
            _log.LogInformation("Started {Id}: {Status}", h.Id, h.Plugin.Status.Message);
        }
        catch (Exception e)
        {
            // Still "running" for tools: they can explain what's wrong when called
            h.Running = true;
            h.StartError = e is OperationCanceledException ? "Took too long to start" : e.Message;
            _log.LogWarning(e, "Plugin {Id} failed to start", h.Id);
        }
        finally
        {
            h.Gate.Release();
            Invalidate();
            StatusChanged?.Invoke(h);
        }
    }

    async Task StopAsync(PluginHost h)
    {
        await h.Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!h.Running) return;
            h.Running = false;
            var stop = h.Plugin.StopAsync();
            if (await Task.WhenAny(stop, Task.Delay(TimeSpan.FromSeconds(10))).ConfigureAwait(false) != stop)
                _log.LogWarning("Plugin {Id} took too long to stop", h.Id);
            else await stop.ConfigureAwait(false);
        }
        catch (Exception e)
        {
            _log.LogWarning(e, "Plugin {Id} failed to stop", h.Id);
        }
        finally
        {
            h.Gate.Release();
            Invalidate();
            StatusChanged?.Invoke(h);
        }
    }

    public async Task SetEnabledAsync(string id, bool enabled)
    {
        var h = Find(id) ?? throw new KeyNotFoundException(id);
        _settings.Update(s =>
        {
            if (!s.Plugins.TryGetValue(h.Id, out var st)) s.Plugins[h.Id] = st = new PluginState();
            st.Enabled = enabled;
        });
        h.Enabled = enabled;
        if (enabled) await StartAsync(h).ConfigureAwait(false);
        else await StopAsync(h).ConfigureAwait(false);
    }

    /// <summary>The plugin's current settings object.</summary>
    public object CurrentSettings(PluginHost h) => _settings.PluginSettings(h.Id, h.Plugin.SettingsType);

    /// <summary>Save new settings and restart the plugin if it's on.</summary>
    public async Task UpdateSettingsAsync(string id, object settings)
    {
        var h = Find(id) ?? throw new KeyNotFoundException(id);
        _settings.SavePluginSettings(h.Id, settings);
        if (h.Enabled)
        {
            await StopAsync(h).ConfigureAwait(false);
            await StartAsync(h).ConfigureAwait(false);
        }
    }

    /// <summary>A plugin saved its own settings (e.g. a refreshed token): persist without restarting.</summary>
    internal void PersistFromPlugin(PluginHost h, object settings) => _settings.SavePluginSettings(h.Id, settings);

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        await Task.WhenAll(Plugins.Where(p => p.Running).Select(StopAsync)).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ what plugins offer

    IEnumerable<PluginHost> Active => Plugins.Where(p => p.Enabled && p.Running);

    internal void Invalidate()
    {
        lock (_cacheGate)
        {
            _tools = null;
            _controls = null;
        }
        ToolsChanged?.Invoke();
        ControlsChanged?.Invoke();
    }

    internal void OnToolsChanged(PluginHost h)
    {
        lock (_cacheGate) _tools = null;
        ToolsChanged?.Invoke();
    }

    internal void OnControlsChanged(PluginHost h)
    {
        lock (_cacheGate) _controls = null;
        ControlsChanged?.Invoke();
    }

    internal void OnControlValue(PluginHost h, string localId, double? value, bool? on, string? text) =>
        ControlValueChanged?.Invoke($"{h.Id}:{localId}", value, on, text);

    internal void OnNowPlaying(PluginHost h, string localSource) => NowPlayingChanged?.Invoke($"{h.Id}:{localSource}");

    internal void OnStatus(PluginHost h) => StatusChanged?.Invoke(h);

    /// <summary>Every tool from every enabled plugin, in a stable order (stable = prompt-cache friendly).</summary>
    public IReadOnlyList<ToolDefinition> Tools
    {
        get
        {
            lock (_cacheGate)
            {
                if (_tools is not null) return _tools;
                var list = new List<ToolDefinition>();
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var h in Active.OrderBy(p => p.Id, StringComparer.Ordinal))
                {
                    IReadOnlyList<ToolDefinition> tools;
                    try { tools = h.Plugin.GetTools(); }
                    catch (Exception e)
                    {
                        _log.LogWarning(e, "Plugin {Id} failed to list tools", h.Id);
                        continue;
                    }
                    foreach (var t in tools)
                    {
                        if (!ToolNamePattern().IsMatch(t.Name) || !names.Add(t.Name))
                        {
                            _log.LogWarning("Tool '{Name}' from {Id} has a bad or duplicate name; skipped", t.Name, h.Id);
                            continue;
                        }
                        t.PluginId = h.Id;
                        list.Add(t);
                    }
                }
                return _tools = list;
            }
        }
    }

    /// <summary>
    /// Runs a tool: asks for confirmation if it wants one, applies a timeout, and turns every
    /// failure into an error result for Claude.
    /// </summary>
    public async Task<ToolResult> ExecuteToolAsync(string name, JsonObject args, ToolCaller caller,
        Func<string, Task<bool>>? confirm, CancellationToken cancel)
    {
        var tool = Tools.FirstOrDefault(t => t.Name == name);
        if (tool is null) return ToolResult.Error($"There's no tool called {name} (its plugin may be off).");
        if (tool.Confirm && confirm is not null)
        {
            var summary = ConfirmText(tool, args);
            bool ok;
            try { ok = await confirm(summary).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch (Exception e) { return ToolResult.Error($"Couldn't ask for confirmation: {e.Message}"); }
            if (!ok) return ToolResult.Error("The person said no (or didn't answer), so it wasn't done.");
        }
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancel, _shutdown.Token);
        cts.CancelAfter(ToolTimeout);
        try
        {
            var task = tool.Handler(new ToolInvocation { Arguments = args, Caller = caller, Cancellation = cts.Token });
            var result = await task.WaitAsync(cts.Token).ConfigureAwait(false);
            return result ?? ToolResult.Ok("Done.");
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
        {
            return ToolResult.Error("That took too long and was stopped.");
        }
        catch (PluginException e)
        {
            return ToolResult.Error(e.Message);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _log.LogWarning(e, "Tool {Name} failed", name);
            return ToolResult.Error($"{tool.Name} failed: {e.Message}");
        }
    }

    static string ConfirmText(ToolDefinition tool, JsonObject args)
    {
        // "Shut down or restart this PC" + the arguments that say something: values, and true
        // flags by name ("restart"); false / empty / zero ones are left out
        var desc = tool.Description.Split('.', 2)[0].Trim().TrimEnd(':', ',');
        var parts = new List<string>();
        foreach (var (key, value) in args)
        {
            if (value is not JsonValue v) continue;
            if (v.TryGetValue<bool>(out var b)) { if (b) parts.Add(key.Replace('_', ' ')); continue; }
            if (v.TryGetValue<string>(out var s)) { if (!string.IsNullOrWhiteSpace(s)) parts.Add(Short(s)); continue; }
            if (v.TryGetValue<double>(out var d) && d != 0) parts.Add($"{key.Replace('_', ' ')} {d:0.##}");
        }
        var text = parts.Count > 0 ? $"{desc}: {string.Join(", ", parts)}?" : $"{desc}?";
        return text.Length > 150 ? text[..147] + "…?" : text;

        static string Short(string s) => s.Length > 40 ? s[..37] + "…" : s;
    }

    /// <summary>Static guidance from plugins for the system prompt, in a stable order.</summary>
    public IReadOnlyList<(string PluginName, string Text)> Instructions =>
        Active.OrderBy(p => p.Id, StringComparer.Ordinal)
            .Select(p => { try { return (p.Plugin.Name, Text: p.Plugin.Instructions); } catch { return (p.Plugin.Name, Text: (string?)null); } })
            .Where(x => !string.IsNullOrWhiteSpace(x.Text))
            .Select(x => (x.Name, x.Text!.Trim()))
            .ToList();

    /// <summary>Live context from every plugin, each given at most <paramref name="budget"/>.</summary>
    public async Task<IReadOnlyList<string>> GatherContextAsync(ToolCaller caller, TimeSpan budget, CancellationToken cancel)
    {
        var tasks = Active.OrderBy(p => p.Id, StringComparer.Ordinal).Select(async h =>
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            cts.CancelAfter(budget);
            try
            {
                var t = h.Plugin.GetContextAsync(caller, cts.Token);
                var s = await t.WaitAsync(cts.Token).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(s)) return null;
                s = s.Trim();
                return s.Length > 600 ? s[..600] + "…" : s;
            }
            catch (Exception e) when (e is not OperationCanceledException || !cancel.IsCancellationRequested)
            {
                return null;
            }
        }).ToArray();
        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        return results.Where(r => r is not null).ToList()!;
    }

    /// <summary>All puck controls (at most 16), ids prefixed with the plugin id.</summary>
    public IReadOnlyList<ControlEntry> Controls
    {
        get
        {
            lock (_cacheGate)
            {
                if (_controls is not null) return _controls;
                var list = new List<ControlEntry>();
                foreach (var h in Active.OrderBy(p => p.Id, StringComparer.Ordinal))
                {
                    try
                    {
                        foreach (var c in h.Plugin.GetControls())
                            list.Add(new ControlEntry($"{h.Id}:{c.Id}", c with { Group = c.Group ?? h.Plugin.Name }, h));
                    }
                    catch (Exception e)
                    {
                        _log.LogWarning(e, "Plugin {Id} failed to list controls", h.Id);
                    }
                }
                return _controls = list.Take(16).ToList();
            }
        }
    }

    public async Task OnControlAsync(string globalId, ControlInput input, CancellationToken cancel)
    {
        int colon = globalId.IndexOf(':');
        if (colon <= 0) return;
        var h = Find(globalId[..colon]);
        if (h is null || !h.Running) return;
        try
        {
            await h.Plugin.OnControlAsync(globalId[(colon + 1)..], input, cancel).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _log.LogWarning(e, "Control {Id} failed", globalId);
        }
    }

    /// <summary>Every media source (Sonos rooms, Spotify, this PC…).</summary>
    public IReadOnlyList<MediaSourceEntry> MediaSources
    {
        get
        {
            var list = new List<MediaSourceEntry>();
            foreach (var h in Active.OrderBy(p => p.Id, StringComparer.Ordinal))
            {
                if (h.Plugin is not IMediaSourceProvider provider) continue;
                try
                {
                    foreach (var s in provider.GetMediaSources())
                        list.Add(new MediaSourceEntry($"{h.Id}:{s.Id}", s.Name, s.Id, provider, h));
                }
                catch (Exception e)
                {
                    _log.LogDebug(e, "Plugin {Id} failed to list media sources", h.Id);
                }
            }
            return list;
        }
    }

    public IReadOnlyList<ISpeechToText> SttEngines => Active.Select(p => p.Plugin).OfType<ISpeechToText>().ToList();

    public IReadOnlyList<ITextToSpeech> TtsEngines => Active.Select(p => p.Plugin).OfType<ITextToSpeech>().ToList();

    /// <summary>An enabled plugin by type or interface.</summary>
    public T? GetPlugin<T>() where T : class => Active.Select(p => p.Plugin).OfType<T>().FirstOrDefault();

    public IPuckTimerHandler? TimerHandler => GetPlugin<IPuckTimerHandler>();
}
