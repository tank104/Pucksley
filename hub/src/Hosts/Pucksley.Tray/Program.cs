using System.Diagnostics;
using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;
using Pucksley.Core.Hosting;
using Pucksley.Core.Pucks;
using Pucksley.Core.Settings;
using WinForms = System.Windows.Forms;

namespace Pucksley.Tray;

/// <summary>
/// Pucksley on Windows: runs the hub in the background with a tray icon. Double-click opens the
/// hub's page; the menu shows which pucks are connected.
/// </summary>
static class Program
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string RunValue = "Pucksley";

    [STAThread]
    static void Main(string[] args)
    {
        // One hub per user: a second launch just opens the page
        using var single = new Mutex(true, @"Local\Pucksley.Hub", out bool first);
        if (!first)
        {
            OpenPage(null);
            return;
        }

        WinForms.Application.SetHighDpiMode(WinForms.HighDpiMode.PerMonitorV2);
        WinForms.Application.EnableVisualStyles();

        WebApplication app;
        try
        {
            app = HubHost.Build(args, LoadPlugins());
            app.StartAsync().GetAwaiter().GetResult();
        }
        catch (Exception e)
        {
            WinForms.MessageBox.Show($"Pucksley couldn't start:\n\n{e.Message}\n\nIs another copy (or another program) using its port?",
                "Pucksley", WinForms.MessageBoxButtons.OK, WinForms.MessageBoxIcon.Error);
            return;
        }

        var url = HubHost.LocalUrl(app);
        using var icon = new WinForms.NotifyIcon
        {
            Icon = LoadIcon(),
            Text = "Pucksley",
            Visible = true,
        };
        var menu = new WinForms.ContextMenuStrip();
        var status = new WinForms.ToolStripMenuItem("Starting…") { Enabled = false };
        var open = new WinForms.ToolStripMenuItem("Open Pucksley", null, (_, _) => OpenPage(url)) { Font = new System.Drawing.Font(WinForms.Control.DefaultFont, System.Drawing.FontStyle.Bold) };
        var startup = new WinForms.ToolStripMenuItem("Start with Windows", null, (s, _) =>
        {
            var item = (WinForms.ToolStripMenuItem)s!;
            SetStartWithWindows(!item.Checked);
            item.Checked = IsStartWithWindows();
        }) { Checked = IsStartWithWindows() };
        var logs = new WinForms.ToolStripMenuItem("Open data folder", null, (_, _) => Process.Start(new ProcessStartInfo(HubPaths.DataDir) { UseShellExecute = true }));
        var quit = new WinForms.ToolStripMenuItem("Quit", null, (_, _) => WinForms.Application.Exit());
        menu.Items.AddRange([open, status, new WinForms.ToolStripSeparator(), startup, logs, new WinForms.ToolStripSeparator(), quit]);
        icon.ContextMenuStrip = menu;
        icon.DoubleClick += (_, _) => OpenPage(url);

        var pucks = app.Services.GetRequiredService<PuckManager>();
        var timer = new WinForms.Timer { Interval = 2000 };
        string lastPending = "";
        timer.Tick += (_, _) =>
        {
            var connected = pucks.Sessions;
            status.Text = connected.Count switch
            {
                0 => "No pucks connected",
                1 => $"Connected: {connected[0].Name}",
                _ => $"{connected.Count} pucks connected",
            };
            icon.Text = $"Pucksley — {status.Text}"[..Math.Min(63, $"Pucksley — {status.Text}".Length)];
            var pending = string.Join(",", pucks.Pending.Select(p => p.Id));
            if (pending.Length > 0 && pending != lastPending)
            {
                var p = pucks.Pending[0];
                icon.ShowBalloonTip(8000, "A new puck wants to join", $"{p.Name} (code {p.Code}). Click to approve it.", WinForms.ToolTipIcon.Info);
            }
            lastPending = pending;
        };
        icon.BalloonTipClicked += (_, _) => OpenPage(url);
        timer.Start();

        if (args.Contains("--open") || !IsStartWithWindows() && Environment.GetEnvironmentVariable("PUCKSLEY_NO_BROWSER") != "1" && FirstRun())
            OpenPage(url);

        WinForms.Application.Run();

        timer.Stop();
        icon.Visible = false;
        try { app.StopAsync().Wait(TimeSpan.FromSeconds(15)); } catch { }
    }

    static IEnumerable<Assembly> LoadPlugins()
    {
        foreach (var name in new[] { "Pucksley.Plugins.Music", "Pucksley.Plugins.Home", "Pucksley.Plugins.Utility", "Pucksley.Plugins.Speech", "Pucksley.Plugins.Windows" })
        {
            Assembly? a = null;
            try { a = Assembly.Load(name); }
            catch (Exception e) when (e is FileNotFoundException or FileLoadException or BadImageFormatException) { }
            if (a is not null) yield return a;
        }
    }

    static System.Drawing.Icon LoadIcon()
    {
        using var s = typeof(Program).Assembly.GetManifestResourceStream("Pucksley.Tray.puck.ico");
        return s is not null ? new System.Drawing.Icon(s) : System.Drawing.SystemIcons.Application;
    }

    static void OpenPage(string? url)
    {
        url ??= "http://127.0.0.1:8765/";
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }

    /// <summary>True once: the very first time the hub runs on this machine.</summary>
    static bool FirstRun()
    {
        var marker = Path.Combine(HubPaths.DataDir, ".opened");
        if (File.Exists(marker)) return false;
        try { Directory.CreateDirectory(HubPaths.DataDir); File.WriteAllText(marker, DateTime.Now.ToString("O")); } catch { }
        return true;
    }

    static bool IsStartWithWindows()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(RunValue) is string;
    }

    static void SetStartWithWindows(bool on)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (on) key.SetValue(RunValue, $"\"{Environment.ProcessPath}\"");
        else key.DeleteValue(RunValue, false);
    }
}
