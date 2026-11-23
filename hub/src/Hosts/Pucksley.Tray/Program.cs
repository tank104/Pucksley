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
            DesktopHost.OpenPage(null);
            return;
        }

        WinForms.Application.SetHighDpiMode(WinForms.HighDpiMode.PerMonitorV2);
        WinForms.Application.EnableVisualStyles();

        WebApplication app;
        try
        {
            app = HubHost.Build(args, DesktopHost.LoadPlugins([.. DesktopHost.CommonPlugins, "Pucksley.Plugins.Windows"]));
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
        var open = new WinForms.ToolStripMenuItem("Open Pucksley", null, (_, _) => DesktopHost.OpenPage(url)) { Font = new System.Drawing.Font(WinForms.Control.DefaultFont, System.Drawing.FontStyle.Bold) };
        var startup = new WinForms.ToolStripMenuItem("Start with Windows", null, (s, _) =>
        {
            var item = (WinForms.ToolStripMenuItem)s!;
            SetStartWithWindows(!item.Checked);
            item.Checked = IsStartWithWindows();
        }) { Checked = IsStartWithWindows() };
        var logs = new WinForms.ToolStripMenuItem("Open data folder", null, (_, _) => DesktopHost.OpenWithSystem(HubPaths.DataDir));
        var quit = new WinForms.ToolStripMenuItem("Quit", null, (_, _) => WinForms.Application.Exit());
        menu.Items.AddRange([open, status, new WinForms.ToolStripSeparator(), startup, logs, new WinForms.ToolStripSeparator(), quit]);
        icon.ContextMenuStrip = menu;
        icon.DoubleClick += (_, _) => DesktopHost.OpenPage(url);

        var pucks = app.Services.GetRequiredService<PuckManager>();
        var waiting = new PendingPuckWatcher(pucks);
        var timer = new WinForms.Timer { Interval = 2000 };
        timer.Tick += (_, _) =>
        {
            status.Text = DesktopHost.StatusText(pucks.Sessions);
            icon.Text = $"Pucksley — {status.Text}"[..Math.Min(63, $"Pucksley — {status.Text}".Length)];
            if (waiting.Check() is { } puck)
            {
                var (title, text) = DesktopHost.PendingNotice(puck);
                icon.ShowBalloonTip(8000, title, text, WinForms.ToolTipIcon.Info);
            }
        };
        icon.BalloonTipClicked += (_, _) => DesktopHost.OpenPage(url);
        timer.Start();

        if (DesktopHost.ShouldOpenPage(args, IsStartWithWindows())) DesktopHost.OpenPage(url);

        WinForms.Application.Run();

        timer.Stop();
        icon.Visible = false;
        try { app.StopAsync().Wait(TimeSpan.FromSeconds(15)); } catch { }
    }

    static System.Drawing.Icon LoadIcon()
    {
        using var s = typeof(Program).Assembly.GetManifestResourceStream("Pucksley.Tray.puck.ico");
        return s is not null ? new System.Drawing.Icon(s) : System.Drawing.SystemIcons.Application;
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
