using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using Pucksley.Plugins.Linux.Audio;
using Pucksley.Sdk;
using Pucksley.TestSupport;
using Tmds.DBus.Protocol;

namespace Pucksley.Plugins.Linux.Tests;

/// <summary>What <c>pactl -f json</c> prints, in the shapes PipeWire gives (recorded, with the names changed).</summary>
internal static class PactlSamples
{
    public const string Sinks = """
        [
          {"index":63,"state":"SUSPENDED","name":"alsa_output.usb-Acme_Desk_Speakers-00.analog-stereo","description":"USB Audio Chip [Some Headset in charging mode] Analog Stereo",
           "mute":false,"volume":{"front-left":{"value":19545,"value_percent":"30%","db":"-31.53 dB"},"front-right":{"value":19545,"value_percent":"30%","db":"-31.53 dB"}},
           "active_port":"analog-output","ports":[{"name":"analog-output","description":"Analog Output","type":"Analog","priority":9900,"availability":"availability unknown"}],
           "properties":{"device.form_factor":"headset","device.bus":"usb","node.nick":"Desk Speakers 2+","alsa.card_name":"Desk Speakers 2+","object.serial":63}},
          {"index":65,"state":"RUNNING","name":"alsa_output.pci-0000_00_1f.3.analog-stereo","description":"Built-in Audio Analog Stereo",
           "mute":true,"volume":{"front-left":{"value":26214,"value_percent":"40%"},"front-right":{"value":32768,"value_percent":"50%"}},
           "active_port":"analog-output-speaker","ports":[
             {"name":"analog-output-speaker","description":"Speakers","type":"Speaker","availability":"availability unknown"},
             {"name":"analog-output-headphones","description":"Headphones","type":"Headphones","availability":"not available"}],
           "properties":{"device.form_factor":"internal","device.bus":"pci","node.nick":"ALC3204 Analog"}},
          {"index":70,"state":"SUSPENDED","name":"alsa_output.pci-0000_01_00.1.hdmi-stereo","description":"GPU High Definition Audio Controller Digital Stereo (HDMI)",
           "mute":false,"volume":{"front-left":{"value":65536,"value_percent":"100%"}},
           "active_port":"hdmi-output-0","ports":[{"name":"hdmi-output-0","description":"HDMI / DisplayPort","type":"HDMI","availability":"not available"}],
           "properties":{"device.bus":"pci"}},
          {"index":81,"state":"SUSPENDED","name":"bluez_output.AA_BB_CC_DD_EE_FF.1","description":"WH-1000XM4",
           "mute":false,"volume":{"front-left":{"value":98304,"value_percent":"150%"}},
           "active_port":"headphone-output","ports":[{"name":"headphone-output","description":"Headphones","type":"Headphones","availability":"available"}],
           "properties":{"device.form_factor":"headphone","device.bus":"bluetooth","api.bluez5.profile":"a2dp-sink"}}
        ]
        """;

    public const string SinkInputs = """
        [
          {"index":101,"sink":63,"client":"55","mute":false,"corked":false,"volume":{"front-left":{"value_percent":"100%"},"front-right":{"value_percent":"100%"}},
           "properties":{"application.name":"spotify","application.process.binary":"spotify","application.process.id":"4242","media.name":"Teardrop"}},
          {"index":102,"sink":63,"client":"56","mute":false,"corked":true,"volume":{"front-left":{"value_percent":"80%"}},
           "properties":{"application.name":"Chromium","application.process.binary":"chromium","media.name":"Playback"}},
          {"index":103,"sink":63,"client":"56","mute":true,"corked":false,"volume":{"front-left":{"value_percent":"60%"}},
           "properties":{"application.name":"Chromium","application.process.binary":"chromium","media.name":"Playback"}},
          {"index":104,"sink":65,"client":"60","mute":false,"corked":false,"volume":{"front-left":{"value_percent":"100%"}},
           "properties":{"node.name":"omarchy_speaker_tuning_output","media.class":"Stream/Output/Audio"}},
          {"index":105,"sink":65,"client":"61","mute":false,"corked":false,"volume":{"front-left":{"value_percent":"100%"}},
           "properties":{"application.name":"EasyEffects","application.process.binary":"easyeffects"}}
        ]
        """;
}

public class PulseJsonTests
{
    static readonly List<PulseSink> Sinks = PulseJson.Sinks(PactlSamples.Sinks);

