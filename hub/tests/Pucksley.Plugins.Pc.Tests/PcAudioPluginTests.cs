using Pucksley.Plugins.Pc.Audio;
using Pucksley.Sdk;
using Pucksley.TestSupport;

namespace Pucksley.Plugins.Pc.Tests;

public sealed class PcAudioPluginTests : IAsyncLifetime
{
    readonly FakePcAudio _audio = new();
    readonly FakePcMedia _media = new();
    readonly RecordingContext _context = new(new FakePluginContext());
    readonly TestPcAudioPlugin _plugin;

    public PcAudioPluginTests() => _plugin = new TestPcAudioPlugin(_audio, _media);

    public async Task InitializeAsync()
    {
        _plugin.Initialize(_context);
        _plugin.ApplySettings(new PcAudioSettings());
        await _plugin.StartAsync(default);
    }

    public Task DisposeAsync() => _plugin.StopAsync();

    Task<ToolResult> Call(string tool, string json = "{}") => FakePluginContext.CallToolAsync(_plugin, tool, json);

    [Fact]
    public void Starts_with_the_output_in_the_status()
    {
        Assert.True(_audio.Started);
        Assert.Equal(StatusLevel.Ok, _plugin.Status.Level);
        Assert.Contains("Speakers (Realtek(R) Audio)", _plugin.Status.Message);
    }

    [Fact]
    public async Task Volume_steps_down()
    {
        var result = await Call("pc_volume", """{"change": -10}""");
        Assert.False(result.IsError, result.Text);
        Assert.Equal("PC volume is now 20%.", result.Text);
        Assert.Equal("PC volume 20%", result.Activity);
        Assert.Equal(20, _audio.Master!.Volume);
    }

    [Fact]
    public async Task Volume_is_clamped_and_reports_the_limit()
    {
        _audio.Master = _audio.Master! with { Volume = 96 };
        var result = await Call("pc_volume", """{"change": 10}""");
        Assert.Equal(100, _audio.Master.Volume);
        result = await Call("pc_volume", """{"change": 10}""");
        Assert.Equal("PC volume is already at 100%.", result.Text);
    }

    [Fact]
    public async Task Setting_a_level_unmutes_like_the_volume_keys()
    {
        _audio.Master = _audio.Master! with { Muted = true };
        var result = await Call("pc_volume", """{"volume": 40}""");
        Assert.Equal("PC volume is now 40% (unmuted).", result.Text);
        Assert.False(_audio.Master.Muted);
        Assert.Contains("master 40 False", _audio.Calls);
    }

    [Fact]
    public async Task Volume_without_arguments_only_reports()
    {
        var result = await Call("pc_volume");
        Assert.Equal("PC volume is 30%.", result.Text);
        Assert.DoesNotContain(_audio.Calls, c => c.StartsWith("master"));
    }

    [Fact]
    public async Task Mute_toggles_by_default_and_can_be_explicit()
    {
        Assert.Equal("The PC is muted.", (await Call("pc_mute")).Text);
        Assert.True(_audio.Master!.Muted);
        Assert.Equal("The PC is unmuted (volume 30%).", (await Call("pc_mute")).Text);
        await Call("pc_mute", """{"muted": false}""");
        Assert.False(_audio.Master.Muted);
    }

    [Fact]
    public async Task Next_reports_the_new_track()
    {
        var result = await Call("pc_media", """{"action": "next"}""");
        Assert.Equal("Next: \"Angel\" by Massive Attack.", result.Text);
        Assert.Equal("PC: Angel", result.Activity);
        Assert.Equal(new[] { MediaAction.Next }, _media.Sent);
    }

    [Theory]
    [InlineData("pause", MediaAction.Pause, "Paused Spotify.")]
    [InlineData("toggle", MediaAction.Toggle, "Paused Spotify.")]
    [InlineData("stop", MediaAction.Pause, "Paused Spotify.")]
    [InlineData("resume", MediaAction.Play, "Playing on Spotify.")]
    public async Task Media_actions_and_synonyms(string action, MediaAction sent, string expected)
    {
        var result = await Call("pc_media", $$"""{"action": "{{action}}"}""");
        Assert.Equal(expected, result.Text);
        Assert.Equal(new[] { sent }, _media.Sent);
    }

    [Fact]
    public async Task Pausing_when_nothing_plays_says_so()
    {
        _media.Current = _media.Current! with { Playing = false };
        Assert.Equal("Nothing is playing on the PC.", (await Call("pc_media", """{"action": "pause"}""")).Text);
    }

