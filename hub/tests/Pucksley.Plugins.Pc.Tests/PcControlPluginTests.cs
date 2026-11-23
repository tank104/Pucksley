using System.Text.Json;
using Pucksley.Plugins.Pc.Desktop;
using Pucksley.Plugins.Pc.Screen;
using Pucksley.Sdk;
using Pucksley.TestSupport;

namespace Pucksley.Plugins.Pc.Tests;

/// <summary>The "This PC" tools as Windows and Linux both run them, against a desktop in memory.</summary>
public sealed class PcControlPluginTests : IAsyncLifetime
{
    readonly FakePcDesktop _desktop = new();
    readonly TestPcControlPlugin _plugin;

    public PcControlPluginTests() => _plugin = new TestPcControlPlugin(_desktop);

    public async Task InitializeAsync()
    {
        _plugin.Initialize(new FakePluginContext());
        _plugin.ApplySettings(new NoSettings());
        await _plugin.StartAsync(default);
        await Wait.UntilAsync(() => _plugin.Status.Message.Contains("apps found"));
    }

    public Task DisposeAsync() => _plugin.StopAsync();

    Task<ToolResult> Call(string tool, string json = "{}") => FakePluginContext.CallToolAsync(_plugin, tool, json);

    // ───────────────────────── Identity, and what differs by operating system

    [Fact]
    public void It_is_the_pc_plugin_whatever_the_system()
    {
        Assert.Equal("pc", _plugin.Id);
        Assert.Equal("This PC", _plugin.Name);
        Assert.Equal("pc", _plugin.Icon);
        Assert.True(_plugin.EnabledByDefault);
        Assert.Equal(
            new[]
            {
                "pc_open_app", "pc_open", "pc_lock", "pc_sleep", "pc_shutdown", "pc_cancel_shutdown", "pc_screenshot", "pc_clipboard_get",
                "pc_clipboard_set", "pc_type_text", "pc_show_desktop", "pc_focus_app", "pc_running_apps", "pc_system_info", "pc_dark_mode", "pc_brightness",
            },
            _plugin.GetTools().Select(t => t.Name));
        Assert.Equal(new[] { "pc_sleep", "pc_shutdown" }, _plugin.GetTools().Where(t => t.Confirm).Select(t => t.Name));
    }

    [Theory]
    [InlineData("Windows")]
    [InlineData("Linux")]
    public void The_system_is_named_to_the_person_and_to_Claude(string os)
    {
        var plugin = new TestPcControlPlugin(new FakePcDesktop { Os = os });
        Assert.Contains($"this {os} PC", plugin.Description);
        Assert.Contains($"the {os} PC the hub runs on", plugin.Instructions);
    }

    [Fact]
    public void A_system_can_reword_a_tool_without_changing_what_it_takes()
    {
        var desktop = new FakePcDesktop();
        desktop.Descriptions["pc_open"] = "Open a web address, file or folder: \"youtube.com\", \"Downloads\", \"the trash\".";
        var plugin = new TestPcControlPlugin(desktop);
        var usual = _plugin.GetTools().Single(t => t.Name == "pc_open");
        var reworded = plugin.GetTools().Single(t => t.Name == "pc_open");
        Assert.Contains("the recycle bin", usual.Description);
        Assert.Contains("the trash", reworded.Description);
        Assert.Equal(usual.InputSchema.ToJsonString(), reworded.InputSchema.ToJsonString());
        Assert.Equal(_plugin.GetTools().Count, plugin.GetTools().Count);
    }

    [Fact]
    public async Task The_status_counts_the_apps_and_adds_what_the_system_notes()
    {
        Assert.Equal("Ready: 4 apps found", _plugin.Status.Message);
        var plugin = new TestPcControlPlugin(new FakePcDesktop { StatusNote = "Hyprland" });
        plugin.Initialize(new FakePluginContext());
        await plugin.StartAsync(default);
        await Wait.UntilAsync(() => plugin.Status.Message.Contains("apps found"));
        Assert.Equal("Ready: 4 apps found (Hyprland)", plugin.Status.Message);
    }

    // ───────────────────────── Opening things

    [Fact]
    public async Task An_app_opens_by_the_name_people_use()
    {
        var result = await Call("pc_open_app", """{"name": "the Chrome app"}""");
        Assert.Equal("Opening Google Chrome.", result.Text);
        Assert.Equal("Opening Google Chrome", result.Activity);
        Assert.Contains("launch chrome.lnk", _desktop.Calls);
    }