    [Fact]
    public void Sinks_are_read_with_their_volume_mute_and_ports()
    {
        Assert.Equal(4, Sinks.Count);
        var usb = Sinks[0];
        Assert.Equal(63, usb.Index);
        Assert.Equal("alsa_output.usb-Acme_Desk_Speakers-00.analog-stereo", usb.Name);
        Assert.Equal(30, usb.Volume);
        Assert.False(usb.Muted);
        Assert.Equal("Analog", usb.Port?.Type);
        Assert.Equal("usb", usb.Property("device.bus"));
        Assert.Equal("63", usb.Property("object.serial")); // a number among the properties is kept as its text
        Assert.Null(usb.Property("no.such.thing"));
    }

    [Fact]
    public void The_loudest_channel_is_the_volume_and_boost_reads_as_full()
    {
        Assert.Equal(50, Sinks[1].Volume);  // left 40 %, right 50 %
        Assert.True(Sinks[1].Muted);
        Assert.Equal(100, Sinks[3].Volume); // 150 %
    }

    [Fact]
    public void A_socket_with_nothing_plugged_in_is_not_usable()
    {
        Assert.True(Sinks[1].Usable);  // its speakers are there even if the headphone socket is empty
        Assert.False(Sinks[2].Usable); // HDMI with no monitor attached
        Assert.True(Sinks[3].Usable);
    }

    [Fact]
    public void A_volume_given_only_as_a_raw_value_is_converted()
    {
        var sink = Assert.Single(PulseJson.Sinks("""[{"index":1,"name":"s","mute":"yes","volume":{"mono":{"value":32768}}}]"""));
        Assert.Equal(50, sink.Volume);
        Assert.True(sink.Muted);       // older pactl says "yes"
        Assert.Equal("s", sink.Description);
        Assert.True(sink.Usable);      // no ports at all (a virtual output)
    }

    [Fact]
    public void Streams_are_read_with_their_app_and_state()
    {
        var streams = PulseJson.Streams(PactlSamples.SinkInputs);
        Assert.Equal(5, streams.Count);
        Assert.Equal("spotify", streams[0].Property("application.process.binary"));
        Assert.False(streams[0].Corked);
        Assert.True(streams[1].Corked);
        Assert.True(streams[2].Muted);
        Assert.Equal(60, streams[2].Volume);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("""[1, "two", null]""")]
    public void Odd_output_reads_as_nothing(string json)
    {
        Assert.Empty(PulseJson.Sinks(json));
        Assert.Empty(PulseJson.Streams(json));
    }
}

public class PulseNamesTests
{
    static readonly List<PulseSink> Sinks = PulseJson.Sinks(PactlSamples.Sinks);

    [Fact]
    public void Outputs_are_named_the_way_people_and_the_matcher_expect()
    {
        // A USB card's own name beats the kind its USB id claims (these speakers say "headset")
        Assert.Equal("Desk Speakers 2+ (USB Audio Chip [Some Headset in charging mode] Analog Stereo)", PulseNames.Output(Sinks[0]));
        // What's plugged in, when the card knows; never the codec chip's name
        Assert.Equal("Speakers (Built-in Audio Analog Stereo)", PulseNames.Output(Sinks[1]));
        Assert.Equal("HDMI / DisplayPort (GPU High Definition Audio Controller Digital Stereo (HDMI))", PulseNames.Output(Sinks[2]));
        Assert.Equal("Headphones (WH-1000XM4)", PulseNames.Output(Sinks[3]));
    }

    [Fact]
    public void A_label_the_description_already_has_is_not_repeated()
    {
        var sink = Assert.Single(PulseJson.Sinks("""[{"index":1,"name":"s","description":"Jabra Evolve 65 Analog Stereo","properties":{"device.bus":"usb","node.nick":"Jabra Evolve 65","device.form_factor":"headset"}}]"""));
        Assert.Equal("Headset (Jabra Evolve 65 Analog Stereo)", PulseNames.Output(sink));
        var plain = Assert.Single(PulseJson.Sinks("""[{"index":1,"name":"s","description":"Speakers"}]"""));
        Assert.Equal("Speakers", PulseNames.Output(plain));
    }

