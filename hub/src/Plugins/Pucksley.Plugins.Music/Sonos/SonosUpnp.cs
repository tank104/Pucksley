using System.Security;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Pucksley.Plugins.Sonos;

/// <summary>A Sonos UPnP service: the name used in SOAP actions and its control URL (port 1400).</summary>
public sealed record SonosService(string Name, string ControlUrl)
{
    public static SonosService AVTransport { get; } = new("AVTransport", "/MediaRenderer/AVTransport/Control");
    public static SonosService RenderingControl { get; } = new("RenderingControl", "/MediaRenderer/RenderingControl/Control");
    public static SonosService GroupRenderingControl { get; } = new("GroupRenderingControl", "/MediaRenderer/GroupRenderingControl/Control");
    public static SonosService ContentDirectory { get; } = new("ContentDirectory", "/MediaServer/ContentDirectory/Control");
    public static SonosService ZoneGroupTopology { get; } = new("ZoneGroupTopology", "/ZoneGroupTopology/Control");
    public static SonosService MusicServices { get; } = new("MusicServices", "/MusicServices/Control");

    /// <summary>e.g. <c>urn:schemas-upnp-org:service:AVTransport:1</c>.</summary>
    public string Urn => $"urn:schemas-upnp-org:service:{Name}:1";
}

/// <summary>A speaker answered with a SOAP fault (a UPnPError code).</summary>
public sealed class SonosUpnpException(string action, int errorCode, string? detail = null)
    : Exception($"Sonos {action} failed with UPnP error {errorCode}{(string.IsNullOrWhiteSpace(detail) ? "" : $" ({detail})")}.")
{
    public string Action { get; } = action;

    public int ErrorCode { get; } = errorCode;

    /// <summary>A sentence for the person.</summary>
    public string FriendlyMessage => SonosErrors.Describe(Action, ErrorCode);
}

/// <summary>UPnP error codes (AVTransport, ContentDirectory and Sonos's own) as sentences.</summary>
public static class SonosErrors
{
    public static string Describe(string action, int code) => code switch
    {
        701 => action switch
        {
            "Next" or "Previous" => "There's nothing to skip to.",
            "Play" => "There's nothing to play there yet.",
            "Pause" or "Stop" => "That can't be paused (the TV and line-in can't be paused, but they can be muted).",
            "Seek" => "Can't jump there in what's playing.",
            "Browse" => "Sonos couldn't find that.",
            _ => "Sonos can't do that right now.",
        },
        711 or 710 => action is "Next" or "Previous" ? "There's nothing to skip to." : "Can't jump there in what's playing.",
        400 => "The speaker didn't understand the request.",
        401 => "The speaker doesn't support that.",
        402 => "The speaker didn't accept those settings.",
        501 => "The speaker couldn't do that right now.",
        702 => "There's nothing in the queue to play.",
        703 or 704 => "Sonos can't play that.",
        705 => "Another app is controlling that speaker right now.",
        712 => "Shuffle and repeat only work when playing from the Sonos queue.",
        714 => "Sonos doesn't accept that kind of link.",
        715 => "The speaker is busy; try again in a moment.",
        716 or 804 => "Sonos couldn't find that.",
        718 => "That speaker isn't available.",
        800 when action is "AddURIToQueue" or "SetAVTransportURI" =>
            "Sonos couldn't play that from the music service (error 800). Check the service is set up in the Sonos app.",
        1023 => "Those rooms can't be grouped.",
        _ => $"Sonos refused that (error {code}).",
    };
}

/// <summary>
/// A small UPnP SOAP client for Sonos players: POST the envelope to <c>http://ip:1400{controlUrl}</c>,
/// return the response's fields, or throw <see cref="SonosUpnpException"/> for a fault and
/// <see cref="HttpRequestException"/> when the speaker can't be reached (including timeouts).
/// </summary>
public sealed class SonosSoapClient(HttpClient http)
{
    /// <summary>LAN speakers answer in milliseconds; a few seconds of silence means it's gone.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(4);

    public async Task<IReadOnlyDictionary<string, string>> CallAsync(
        string host, SonosService service, string action, IReadOnlyList<(string Name, string Value)> args, CancellationToken cancel)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        cts.CancelAfter(Timeout);
        using var request = new HttpRequestMessage(HttpMethod.Post, BaseUrl(host) + service.ControlUrl)
        {
            Content = new StringContent(BuildEnvelope(service, action, args), Encoding.UTF8, "text/xml"),
        };
        request.Headers.TryAddWithoutValidation("SOAPACTION", $"\"{service.Urn}#{action}\"");
        string text;
        int status;
        try
        {
            using var response = await http.SendAsync(request, cts.Token).ConfigureAwait(false);
            status = (int)response.StatusCode;
            text = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException e) when (!cancel.IsCancellationRequested)
        {
            throw new HttpRequestException($"{host} didn't answer within {Timeout.TotalSeconds:0} s", e);
        }
        return ParseResponse(action, text, status);
    }

    /// <summary><c>http://192.168.1.40:1400</c>; a host that already has a port keeps it.</summary>
    public static string BaseUrl(string host) => host.Contains(':') ? $"http://{host}" : $"http://{host}:1400";

    public static string BuildEnvelope(SonosService service, string action, IEnumerable<(string Name, string Value)> args)
    {
        var sb = new StringBuilder(320);
        sb.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?>")
          .Append("<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\"><s:Body>")
          .Append("<u:").Append(action).Append(" xmlns:u=\"").Append(service.Urn).Append("\">");
        foreach (var (name, value) in args)
            sb.Append('<').Append(name).Append('>').Append(SecurityElement.Escape(value ?? "")).Append("</").Append(name).Append('>');
        sb.Append("</u:").Append(action).Append("></s:Body></s:Envelope>");
        return sb.ToString();
    }

    /// <summary>The response's out-arguments by name, or throws for a SOAP fault.</summary>
    public static IReadOnlyDictionary<string, string> ParseResponse(string action, string xml, int httpStatus = 200)
    {
        XDocument doc;
        try
        {
            doc = XDocument.Parse(xml);
        }
        catch (XmlException)
        {
            // Not SOAP at all: report the HTTP status as the error.
            throw new SonosUpnpException(action, httpStatus is >= 200 and < 300 ? 0 : httpStatus, "unreadable answer");
        }
        var body = doc.Root?.Elements().FirstOrDefault(e => e.Name.LocalName == "Body");
        var first = body?.Elements().FirstOrDefault()
                    ?? throw new SonosUpnpException(action, httpStatus is >= 200 and < 300 ? 0 : httpStatus, "empty answer");
        if (first.Name.LocalName == "Fault")
        {
            var upnp = first.Descendants().FirstOrDefault(e => e.Name.LocalName == "UPnPError");
            var codeText = upnp?.Elements().FirstOrDefault(e => e.Name.LocalName == "errorCode")?.Value.Trim();
            var description = upnp?.Elements().FirstOrDefault(e => e.Name.LocalName == "errorDescription")?.Value.Trim()
                              ?? first.Elements().FirstOrDefault(e => e.Name.LocalName == "faultstring")?.Value.Trim();
            throw new SonosUpnpException(action, int.TryParse(codeText, out var code) ? code : httpStatus, description);
        }
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var e in first.Elements()) values[e.Name.LocalName] = e.Value;
        return values;
    }
}