    [Fact]
    public async Task Unknown_media_action_is_explained()
    {
        var result = await Call("pc_media", """{"action": "rewind"}""");
        Assert.True(result.IsError);
        Assert.Contains("play, pause", result.Text);
    }

    [Fact]
    public async Task Now_playing_names_the_track_app_and_volume()
    {
        var result = await Call("pc_now_playing");
        Assert.Equal("Spotify is playing \"Teardrop\" by Massive Attack. PC volume 30%.", result.Text);
        Assert.Equal("PC: Teardrop", result.Activity);
        _media.Current = null;
        Assert.Equal("Nothing is playing on the PC. PC volume 30%.", (await Call("pc_now_playing")).Text);
    }

    [Fact]
    public async Task Outputs_are_listed_with_the_one_in_use()
    {
        var result = await Call("pc_audio_outputs");
        Assert.StartsWith("In use: Speakers (Realtek(R) Audio).", result.Text);
        Assert.Contains("Headphones (WH-1000XM4 Stereo)", result.Text);
    }

    [Fact]
    public async Task Switching_to_headphones_sets_the_default_output()
    {
        var result = await Call("pc_set_audio_output", """{"name": "my headphones"}""");
        Assert.False(result.IsError, result.Text);
        Assert.Equal("The PC now plays through Headphones (WH-1000XM4 Stereo).", result.Text);
        Assert.Equal("Output: Headphones", result.Activity);
        Assert.Contains("default id-headphones", _audio.Calls);
    }

    [Fact]
    public async Task Switching_to_the_output_in_use_changes_nothing()
    {
        var result = await Call("pc_set_audio_output", """{"name": "speakers"}""");
        Assert.Equal("The PC is already playing through Speakers (Realtek(R) Audio).", result.Text);
        Assert.DoesNotContain(_audio.Calls, c => c.StartsWith("default"));
    }

    [Fact]
    public async Task Unknown_output_lists_the_outputs()
    {
        var result = await Call("pc_set_audio_output", """{"name": "kazoo"}""");
        Assert.True(result.IsError);
        Assert.Contains("DELL U2720Q", result.Text);
    }

    [Fact]
    public async Task App_volume_steps_relative_to_its_own_level()
    {
        var result = await Call("pc_app_volume", """{"app": "spotify", "change": -20}""");
        Assert.Equal("Spotify's volume is now 80%.", result.Text);
        Assert.Equal("Spotify 80%", result.Activity);
        Assert.Contains("app spotify 80 -", _audio.Calls);
    }

    [Fact]
    public async Task Turning_a_muted_app_up_unmutes_it()
    {
        var result = await Call("pc_app_volume", """{"app": "Teams", "volume": 60}""");
        Assert.Equal("Microsoft Teams's volume is now 60%. (Unmuted.)", result.Text);
        Assert.Contains("app ms-teams 60 False", _audio.Calls);
    }

    [Fact]
    public async Task An_app_can_be_muted_and_reported()
    {
        var muted = await Call("pc_app_volume", """{"app": "chrome", "muted": true}""");
        Assert.Equal("Google Chrome is muted.", muted.Text);
        Assert.Equal("Google Chrome muted", muted.Activity);
        Assert.Contains("app chrome - True", _audio.Calls);
        Assert.Equal("Spotify's volume is 100%.", (await Call("pc_app_volume", """{"app": "the Spotify app"}""")).Text);
    }

    [Fact]
    public async Task Unknown_app_lists_the_apps_with_sound()
    {
        var result = await Call("pc_app_volume", """{"app": "zoom", "volume": 10}""");
        Assert.True(result.IsError);
        Assert.Contains("Spotify", result.Text);
    }

    [Fact]
    public async Task Apps_playing_audio_lists_the_playing_and_the_quiet()
    {
        var result = await Call("pc_apps_playing_audio");
        Assert.Equal("Playing sound: Spotify 100%. Quiet apps with a volume setting: Google Chrome, Microsoft Teams.", result.Text);
        Assert.Equal("Spotify playing", result.Activity);
    }

    [Fact]
    public async Task Context_is_one_cached_line()
    {
        Assert.Equal("PC: Spotify playing \"Teardrop\" – Massive Attack; volume 30.", await _plugin.GetContextAsync(ToolCaller.Web, default));
        _media.Current = _media.Current! with { Playing = false };
        _audio.Master = _audio.Master! with { Muted = true };
        await Call("pc_volume"); // refreshes the cached master state
        Assert.Equal("PC: Spotify paused \"Teardrop\" – Massive Attack; volume 30 (muted).", await _plugin.GetContextAsync(ToolCaller.Web, default));
    }

