using Pucksley.Plugins.Memory;
using Pucksley.Sdk;
using Pucksley.TestSupport;

namespace Pucksley.Plugins.Tests;

public class MemoryPluginTests
{
    static Task<ToolResult> Call(IPlugin plugin, string tool, string json = "{}") =>
        FakePluginContext.CallToolAsync(plugin, tool, json, TestUtil.Kitchen);

    [Fact]
    public async Task Remembered_facts_are_in_the_instructions_until_forgotten()
    {
        var plugin = await TestUtil.StartAsync<MemoryPlugin>(new FakePluginContext());
        Assert.Null(plugin.Instructions);

        var remembered = await Call(plugin, "memory_remember", """{"fact":"The wifi password is on the fridge"}""");
        Assert.Equal("Remembered: The wifi password is on the fridge.", remembered.Text);
        Assert.Equal("Remembered", remembered.Activity);
        await Call(plugin, "memory_remember", """{"fact":"Sam is allergic to peanuts."}""");
        Assert.Equal("I already remember that: Sam is allergic to peanuts.", (await Call(plugin, "memory_remember", """{"fact":"sam is allergic to peanuts"}""")).Text);

        Assert.Equal(
            "Things the household asked you to remember:\n- The wifi password is on the fridge\n- Sam is allergic to peanuts.",
            plugin.Instructions);
        Assert.Equal("2 facts, 0 lists", plugin.Status.Message);

        var forgot = await Call(plugin, "memory_forget", """{"about":"wifi code"}""");
        Assert.Equal("Forgot: The wifi password is on the fridge.", forgot.Text);
        Assert.Equal("Things the household asked you to remember:\n- Sam is allergic to peanuts.", plugin.Instructions);

        Assert.Equal("I remember 1 thing:\n1. Sam is allergic to peanuts.", (await Call(plugin, "memory_list")).Text);
    }

    [Fact]
    public async Task Forgetting_something_unknown_says_what_is_remembered()
    {
        var plugin = await TestUtil.StartAsync<MemoryPlugin>(new FakePluginContext());
        await Call(plugin, "memory_remember", """{"fact":"The spare key is under the blue pot"}""");
        var result = await Call(plugin, "memory_forget", """{"about":"birthday"}""");
        Assert.True(result.IsError);
        Assert.Equal("I don't remember anything about 'birthday'. I remember: The spare key is under the blue pot.", result.Text);
    }

    [Fact]
    public async Task Forgetting_everything_asks_first()
    {
        var ctx = new FakePluginContext { ConfirmAnswer = false };
        var plugin = await TestUtil.StartAsync<MemoryPlugin>(ctx);
        await Call(plugin, "memory_remember", """{"fact":"one"}""");
        await Call(plugin, "memory_remember", """{"fact":"two"}""");

        Assert.Equal("OK, I haven't forgotten anything.", (await Call(plugin, "memory_forget", """{"about":"everything"}""")).Text);
        Assert.Equal("Forget all 2 things I remember?", Assert.Single(ctx.ConfirmQuestions));
        Assert.NotNull(plugin.Instructions);

        ctx.ConfirmAnswer = true;
        Assert.Equal("Forgot all 2 things I remembered.", (await Call(plugin, "memory_forget", """{"about":"everything"}""")).Text);
        Assert.Null(plugin.Instructions);
    }

    [Fact]
    public async Task Instructions_hold_the_latest_sixty_facts_each_capped()
    {
        var plugin = await TestUtil.StartAsync<MemoryPlugin>(new FakePluginContext());
        for (int i = 1; i <= 61; i++) await Call(plugin, "memory_remember", $$"""{"fact":"fact number {{i}}"}""");
        await Call(plugin, "memory_remember", $$"""{"fact":"{{new string('x', 300)}}"}""");

        var lines = plugin.Instructions!.Split('\n');
        Assert.Equal(61, lines.Length); // heading + 60
        Assert.Equal("- fact number 3", lines[1]);
        Assert.Equal(202, lines[^1].Length); // "- " + 199 characters + "…"
        Assert.EndsWith("…", lines[^1]);
    }

