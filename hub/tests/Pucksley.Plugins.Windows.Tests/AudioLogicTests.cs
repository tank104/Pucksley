using Pucksley.Plugins.Windows.Audio;
using Pucksley.Sdk;

namespace Pucksley.Plugins.Windows.Tests;

public class VolumeMathTests
{
    [Theory]
    [InlineData(30, 50, null, 50)]    // absolute
    [InlineData(30, 150, null, 100)]  // absolute, clamped high
    [InlineData(30, -5, null, 0)]     // absolute, clamped low
    [InlineData(30, null, -10, 20)]   // relative down
    [InlineData(30, null, 25, 55)]    // relative up
    [InlineData(95, null, 10, 100)]   // relative, clamped high
    [InlineData(4, null, -10, 0)]     // relative, clamped low
    [InlineData(30, 70, -10, 70)]     // both: the absolute level wins
    public void Target_resolves_absolute_and_relative_levels_within_0_to_100(int current, int? volume, int? change, int expected) =>
        Assert.Equal(expected, VolumeMath.Target(current, volume, change));

    [Fact]
    public void Target_is_null_when_nothing_was_asked_for() => Assert.Null(VolumeMath.Target(30, null, null));

    [Theory]
    [InlineData(0f, 0)]
    [InlineData(0.3f, 30)]
    [InlineData(0.305f, 31)] // Core Audio reports 0.305 for a 30.5 % slider: round half away from zero
    [InlineData(1f, 100)]
    [InlineData(1.2f, 100)]
    [InlineData(-0.1f, 0)]
    public void Scalar_converts_to_percent(float scalar, int percent) => Assert.Equal(percent, VolumeMath.ToPercent(scalar));

    [Theory]
    [InlineData(0, 0f)]
    [InlineData(45, 0.45f)]
    [InlineData(130, 1f)]
    public void Percent_converts_to_scalar(int percent, float scalar) => Assert.Equal(scalar, VolumeMath.ToScalar(percent), 3);

    [Fact]
    public void Every_percent_survives_a_round_trip_through_the_scalar()
    {
        for (int p = 0; p <= 100; p++) Assert.Equal(p, VolumeMath.ToPercent(VolumeMath.ToScalar(p)));
    }
}

public class OutputMatcherTests
{
    static readonly AudioOutput Speakers = new("1", "Speakers (Realtek(R) Audio)", true);
    static readonly AudioOutput Headphones = new("2", "Headphones (WH-1000XM4 Stereo)", false);
    static readonly AudioOutput HandsFree = new("3", "Headset (WH-1000XM4 Hands-Free AG Audio)", false);
    static readonly AudioOutput Monitor = new("4", "DELL U2720Q (NVIDIA High Definition Audio)", false);
    static readonly AudioOutput Jabra = new("5", "Headset Earphone (Jabra Evolve 65)", false);
    static readonly AudioOutput[] All = [Speakers, Headphones, HandsFree, Monitor, Jabra];

    [Theory]
    [InlineData("headphones", "2")]
    [InlineData("my headphones", "2")]
    [InlineData("speakers", "1")]
    [InlineData("the speakers", "1")]
    [InlineData("laptop speakers", "1")]
    [InlineData("monitor", "4")]
    [InlineData("the monitor speakers", "4")] // "monitor" outranks "speakers"
    [InlineData("TV", "4")]
    [InlineData("screen", "4")]
    [InlineData("Realtek", "1")]
    [InlineData("Dell", "4")]
    [InlineData("jabra", "5")]
    [InlineData("WH-1000XM4", "2")]
    [InlineData("Speakers (Realtek(R) Audio)", "1")] // the exact name, as pc_audio_outputs lists it
    public void Finds_outputs_by_kind_or_name(string spoken, string expectedId) =>
        Assert.Equal(expectedId, OutputMatcher.Find(spoken, All).Id);

    [Fact]
    public void Prefers_stereo_headphones_over_the_hands_free_endpoint_for_a_kind()
    {
        // "earbuds" names no device: it's matched by kind, and Hands-Free (call quality) comes last
        Assert.Equal("2", OutputMatcher.Find("earbuds", [HandsFree, Headphones, Speakers]).Id);
    }

    [Fact]
    public void Picks_the_current_output_when_several_of_a_kind_match()
    {
        var usb = new AudioOutput("6", "Speakers (USB Audio Device)", false);
        var realtekInUse = Speakers with { IsDefault = true };
        Assert.Equal("1", OutputMatcher.Find("speakers", [usb, realtekInUse]).Id);
    }

    [Fact]
    public void Unknown_output_lists_what_exists()
    {
        var e = Assert.Throws<PluginException>(() => OutputMatcher.Find("kazoo", All));
        Assert.Contains("Speakers (Realtek(R) Audio)", e.Message);
        Assert.Contains("DELL U2720Q", e.Message);
    }

    [Fact]
    public void No_outputs_is_a_clear_error() =>
        Assert.Contains("no audio outputs", Assert.Throws<PluginException>(() => OutputMatcher.Find("speakers", [])).Message);
}

public class AppAudioMatcherTests
{
    static readonly AppAudio[] Apps =
    [
        new("spotify", "Spotify", "Spotify", 100, false, true),
        new("chrome", "Google Chrome", "chrome", 80, false, true),
        new("ms-teams", "Microsoft Teams", "ms-teams", 50, false, false),
        new("msedge", "Microsoft Edge", "msedge", 100, false, false),
        new("system", "System sounds", "system", 100, false, false),
    ];

    [Theory]
    [InlineData("Spotify", "spotify")]
    [InlineData("the Spotify app", "spotify")]
    [InlineData("chrome", "chrome")]
    [InlineData("Google Chrome", "chrome")]
    [InlineData("teams", "ms-teams")]
    [InlineData("edge", "msedge")]
    [InlineData("system sounds", "system")]
    public void Finds_apps_by_display_or_process_name(string spoken, string key) =>
        Assert.Equal(key, AppAudioMatcher.Find(spoken, Apps).Key);

    [Fact]
    public void Unknown_app_lists_the_apps_with_sound()
    {
        var e = Assert.Throws<PluginException>(() => AppAudioMatcher.Find("zoom", Apps));
        Assert.Contains("Spotify", e.Message);
        Assert.Contains("Microsoft Teams", e.Message);
    }

    [Fact]
    public void No_sessions_is_a_clear_error() =>
        Assert.Contains("No apps", Assert.Throws<PluginException>(() => AppAudioMatcher.Find("spotify", [])).Message);
}
