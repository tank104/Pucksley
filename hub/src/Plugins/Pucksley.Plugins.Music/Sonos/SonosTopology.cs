using System.Xml.Linq;

namespace Pucksley.Plugins.Sonos;

/// <summary>A visible Sonos player: a room (or the visible half of a stereo pair / home theatre set).</summary>
/// <param name="Uuid">e.g. <c>RINCON_000E5800000101400</c> (no <c>uuid:</c> prefix).</param>
/// <param name="Host">IP address (port 1400 implied) or <c>host:port</c>.</param>
public sealed record SonosPlayer(string Uuid, string RoomName, string Host);

/// <summary>Rooms playing together. Transport commands go to the <see cref="Coordinator"/>.</summary>
public sealed class SonosGroup
{
    public SonosGroup(string id, SonosPlayer coordinator, IReadOnlyList<SonosPlayer> members)
    {
        Id = id;
        Coordinator = coordinator;
        Members = members;
        Name = SonosTopology.GroupName(members.Select(m => m.RoomName));
    }

    public string Id { get; }

    public SonosPlayer Coordinator { get; }

    /// <summary>Visible members, coordinator first.</summary>
    public IReadOnlyList<SonosPlayer> Members { get; }

    /// <summary>"Kitchen", or "Kitchen + Dining" for a group.</summary>
    public string Name { get; }

    public bool Contains(string uuid) => Members.Any(m => m.Uuid == uuid);
}

/// <summary>An immutable snapshot of the household's zone groups (replaced, never mutated).</summary>
public sealed class SonosTopology
{
    public static SonosTopology Empty { get; } = new([]);

    public SonosTopology(IReadOnlyList<SonosGroup> groups)
    {
        Groups = groups;
        Players = groups.SelectMany(g => g.Members).OrderBy(p => p.RoomName, StringComparer.OrdinalIgnoreCase).ToList();
        Signature = string.Join("|", groups.Select(g => string.Join(",", g.Members.Select(m => $"{m.Uuid}={m.RoomName}@{m.Host}"))));
    }

    public IReadOnlyList<SonosGroup> Groups { get; }

    /// <summary>Every visible player, sorted by room name.</summary>
    public IReadOnlyList<SonosPlayer> Players { get; }

    /// <summary>Changes whenever rooms, groups or addresses change.</summary>
    public string Signature { get; }

    /// <summary>"Kitchen, Lounge, Office".</summary>
    public string RoomList => string.Join(", ", Players.Select(p => p.RoomName));

    public SonosGroup? GroupOf(string uuid) => Groups.FirstOrDefault(g => g.Contains(uuid));

    public SonosPlayer? Player(string uuid) => Players.FirstOrDefault(p => p.Uuid == uuid);

    /// <summary>"Kitchen", "Kitchen + Dining", "Kitchen + Dining + Lounge", "Kitchen + 3 more".</summary>
    public static string GroupName(IEnumerable<string> rooms)
    {
        var list = rooms.ToList();
        return list.Count switch
        {
            0 => "",
            <= 3 => string.Join(" + ", list),
            _ => $"{list[0]} + {list.Count - 1} more",
        };
    }

    /// <summary>
    /// Parses the (already unescaped) <c>ZoneGroupState</c> XML from
    /// <c>ZoneGroupTopology#GetZoneGroupState</c>. Both the newer
    /// <c>&lt;ZoneGroupState&gt;&lt;ZoneGroups&gt;</c> and older <c>&lt;ZoneGroups&gt;</c> shapes work.
    /// Invisible members (bonded surrounds, subs, the second speaker of a stereo pair) and bridges are
    /// skipped; satellites nested inside a member are ignored.
    /// </summary>
    public static SonosTopology ParseZoneGroupState(string xml)
    {
        if (string.IsNullOrWhiteSpace(xml)) return Empty;
        var doc = XDocument.Parse(xml);
        var groups = new List<SonosGroup>();
        foreach (var zg in doc.Descendants().Where(e => e.Name.LocalName == "ZoneGroup"))
        {
            var coordinatorUuid = (string?)zg.Attribute("Coordinator");
            var members = new List<SonosPlayer>();
            foreach (var m in zg.Elements().Where(e => e.Name.LocalName == "ZoneGroupMember"))
            {
                if ((string?)m.Attribute("Invisible") == "1" || (string?)m.Attribute("IsZoneBridge") == "1") continue;
                var uuid = (string?)m.Attribute("UUID");
                var name = (string?)m.Attribute("ZoneName");
                var host = HostFromLocation((string?)m.Attribute("Location"));
                if (string.IsNullOrEmpty(uuid) || string.IsNullOrWhiteSpace(name) || host is null) continue;
                members.Add(new SonosPlayer(uuid, name.Trim(), host));
            }
            if (members.Count == 0) continue;
            var coordinator = members.FirstOrDefault(m => m.Uuid == coordinatorUuid) ?? members[0];
            var ordered = new List<SonosPlayer> { coordinator };
            ordered.AddRange(members.Where(m => m != coordinator).OrderBy(m => m.RoomName, StringComparer.OrdinalIgnoreCase));
            groups.Add(new SonosGroup((string?)zg.Attribute("ID") ?? coordinator.Uuid, coordinator, ordered));
        }
        groups.Sort((a, b) => string.Compare(a.Coordinator.RoomName, b.Coordinator.RoomName, StringComparison.OrdinalIgnoreCase));
        return new SonosTopology(groups);
    }

    /// <summary><c>http://192.168.1.102:1400/xml/device_description.xml</c> → <c>192.168.1.102</c>.</summary>
    public static string? HostFromLocation(string? location)
    {
        if (string.IsNullOrWhiteSpace(location) || !Uri.TryCreate(location.Trim(), UriKind.Absolute, out var uri)) return null;
        return uri.Port is 1400 or 80 or -1 ? uri.Host : $"{uri.Host}:{uri.Port}";
    }
}

/// <summary>What a player's <c>device_description.xml</c> says about it.</summary>
public sealed record SonosDeviceInfo(string Uuid, string RoomName, string Model, bool HasTv, bool HasLineIn)
{
    /// <summary>Parses <c>http://ip:1400/xml/device_description.xml</c>; null if it isn't one.</summary>
    public static SonosDeviceInfo? Parse(string xml)
    {
        var doc = XDocument.Parse(xml);
        var device = doc.Root?.Elements().FirstOrDefault(e => e.Name.LocalName == "device");
        if (device is null) return null;
        string? Get(string name) => device.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value.Trim();
        var udn = Get("UDN");
        if (string.IsNullOrEmpty(udn)) return null;
        var uuid = udn.StartsWith("uuid:", StringComparison.OrdinalIgnoreCase) ? udn[5..] : udn;
        var services = doc.Descendants().Where(e => e.Name.LocalName == "serviceType").Select(e => e.Value).ToList();
        return new SonosDeviceInfo(
            uuid,
            Get("roomName") ?? "",
            Get("modelName") ?? Get("displayName") ?? "",
            // Home theatre products (Arc, Beam, Ray, Amp, Playbar…) have the HTControl service.
            HasTv: services.Any(s => s.Contains(":HTControl:", StringComparison.Ordinal)),
            HasLineIn: services.Any(s => s.Contains(":AudioIn:", StringComparison.Ordinal)));
    }
}
