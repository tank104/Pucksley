using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Pucksley.Core.Hosting;
using Pucksley.Core.Pucks;
using Pucksley.Core.Settings;
using Pucksley.Linux.Tray;
using Pucksley.Plugins.Linux.Common;
using Pucksley.Sdk;

namespace Pucksley.Linux;

/// <summary>
/// Pucksley on a Linux desktop: runs the hub in the background with an icon in the system tray.
/// A click opens the hub's page; the menu shows which pucks are connected. The Linux twin of the
/// Windows tray app, down to the plugins that control this PC.
/// </summary>
static class Program
{
    const string Usage = """
        Pucksley hub for the Linux desktop.

          pucksley                  Run the hub, with an icon in the system tray
          pucksley --open           …and open its page in the browser
          pucksley --no-tray        Run without the tray icon
          pucksley --install        Add Pucksley to the app launcher (run again after moving it)
          pucksley --uninstall      Remove it from the launcher and from the login (settings stay)
          pucksley --autostart on   Start with the desktop session (off to stop)

        Settings live in ~/.config/pucksley (or PUCKSLEY_DATA); the port is 8765 (or PUCKSLEY_PORT).
        """;

    // Read here, and not passed on to the hub's own configuration
    static readonly string[] OwnFlags = ["--open", "--no-tray"];