    [Fact]
    public async Task The_systems_aliases_are_used()
    {
        await Call("pc_open_app", """{"name": "VS Code"}""");
        Assert.Contains("launch code.lnk", _desktop.Calls);
    }

    [Fact]
    public async Task An_unknown_app_names_the_closest_ones()
    {
        var result = await Call("pc_open_app", """{"name": "Photoshop"}""");
        Assert.True(result.IsError);
        Assert.Equal("I can't find an app called 'Photoshop' on the PC.", result.Text);
        result = await Call("pc_open_app", """{"name": "Calendar"}""");
        Assert.True(result.IsError);
        Assert.StartsWith("I can't find an app called 'Calendar' on the PC. Closest: ", result.Text);
        Assert.Contains("Calculator", result.Text);
        Assert.DoesNotContain(_desktop.Calls, c => c.StartsWith("launch"));
    }

    [Fact]
    public async Task Opening_says_what_the_system_opened()
    {
        Assert.Equal("Opened youtube.com.", (await Call("pc_open", """{"target": "youtube.com"}""")).Text);
        var folder = await Call("pc_open", """{"target": "downloads"}""");
        Assert.Equal("Opened Downloads.", folder.Text);
        Assert.Equal("Opening Downloads", folder.Activity);
    }

    [Fact]
    public async Task What_the_system_refuses_is_passed_on()
    {
        _desktop.Fails = new PluginException("I won't open 'javascript:' links.");
        var result = await Call("pc_open", """{"target": "javascript:alert(1)"}""");
        Assert.True(result.IsError);
        Assert.Equal("I won't open 'javascript:' links.", result.Text);
    }

    // ───────────────────────── Lock, sleep, shut down

    [Fact]
    public async Task Locking_locks_once()
    {
        Assert.Equal("Locked the PC.", (await Call("pc_lock")).Text);
        Assert.Equal("The PC is already locked.", (await Call("pc_lock")).Text);
        Assert.Single(_desktop.Calls, c => c == "lock");
    }

    [Fact]
    public async Task Sleep_waits_for_the_reply_to_be_spoken()
    {
        var result = await Call("pc_sleep");
        Assert.Equal("The PC will go to sleep in a few seconds.", result.Text);
        Assert.Contains("sleep after 5s", _desktop.Calls);
    }

    [Theory]
    [InlineData("{}", "shutdown in 1", "The PC will shut down in 1 minute. Say \"cancel the shutdown\" to stop it.", "Shutdown in 1 min")]
    [InlineData("""{"restart": true, "minutes": 5}""", "restart in 5", "The PC will restart in 5 minutes. Say \"cancel the shutdown\" to stop it.", "Restart in 5 min")]
    [InlineData("""{"minutes": 99999}""", "shutdown in 1440", "The PC will shut down in 1440 minutes. Say \"cancel the shutdown\" to stop it.", "Shutdown in 1440 min")]
    public async Task Shutdown_counts_down(string json, string asked, string said, string activity)
    {
        var result = await Call("pc_shutdown", json);
        Assert.Equal(said, result.Text);
        Assert.Equal(activity, result.Activity);
        Assert.Contains(asked, _desktop.Calls);
    }

    [Fact]
    public async Task A_shutdown_can_be_cancelled_once()
    {
        await Call("pc_shutdown");
        Assert.True((await Call("pc_shutdown")).IsError); // already counting down
        Assert.Equal("Cancelled the shutdown.", (await Call("pc_cancel_shutdown")).Text);
        Assert.Equal("No shutdown or restart was counting down.", (await Call("pc_cancel_shutdown")).Text);
    }

    // ───────────────────────── Screen, clipboard, typing

    [Fact]
    public async Task A_screenshot_comes_with_its_size_and_the_window_title()
    {
        var result = await Call("pc_screenshot", """{"screen": "primary"}""");
        Assert.Equal("Screenshot of the screen, 3840×2160 (sent at 1568×882). Active window: \"Inbox - Outlook\".", result.Text);
        Assert.Equal("image/jpeg", result.ImageMediaType);
        Assert.Equal(_desktop.Shot.Jpeg, result.Image);
        Assert.Contains($"capture {ScreenArea.Primary}", _desktop.Calls);
    }

