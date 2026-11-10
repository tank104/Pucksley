using Pucksley.Sdk;

namespace Pucksley.Plugins.Tv;

public sealed class TvSettings
{
    [Setting("TVs", Order = 0,
        Help = "One row per TV. Name it after its room (e.g. Lounge) so 'turn on the TV' picks the one where the puck is. Click Find TVs to look for them, then Pair.")]
    public List<TvDevice> Tvs { get; set; } = [];
}

/// <summary>One TV.</summary>
public sealed class TvDevice
{
    [Setting("Name", Placeholder = "Lounge", Order = 0)]
    public string Name { get; set; } = "";

    [Setting("Type", Options = ["lg", "samsung", "roku"], Order = 1)]
    public string Type { get; set; } = "lg";

    [Setting("Host", Placeholder = "192.168.1.60", Order = 2, Help = "IP address (give the TV a fixed address in your router).")]
    public string Host { get; set; } = "";

    [Setting("MAC", Placeholder = "AA:BB:CC:DD:EE:FF", Order = 3,
        Help = "For turning it on (Wake-on-LAN). On LG enable Quick Start+ / 'Turn on via Wi-Fi'; on Samsung 'Power on with mobile'.")]
    public string Mac { get; set; } = "";
}
