using System.Text.RegularExpressions;
using Pucksley.Plugins.Windows.Audio;
using Pucksley.Plugins.Windows.Pc;
using Pucksley.Plugins.Windows.Speech;
using Pucksley.Sdk;

namespace Pucksley.Plugins.Windows.Tests;

/// <summary>What the hub and Claude see: discovery, tool names, schemas. Constructing the plugins touches no hardware.</summary>
public class ToolSchemaTests
{
    static readonly IPlugin[] Plugins = [new PcAudioPlugin(), new PcControlPlugin(), new WindowsSpeechPlugin()];

    [Fact]
    public void Plugins_are_discoverable_with_the_expected_identity()
    {
        foreach (var type in new[] { typeof(PcAudioPlugin), typeof(PcControlPlugin), typeof(WindowsSpeechPlugin) })
        {
            Assert.True(type.IsPublic && !type.IsAbstract);
            Assert.NotNull(type.GetConstructor(Type.EmptyTypes)); // the hub creates plugins by reflection
        }
        Assert.Equal(new[] { "pc-audio", "pc", "windows-speech" }, Plugins.Select(p => p.Id));
        Assert.Equal(new[] { "volume", "pc", "pc" }, Plugins.Select(p => p.Icon));
        Assert.All(Plugins, p => Assert.True(p.EnabledByDefault));
    }

    [Fact]
    public void Tools_match_the_spec()
    {
        Assert.Equal(
            new[] { "pc_volume", "pc_mute", "pc_media", "pc_now_playing", "pc_audio_outputs", "pc_set_audio_output", "pc_app_volume", "pc_apps_playing_audio" },
            Plugins[0].GetTools().Select(t => t.Name));
        Assert.Equal(
            new[]
            {
                "pc_open_app", "pc_open", "pc_lock", "pc_sleep", "pc_shutdown", "pc_cancel_shutdown", "pc_screenshot", "pc_clipboard_get",
                "pc_clipboard_set", "pc_type_text", "pc_show_desktop", "pc_focus_app", "pc_running_apps", "pc_system_info", "pc_dark_mode", "pc_brightness",
            },
            Plugins[1].GetTools().Select(t => t.Name));
        Assert.Empty(Plugins[2].GetTools());
    }

    [Fact]
    public void Tool_names_are_valid_unique_and_prefixed()
    {
        var tools = Plugins.SelectMany(p => p.GetTools()).ToList();
        Assert.All(tools, t => Assert.Matches(new Regex("^pc_[a-z0-9_]{1,61}$"), t.Name));
        Assert.Equal(tools.Count, tools.Select(t => t.Name).Distinct().Count());
        Assert.All(tools, t => Assert.InRange(t.Description.Length, 20, 300));
    }

    [Fact]
    public void Only_drastic_tools_ask_first() =>
        Assert.Equal(new[] { "pc_sleep", "pc_shutdown" }, Plugins.SelectMany(p => p.GetTools()).Where(t => t.Confirm).Select(t => t.Name));

    [Fact]
    public void Schemas_mark_what_is_required()
    {
        var tools = Plugins.SelectMany(p => p.GetTools()).ToDictionary(t => t.Name);
        Assert.Null(tools["pc_volume"].InputSchema["required"]);
        Assert.Equal("""["action"]""", tools["pc_media"].InputSchema["required"]!.ToJsonString());
        Assert.Equal("""["app"]""", tools["pc_app_volume"].InputSchema["required"]!.ToJsonString());
        Assert.Equal("""["all","primary","active_window"]""", tools["pc_screenshot"].InputSchema["properties"]!["screen"]!["enum"]!.ToJsonString());
        Assert.Equal("""["on"]""", tools["pc_dark_mode"].InputSchema["required"]!.ToJsonString());
    }

    [Fact]
    public void The_speech_plugin_offers_both_engines()
    {
        var plugin = new WindowsSpeechPlugin();
        ITextToSpeech tts = plugin;
        ISpeechToText stt = plugin;
        Assert.Equal("windows", tts.EngineId);
        Assert.Equal("Windows voices", tts.EngineName);
        Assert.Equal("windows", stt.EngineId);
        Assert.Contains("basic", stt.EngineName);
        Assert.Equal("list-voices", Assert.Single(plugin.GetActions()).Id);
    }
}
