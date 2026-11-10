using System.Globalization;
using System.Net;
using System.Security;
using System.Text;
using System.Xml.Linq;
using Pucksley.Plugins.Sonos;
using Pucksley.Sdk;
using Pucksley.TestSupport;

namespace Pucksley.Plugins.Music.Tests.Support;

/// <summary>
/// A pretend Sonos household that answers the UPnP SOAP calls the plugin makes, keeps a little
/// state (volume, transport, queue, grouping) and records every call.
/// </summary>
public sealed class FakeSonos
{
    public sealed class Player
    {
        public required string Uuid { get; init; }
        public required string Room { get; init; }
        public required string Host { get; set; }
        public int Volume { get; set; } = 20;
        public bool Muted { get; set; }
        public string TransportState { get; set; } = "STOPPED";
        public string MediaUri { get; set; } = "";
        public string MediaMeta { get; set; } = "";
        public string TrackUri { get; set; } = "";
        public string TrackMeta { get; set; } = "";
        public int Track { get; set; }
        public string RelTime { get; set; } = "0:00:00";
        public string Duration { get; set; } = "0:00:00";
        public string PlayMode { get; set; } = "NORMAL";
        public string SleepTimer { get; set; } = "";
        public List<string> Queue { get; } = [];
        public string? DeviceDescription { get; set; }
        public bool Unreachable { get; set; }

        /// <summary>The coordinator this player follows; null = its own group.</summary>
        public string? Coordinator { get; set; }

        public void PlayQueue(int track, string relTime = "0:00:00")
        {
            MediaUri = $"x-rincon-queue:{Uuid}#0";
            Track = track;
            RelTime = relTime;
            TrackUri = Queue.Count >= track && track > 0 ? Queue[track - 1] : "";
            TransportState = "PLAYING";
        }
    }

    public sealed record SoapCall(string Host, string Action, IReadOnlyDictionary<string, string> Args);

    public List<Player> Players { get; } = [];
    readonly List<SoapCall> _calls = [];

    /// <summary>A snapshot of every call so far (background refreshes may still be adding).</summary>
    public IReadOnlyList<SoapCall> Calls
    {
        get { lock (_calls) return _calls.ToList(); }
    }
    public string FavoritesDidl { get; set; } = DidlDoc();
    public int SpotifyServiceId { get; set; } = 12;

    /// <summary>(host, action) → a UPnP error code to fail with.</summary>
    public Func<string, string, int?>? Fault { get; set; }

    public Player Add(string uuid, string room, string host)
    {
        var p = new Player { Uuid = uuid, Room = room, Host = host };
        Players.Add(p);
        return p;
    }

    public Player this[string room] => Players.Single(p => p.Room == room);

    /// <summary>A household like the real one: Kitchen (Amp, TV), Loft Study (One), Sonos Move.</summary>
    public static FakeSonos Household()
    {
        var s = new FakeSonos();
        var kitchen = s.Add("RINCON_000E5800000101400", "Kitchen", "10.0.0.1");
        kitchen.DeviceDescription = Fixtures.Read("DeviceDescription_amp_real.xml");
        kitchen.Volume = 18;
        var study = s.Add("RINCON_000E5800000201400", "Loft Study", "10.0.0.2");
        study.DeviceDescription = Fixtures.Read("DeviceDescription_one_real.xml");
        study.Volume = 65;
        var move = s.Add("RINCON_000E5800000301400", "Sonos Move", "10.0.0.3");
        move.Volume = 80;
        return s;
    }

    public IEnumerable<SoapCall> CallsTo(string action) => Calls.Where(c => c.Action == action);

    public HttpClient CreateClient() => new(new FakeHttpHandler(Respond)) { Timeout = TimeSpan.FromSeconds(30) };

