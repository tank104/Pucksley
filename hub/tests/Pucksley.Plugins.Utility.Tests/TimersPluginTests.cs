using System.Text.Json;
using System.Text.Json.Nodes;
using Pucksley.Plugins.Timers;
using Pucksley.Sdk;
using Pucksley.TestSupport;

namespace Pucksley.Plugins.Tests;

public class TimersPluginTests
{
    static Task<ToolResult> Call(IPlugin plugin, string tool, string json = "{}", ToolCaller? caller = null) =>
        FakePluginContext.CallToolAsync(plugin, tool, json, caller ?? TestUtil.Kitchen);

    [Fact]
    public async Task Timer_set_list_context_and_cancel()
    {
        var ctx = new PuckRecordingContext(new FakePluginContext());
        var plugin = await TestUtil.StartAsync<TimersPlugin>(ctx);
        try
        {
            var set = await Call(plugin, "timer_set", """{"minutes":10,"label":"the pasta timer"}""");
            Assert.False(set.IsError, set.Text);
            Assert.StartsWith("Pasta timer set for 10 min; it ends at ", set.Text);
            Assert.Equal("Pasta 10 min", set.Activity);

            var shown = Assert.Single(ctx.Shown);
            Assert.Equal(("pasta", "puck-kitchen"), (shown.Label, shown.PuckId));
            Assert.InRange(shown.EndsAt!.Value - ctx.Now, TimeSpan.FromMinutes(9.9), TimeSpan.FromMinutes(10));

            var list = await Call(plugin, "timer_list");
            Assert.Matches(@"^1 timer: pasta timer, (9 min \d+ s|10 min) left \(t\d+\)\.$", list.Text);

            Assert.Equal("Timer: pasta 10 min left.", await plugin.GetContextAsync(ToolCaller.Web, default));
            Assert.Equal("1 timer", plugin.Status.Message);

            var cancel = await Call(plugin, "timer_cancel");
            Assert.Equal("Cancelled the pasta timer.", cancel.Text);
            Assert.Null(ctx.Shown.Last().EndsAt);
            Assert.Null(await plugin.GetContextAsync(ToolCaller.Web, default));
            Assert.Equal("No timers running.", (await Call(plugin, "timer_list")).Text);
        }
        finally
        {
            await plugin.StopAsync();
        }
    }

    [Fact]
    public async Task Timer_goes_off_on_the_puck_that_set_it()
    {
        var ctx = new PuckRecordingContext(new FakePluginContext());
        var plugin = await TestUtil.StartAsync<TimersPlugin>(ctx);
        try
        {
            await Call(plugin, "timer_set", """{"seconds":1,"label":"eggs"}""");
            await TestUtil.WaitUntil(() => ctx.Said.Any(), TimeSpan.FromSeconds(5), "the timer to go off");

            Assert.Equal(("Your eggs timer is done.", "puck-kitchen"), Assert.Single(ctx.Said));
            var banner = Assert.Single(ctx.Notified);
            Assert.Equal(("Eggs timer done", NotifyKind.Alert, true, "puck-kitchen"), banner);
            Assert.Null(ctx.Shown.Last().EndsAt); // taken off the puck's Timer face
            Assert.Equal("No timers running.", (await Call(plugin, "timer_list")).Text);
        }
        finally
        {
            await plugin.StopAsync();
        }
    }

    [Fact]
    public async Task Unlabelled_timer_is_named_by_its_length()
    {
        var ctx = new FakePluginContext();
        var plugin = await TestUtil.StartAsync<TimersPlugin>(ctx);
        try
        {
            var set = await Call(plugin, "timer_set", """{"seconds":1}""");
            Assert.StartsWith("Timer set for 1 s;", set.Text);
            await TestUtil.WaitUntil(() => !ctx.Spoken.IsEmpty, TimeSpan.FromSeconds(5), "the timer to go off");
            Assert.Equal("Your 1 second timer is done.", ctx.Spoken.Single());
            Assert.Equal("1 s timer done", ctx.Notifications.Single());
        }
        finally
        {
            await plugin.StopAsync();
        }
    }