    [Fact]
    public void A_bluetooth_headset_in_call_mode_says_so()
    {
        var sink = Assert.Single(PulseJson.Sinks("""[{"index":1,"name":"bluez_output.x","description":"WH-1000XM4","properties":{"device.bus":"bluetooth","api.bluez5.profile":"headset-head-unit","device.form_factor":"headset"}}]"""));
        Assert.Equal("Headset Hands-Free (WH-1000XM4)", PulseNames.Output(sink));
    }

    [Fact]
    public void The_shared_matcher_finds_these_outputs_by_what_people_say()
    {
        var outputs = Sinks.Select(s => new AudioOutput(s.Name, PulseNames.Output(s), s.Index == 63)).ToList();
        Assert.Equal(Sinks[3].Name, OutputMatcher.Find("headphones", outputs.Where(o => o.Name.Contains("WH-") || o.Name.StartsWith("Speakers")).ToList()).Id);
        Assert.Equal(Sinks[1].Name, OutputMatcher.Find("the built-in speakers", outputs).Id);
        Assert.Equal(Sinks[0].Name, OutputMatcher.Find("the speakers", outputs).Id); // two are speakers: the one in use
        Assert.Equal(Sinks[2].Name, OutputMatcher.Find("the monitor", outputs).Id);
        Assert.Equal(Sinks[0].Name, OutputMatcher.Find("desk speakers", outputs).Id);
        Assert.Equal("Desk Speakers 2+", Text.ShortDeviceName(outputs[0].Name));
    }

    [Theory]
    [InlineData("spotify", "Spotify")]
    [InlineData("google-chrome-stable", "Google Chrome")]
    [InlineData("chromium", "Chromium")]
    [InlineData("firefox-bin", "Firefox")]
    [InlineData("vlc", "VLC")]
    [InlineData("my-cool_player", "My Cool Player")]
    public void Programs_get_the_names_people_use(string process, string expected) => Assert.Equal(expected, PulseNames.Friendly(process));

    [Fact]
    public void A_streams_app_is_keyed_by_its_program()
    {
        var streams = PulseJson.Streams(PactlSamples.SinkInputs);
        Assert.Equal(("spotify", "Spotify", "spotify"), PulseNames.App(streams[0]));
        Assert.Equal(("chromium", "Chromium", "chromium"), PulseNames.App(streams[1]));
        var flatpak = Assert.Single(PulseJson.Streams("""[{"index":1,"sink":1,"properties":{"application.name":"Tidal","pipewire.access.portal.app_id":"com.mastermindzh.tidal-hifi"}}]"""));
        Assert.Equal(("tidal-hifi", "Tidal", "tidal-hifi"), PulseNames.App(flatpak));
    }
}

/// <summary>pactl in memory: answers from the samples and records what it was told to change.</summary>
internal sealed class FakePactl : IPactl
{
    public string DefaultSink { get; set; } = "alsa_output.usb-Acme_Desk_Speakers-00.analog-stereo";
    public string Sinks { get; set; } = PactlSamples.Sinks;
    public string SinkInputs { get; set; } = PactlSamples.SinkInputs;
    public string? LevelSink { get; set; }
    public string? Fronted { get; set; }
    public ConcurrentQueue<string> Commands { get; } = new();
    public TaskCompletionSource<Action<string>> Subscribed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public string Run(TimeSpan timeout, params string[] arguments)
    {
        string command = string.Join(' ', arguments);
        switch (command)
        {
            case "info": return "Server Name: PulseAudio (on PipeWire 1.6.9)";
            case "get-default-sink": return DefaultSink;
            case "-f json list sinks": return Sinks;
            case "-f json list sink-inputs": return SinkInputs;
        }
        Commands.Enqueue(command);
        if (arguments[0] == "set-default-sink") DefaultSink = arguments[1];
        return "";
    }

    public async Task SubscribeAsync(Action<string> onLine, CancellationToken cancel)
    {
        Subscribed.TrySetResult(onLine);
        await Task.Delay(Timeout.Infinite, cancel);
    }

    public string? ResolveLevelSink(string sinkName) => LevelSink;
    public string? FrontedSink() => Fronted;
}

public class PulseAudioTests
{
    readonly FakePactl _pactl = new();

    PulseAudio Start()
    {
        var audio = new PulseAudio(NullLogger.Instance, _pactl);
        audio.Start();
        return audio;
    }

    [Fact]
    public void The_master_volume_is_the_default_outputs()
    {
        using var audio = Start();
        var master = audio.GetMaster();
        Assert.Equal(new MasterVolume("Desk Speakers 2+ (USB Audio Chip [Some Headset in charging mode] Analog Stereo)", 30, false), master);
    }