    /// <summary>A Sonos plugin wired to this household (no SSDP, no background loop).</summary>
    public (SonosPlugin Plugin, FakePluginContext Context) CreatePlugin(SonosSettings? settings = null, FakePluginContext? context = null)
    {
        context ??= new FakePluginContext(http: CreateClient());
        var plugin = new SonosPlugin
        {
            DiscoverOverride = _ => Task.FromResult<IReadOnlyList<string>>(Players.Where(p => !p.Unreachable).Select(p => p.Host).ToList()),
            AnnouncementPadding = TimeSpan.Zero,
        };
        plugin.Initialize(context);
        plugin.ApplySettings(settings ?? new SonosSettings { SpeakerIps = [Players[0].Host] });
        return (plugin, context);
    }

    public static string DidlDoc(params string[] items) =>
        "<DIDL-Lite xmlns:dc=\"http://purl.org/dc/elements/1.1/\" xmlns:upnp=\"urn:schemas-upnp-org:metadata-1-0/upnp/\" " +
        "xmlns:r=\"urn:schemas-rinconnetworks-com:metadata-1-0/\" xmlns=\"urn:schemas-upnp-org:metadata-1-0/DIDL-Lite/\">" +
        string.Concat(items) + "</DIDL-Lite>";

    /// <summary>A favourite item; <paramref name="resMd"/> is raw DIDL (escaped here).</summary>
    public static string Favorite(string id, string title, string res, string resMd, string? description = null) =>
        $"<item id=\"{id}\" parentID=\"FV:2\" restricted=\"false\"><dc:title>{SecurityElement.Escape(title)}</dc:title>" +
        "<upnp:class>object.itemobject.item.sonos-favorite</upnp:class>" +
        $"<res protocolInfo=\"x-rincon-cpcontainer:*:*:*\">{SecurityElement.Escape(res)}</res>" +
        (description is null ? "" : $"<r:description>{SecurityElement.Escape(description)}</r:description>") +
        $"<r:resMD>{SecurityElement.Escape(resMd)}</r:resMD></item>";

    string ZoneGroupState()
    {
        var sb = new StringBuilder("<ZoneGroupState><ZoneGroups>");
        foreach (var c in Players.Where(p => p.Coordinator is null))
        {
            sb.Append($"<ZoneGroup Coordinator=\"{c.Uuid}\" ID=\"{c.Uuid}:1\">");
            foreach (var m in Players.Where(p => p == c || p.Coordinator == c.Uuid))
                sb.Append($"<ZoneGroupMember UUID=\"{m.Uuid}\" Location=\"http://{m.Host}:1400/xml/device_description.xml\" ZoneName=\"{m.Room}\" Invisible=\"0\"/>");
            sb.Append("</ZoneGroup>");
        }
        return sb.Append("</ZoneGroups><VanishedDevices></VanishedDevices></ZoneGroupState>").ToString();
    }

