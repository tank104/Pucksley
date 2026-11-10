using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Pucksley.Plugins.Sonos;

/// <summary>One <c>item</c> or <c>container</c> of a DIDL-Lite document (Sonos track metadata, favourites, queue).</summary>
public sealed record DidlItem
{
    public string Id { get; init; } = "";
    public string ParentId { get; init; } = "";
    public bool IsContainerElement { get; init; }
    public string? Title { get; init; }
    public string? Creator { get; init; }
    public string? Album { get; init; }
    public string? Class { get; init; }

    /// <summary>The <c>res</c> URI (what to play).</summary>
    public string? Res { get; init; }

    /// <summary>Favourites: <c>r:resMD</c>, the DIDL metadata to send along with <see cref="Res"/>.</summary>
    public string? ResMetaData { get; init; }

    /// <summary>Favourites: <c>r:type</c> ("instantPlay", "shortcut"…).</summary>
    public string? Type { get; init; }

    /// <summary>Favourites: <c>r:description</c>, e.g. "From Sonos Radio" or "Spotify Playlist".</summary>
    public string? Description { get; init; }

    /// <summary>Radio: <c>r:streamContent</c>, usually "Artist - Title".</summary>
    public string? StreamContent { get; init; }

    /// <summary>The <c>desc</c> text, e.g. <c>SA_RINCON2311_X_#Svc2311-0-Token</c>.</summary>
    public string? Desc { get; init; }

    public bool IsContainer => IsContainerElement || (Class?.StartsWith("object.container", StringComparison.Ordinal) ?? false);

    /// <summary>The music service type in <see cref="Desc"/> (sid × 256 + 7), e.g. 2311 for Spotify sid 9.</summary>
    public int? ServiceType => Didl.ServiceTypeFromDesc(Desc);
}

/// <summary>DIDL-Lite parsing. Never throws: Sonos sends "NOT_IMPLEMENTED" or nothing for metadata it doesn't have.</summary>
public static partial class Didl
{
    public static readonly XNamespace Ns = "urn:schemas-upnp-org:metadata-1-0/DIDL-Lite/";
    public static readonly XNamespace Dc = "http://purl.org/dc/elements/1.1/";
    public static readonly XNamespace Upnp = "urn:schemas-upnp-org:metadata-1-0/upnp/";
    public static readonly XNamespace R = "urn:schemas-rinconnetworks-com:metadata-1-0/";

    public static IReadOnlyList<DidlItem> Parse(string? didl)
    {
        if (string.IsNullOrWhiteSpace(didl) || !didl.TrimStart().StartsWith('<')) return [];
        XDocument doc;
        try
        {
            doc = XDocument.Parse(didl);
        }
        catch (XmlException)
        {
            return [];
        }
        if (doc.Root is null) return [];
        return doc.Root.Elements()
            .Where(e => e.Name.LocalName is "item" or "container")
            .Select(ParseItem)
            .ToList();
    }

    static DidlItem ParseItem(XElement e) => new()
    {
        Id = (string?)e.Attribute("id") ?? "",
        ParentId = (string?)e.Attribute("parentID") ?? "",
        IsContainerElement = e.Name.LocalName == "container",
        Title = Text(e, Dc + "title"),
        Creator = Text(e, Dc + "creator") ?? Text(e, Upnp + "artist"),
        Album = Text(e, Upnp + "album"),
        Class = Text(e, Upnp + "class"),
        Res = Text(e, Ns + "res"),
        ResMetaData = Text(e, R + "resMD"),
        Type = Text(e, R + "type"),
        Description = Text(e, R + "description"),
        StreamContent = Text(e, R + "streamContent"),
        Desc = Text(e, Ns + "desc"),
    };

    static string? Text(XElement parent, XName name)
    {
        var value = parent.Element(name)?.Value;
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    /// <summary><c>SA_RINCON3079_X_#Svc3079-0-Token</c> → 3079.</summary>
    public static int? ServiceTypeFromDesc(string? desc)
    {
        if (string.IsNullOrEmpty(desc)) return null;
        var m = ServiceTypeRegex().Match(desc);
        return m.Success && int.TryParse(m.Groups[1].Value, out var n) ? n : null;
    }

    [GeneratedRegex(@"SA_RINCON(\d+)_")]
    private static partial Regex ServiceTypeRegex();
}

/// <summary>A Sonos Favourite (ContentDirectory <c>FV:2</c>) and how to play it.</summary>
/// <param name="Uri">What to play; null when Sonos gave nothing playable.</param>
/// <param name="MetaData">The favourite's <c>r:resMD</c>, sent along with the URI.</param>
/// <param name="Service">"Sonos Radio", "Spotify Playlist"… when Sonos says.</param>
/// <param name="IsContainer">Playlists and albums: they go through the queue.</param>
/// <param name="IsShortcut">Newer favourites that carry no URI (we build one from their metadata).</param>
public sealed record SonosFavorite(string Title, string? Uri, string MetaData, string? Service, bool IsContainer, bool IsShortcut)
{
    public static SonosFavorite? From(DidlItem item)
    {
        if (string.IsNullOrWhiteSpace(item.Title)) return null;
        var inner = Didl.Parse(item.ResMetaData).FirstOrDefault();
        var uri = string.IsNullOrWhiteSpace(item.Res) ? null : item.Res.Trim();
        bool shortcut = uri is null;
        // Shortcut favourites (the new Sonos app) have an empty res: a music-service container can
        // still be queued by its item id, the same way Sonos's own share links work.
        if (uri is null && inner is { IsContainer: true } && inner.Id.Length > 0)
            uri = "x-rincon-cpcontainer:" + inner.Id;
        bool container = uri is not null && !SonosUris.IsRadio(uri) &&
                         (SonosUris.IsContainer(uri) || (inner?.IsContainer ?? false));
        var service = item.Description;
        if (service is not null && service.StartsWith("From ", StringComparison.OrdinalIgnoreCase)) service = service[5..];
        return new SonosFavorite(item.Title.Trim(), uri, item.ResMetaData ?? "", service, container, shortcut);
    }
}
