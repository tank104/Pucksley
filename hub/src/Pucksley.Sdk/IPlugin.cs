using Microsoft.Extensions.Logging;

namespace Pucksley.Sdk;

/// <summary>
/// A plugin: a bundle of tools Claude can call, plus (optionally) live context for the system
/// prompt, dials for the puck, media sources, speech engines, buttons for the hub's web page and
/// HTTP endpoints. Most plugins derive from <see cref="PuckPlugin{TSettings}"/> rather than
/// implementing this directly.
/// </summary>
/// <remarks>
/// Plugins are discovered by reflection: any public, non-abstract class implementing
/// <see cref="IPlugin"/> with a public parameterless constructor, in the hub's own assemblies or in
/// a DLL dropped into the <c>plugins</c> folder. One instance per plugin lives for the life of the hub.
/// Everything here may be called concurrently (several tools can run at once), so keep state
/// thread-safe.
/// </remarks>
public interface IPlugin
{
    /// <summary>Stable id, lower-case, e.g. <c>"sonos"</c>. Used in settings, URLs and control ids.</summary>
    string Id { get; }

    /// <summary>Display name, e.g. "Sonos".</summary>
    string Name { get; }

    /// <summary>One or two sentences for the web page.</summary>
    string Description { get; }

    /// <summary>Activity icon shown on the puck while this plugin's tools run: one of
    /// <c>music volume heat light tv plug search timer pc home note weather tool</c>.</summary>
    string Icon { get; }

    /// <summary>Whether the plugin starts enabled on a fresh install.</summary>
    bool EnabledByDefault { get; }

    /// <summary>The settings class (a POCO with <see cref="SettingAttribute"/> on its properties).</summary>
    Type SettingsType { get; }

    /// <summary>Called once, before anything else.</summary>
    void Initialize(IPluginContext context);

    /// <summary>New settings (on start and whenever they're changed in the web page). Called while stopped.</summary>
    void ApplySettings(object settings);

    /// <summary>Start (connect, discover, warm caches). Throwing marks the plugin as failed; it stays loaded.</summary>
    Task StartAsync(CancellationToken cancel);

    /// <summary>Stop (before new settings are applied, when disabled, and at shutdown).</summary>
    Task StopAsync();

    /// <summary>Current status for the web page.</summary>
    PluginStatus Status { get; }

    /// <summary>The tools Claude can call. Re-read after settings change and when <see cref="IPluginContext.ToolsChanged"/> is called.</summary>
    IReadOnlyList<ToolDefinition> GetTools();

    /// <summary>
    /// Static guidance added to the system prompt (cached), e.g. "When no room is named, use the
    /// room the puck is in." Keep it short. Null for none.
    /// </summary>
    string? Instructions { get; }

    /// <summary>
    /// Live state added to each user turn, e.g. "Kitchen: playing Teardrop (vol 25); Lounge: idle".
    /// Must be quick (it is awaited with a ~1 s budget before every turn) and short (≤ 400 chars).
    /// Null for nothing.
    /// </summary>
    Task<string?> GetContextAsync(ToolCaller caller, CancellationToken cancel);

    /// <summary>Dials for the puck's Controls face. Ids are local to the plugin.</summary>
    IReadOnlyList<PuckControl> GetControls();

    /// <summary>A person turned / toggled / pressed one of this plugin's controls on the puck.</summary>
    Task OnControlAsync(string controlId, ControlInput input, CancellationToken cancel);

    /// <summary>Buttons for the web page (Connect account, Discover devices, Pair TV…).</summary>
    IReadOnlyList<PluginAction> GetActions();

    /// <summary>A button was clicked.</summary>
    Task<ActionResult> RunActionAsync(string actionId, IReadOnlyDictionary<string, string> input, CancellationToken cancel);

    /// <summary>
    /// A request to <c>/plugins/{id}/{path}</c> on the hub (OAuth callbacks and the like). Return null for 404.
    /// </summary>
    Task<PluginHttpResponse?> HandleHttpAsync(PluginHttpRequest request, CancellationToken cancel);
}

/// <summary>
/// The usual way to write a plugin. Override what you need; put <see cref="ToolAttribute"/> on
/// methods to make tools (see <see cref="ToolAttribute"/> for the supported signatures).
/// </summary>
/// <typeparam name="TSettings">A POCO with a parameterless constructor; use <see cref="NoSettings"/> for none.</typeparam>
public abstract class PuckPlugin<TSettings> : IPlugin where TSettings : class, new()
{
    PluginStatus _status = PluginStatus.Idle;
    IReadOnlyList<ToolDefinition>? _attributeTools;

    public abstract string Id { get; }
    public abstract string Name { get; }
    public abstract string Description { get; }
    public virtual string Icon => "tool";
    public virtual bool EnabledByDefault => false;
    public Type SettingsType => typeof(TSettings);
    public virtual string? Instructions => null;

    /// <summary>The current settings. Replaced (not mutated) when the person saves new ones.</summary>
    protected TSettings Settings { get; private set; } = new();

    /// <summary>The hub's services. Available from <see cref="Initialize"/> on.</summary>
    protected IPluginContext Context { get; private set; } = null!;

    protected ILogger Log => Context.Log;

    public PluginStatus Status => _status;

    /// <summary>Update the status shown in the web page.</summary>
    protected void SetStatus(PluginStatus status)
    {
        _status = status;
        Context?.StatusChanged();
    }

    protected void SetStatus(StatusLevel level, string message) => SetStatus(new PluginStatus(level, message));

    public virtual void Initialize(IPluginContext context) => Context = context;

    public void ApplySettings(object settings)
    {
        Settings = settings as TSettings ?? new TSettings();
        OnSettingsApplied();
    }

    /// <summary>Called after <see cref="Settings"/> changed (the plugin is stopped at this point).</summary>
    protected virtual void OnSettingsApplied() { }

    /// <summary>Persist <see cref="Settings"/> after changing it yourself (e.g. a refreshed OAuth token).</summary>
    protected void SaveSettings() => Context.SaveSettings(Settings);

    public virtual Task StartAsync(CancellationToken cancel)
    {
        SetStatus(StatusLevel.Ok, "Ready");
        return Task.CompletedTask;
    }

    public virtual Task StopAsync() => Task.CompletedTask;

    /// <summary>
    /// The tools: by default every <see cref="ToolAttribute"/> method on this class. Override to add
    /// dynamic tools (call <c>base.GetTools()</c> to keep the attribute ones).
    /// </summary>
    public virtual IReadOnlyList<ToolDefinition> GetTools() => _attributeTools ??= ToolBuilder.FromMethods(this, Icon);

    public virtual Task<string?> GetContextAsync(ToolCaller caller, CancellationToken cancel) => Task.FromResult<string?>(null);

    public virtual IReadOnlyList<PuckControl> GetControls() => [];

    public virtual Task OnControlAsync(string controlId, ControlInput input, CancellationToken cancel) => Task.CompletedTask;

    public virtual IReadOnlyList<PluginAction> GetActions() => [];

    public virtual Task<ActionResult> RunActionAsync(string actionId, IReadOnlyDictionary<string, string> input, CancellationToken cancel) =>
        Task.FromResult(new ActionResult($"Unknown action '{actionId}'.", false));

    public virtual Task<PluginHttpResponse?> HandleHttpAsync(PluginHttpRequest request, CancellationToken cancel) =>
        Task.FromResult<PluginHttpResponse?>(null);
}

/// <summary>Settings type for plugins that have none.</summary>
public sealed class NoSettings { }