    [Fact]
    public void Setting_the_master_volume_and_mute_talks_to_the_default_output()
    {
        using var audio = Start();
        audio.SetMaster(45, true);
        Assert.Equal(
            new[] { "set-sink-volume alsa_output.usb-Acme_Desk_Speakers-00.analog-stereo 45%", "set-sink-mute alsa_output.usb-Acme_Desk_Speakers-00.analog-stereo 1" },
            _pactl.Commands);
        audio.SetMaster(250, null); // clamped
        Assert.Contains("set-sink-volume alsa_output.usb-Acme_Desk_Speakers-00.analog-stereo 100%", _pactl.Commands);
    }

    [Fact]
    public void Loudness_lives_on_the_hardware_behind_a_processing_stage()
    {
        // Omarchy's speaker tuning: the chosen output is a filter, and the real speakers are behind it
        _pactl.Sinks = PactlSamples.Sinks.TrimEnd().TrimEnd(']') + """
            ,{"index":90,"name":"omarchy_speaker_tuning","description":"Laptop Speakers (tuned)","mute":false,"volume":{"front-left":{"value_percent":"100%"}}}]
            """;
        _pactl.DefaultSink = "omarchy_speaker_tuning";
        _pactl.LevelSink = "alsa_output.pci-0000_00_1f.3.analog-stereo";
        using var audio = Start();

        var master = audio.GetMaster()!;
        Assert.Equal("Laptop Speakers (tuned)", master.DeviceName); // what's in use…
        Assert.Equal(50, master.Volume);                            // …at the level of the speakers behind it
        Assert.True(master.Muted);

        audio.SetMaster(20, false);
        Assert.Contains("set-sink-volume alsa_output.pci-0000_00_1f.3.analog-stereo 20%", _pactl.Commands);
        Assert.Contains("set-sink-mute alsa_output.pci-0000_00_1f.3.analog-stereo 0", _pactl.Commands);
    }

    [Fact]
    public void No_default_output_is_no_master_volume()
    {
        _pactl.DefaultSink = "@DEFAULT_SINK@"; // what pactl prints when there's none
        using var audio = Start();
        Assert.Null(audio.GetMaster());
        Assert.Throws<PluginException>(() => audio.SetMaster(10, null));
    }

    [Fact]
    public void Outputs_leave_out_empty_sockets_and_the_speakers_behind_a_tuning()
    {
        using var audio = Start();
        var outputs = audio.GetOutputs();
        Assert.Equal(new[] { 63, 65, 81 }, outputs.Select(o => PulseJson.Sinks(PactlSamples.Sinks).Single(s => s.Name == o.Id).Index)); // not the unplugged HDMI
        Assert.Equal("alsa_output.usb-Acme_Desk_Speakers-00.analog-stereo", Assert.Single(outputs, o => o.IsDefault).Id);

        _pactl.Fronted = "alsa_output.pci-0000_00_1f.3.analog-stereo";
        Assert.DoesNotContain(audio.GetOutputs(), o => o.Id == _pactl.Fronted);
    }

    [Fact]
    public void Switching_output_moves_the_apps_but_not_the_plumbing()
    {
        using var audio = Start();
        audio.SetDefaultOutput("bluez_output.AA_BB_CC_DD_EE_FF.1");
        Assert.Equal(
            new[]
            {
                "set-default-sink bluez_output.AA_BB_CC_DD_EE_FF.1",
                "move-sink-input 101 bluez_output.AA_BB_CC_DD_EE_FF.1",
                "move-sink-input 102 bluez_output.AA_BB_CC_DD_EE_FF.1",
                "move-sink-input 103 bluez_output.AA_BB_CC_DD_EE_FF.1",
            },
            _pactl.Commands); // not 104 (a filter's own stream) nor 105 (EasyEffects)
        Assert.True(audio.GetOutputs().Single(o => o.Id.StartsWith("bluez")).IsDefault);
    }

    [Fact]
    public void Apps_are_one_entry_each_however_many_streams_they_play()
    {
        using var audio = Start();
        var apps = audio.GetApps();
        Assert.Equal(new[] { "Chromium", "Spotify" }, apps.Select(a => a.Name)); // both playing: by name
        var chromium = apps[0];
        Assert.Equal(("chromium", 80, false, true), (chromium.Key, chromium.Volume, chromium.Muted, chromium.Playing)); // loudest stream; muted only if all are
    }

