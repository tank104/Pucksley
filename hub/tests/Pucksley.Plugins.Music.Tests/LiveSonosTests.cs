using System.Reflection;
using System.Text.Json;
using Pucksley.Plugins.Sonos;
using Pucksley.Sdk;
using Pucksley.TestSupport;
using Xunit.Abstractions;

namespace Pucksley.Plugins.Music.Tests;

/// <summary>Runs only with PUCKSLEY_LIVE_SONOS=1: talks to the real speakers on this network.</summary>
public sealed class LiveFactAttribute : FactAttribute
{
    public LiveFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("PUCKSLEY_LIVE_SONOS") != "1")
            Skip = "Talks to real Sonos speakers (read-only). Set PUCKSLEY_LIVE_SONOS=1 to run.";
    }
}

/// <summary>
/// A smoke test against the household's real speakers. READ-ONLY on purpose: discovery, zone
/// groups, favourites and state queries only — nothing here plays, pauses, changes volume,
/// touches the queue or regroups.
/// </summary>
public sealed class LiveSonosTests(ITestOutputHelper output)
{
    [LiveFact]
    public async Task Discovers_and_reads_state_without_changing_anything()
    {
        var context = new FakePluginContext();
        var plugin = new SonosPlugin();
        plugin.Initialize(context);
        plugin.ApplySettings(new SonosSettings());

        var rooms = await FakePluginContext.CallToolAsync(plugin, "sonos_rooms");
        output.WriteLine("sonos_rooms:\n" + rooms.Text);
        Assert.False(rooms.IsError, rooms.Text);
        Assert.NotEmpty(plugin.RoomNames);

        var favourites = await FakePluginContext.CallToolAsync(plugin, "sonos_favorites");
        output.WriteLine("sonos_favorites: " + favourites.Text);
        Assert.False(favourites.IsError, favourites.Text);

        foreach (var room in plugin.RoomNames)
        {
            var now = await FakePluginContext.CallToolAsync(plugin, "sonos_now_playing", JsonSerializer.Serialize(new { room }));
            output.WriteLine("sonos_now_playing: " + now.Text);
            Assert.False(now.IsError, now.Text);
        }

        output.WriteLine("context: " + await plugin.GetContextAsync(ToolCaller.Web, default));
        foreach (var c in plugin.GetControls()) output.WriteLine($"control {c.Id} \"{c.Name}\" {c.Value}{c.Unit} step {c.Step}");
        foreach (var source in plugin.GetMediaSources())
            output.WriteLine($"source {source.Id} \"{source.Name}\": {await plugin.GetNowPlayingAsync(source.Id, default)}");

        // Which Spotify account Sonos would use (Browse FV:2 / Q:0 and ListAvailableServices: read-only).
        var account = typeof(SonosPlugin).GetMethod("GetSpotifyAccountAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var topology = (SonosTopology)typeof(SonosPlugin).GetField("_topology", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(plugin)!;
        var task = (Task)account.Invoke(plugin, [topology.Players[0], false, CancellationToken.None])!;
        try
        {
            await task;
            output.WriteLine("Spotify on Sonos (sid, sn): " + task.GetType().GetProperty("Result")!.GetValue(task));
        }
        catch (PluginException e)
        {
            output.WriteLine("Spotify on Sonos: " + e.Message);
        }
        output.WriteLine("status: " + plugin.Status.Message);
    }
}