    HttpResponseMessage Respond(RecordedRequest request)
    {
        var host = request.Uri.Port == 1400 ? request.Uri.Host : $"{request.Uri.Host}:{request.Uri.Port}";
        var player = Players.FirstOrDefault(p => p.Host == host);
        if (player is null || player.Unreachable) throw new HttpRequestException($"No route to host {host}");

        if (request.Method == HttpMethod.Get)
            return player.DeviceDescription is { } xml ? FakeHttpHandler.Text(xml) : FakeHttpHandler.Text("", HttpStatusCode.NotFound);

        var soapAction = request.Headers["SOAPACTION"].Trim('"');
        var urn = soapAction[..soapAction.IndexOf('#')];
        var action = soapAction[(soapAction.IndexOf('#') + 1)..];
        var body = XDocument.Parse(request.Body!).Root!.Elements().First(e => e.Name.LocalName == "Body").Elements().First();
        var args = body.Elements().ToDictionary(e => e.Name.LocalName, e => e.Value);
        lock (_calls) _calls.Add(new SoapCall(host, action, args));

        if (Fault?.Invoke(host, action) is int code) return FaultResponse(code);
        string Arg(string name) => args.GetValueOrDefault(name) ?? "";
        HttpResponseMessage Ok(params (string Name, string Value)[] fields) => Response(urn, action, fields);

        switch (action)
        {
            case "GetZoneGroupState":
                return Ok(("ZoneGroupState", ZoneGroupState()));
            case "GetTransportInfo":
                // An announcement clip (an http URL) has always finished by the time anyone asks.
                var state = player.MediaUri.StartsWith("http", StringComparison.Ordinal) ? "STOPPED" : player.TransportState;
                return Ok(("CurrentTransportState", state), ("CurrentTransportStatus", "OK"), ("CurrentSpeed", "1"));
            case "GetPositionInfo":
                return Ok(("Track", player.Track.ToString(CultureInfo.InvariantCulture)), ("TrackDuration", player.Duration),
                    ("TrackMetaData", player.TrackMeta.Length == 0 ? "NOT_IMPLEMENTED" : player.TrackMeta), ("TrackURI", player.TrackUri),
                    ("RelTime", player.RelTime), ("AbsTime", "NOT_IMPLEMENTED"), ("RelCount", "2147483647"), ("AbsCount", "2147483647"));
            case "GetMediaInfo":
                return Ok(("NrTracks", player.Queue.Count.ToString(CultureInfo.InvariantCulture)), ("MediaDuration", "NOT_IMPLEMENTED"),
                    ("CurrentURI", player.MediaUri), ("CurrentURIMetaData", player.MediaMeta), ("NextURI", ""), ("NextURIMetaData", ""),
                    ("PlayMedium", "NETWORK"), ("RecordMedium", "NOT_IMPLEMENTED"), ("WriteStatus", "NOT_IMPLEMENTED"));
            case "GetTransportSettings":
                return Ok(("PlayMode", player.PlayMode), ("RecQualityMode", "NOT_IMPLEMENTED"));
            case "GetVolume":
                return Ok(("CurrentVolume", player.Volume.ToString(CultureInfo.InvariantCulture)));
            case "GetMute":
                return Ok(("CurrentMute", player.Muted ? "1" : "0"));
            case "SetVolume":
                player.Volume = int.Parse(Arg("DesiredVolume"), CultureInfo.InvariantCulture);
                return Ok();
            case "SetRelativeVolume":
                player.Volume = Math.Clamp(player.Volume + int.Parse(Arg("Adjustment"), CultureInfo.InvariantCulture), 0, 100);
                return Ok(("NewVolume", player.Volume.ToString(CultureInfo.InvariantCulture)));
            case "SetMute":
                player.Muted = Arg("DesiredMute") == "1";
                return Ok();
            case "Play":
                if (player.MediaUri.Length == 0) return FaultResponse(701);
                player.TransportState = "PLAYING";
                return Ok();
            case "Pause":
                if (player.MediaUri.StartsWith("x-sonos-htastream:", StringComparison.Ordinal)) return FaultResponse(701);
                player.TransportState = "PAUSED_PLAYBACK";
                return Ok();
            case "Next":
            case "Previous":
                if (!player.MediaUri.StartsWith("x-rincon-queue:", StringComparison.Ordinal)) return FaultResponse(701);
                int next = player.Track + (action == "Next" ? 1 : -1);
                if (next < 1 || next > player.Queue.Count) return FaultResponse(701);
                player.Track = next;
                player.TrackUri = player.Queue[next - 1];
                return Ok();
            case "RemoveAllTracksFromQueue":
                player.Queue.Clear();
                return Ok();
            case "AddURIToQueue":
            {
                int at = int.Parse(Arg("DesiredFirstTrackNumberEnqueued"), CultureInfo.InvariantCulture);
                if (at <= 0 || at > player.Queue.Count + 1) at = player.Queue.Count + 1;
                player.Queue.Insert(at - 1, Arg("EnqueuedURI"));
                return Ok(("FirstTrackNumberEnqueued", at.ToString(CultureInfo.InvariantCulture)), ("NumTracksAdded", "1"),
                    ("NewQueueLength", player.Queue.Count.ToString(CultureInfo.InvariantCulture)));
            }
            case "SetAVTransportURI":
            {
                var uri = Arg("CurrentURI");
                if (uri.StartsWith("x-rincon:", StringComparison.Ordinal))
                {
                    player.Coordinator = uri["x-rincon:".Length..];
                    return Ok();
                }
                player.Coordinator = null; // playing something of its own takes it out of its group
                player.MediaUri = uri;
                player.MediaMeta = Arg("CurrentURIMetaData");
                player.TrackUri = uri.StartsWith("x-rincon-queue:", StringComparison.Ordinal) ? player.Queue.FirstOrDefault() ?? "" : uri;
                player.Track = 1;
                player.TransportState = "STOPPED";
                return Ok();
            }
            case "Seek":
                if (Arg("Unit") == "TRACK_NR") player.Track = int.Parse(Arg("Target"), CultureInfo.InvariantCulture);
                else player.RelTime = Arg("Target");
                return Ok();
            case "BecomeCoordinatorOfStandaloneGroup":
                player.Coordinator = null;
                foreach (var follower in Players.Where(p => p.Coordinator == player.Uuid)) follower.Coordinator = null;
                return Ok();
            case "ConfigureSleepTimer":
                player.SleepTimer = Arg("NewSleepTimerDuration");
                return Ok();
            case "SetPlayMode":
                player.PlayMode = Arg("NewPlayMode");
                return Ok();
            case "SnapshotGroupVolume":
                return Ok();
            case "SetGroupVolume":
                foreach (var m in Players.Where(p => p == player || p.Coordinator == player.Uuid))
                    m.Volume = int.Parse(Arg("DesiredVolume"), CultureInfo.InvariantCulture);
                return Ok();
            case "Browse":
            {
                var didl = Arg("ObjectID") switch
                {
                    "FV:2" => FavoritesDidl,
                    "Q:0" => DidlDoc(player.Queue.Select((u, i) =>
                        $"<item id=\"Q:0/{i + 1}\" parentID=\"Q:0\" restricted=\"true\"><dc:title>Track {i + 1}</dc:title><res>{SecurityElement.Escape(u)}</res></item>").ToArray()),
                    _ => null,
                };
                if (didl is null) return FaultResponse(701);
                int count = XDocument.Parse(didl).Root!.Elements().Count();
                return Ok(("Result", didl), ("NumberReturned", count.ToString(CultureInfo.InvariantCulture)),
                    ("TotalMatches", count.ToString(CultureInfo.InvariantCulture)), ("UpdateID", "1"));
            }
            case "ListAvailableServices":
                return Ok(("AvailableServiceDescriptorList",
                    $"<?xml version=\"1.0\"?><Services><Service Capabilities=\"0\" Id=\"{SpotifyServiceId}\" Name=\"Spotify\" Version=\"1.1\" Uri=\"https://spotify-v5.ws.sonos.com/smapi\"/>" +
                    "<Service Id=\"303\" Name=\"Sonos Radio\" Version=\"1.1\" Uri=\"https://example\"/></Services>"),
                    ("AvailableServiceTypeList", "3079,77575"), ("AvailableServiceListVersion", "RINCON_1:1"));
            default:
                return FaultResponse(401);
        }
    }

    static HttpResponseMessage Response(string urn, string action, (string Name, string Value)[] fields)
    {
        var inner = string.Concat(fields.Select(f => $"<{f.Name}>{SecurityElement.Escape(f.Value)}</{f.Name}>"));
        return FakeHttpHandler.Text(
            "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\"><s:Body>" +
            $"<u:{action}Response xmlns:u=\"{urn}\">{inner}</u:{action}Response></s:Body></s:Envelope>");
    }

    static HttpResponseMessage FaultResponse(int code) => FakeHttpHandler.Text(
        "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\"><s:Body>" +
        "<s:Fault><faultcode>s:Client</faultcode><faultstring>UPnPError</faultstring><detail>" +
        $"<UPnPError xmlns=\"urn:schemas-upnp-org:control-1-0\"><errorCode>{code}</errorCode></UPnPError></detail></s:Fault></s:Body></s:Envelope>",
        HttpStatusCode.InternalServerError);

    /// <summary>Calls a tool from a puck in <paramref name="room"/>.</summary>
    public static Task<ToolResult> Call(IPlugin plugin, string tool, string json = "{}", string? room = "Kitchen") =>
        FakePluginContext.CallToolAsync(plugin, tool, json, new ToolCaller("puck-1", "Test puck", room));
}