    [Fact]
    public void An_apps_volume_is_set_on_all_its_streams()
    {
        using var audio = Start();
        var after = audio.SetApp("chromium", 25, false);
        Assert.Equal(
            new[] { "set-sink-input-volume 102 25%", "set-sink-input-mute 102 0", "set-sink-input-volume 103 25%", "set-sink-input-mute 103 0" },
            _pactl.Commands);
        Assert.Equal("Chromium", after?.Name);
        Assert.Null(audio.SetApp("zoom", 10, null));
    }

    [Theory]
    [InlineData("Event 'change' on sink #63", true)]
    [InlineData("Event 'new' on sink #70", true)]
    [InlineData("Event 'change' on server #4294967295", true)] // another default output
    [InlineData("Event 'change' on card #45", true)]           // headphones plugged in
    [InlineData("Event 'new' on sink-input #101", false)]
    [InlineData("Event 'change' on source #64", false)]
    [InlineData("Event 'remove' on client #200", false)]
    [InlineData("gibberish", false)]
    public void Only_events_that_can_move_the_master_volume_count(string line, bool counts) => Assert.Equal(counts, PulseAudio.IsMasterEvent(line));

    [Fact]
    public async Task A_change_made_elsewhere_is_announced()
    {
        using var audio = Start();
        int changes = 0;
        audio.Changed += () => Interlocked.Increment(ref changes);
        var onLine = await _pactl.Subscribed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        onLine("Event 'change' on sink-input #101");
        Assert.Equal(0, changes);
        onLine("Event 'change' on sink #63");
        Assert.Equal(1, changes);
    }

    [Fact]
    public async Task The_plugin_is_the_shared_one_speaking_of_linux()
    {
        var media = new StillMedia();
        var plugin = new PcAudioPlugin(_ => new PulseAudio(NullLogger.Instance, _pactl), _ => media);
        plugin.Initialize(new FakePluginContext());
        plugin.ApplySettings(new PcAudioSettings());
        await plugin.StartAsync(default);
        try
        {
            Assert.Equal("pc-audio", plugin.Id);
            Assert.Contains("the Linux PC the hub runs on", plugin.Instructions);
            Assert.Equal(StatusLevel.Ok, plugin.Status.Level);
            Assert.StartsWith("Output: Desk Speakers 2+", plugin.Status.Message);
            var result = await FakePluginContext.CallToolAsync(plugin, "pc_volume", """{"change": 10}""");
            Assert.Equal("PC volume is now 30%.", result.Text); // the fake doesn't move; the wording is the shared plugin's
            Assert.Contains("set-sink-volume alsa_output.usb-Acme_Desk_Speakers-00.analog-stereo 40%", _pactl.Commands);
            var outputs = await FakePluginContext.CallToolAsync(plugin, "pc_audio_outputs");
            Assert.StartsWith("In use: Desk Speakers 2+", outputs.Text);
            Assert.Contains("Headphones (WH-1000XM4)", outputs.Text);
        }
        finally
        {
            await plugin.StopAsync();
        }
    }

    sealed class StillMedia : IPcMedia
    {
        public event Action? Changed { add { } remove { } }
        public MediaInfo? Current => null;
        public Task StartAsync(CancellationToken cancel) => Task.CompletedTask;
        public Task<MediaInfo?> ReadAsync(CancellationToken cancel) => Task.FromResult<MediaInfo?>(null);
        public Task<bool> SendAsync(MediaAction action, CancellationToken cancel) => Task.FromResult(false);
        public void Dispose() { }
    }
}

public class MprisTests
{
    static Dictionary<string, VariantValue> Properties(string status, params (string Key, VariantValue Value)[] metadata) => new()
    {
        ["PlaybackStatus"] = VariantValue.String(status),
        ["Metadata"] = new Dict<string, VariantValue>(metadata.ToDictionary(m => m.Key, m => m.Value)).AsVariantValue(),
    };