    static async Task<int> Main(string[] args)
    {
        if (args.Contains("--help") || args.Contains("-h"))
        {
            Console.WriteLine(Usage);
            return 0;
        }
        if (args.Contains("--version"))
        {
            Console.WriteLine(typeof(HubHost).Assembly.GetName().Version?.ToString(3));
            return 0;
        }
        if (Setup(args) is int done) return done;

        // One hub per person: a second launch just opens the page
        using var instance = SingleInstance.TryAcquire();
        if (instance is null)
        {
            await OpenAsync(SingleInstance.RunningUrl() ?? DesktopHost.DefaultLocalUrl).ConfigureAwait(false);
            return 0;
        }

        WebApplication app;
        try
        {
            var plugins = DesktopHost.LoadPlugins([.. DesktopHost.CommonPlugins, "Pucksley.Plugins.Linux"],
                (name, e) => Console.Error.WriteLine($"Plugin assembly {name} not found: {e.Message}"));
            app = HubHost.Build(args.Where(a => !OwnFlags.Contains(a)).ToArray(), plugins);
            await app.StartAsync().ConfigureAwait(false);
        }
        catch (Exception e)
        {
            string why = $"{e.Message} Is another copy (or another program) using its port?";
            Console.Error.WriteLine($"Pucksley couldn't start: {why}");
            await DesktopNotifications.ShowAsync("Pucksley couldn't start", why, critical: true).ConfigureAwait(false);
            return 1;
        }

        string url = HubHost.LocalUrl(app);
        instance.Publish(url);
        var log = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Tray");
        var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
        var pucks = app.Services.GetRequiredService<PuckManager>();

        var tray = new TrayIcon("pucksley", "Pucksley", log, PuckIcon.Argb) { Activated = () => _ = OpenAsync(url) };
        tray.Menu.Add(new TrayMenuItem { Label = "Open Pucksley", Clicked = () => _ = OpenAsync(url) });
        var status = tray.Menu.Add(new TrayMenuItem { Label = "Starting…", Enabled = false });
        tray.Menu.AddSeparator();
        TrayMenuItem startup = null!;
        startup = tray.Menu.Add(new TrayMenuItem
        {
            Label = "Start at login",
            Checked = DesktopIntegration.StartsAtLogin,
            Clicked = () =>
            {
                try
                {
                    DesktopIntegration.SetStartAtLogin(!DesktopIntegration.StartsAtLogin);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    log.LogWarning("Couldn't change the start at login: {Message}", e.Message);
                }
                startup.Checked = DesktopIntegration.StartsAtLogin;
                tray.MenuItemChanged(startup);
            },
        });
        tray.Menu.Add(new TrayMenuItem { Label = "Open data folder", Clicked = () => _ = OpenAsync(HubPaths.DataDir) });
        tray.Menu.AddSeparator();
        tray.Menu.Add(new TrayMenuItem { Label = "Quit", Clicked = lifetime.StopApplication });

        var stopping = lifetime.ApplicationStopping;
        var showing = args.Contains("--no-tray") ? Task.CompletedTask : tray.RunAsync(stopping);
        var watching = WatchPucksAsync(pucks, tray, status, url, stopping);

        if (DesktopHost.ShouldOpenPage(args, DesktopIntegration.StartsAtLogin)) await OpenAsync(url).ConfigureAwait(false);

        await app.WaitForShutdownAsync().ConfigureAwait(false);
        try { await Task.WhenAll(showing, watching).WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
        catch (Exception) { /* they end with the hub */ }
        return 0;
    }

    /// <summary>The one-off commands (--install, --uninstall, --autostart): the exit code when one was given.</summary>
    static int? Setup(string[] args)
    {
        try
        {
            if (args.Contains("--install"))
            {
                foreach (var file in DesktopIntegration.Install()) Console.WriteLine($"wrote {file}");
                Console.WriteLine("Pucksley is in the app launcher. \"pucksley --autostart on\" starts it with the desktop.");
                return 0;
            }
            if (args.Contains("--uninstall"))
            {
                foreach (var file in DesktopIntegration.Uninstall()) Console.WriteLine($"removed {file}");
                Console.WriteLine($"Pucksley is out of the launcher and the login. Its settings are still in {HubPaths.DataDir}.");
                return 0;
            }
            int flag = Array.IndexOf(args, "--autostart");
            if (flag >= 0)
            {
                string? choice = flag + 1 < args.Length ? args[flag + 1].ToLowerInvariant() : null;
                if (choice is not ("on" or "off"))
                {
                    Console.Error.WriteLine("Say which: pucksley --autostart on, or pucksley --autostart off.");
                    return 2;
                }
                DesktopIntegration.SetStartAtLogin(choice == "on");
                Console.WriteLine(choice == "on" ? $"Pucksley starts with the desktop ({DesktopIntegration.AutostartFile})." : "Pucksley no longer starts with the desktop.");
                return 0;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"That didn't work: {e.Message}");
            return 1;
        }
        return null;
    }

    /// <summary>
    /// Every couple of seconds: says in the menu and the tooltip who's connected, and announces a
    /// puck that's waiting to be approved (a click on the notification opens the page).
    /// </summary>
    static async Task WatchPucksAsync(PuckManager pucks, TrayIcon tray, TrayMenuItem status, string url, CancellationToken stop)
    {
        var waiting = new PendingPuckWatcher(pucks);
        uint announced = 0;
        using var clicks = await DesktopNotifications.WatchActionsAsync((id, action) =>
        {
            if (id != 0 && id == Volatile.Read(ref announced)) _ = OpenAsync(url);
        }, stop).ConfigureAwait(false);

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        try
        {
            do
            {
                string text = DesktopHost.StatusText(pucks.Sessions);
                if (status.Label != text)
                {
                    status.Label = text;
                    tray.MenuItemChanged(status);
                    tray.SetToolTip($"Pucksley — {text}");
                }
                if (waiting.Check() is { } puck)
                {
                    var (title, body) = DesktopHost.PendingNotice(puck);
                    uint id = await DesktopNotifications.ShowAsync(title, body, timeoutMs: 8000, actions: [("default", "Open Pucksley")], cancel: stop).ConfigureAwait(false);
                    Volatile.Write(ref announced, id);
                }
            }
            while (await timer.WaitForNextTickAsync(stop).ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            // the hub is stopping
        }
    }

    /// <summary>
    /// Opens the hub's page (or a folder) with the desktop's own app for it, started free of the
    /// hub: a browser opened by a hub that was started at login would otherwise close with it.
    /// </summary>
    static async Task OpenAsync(string target)
    {
        try
        {
            await Detached.StartAsync(["xdg-open", target], TimeSpan.FromSeconds(2), CancellationToken.None, ownScope: true, scopeName: "open").ConfigureAwait(false);
        }
        catch (PluginException)
        {
            DesktopHost.OpenWithSystem(target); // no xdg-open: whatever .NET can find
        }
    }
}
