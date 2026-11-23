using Pucksley.Core.Hosting;
using Pucksley.Core.Pucks;

namespace Pucksley.Core.Tests;

/// <summary>What the hosts (the Windows tray app, the Linux desktop app, the console) share.</summary>
public sealed class DesktopHostTests
{
    [Fact]
    public void Plugin_assemblies_that_are_not_there_are_left_out_and_reported()
    {
        var missing = new List<string>();
        var loaded = DesktopHost.LoadPlugins(["Pucksley.Core", "Pucksley.Plugins.DoesNotExist", "Pucksley.Sdk"], (name, _) => missing.Add(name)).ToList();
        Assert.Equal(new[] { "Pucksley.Core", "Pucksley.Sdk" }, loaded.Select(a => a.GetName().Name));
        Assert.Equal(new[] { "Pucksley.Plugins.DoesNotExist" }, missing);
    }

    [Fact]
    public void Every_host_ships_the_plugins_that_run_anywhere() =>
        Assert.Equal(new[] { "Pucksley.Plugins.Music", "Pucksley.Plugins.Home", "Pucksley.Plugins.Utility", "Pucksley.Plugins.Speech" }, DesktopHost.CommonPlugins);

    [Fact]
    public void With_no_pucks_the_status_says_so() => Assert.Equal("No pucks connected", DesktopHost.StatusText([]));

    [Fact]
    public void A_waiting_puck_is_announced_with_its_code()
    {
        var (title, text) = DesktopHost.PendingNotice(new PendingPuck("puck-1", "Kitchen puck", "417", "192.168.1.50", DateTimeOffset.UtcNow));
        Assert.Equal("A new puck wants to join", title);
        Assert.Equal("Kitchen puck (code 417). Click to approve it.", text);
    }

    [Fact]
    public void The_page_opens_when_asked_to() => Assert.True(DesktopHost.ShouldOpenPage(["--open"], startsAtLogin: true));

    [Fact]
    public void The_default_address_is_this_machine()
    {
        var url = new Uri(DesktopHost.DefaultLocalUrl);
        Assert.Equal("127.0.0.1", url.Host);
        Assert.True(url.Port > 0);
    }
}
