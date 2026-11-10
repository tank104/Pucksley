using Makaretu.Dns;
using Microsoft.Extensions.Logging;

namespace Pucksley.Core.Hosting;

/// <summary>
/// Announces the hub on the LAN as <c>_pucksley._tcp</c> so pucks find it without being told an
/// address. (In Docker this needs host networking.)
/// </summary>
public sealed class MdnsAdvertiser(ILogger<MdnsAdvertiser> log) : IDisposable
{
    MulticastService? _mdns;
    ServiceDiscovery? _sd;

    public bool Running => _mdns is not null;

    public void Start(string hubName, string hubId, int port)
    {
        if (_mdns is not null) return;
        try
        {
            _mdns = new MulticastService();
            _sd = new ServiceDiscovery(_mdns);
            var instance = new string(("Pucksley " + hubName).Where(c => char.IsLetterOrDigit(c) || c is ' ' or '-').ToArray());
            var profile = new ServiceProfile(instance, "_pucksley._tcp", (ushort)port);
            profile.AddProperty("path", "/puck");
            profile.AddProperty("id", hubId);
            profile.AddProperty("v", "1");
            _sd.Advertise(profile);
            _mdns.Start();
            log.LogInformation("Announcing the hub on the LAN (mDNS _pucksley._tcp, port {Port})", port);
        }
        catch (Exception e)
        {
            log.LogWarning("mDNS announcement failed ({Message}); pucks will need the hub address typed in", e.Message);
            Dispose();
        }
    }

    /// <summary>Stops announcing (this hub stopped taking pucks).</summary>
    public void Stop()
    {
        if (_mdns is null) return;
        Dispose();
        log.LogInformation("Stopped announcing the hub on the LAN");
    }

    public void Dispose()
    {
        try
        {
            _sd?.Dispose();
            _mdns?.Stop();
            _mdns?.Dispose();
        }
        catch (Exception) { }
        _sd = null;
        _mdns = null;
    }
}