    [Theory]
    [InlineData(null, null, "PC: nothing playing; no audio output.")]
    [InlineData(25, null, "PC: nothing playing; volume 25.")]
    public void Context_formats_missing_parts(int? volume, string? title, string expected)
    {
        var master = volume is int v ? new MasterVolume("Speakers", v, false) : null;
        var media = title is null ? null : new MediaInfo(title, null, null, "Chrome", true);
        Assert.Equal(expected, PcAudioPluginBase.FormatContext(master, media));
    }

    [Fact]
    public void The_dial_shows_the_volume()
    {
        var control = Assert.Single(_plugin.GetControls());
        Assert.Equal("volume", control.Id);
        Assert.Equal("PC volume", control.Name);
        Assert.Equal(ControlKind.Number, control.Kind);
        Assert.Equal(30, control.Value);
        Assert.Equal(2, control.Step);
        Assert.Equal("%", control.Unit);
        Assert.Null(control.Text);
    }

    [Fact]
    public async Task Turning_the_dial_sets_the_volume_without_echoing_it_back()
    {
        await _plugin.OnControlAsync("volume", new ControlInput { Value = 50 }, default);
        Assert.Equal(50, _audio.Master!.Volume);
        _audio.ChangeOutsideTheHub(50); // Core Audio's own notification of that change
        await Task.Delay(300);
        Assert.Empty(_context.ControlValues);
    }

    [Fact]
    public async Task Volume_changed_elsewhere_reaches_the_pucks()
    {
        _audio.ChangeOutsideTheHub(55); // e.g. the keyboard's volume keys
        await Wait.UntilAsync(() => _context.ControlValues.Contains(("volume", 55)));
        Assert.Contains("pc", _context.NowPlayingChanges);
        Assert.Equal(55, Assert.Single(_plugin.GetControls()).Value);
    }

    [Fact]
    public async Task Muting_elsewhere_refreshes_the_whole_control()
    {
        int before = _context.ControlsChangedCount;
        _audio.ChangeOutsideTheHub(30, muted: true);
        await Wait.UntilAsync(() => _context.ControlsChangedCount > before);
        Assert.Equal("Muted", Assert.Single(_plugin.GetControls()).Text);
    }

    [Fact]
    public void A_new_track_reaches_the_pucks()
    {
        _media.RaiseChanged();
        Assert.Contains("pc", _context.NowPlayingChanges);
    }

    [Fact]
    public async Task The_home_face_source_is_this_pc()
    {
        var source = Assert.Single(_plugin.GetMediaSources());
        Assert.Equal(new MediaSourceInfo("pc", "This PC"), source);
        var now = await _plugin.GetNowPlayingAsync("pc", default);
        Assert.NotNull(now);
        Assert.Equal("Teardrop", now.Title);
        Assert.Equal("Massive Attack", now.Artist);
        Assert.Equal("Mezzanine", now.Album);
        Assert.True(now.Playing);
        Assert.Equal(30, now.Volume);
        Assert.False(now.Muted);
        Assert.Equal("Spotify", now.Source);
        Assert.Null(await _plugin.GetNowPlayingAsync("kitchen", default));
    }

    [Fact]
    public async Task The_source_can_be_renamed()
    {
        await _plugin.StopAsync();
        _plugin.ApplySettings(new PcAudioSettings { SourceName = "Sam's PC" });
        await _plugin.StartAsync(default);
        Assert.Equal("Sam's PC", Assert.Single(_plugin.GetMediaSources()).Name);
    }

    [Fact]
    public async Task Home_face_commands_drive_media_and_volume()
    {
        await _plugin.MediaCommandAsync("pc", MediaCommand.Next, default);
        await _plugin.MediaCommandAsync("pc", MediaCommand.SetVolume(44), default);
        await _plugin.MediaCommandAsync("pc", MediaCommand.Mute, default);
        Assert.Equal(new[] { MediaAction.Next }, _media.Sent);
        Assert.Equal(44, _audio.Master!.Volume);
        Assert.True(_audio.Master.Muted);
    }

    [Fact]
    public async Task Stopping_releases_the_audio_objects()
    {
        await _plugin.StopAsync();
        Assert.True(_audio.Disposed);
        var result = await Call("pc_volume");
        Assert.True(result.IsError);
        Assert.Contains("isn't running", result.Text);
    }
}
