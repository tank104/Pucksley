namespace Pucksley.Plugins.Tv;

internal enum TvPower { Unknown, On, Standby, Off }

/// <summary>An input or app a TV offers: its id and display name.</summary>
internal sealed record TvItem(string Id, string Name);

/// <summary>
/// Talks to one TV. Methods return one short sentence for Claude ("Lounge TV volume is 20.") and
/// throw <see cref="Pucksley.Sdk.PluginException"/> for anything the person should hear about.
/// </summary>
internal interface ITvDriver : IAsyncDisposable
{
    TvDevice Device { get; }

    /// <summary>"lounge TV": how results refer to it.</summary>
    string Label { get; }

    /// <summary>True when the TV must be paired (accept a prompt on the TV) before it can be controlled.</summary>
    bool NeedsPairing { get; }

    Task<TvPower> GetPowerAsync(CancellationToken cancel);

    Task<string> PowerAsync(bool on, CancellationToken cancel);

    Task<string> VolumeAsync(int? volume, int? change, bool? mute, CancellationToken cancel);

    Task<string> InputAsync(string input, CancellationToken cancel);

    Task<string> AppAsync(string app, CancellationToken cancel);

    /// <summary>play, pause, stop, home or back.</summary>
    Task<string> MediaAsync(string action, CancellationToken cancel);

    Task<string> ChannelAsync(bool up, CancellationToken cancel);

    Task<string> MessageAsync(string text, CancellationToken cancel);

    /// <summary>Connects and waits for the person to accept on the TV; stores the key it gives.</summary>
    Task<string> PairAsync(CancellationToken cancel);
}