    [Fact]
    public async Task Lists_are_created_on_first_add_and_matched_loosely()
    {
        var plugin = await TestUtil.StartAsync<MemoryPlugin>(new FakePluginContext());

        var added = await Call(plugin, "list_add", """{"list":"shopping list","items":["milk","eggs"]}""");
        Assert.Equal("Added milk and eggs to the shopping list (2 items).", added.Text);
        Assert.Equal("Added 2 to shopping", added.Activity);

        var more = await Call(plugin, "list_add", """{"list":"the Shopping","items":["bread","Milk"]}""");
        Assert.Equal("Added bread to the shopping list (3 items). Already on it: Milk.", more.Text);

        await Call(plugin, "list_add", """{"list":"To-Do","items":"fix the gate, call the plumber"}""");
        Assert.Equal("Lists: shopping (3), to do (2).", (await Call(plugin, "lists")).Text);
        Assert.Equal("To do list (2): fix the gate, call the plumber.", (await Call(plugin, "list_show", """{"list":"todo list"}""")).Text);
        Assert.Equal("Shopping list (3): milk, eggs, bread.", (await Call(plugin, "list_show", """{"list":"groceries"}""")).Text);
    }

    [Fact]
    public async Task Removing_matches_items_loosely_and_reports_the_rest()
    {
        var plugin = await TestUtil.StartAsync<MemoryPlugin>(new FakePluginContext());
        await Call(plugin, "list_add", """{"list":"shopping","items":["a dozen eggs","almond milk","bread"]}""");

        var removed = await Call(plugin, "list_remove", """{"list":"shopping","items":["eggs","cheese"]}""");
        Assert.Equal("Removed a dozen eggs from the shopping list (2 left). Not on it: cheese.", removed.Text);
        Assert.Equal("Removed 1 from shopping", removed.Activity);

        var nothing = await Call(plugin, "list_remove", """{"list":"shopping","items":["caviar"]}""");
        Assert.True(nothing.IsError);
        Assert.Equal("Caviar isn't on the shopping list. It has: almond milk, bread.", nothing.Text);

        var noList = await Call(plugin, "list_show", """{"list":"packing"}""");
        Assert.True(noList.IsError);
        Assert.Equal("I can't find a 'packing' list. Lists: shopping.", noList.Text);
    }

    [Fact]
    public async Task Clearing_a_list_deletes_it_and_asks_first()
    {
        var plugin = await TestUtil.StartAsync<MemoryPlugin>(new FakePluginContext());
        Assert.True(plugin.GetTools().Single(t => t.Name == "list_clear").Confirm);
        Assert.Equal("There are no lists yet.", (await Call(plugin, "lists")).Text);

        await Call(plugin, "list_add", """{"list":"packing","items":["passport","charger"]}""");
        var cleared = await Call(plugin, "list_clear", """{"list":"packing list"}""");
        Assert.Equal("Cleared the packing list (2 items).", cleared.Text);
        Assert.Equal("There are no lists yet.", (await Call(plugin, "lists")).Text);
    }

    [Fact]
    public async Task Memory_survives_a_restart()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pucksley-tests", Guid.NewGuid().ToString("n"));
        var first = await TestUtil.StartAsync<MemoryPlugin>(new FakePluginContext(dir));
        await Call(first, "memory_remember", """{"fact":"Bins go out on Tuesday night"}""");
        await Call(first, "list_add", """{"list":"shopping","items":["coffee"]}""");
        await first.StopAsync();

        var second = new MemoryPlugin();
        second.Initialize(new FakePluginContext(dir));
        second.ApplySettings(new NoSettings());
        // Instructions are available straight away (they're read before the first turn, maybe before StartAsync).
        Assert.Equal("Things the household asked you to remember:\n- Bins go out on Tuesday night", second.Instructions);
        Assert.Equal("Shopping list (1): coffee.", (await Call(second, "list_show", """{"list":"shopping"}""")).Text);
        Assert.True(File.Exists(Path.Combine(dir, "memory.json")));
        Assert.Empty(Directory.GetFiles(dir, "*.tmp"));
    }

    [Fact]
    public void List_names_are_normalised()
    {
        Assert.Equal("shopping", MemoryPlugin.ListName("The Shopping List"));
        Assert.Equal("to do", MemoryPlugin.ListName("to-do list"));
        Assert.Equal("to do", MemoryPlugin.ListName("todo"));
        Assert.Equal("packing", MemoryPlugin.ListName("my packing lists"));
        Assert.Equal("list", MemoryPlugin.ListName("list"));
    }
}