    [Fact]
    public async Task Add_time_and_cancel_by_label_or_all()
    {
        var ctx = new FakePluginContext();
        var plugin = await TestUtil.StartAsync<TimersPlugin>(ctx);
        try
        {
            await Call(plugin, "timer_set", """{"minutes":5,"label":"rice"}""");
            await Call(plugin, "timer_set", """{"minutes":20,"label":"roast"}""");

            var added = await Call(plugin, "timer_add_time", """{"which":"rice","minutes":2}""");
            Assert.Matches(@"^Rice timer: added 2 min, (6 min \d+ s|7 min) left\.$", added.Text);
            Assert.Equal("Rice +2 min", added.Activity);

            var missing = await Call(plugin, "timer_cancel", """{"which":"pizza"}""");
            Assert.True(missing.IsError);
            Assert.Contains("rice timer", missing.Text);
            Assert.Contains("roast timer", missing.Text);

            Assert.Equal("Cancelled the roast timer.", (await Call(plugin, "timer_cancel", """{"which":"the roast"}""")).Text);
            await Call(plugin, "timer_set", """{"minutes":3}""");
            Assert.Equal("Cancelled all 2 timers.", (await Call(plugin, "timer_cancel", """{"which":"all"}""")).Text);
        }
        finally
        {
            await plugin.StopAsync();
        }
    }

    [Fact]
    public async Task Alarm_set_list_context_and_cancel()
    {
        var ctx = new FakePluginContext();
        var plugin = await TestUtil.StartAsync<TimersPlugin>(ctx);
        try
        {
            var set = await Call(plugin, "alarm_set", """{"time":"7:30","days":"weekdays","label":"wake up"}""");
            Assert.False(set.IsError, set.Text);
            Assert.StartsWith("Alarm \"wake up\" set for 07:30 weekdays; the next one is at 07:30 ", set.Text);
            Assert.Equal("Alarm 07:30", set.Activity);

            var again = await Call(plugin, "alarm_set", """{"time":"07:30","days":["mon","tue","wed","thu","fri"],"label":"Wake up"}""");
            Assert.StartsWith("There's already an alarm for 07:30 weekdays", again.Text);

            Assert.StartsWith("1 alarm: 07:30 weekdays \"wake up\" (next 07:30 ", (await Call(plugin, "alarm_list")).Text);
            Assert.Equal("Alarm: 07:30 weekdays.", await plugin.GetContextAsync(ToolCaller.Web, default));

            var cancel = await Call(plugin, "alarm_cancel", """{"which":"7:30 alarm"}""");
            Assert.Equal("Cancelled the 07:30 weekdays \"wake up\" alarm.", cancel.Text);
            Assert.Equal("No alarms set.", (await Call(plugin, "alarm_list")).Text);
        }
        finally
        {
            await plugin.StopAsync();
        }
    }

    [Fact]
    public async Task One_off_alarm_rings_with_its_time()
    {
        var ctx = new PuckRecordingContext(new FakePluginContext());
        var plugin = await TestUtil.StartAsync<TimersPlugin>(ctx);
        try
        {
            // An alarm for the next minute, then pretend it's due by editing its due time through a restart.
            var at = ctx.Now.AddMinutes(1);
            var hhmm = at.ToString("HH:mm");
            var set = await Call(plugin, "alarm_set", $$"""{"time":"{{hhmm}}"}""");
            Assert.StartsWith($"Alarm set for {hhmm} ", set.Text);
            await plugin.StopAsync();

            var path = Path.Combine(ctx.DataDirectory, "timers.json");
            var state = JsonNode.Parse(File.ReadAllText(path))!;
            state["items"]![0]!["dueAt"] = DateTimeOffset.Now.AddSeconds(-30).ToString("o");
            File.WriteAllText(path, state.ToJsonString());

            plugin = await TestUtil.StartAsync<TimersPlugin>(ctx);
            await TestUtil.WaitUntil(() => !ctx.Said.IsEmpty, TimeSpan.FromSeconds(5), "the alarm");
            var spoken = TimeMath.SpokenClock(TimeOnly.FromDateTime(at.DateTime));
            Assert.Equal(($"It's {spoken} — your alarm.", "puck-kitchen"), ctx.Said.Single());
            Assert.Equal($"Alarm {hhmm}", ctx.Notified.Single().Text);
            Assert.Equal("No alarms set.", (await Call(plugin, "alarm_list")).Text); // one-off: gone after ringing
        }
        finally
        {
            await plugin.StopAsync();
        }
    }