    [Fact]
    public async Task The_window_in_front_is_captured_unless_told_otherwise()
    {
        await Call("pc_screenshot");
        Assert.Contains($"capture {ScreenArea.ActiveWindow}", _desktop.Calls);
        var result = await Call("pc_screenshot", """{"screen": "the left one"}""");
        Assert.True(result.IsError);
        Assert.Contains("all, primary or active_window", result.Text);
    }

    [Fact]
    public async Task A_locked_pc_is_neither_looked_at_nor_typed_into()
    {
        _desktop.Locked = true;
        var shot = await Call("pc_screenshot");
        Assert.True(shot.IsError);
        Assert.Equal("The PC is locked, so I can't see its screen.", shot.Text);
        var typed = await Call("pc_type_text", """{"text": "hello"}""");
        Assert.True(typed.IsError);
        Assert.Equal("The PC is locked, so I can't type into it.", typed.Text);
        Assert.DoesNotContain(_desktop.Calls, c => c.StartsWith("capture") || c.StartsWith("type"));
    }

    [Fact]
    public async Task The_clipboard_reads_text_files_and_pictures()
    {
        Assert.Equal("Clipboard text:\nhello", (await Call("pc_clipboard_get")).Text);

        _desktop.Clipboard = new ClipboardContents("", null, 0, 0, null);
        Assert.Equal("The clipboard is empty.", (await Call("pc_clipboard_get")).Text);

        _desktop.Clipboard = new ClipboardContents(new string('x', 7000), null, 0, 0, null);
        Assert.StartsWith("Clipboard text (first 6000 of 7000 characters):", (await Call("pc_clipboard_get")).Text);

        // A folder's trailing slash doesn't hide its name
        _desktop.Clipboard = new ClipboardContents(null, null, 0, 0, ["/home/me/report.docx", "/home/me/Pictures/cat.png", "/home/me/Projects/"]);
        Assert.Equal("The clipboard holds 3 files: report.docx, cat.png, Projects.", (await Call("pc_clipboard_get")).Text);

        _desktop.Clipboard = new ClipboardContents(null, [1, 2, 3], 800, 600, null);
        var picture = await Call("pc_clipboard_get");
        Assert.Equal("The clipboard holds an image, 800×600.", picture.Text);
        Assert.Equal(new byte[] { 1, 2, 3 }, picture.Image);

        _desktop.Clipboard = ClipboardContents.Empty;
        Assert.Equal("The clipboard is empty.", (await Call("pc_clipboard_get")).Text);
    }

    [Fact]
    public async Task Text_is_copied_to_the_clipboard()
    {
        Assert.Equal("Copied \"milk and eggs\" to the clipboard.", (await Call("pc_clipboard_set", """{"text": "milk and eggs"}""")).Text);
        Assert.Equal("milk and eggs", _desktop.Clipboard.Text);
        Assert.Equal("Copied 200 characters to the clipboard.", (await Call("pc_clipboard_set", JsonSerializer.Serialize(new { text = new string('a', 200) }))).Text);
        Assert.True((await Call("pc_clipboard_set", """{"text": ""}""")).IsError);
    }

    [Fact]
    public async Task Typing_says_where_it_went()
    {
        var result = await Call("pc_type_text", """{"text": "Dear Sam,"}""");
        Assert.Equal("Typed 9 characters into Outlook.", result.Text);
        Assert.Equal("Typed into Outlook", result.Activity);
        Assert.Contains("type Dear Sam,", _desktop.Calls);
        Assert.Equal("Typed 1 character into Outlook.", (await Call("pc_type_text", """{"text": "x"}""")).Text);
    }

    [Fact]
    public async Task Text_too_long_to_type_is_sent_to_the_clipboard_instead()
    {
        var result = await Call("pc_type_text", JsonSerializer.Serialize(new { text = new string('a', TypedText.MaxLength + 1) }));
        Assert.True(result.IsError);
        Assert.Contains("clipboard", result.Text);
        Assert.DoesNotContain(_desktop.Calls, c => c.StartsWith("type"));
    }

    // ───────────────────────── Windows

    [Fact]
    public async Task An_open_app_comes_to_the_front_by_name_or_title()
    {
        var byName = await Call("pc_focus_app", """{"name": "chrome"}""");
        Assert.Equal("Google Chrome is in front: \"YouTube - Google Chrome\".", byName.Text);
        Assert.Contains("focus 2", _desktop.Calls);
        await Call("pc_focus_app", """{"name": "the budget"}""");
        Assert.Contains("focus 3", _desktop.Calls);
    }

