using System.Xml.Linq;
using Pucksley.Plugins.Music.Tests.Support;
using Pucksley.Plugins.Sonos;

namespace Pucksley.Plugins.Music.Tests;

public sealed class SonosSoapTests
{
    [Fact]
    public void Real_fault_becomes_an_exception_with_the_upnp_code()
    {
        var e = Assert.Throws<SonosUpnpException>(() => SonosSoapClient.ParseResponse("Next", Fixtures.Read("Fault_701_real.xml"), 500));
        Assert.Equal(701, e.ErrorCode);
        Assert.Equal("Next", e.Action);
        Assert.Equal("There's nothing to skip to.", e.FriendlyMessage);
    }

    [Theory]
    [InlineData("Next", 701, "There's nothing to skip to.")]
    [InlineData("Previous", 711, "There's nothing to skip to.")]
    [InlineData("Play", 701, "There's nothing to play there yet.")]
    [InlineData("Browse", 701, "Sonos couldn't find that.")]
    [InlineData("SetPlayMode", 712, "Shuffle and repeat only work when playing from the Sonos queue.")]
    [InlineData("AddURIToQueue", 800, "Sonos couldn't play that from the music service (error 800). Check the service is set up in the Sonos app.")]
    [InlineData("Play", 999, "Sonos refused that (error 999).")]
    public void Error_codes_are_described_for_people(string action, int code, string expected) =>
        Assert.Equal(expected, SonosErrors.Describe(action, code));

    [Fact]
    public void Fault_with_a_description_keeps_it_in_the_exception_message()
    {
        const string xml = "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\"><s:Body><s:Fault><faultcode>s:Client</faultcode>" +
                           "<faultstring>UPnPError</faultstring><detail><UPnPError xmlns=\"urn:schemas-upnp-org:control-1-0\"><errorCode>402</errorCode>" +
                           "<errorDescription>Invalid Args</errorDescription></UPnPError></detail></s:Fault></s:Body></s:Envelope>";
        var e = Assert.Throws<SonosUpnpException>(() => SonosSoapClient.ParseResponse("SetVolume", xml, 500));
        Assert.Equal(402, e.ErrorCode);
        Assert.Contains("Invalid Args", e.Message);
    }

    [Fact]
    public void Non_soap_errors_report_the_http_status()
    {
        var e = Assert.Throws<SonosUpnpException>(() => SonosSoapClient.ParseResponse("Play", "<html>busy</html", 503));
        Assert.Equal(503, e.ErrorCode);
    }

    [Fact]
    public void Response_fields_are_returned_by_name()
    {
        var r = SonosSoapClient.ParseResponse("GetVolume", Fixtures.Read("Volume_real.xml"));
        Assert.Equal("18", r["CurrentVolume"]);
    }

    [Fact]
    public void Envelope_escapes_uris_and_metadata()
    {
        var xml = SonosSoapClient.BuildEnvelope(SonosService.AVTransport, "SetAVTransportURI",
            [("InstanceID", "0"), ("CurrentURI", "x-sonos-spotify:a?sid=9&sn=1"), ("CurrentURIMetaData", "<DIDL-Lite a=\"b\"/>")]);

        Assert.Contains("<u:SetAVTransportURI xmlns:u=\"urn:schemas-upnp-org:service:AVTransport:1\">", xml);
        Assert.Contains("<CurrentURI>x-sonos-spotify:a?sid=9&amp;sn=1</CurrentURI>", xml);
        var parsed = XDocument.Parse(xml).Descendants().Single(e => e.Name.LocalName == "CurrentURIMetaData").Value;
        Assert.Equal("<DIDL-Lite a=\"b\"/>", parsed);
    }

    [Fact]
    public async Task Calls_post_to_the_control_url_with_the_soap_action_header()
    {
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Text(Fixtures.Read("Volume_real.xml")));
        var client = new SonosSoapClient(new HttpClient(handler));

        var r = await client.CallAsync("192.168.1.101", SonosService.RenderingControl, "GetVolume", [("InstanceID", "0"), ("Channel", "Master")], default);

        Assert.Equal("18", r["CurrentVolume"]);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("http://192.168.1.101:1400/MediaRenderer/RenderingControl/Control", request.Uri.ToString());
        Assert.Equal("\"urn:schemas-upnp-org:service:RenderingControl:1#GetVolume\"", request.Headers["SOAPACTION"]);
        Assert.StartsWith("text/xml", request.Headers["Content-Type"]);
        Assert.Contains("<InstanceID>0</InstanceID><Channel>Master</Channel>", request.Body);
    }

    [Fact]
    public async Task A_speaker_that_never_answers_is_reported_as_unreachable()
    {
        var client = new SonosSoapClient(new HttpClient(new SilentHandler())) { Timeout = TimeSpan.FromMilliseconds(100) };
        await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.CallAsync("192.168.1.99", SonosService.AVTransport, "Play", [("InstanceID", "0"), ("Speed", "1")], default));
    }

    [Theory]
    [InlineData("192.168.1.40", "http://192.168.1.40:1400")]
    [InlineData("speaker.local:1401", "http://speaker.local:1401")]
    public void Base_urls(string host, string expected) => Assert.Equal(expected, SonosSoapClient.BaseUrl(host));

    [Theory]
    [InlineData("192.168.1.40", "192.168.1.40")]
    [InlineData(" 192.168.1.40/ ", "192.168.1.40")]
    [InlineData("http://192.168.1.40:1400/xml/device_description.xml", "192.168.1.40")]
    [InlineData("http://192.168.1.40", "192.168.1.40")]
    [InlineData("http://10.0.0.5:1500/", "10.0.0.5:1500")]
    [InlineData("", null)]
    public void Configured_addresses_are_normalised(string raw, string? expected) => Assert.Equal(expected, SonosPlugin.NormalizeHost(raw));

    sealed class SilentHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException();
        }
    }
}