    [Fact]
    public void A_players_track_is_read_from_its_metadata()
    {
        var player = MprisMedia.Describe("org.mpris.MediaPlayer2.spotify", "Spotify", Properties("Playing",
            ("xesam:title", VariantValue.String("Teardrop")),
            ("xesam:artist", VariantValue.Array(new[] { "Massive Attack", "Elizabeth Fraser" })),
            ("xesam:album", VariantValue.String("Mezzanine"))));
        Assert.True(player.Playing);
        Assert.Equal(new MediaInfo("Teardrop", "Massive Attack, Elizabeth Fraser", "Mezzanine", "Spotify", true), player.ToMediaInfo());
    }

    [Fact]
    public void Missing_and_blank_metadata_is_null()
    {
        var player = MprisMedia.Describe("org.mpris.MediaPlayer2.vlc", "VLC", Properties("Paused",
            ("xesam:title", VariantValue.String("  ")),
            ("xesam:artist", VariantValue.Array(Array.Empty<string>())),
            ("xesam:albumArtist", VariantValue.Array(new[] { "Various" }))));
        Assert.False(player.Playing);
        Assert.Null(player.Title);
        Assert.Equal("Various", player.Artist); // the album's artist stands in
        Assert.Null(player.Album);

        var bare = MprisMedia.Describe("org.mpris.MediaPlayer2.x", "X", new Dictionary<string, VariantValue>());
        Assert.Equal("Stopped", bare.Status);
        Assert.Null(bare.Title);
    }

    [Fact]
    public void An_artist_given_as_plain_text_is_taken_too()
    {
        var player = MprisMedia.Describe("org.mpris.MediaPlayer2.odd", "Odd", Properties("Playing", ("xesam:artist", VariantValue.String("Portishead"))));
        Assert.Equal("Portishead", player.Artist);
    }

    static MprisPlayer Player(string name, string status, string? title = "Song") => new($"org.mpris.MediaPlayer2.{name}", name, status, title, null, null);

    [Fact]
    public void The_player_shown_last_stays_while_it_plays()
    {
        MprisPlayer[] players = [Player("chromium", "Playing"), Player("spotify", "Playing")];
        Assert.Equal("spotify", MprisMedia.Pick(players, "org.mpris.MediaPlayer2.spotify")?.Identity);
        Assert.Equal("chromium", MprisMedia.Pick(players, null)?.Identity);
    }

    [Fact]
    public void A_playing_player_beats_a_paused_one_shown_last()
    {
        MprisPlayer[] players = [Player("chromium", "Paused"), Player("spotify", "Playing")];
        Assert.Equal("spotify", MprisMedia.Pick(players, "org.mpris.MediaPlayer2.chromium")?.Identity);
    }

    [Fact]
    public void With_nothing_playing_it_is_the_one_shown_last_then_one_with_a_track()
    {
        MprisPlayer[] players = [Player("chromium", "Stopped", null), Player("vlc", "Paused"), Player("spotify", "Paused")];
        Assert.Equal("spotify", MprisMedia.Pick(players, "org.mpris.MediaPlayer2.spotify")?.Identity);
        Assert.Equal("vlc", MprisMedia.Pick(players, "org.mpris.MediaPlayer2.gone")?.Identity);
        Assert.Null(MprisMedia.Pick([], null));
    }

    [Theory]
    [InlineData("org.mpris.MediaPlayer2.spotify", true)]
    [InlineData("org.mpris.MediaPlayer2.chromium.instance1234", true)]
    [InlineData("org.mpris.MediaPlayer2.playerctld", false)] // repeats another player
    [InlineData("org.mpris.MediaPlayer2.", false)]
    [InlineData("org.freedesktop.Notifications", false)]
    public void Players_are_known_by_their_bus_name(string name, bool player) => Assert.Equal(player, MprisMedia.IsPlayerName(name));

    [Theory]
    [InlineData("org.mpris.MediaPlayer2.chromium.instance1234", "Chromium")]
    [InlineData("org.mpris.MediaPlayer2.firefox.instance_1_23", "Firefox")]
    [InlineData("org.mpris.MediaPlayer2.vlc", "VLC")]
    public void A_player_without_a_name_is_named_from_its_bus_name(string busName, string expected) => Assert.Equal(expected, MprisMedia.NameFromBusName(busName));

    [Fact]
    public void Firefox_is_called_firefox()
    {
        Assert.Equal("Firefox", MprisMedia.CleanIdentity("Mozilla firefox", "org.mpris.MediaPlayer2.firefox.instance_1_23"));
        Assert.Equal("Spotify", MprisMedia.CleanIdentity(" Spotify ", "org.mpris.MediaPlayer2.spotify"));
    }
}