    [Fact]
    public async Task Reminder_goes_off_and_speaks()
    {
        var ctx = new PuckRecordingContext(new FakePluginContext());
        var plugin = await TestUtil.StartAsync<TimersPlugin>(ctx);
        try
        {
            var set = await Call(plugin, "reminder_set", """{"text":"to call Mum","in_minutes":0.02}""");
            Assert.False(set.IsError, set.Text);
            Assert.Matches(@"^I'll remind you at \d\d:\d\d (today|tomorrow) \(in 1 s\): call Mum\.$", set.Text);

            await TestUtil.WaitUntil(() => !ctx.Said.IsEmpty, TimeSpan.FromSeconds(5), "the reminder");
            Assert.Equal(("Reminder: call Mum.", "puck-kitchen"), ctx.Said.Single());
            Assert.Equal(("Reminder: call Mum", NotifyKind.Alert, true, "puck-kitchen"), ctx.Notified.Single());
        }
        finally
        {
            await plugin.StopAsync();
        }
    }

    [Fact]
    public async Task Reminders_list_and_cancel_by_words()
    {
        var ctx = new FakePluginContext();
        var plugin = await TestUtil.StartAsync<TimersPlugin>(ctx);
        try
        {
            await Call(plugin, "reminder_set", """{"text":"put the bins out","in_minutes":60}""");
            await Call(plugin, "reminder_set", """{"text":"call Mum","in_minutes":30}""");
            var list = await Call(plugin, "reminder_list");
            Assert.Matches(@"^2 reminders: call Mum at .+ \(r\d+\); put the bins out at .+ \(r\d+\)\.$", list.Text);

            var which = await Call(plugin, "reminder_cancel");
            Assert.True(which.IsError);
            Assert.StartsWith("Which reminder?", which.Text);

            Assert.Equal("Cancelled the reminder: put the bins out.", (await Call(plugin, "reminder_cancel", """{"which":"bins"}""")).Text);
            Assert.Equal("Cancelled the reminder: call Mum.", (await Call(plugin, "reminder_cancel")).Text);
        }
        finally
        {
            await plugin.StopAsync();
        }
    }

    [Fact]
    public async Task Everything_survives_a_restart()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pucksley-tests", Guid.NewGuid().ToString("n"));
        var first = await TestUtil.StartAsync<TimersPlugin>(new FakePluginContext(dir));
        await Call(first, "timer_set", """{"minutes":10,"label":"pasta"}""");
        await Call(first, "alarm_set", """{"time":"07:30","days":"weekdays"}""");
        await Call(first, "reminder_set", """{"text":"bins","in_minutes":60}""");
        await first.StopAsync();

