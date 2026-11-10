namespace Pucksley.Sdk;

/// <summary>
/// Implemented by the plugin that owns timers (the built-in Timers plugin). The puck's Timer face
/// starts and cancels timers through it; the plugin shows them on the pucks with
/// <see cref="IPluginContext.ShowTimer"/>.
/// </summary>
public interface IPuckTimerHandler
{
    /// <summary>Someone set a timer on the puck's Timer face.</summary>
    Task StartFromPuckAsync(TimeSpan duration, string? label, ToolCaller caller, CancellationToken cancel);

    /// <summary>Someone cancelled a timer on the puck.</summary>
    Task CancelFromPuckAsync(string timerId, CancellationToken cancel);

    /// <summary>Running timers, for a puck that just connected.</summary>
    IReadOnlyList<PuckTimerInfo> ActiveTimers { get; }
}

/// <summary>A running timer as the puck shows it.</summary>
public sealed record PuckTimerInfo(string Id, DateTimeOffset EndsAt, string? Label, string? PuckId);
