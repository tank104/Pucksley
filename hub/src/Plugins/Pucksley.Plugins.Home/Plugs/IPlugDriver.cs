namespace Pucksley.Plugins.Plugs;

/// <summary>What a plug reported: on/off (null = unknown) and power draw in watts when it measures it.</summary>
internal sealed record PlugState(bool? On, double? Watts = null);

/// <summary>Talks to one kind of plug. Calls may come from several tools at once; implementations must cope.</summary>
internal interface IPlugDriver
{
    Task<PlugState> GetStateAsync(CancellationToken cancel);

    Task SetAsync(bool on, CancellationToken cancel);

    /// <summary>Flips it; returns the new state.</summary>
    Task<bool> ToggleAsync(CancellationToken cancel);
}
