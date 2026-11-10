using Pucksley.Sdk;

namespace Pucksley.Plugins.Sonos;

/// <summary>Sonos settings. All optional: speakers are found automatically.</summary>
public sealed class SonosSettings
{
    [Setting("Speaker IP addresses", Order = 1, Placeholder = "192.168.1.40",
        Help = "Optional, one per line. Only needed when automatic discovery doesn't work (e.g. the hub runs in Docker without host networking). One speaker is enough.")]
    public List<string> SpeakerIps { get; set; } = [];

    [Setting("Default room", Order = 2, Placeholder = "Kitchen",
        Help = "Used when the puck you're talking to has no room set, or its room has no Sonos.")]
    public string DefaultRoom { get; set; } = "";

    [Setting("Volume dials on the puck", Order = 3, Placeholder = "Kitchen",
        Help = "Rooms to show as volume dials on the puck's Controls face, one per line. Empty = every room.")]
    public List<string> DialRooms { get; set; } = [];

    [Setting("Announcement volume", Order = 4, Help = "Volume (0-100) for spoken announcements on the speakers.")]
    public int AnnouncementVolume { get; set; } = 35;

    [Setting("Spotify service id (sid)", Order = 10, Advanced = true,
        Help = "Only if playing Spotify on Sonos fails. 0 = automatic (usually 9 or 12).")]
    public int SpotifySid { get; set; }

    [Setting("Spotify account number (sn)", Order = 11, Advanced = true,
        Help = "Only if playing Spotify tracks on Sonos fails. 0 = learn it from a Spotify item in your Sonos Favourites.")]
    public int SpotifySn { get; set; }
}