    [Fact]
    public async Task An_app_that_is_not_open_lists_what_is()
    {
        var result = await Call("pc_focus_app", """{"name": "Spotify"}""");
        Assert.True(result.IsError);
        Assert.Equal("'Spotify' isn't open. Open apps: Outlook, Google Chrome, Excel. (pc_open_app can start it.)", result.Text);

        _desktop.Open.Clear();
        Assert.Equal("'Spotify' isn't open, and no app windows are. pc_open_app can start it.", (await Call("pc_focus_app", """{"name": "Spotify"}""")).Text);
    }

    [Fact]
    public async Task A_system_that_will_not_switch_is_named()
    {
        _desktop.FocusWorks = false;
        _desktop.Os = "Linux";
        var result = await Call("pc_focus_app", """{"name": "outlook"}""");
        Assert.True(result.IsError);
        Assert.Equal("Linux wouldn't bring Outlook to the front.", result.Text);

        // A locked screen keeps the focus: that's the reason given then
        _desktop.Locked = true;
        Assert.Equal("The PC is locked, so I can't switch its windows.", (await Call("pc_focus_app", """{"name": "outlook"}""")).Text);
    }

    [Fact]
    public async Task Running_apps_are_listed_front_most_first()
    {
        var result = await Call("pc_running_apps");
        Assert.Equal("""[{"app":"Outlook","windows":["Inbox - me@example.com - Outlook"]},{"app":"Google Chrome","windows":["YouTube - Google Chrome"]},{"app":"Excel","windows":["Budget 2026.xlsx - Excel"]}]""", result.Text);
        Assert.Equal("3 apps open", result.Activity);
        _desktop.Open.Clear();
        Assert.Equal("No app windows are open on the PC.", (await Call("pc_running_apps")).Text);
    }

    [Fact]
    public async Task The_desktop_is_shown()
    {
        Assert.Equal("Showing the desktop.", (await Call("pc_show_desktop")).Text);
        Assert.Contains("show desktop", _desktop.Calls);
    }

    // ───────────────────────── System

    [Fact]
    public async Task System_information_is_the_systems_paragraph() =>
        Assert.Equal("TEST-PC: Test OS 1.0. CPU 3%.", (await Call("pc_system_info")).Text);

    [Fact]
    public async Task Dark_mode_changes_only_when_it_needs_to()
    {
        Assert.Equal("Light mode is already on.", (await Call("pc_dark_mode", """{"on": false}""")).Text);
        Assert.Empty(_desktop.Calls);
        Assert.Equal("Dark mode is on.", (await Call("pc_dark_mode", """{"on": true}""")).Text);
        Assert.Contains("dark True", _desktop.Calls);
    }

    [Fact]
    public async Task Brightness_reads_sets_and_steps()
    {
        Assert.Equal("Screen brightness is 60%.", (await Call("pc_brightness")).Text);
        Assert.Equal("Screen brightness is now 30%.", (await Call("pc_brightness", """{"level": 30}""")).Text);
        var stepped = await Call("pc_brightness", """{"change": -20}""");
        Assert.Equal("Screen brightness is now 10%.", stepped.Text);
        Assert.Equal("Brightness 10%", stepped.Activity);
    }

    // ───────────────────────── Context

    [Fact]
    public async Task The_context_says_what_is_in_front_or_that_it_is_locked()
    {
        Assert.Equal("PC: Outlook is in front.", await _plugin.GetContextAsync(ToolCaller.Web, default));

        var locked = new TestPcControlPlugin(new FakePcDesktop { Locked = true });
        locked.Initialize(new FakePluginContext());
        Assert.Equal("PC: locked.", await locked.GetContextAsync(ToolCaller.Web, default));

        var bare = new FakePcDesktop();
        bare.Open.Clear();
        var empty = new TestPcControlPlugin(bare);
        empty.Initialize(new FakePluginContext());
        Assert.Null(await empty.GetContextAsync(ToolCaller.Web, default));
    }

    [Fact]
    public async Task The_context_is_cached_for_a_moment()
    {
        Assert.Equal("PC: Outlook is in front.", await _plugin.GetContextAsync(ToolCaller.Web, default));
        _desktop.Open.Insert(0, new AppWindow(9, "Spotify", 20, "spotify", "Spotify"));
        Assert.Equal("PC: Outlook is in front.", await _plugin.GetContextAsync(ToolCaller.Web, default));
    }
}