        var ctx = new FakePluginContext(dir);
        var second = await TestUtil.StartAsync<TimersPlugin>(ctx);
        try
        {
            Assert.Contains("pasta timer", (await Call(second, "timer_list")).Text);
            Assert.Contains("07:30 weekdays", (await Call(second, "alarm_list")).Text);
            Assert.Contains("bins at", (await Call(second, "reminder_list")).Text);
            Assert.Equal("pasta", Assert.Single(ctx.Timers).Value.Label); // shown on the pucks again
            Assert.Equal("1 timer, 1 alarm, 1 reminder", second.Status.Message);

            // Ids keep counting rather than being reused.
            await Call(second, "timer_set", """{"minutes":1,"label":"tea"}""");
            Assert.Contains("(t4)", (await Call(second, "timer_list")).Text);
            Assert.Empty(Directory.GetFiles(dir, "*.tmp")); // atomic writes leave nothing behind
        }
        finally
        {
            await second.StopAsync();
        }
    }

    [Fact]
    public async Task On_start_recent_overdue_items_fire_and_old_ones_are_dropped()
    {
        var ctx = new FakePluginContext();
        var now = DateTimeOffset.Now;
        var state = new
        {
            nextId = 5,
            items = new object[]
            {
                new { id = "t1", kind = "timer", label = "recent", dueAt = now.AddMinutes(-5), createdAt = now.AddMinutes(-15), duration = "00:10:00" },
                new { id = "r2", kind = "reminder", label = "old one", dueAt = now.AddMinutes(-30), createdAt = now.AddHours(-1) },
                new { id = "a3", kind = "alarm", time = "07:30", days = new[] { "monday", "tuesday", "wednesday", "thursday", "friday", "saturday", "sunday" }, dueAt = now.AddHours(-2), createdAt = now.AddDays(-3) },
                new { id = "a4", kind = "alarm", time = "06:00", dueAt = now.AddHours(-1), createdAt = now.AddDays(-1) },
            },
        };
        File.WriteAllText(Path.Combine(ctx.DataDirectory, "timers.json"), JsonSerializer.Serialize(state));

        var plugin = await TestUtil.StartAsync<TimersPlugin>(ctx);
        try
        {
            await TestUtil.WaitUntil(() => !ctx.Spoken.IsEmpty, TimeSpan.FromSeconds(5), "the late timer");
            await Task.Delay(300);
            Assert.Equal("Your recent timer is done.", Assert.Single(ctx.Spoken)); // the 30-minute-old reminder didn't fire

            Assert.Equal("No reminders set.", (await Call(plugin, "reminder_list")).Text);
            var alarms = (await Call(plugin, "alarm_list")).Text;
            Assert.StartsWith("1 alarm: 07:30 daily (next 07:30 ", alarms); // the repeating alarm moved on; the one-off was dropped
            Assert.DoesNotContain("a4", alarms);
        }
        finally
        {
            await plugin.StopAsync();
        }
    }

    [Fact]
    public async Task Puck_timer_face_start_cancel_and_active_timers()
    {
        var ctx = new PuckRecordingContext(new FakePluginContext());
        var plugin = await TestUtil.StartAsync<TimersPlugin>(ctx);
        try
        {
            await plugin.StartFromPuckAsync(TimeSpan.FromMinutes(3), "tea", TestUtil.Kitchen, default);
            var active = Assert.Single(plugin.ActiveTimers);
            Assert.Equal(("tea", "puck-kitchen"), (active.Label, active.PuckId));
            Assert.InRange(active.EndsAt - ctx.Now, TimeSpan.FromMinutes(2.9), TimeSpan.FromMinutes(3));

            await plugin.CancelFromPuckAsync(active.Id, default);
            Assert.Empty(plugin.ActiveTimers);
            Assert.Equal((active.Id, (DateTimeOffset?)null), (ctx.Shown.Last().Id, ctx.Shown.Last().EndsAt));
        }
        finally
        {
            await plugin.StopAsync();
        }
    }

    [Fact]
    public async Task Bad_requests_get_helpful_errors()
    {
        var plugin = await TestUtil.StartAsync<TimersPlugin>(new FakePluginContext());
        try
        {
            Assert.StartsWith("How long", (await Call(plugin, "timer_set")).Text);
            Assert.StartsWith("Timers can be up to 24 hours", (await Call(plugin, "timer_set", """{"minutes":2000}""")).Text);
            Assert.Contains("didn't understand the time '25:99'", (await Call(plugin, "alarm_set", """{"time":"25:99"}""")).Text);
            Assert.Contains("'funday'", (await Call(plugin, "alarm_set", """{"time":"07:00","days":["funday"]}""")).Text);
            Assert.StartsWith("When?", (await Call(plugin, "reminder_set", """{"text":"x"}""")).Text);
            Assert.Equal("There are no timers running.", (await Call(plugin, "timer_cancel")).Text);
            Assert.True((await Call(plugin, "timer_add_time", """{"minutes":2}""")).IsError);
        }
        finally
        {
            await plugin.StopAsync();
        }
    }

    [Fact]
    public void Tools_are_named_and_described()
    {
        var plugin = new TimersPlugin();
        plugin.Initialize(new FakePluginContext());
        var names = plugin.GetTools().Select(t => t.Name).ToList();
        Assert.Equal(
            ["timer_set", "timer_list", "timer_cancel", "timer_add_time", "alarm_set", "alarm_list", "alarm_cancel", "reminder_set", "reminder_list", "reminder_cancel"],
            names);
        var reminder = plugin.GetTools().Single(t => t.Name == "reminder_set").InputSchema;
        Assert.Equal("""["text"]""", reminder["required"]!.ToJsonString());
        Assert.NotNull(reminder["properties"]!["in_minutes"]);
    }
}
