using Pucksley.Sdk;

namespace Pucksley.Plugins.Plugs;

public sealed class PlugsSettings
{
    [Setting("Plugs", Order = 0,
        Help = "One row per plug or relay. Type: shelly, kasa (TP-Link Kasa), tapo (TP-Link Tapo) or http (your own on/off URLs).")]
    public List<PlugDevice> Devices { get; set; } = [];

    [Setting("TP-Link account email", Order = 1,
        Help = "Only for Tapo plugs and newer Kasa plugs: the account you use in the Tapo or Kasa app.")]
    public string TpLinkEmail { get; set; } = "";

    [Setting("TP-Link account password", Order = 2, Secret = true)]
    public string TpLinkPassword { get; set; } = "";
}

/// <summary>One plug, relay or switch.</summary>
public sealed class PlugDevice
{
    [Setting("Name", Placeholder = "Lamp", Order = 0)]
    public string Name { get; set; } = "";

    [Setting("Type", Options = ["shelly", "kasa", "tapo", "http"], Order = 1)]
    public string Type { get; set; } = "shelly";

    [Setting("Host", Placeholder = "192.168.1.50", Order = 2, Help = "IP address or host name (not needed for http).")]
    public string Host { get; set; } = "";

    [Setting("On URL", Placeholder = "http://192.168.1.60/on", Order = 3, Help = "http type only: requested (GET) to turn it on.")]
    public string OnUrl { get; set; } = "";

    [Setting("Off URL", Placeholder = "http://192.168.1.60/off", Order = 4, Help = "http type only: requested (GET) to turn it off.")]
    public string OffUrl { get; set; } = "";

    [Setting("Status URL", Placeholder = "http://192.168.1.60/status", Order = 5,
        Help = "http type only, optional: a reply containing on / true / 1 means it's on.")]
    public string StatusUrl { get; set; } = "";
}
