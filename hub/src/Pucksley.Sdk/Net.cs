using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace Pucksley.Sdk;

/// <summary>One SSDP (UPnP) answer.</summary>
public sealed record SsdpResponse(IPEndPoint From, string Location, string SearchTarget, string Usn, IReadOnlyDictionary<string, string> Headers);

/// <summary>LAN discovery and wake-up helpers shared by device plugins.</summary>
public static class LanTools
{
    /// <summary>
    /// SSDP M-SEARCH on every IPv4 interface, e.g. <c>urn:schemas-upnp-org:device:ZonePlayer:1</c>
    /// (Sonos) or <c>roku:ecp</c>. Returns distinct answers (by USN) heard within <paramref name="timeout"/>.
    /// </summary>
    public static async Task<IReadOnlyList<SsdpResponse>> SsdpSearchAsync(string searchTarget, TimeSpan timeout, CancellationToken cancel = default)
    {
        var results = new Dictionary<string, SsdpResponse>(StringComparer.OrdinalIgnoreCase);
        var request = Encoding.ASCII.GetBytes(
            "M-SEARCH * HTTP/1.1\r\nHOST: 239.255.255.250:1900\r\nMAN: \"ssdp:discover\"\r\n" +
            $"MX: {Math.Max(1, (int)timeout.TotalSeconds)}\r\nST: {searchTarget}\r\n\r\n");
        var target = new IPEndPoint(IPAddress.Parse("239.255.255.250"), 1900);
        var sockets = new List<UdpClient>();
        try
        {
            foreach (var addr in LocalIPv4Addresses())
            {
                try
                {
                    var udp = new UdpClient(new IPEndPoint(addr, 0));
                    udp.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 2);
                    sockets.Add(udp);
                    await udp.SendAsync(request, target, cancel).ConfigureAwait(false);
                    await Task.Delay(30, cancel).ConfigureAwait(false);
                    await udp.SendAsync(request, target, cancel).ConfigureAwait(false);
                }
                catch (SocketException) { }
            }
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            cts.CancelAfter(timeout);
            var tasks = sockets.Select(s => ReceiveAll(s, results, cts.Token)).ToArray();
            try { await Task.WhenAll(tasks).ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
        finally
        {
            foreach (var s in sockets) s.Dispose();
        }
        cancel.ThrowIfCancellationRequested();
        lock (results) return results.Values.ToList();
    }

    static async Task ReceiveAll(UdpClient udp, Dictionary<string, SsdpResponse> into, CancellationToken cancel)
    {
        while (!cancel.IsCancellationRequested)
        {
            UdpReceiveResult r;
            try { r = await udp.ReceiveAsync(cancel).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            catch (SocketException) { return; }
            catch (ObjectDisposedException) { return; }
            var text = Encoding.UTF8.GetString(r.Buffer);
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in text.Split("\r\n").Skip(1))
            {
                int colon = line.IndexOf(':');
                if (colon > 0) headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
            }
            if (!headers.TryGetValue("LOCATION", out var location)) continue;
            headers.TryGetValue("ST", out var st);
            headers.TryGetValue("USN", out var usn);
            lock (into) into[usn ?? location] = new SsdpResponse(r.RemoteEndPoint, location, st ?? "", usn ?? "", headers);
        }
    }

    /// <summary>The machine's IPv4 addresses on interfaces that are up (not loopback).</summary>
    public static IEnumerable<IPAddress> LocalIPv4Addresses() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Select(u => u.Address)
            .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a))
            .Distinct();

    /// <summary>Wake-on-LAN magic packet to <paramref name="mac"/> ("AA:BB:CC:DD:EE:FF" or "AABBCCDDEEFF").</summary>
    public static async Task WakeOnLanAsync(string mac, CancellationToken cancel = default)
    {
        var hex = new string(mac.Where(Uri.IsHexDigit).ToArray());
        if (hex.Length != 12) throw new PluginException($"'{mac}' isn't a MAC address.");
        var macBytes = Convert.FromHexString(hex);
        var packet = new byte[6 + 16 * 6];
        for (int i = 0; i < 6; i++) packet[i] = 0xFF;
        for (int i = 0; i < 16; i++) macBytes.CopyTo(packet, 6 + i * 6);
        // To 255.255.255.255 and to each interface's own broadcast address, from that interface:
        // on a machine with several adapters (Hyper-V, VPNs) the global broadcast can leave by the
        // wrong one.
        var targets = new List<(IPAddress Local, IPAddress Broadcast)> { (IPAddress.Any, IPAddress.Broadcast) };
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up))
            foreach (var u in nic.GetIPProperties().UnicastAddresses)
            {
                if (u.Address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(u.Address) || u.IPv4Mask is null) continue;
                var ip = u.Address.GetAddressBytes();
                var mask = u.IPv4Mask.GetAddressBytes();
                var bc = new byte[4];
                for (int i = 0; i < 4; i++) bc[i] = (byte)(ip[i] | ~mask[i]);
                targets.Add((u.Address, new IPAddress(bc)));
            }
        for (int round = 0; round < 3; round++)
        {
            foreach (var (local, broadcast) in targets)
            {
                try
                {
                    using var udp = new UdpClient(new IPEndPoint(local, 0)) { EnableBroadcast = true };
                    await udp.SendAsync(packet, new IPEndPoint(broadcast, 9), cancel).ConfigureAwait(false);
                    await udp.SendAsync(packet, new IPEndPoint(broadcast, 7), cancel).ConfigureAwait(false);
                }
                catch (SocketException) { }
            }
            await Task.Delay(100, cancel).ConfigureAwait(false);
        }
    }
}
