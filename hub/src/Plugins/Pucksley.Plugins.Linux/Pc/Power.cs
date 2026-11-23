using Microsoft.Extensions.Logging;
using Pucksley.Plugins.Linux.Common;
using Pucksley.Plugins.Linux.Pc.Windows;
using Pucksley.Sdk;
using Tmds.DBus.Protocol;

namespace Pucksley.Plugins.Linux.Pc;

/// <summary>
/// Lock, sleep, shut down and restart. Linux desktops each have their own screen locker and their
/// own tidy way to end a session, so each step tries the desktop's way first and falls back to
/// what every systemd machine has (logind).
/// </summary>
internal sealed class Power(DesktopSession session, IWindowManager windows)
{
    const string Login1 = "org.freedesktop.login1";
    static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    static readonly string[] Lockers = ["hyprlock", "swaylock", "waylock", "gtklock", "i3lock", "xsecurelock", "slock", "xscreensaver-lock", "kscreenlocker_greet", "physlock"];

    readonly ShutdownCountdown _countdown = new();

    // ───────────────────────── Locked?

    /// <summary>
    /// True when the screen is locked. Asked three ways, because no single one covers every desktop:
    /// the compositor (Hyprland), logind's flag (GNOME and KDE set it), and whether a locker program
    /// is running (swaylock, hyprlock, i3lock).
    /// </summary>
    public async Task<bool> IsLockedAsync(CancellationToken cancel)
    {
        try
        {
            if (await windows.IsLockedAsync(cancel).ConfigureAwait(false) == true) return true;
        }
        catch (PluginException)
        {
            // the compositor didn't answer: the other two still can
        }
        if (await LockedHintAsync(cancel).ConfigureAwait(false)) return true;
        return LockerRunning();
    }

    static async Task<bool> LockedHintAsync(CancellationToken cancel)
    {
        if (!Bus.SystemWide.Exists) return false;
        try
        {
            var hint = await Bus.SystemWide.GetAsync(Login1, await SessionPathAsync(cancel).ConfigureAwait(false), "org.freedesktop.login1.Session", "LockedHint", Timeout, cancel).ConfigureAwait(false);
            return hint.Type == VariantValueType.Bool && hint.GetBool();
        }
        catch (Exception e) when (e is DBusExceptionBase or PluginException)
        {
            return false;
        }
    }

    /// <summary>
    /// This login session at logind: "auto" when the hub runs inside it, otherwise the person's
    /// graphical session (a hub started as a background service isn't in any session itself).
    /// </summary>
    static async Task<string> SessionPathAsync(CancellationToken cancel)
    {
        try
        {
            await Bus.SystemWide.GetAsync(Login1, "/org/freedesktop/login1/session/auto", "org.freedesktop.login1.Session", "Id", Timeout, cancel).ConfigureAwait(false);
            return "/org/freedesktop/login1/session/auto";
        }
        catch (DBusExceptionBase)
        {
            var display = await Bus.SystemWide.GetAsync(Login1, "/org/freedesktop/login1/user/self", "org.freedesktop.login1.User", "Display", Timeout, cancel).ConfigureAwait(false);
            // (session id, object path)
            return display.Type == VariantValueType.Struct && display.Count == 2 ? display.GetItem(1).GetObjectPathAsString() : "/org/freedesktop/login1/session/auto";
        }
    }

    static bool LockerRunning()
    {
        try
        {
            foreach (var dir in Directory.EnumerateDirectories("/proc"))
            {
                if (!char.IsAsciiDigit(Path.GetFileName(dir)[0])) continue;
                try
                {
                    if (Lockers.Contains(File.ReadAllText(Path.Combine(dir, "comm")).Trim())) return true;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    // the process ended, or isn't ours to look at
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // no /proc
        }
        return false;
    }

    // ───────────────────────── Lock

    public async Task LockAsync(CancellationToken cancel)
    {
        foreach (var attempt in LockPlan(session, Programs.Has))
        {
            try
            {
                if (attempt.Program is { } program)
                {
                    // A locker stays running while the screen is locked; a command that asks someone else to lock ends at once
                    int? exit = await Detached.StartAsync([program, .. attempt.Arguments], TimeSpan.FromMilliseconds(600), cancel).ConfigureAwait(false);
                    if (exit is null or 0) return;
                }
                else
                {
                    await Bus.Session.CallAsync(attempt.Service!, attempt.Path!, attempt.Interface!, "Lock", timeout: Timeout, cancel: cancel).ConfigureAwait(false);
                    return;
                }
            }
            catch (Exception e) when (e is DBusExceptionBase or PluginException)
            {
                // this desktop doesn't have that one: try the next
            }
        }
        throw new PluginException("I don't know how to lock this desktop. A screen locker (hyprlock, swaylock) or a desktop that answers loginctl lock-session is needed.");
    }

    /// <summary>One way to lock: a program to run, or a D-Bus service to ask.</summary>
    internal sealed record LockAttempt(string? Program, string[] Arguments, string? Service = null, string? Path = null, string? Interface = null);

    /// <summary>The ways to lock this desktop, most fitting first.</summary>
    internal static List<LockAttempt> LockPlan(DesktopSession session, Func<string, bool> has)
    {
        var plan = new List<LockAttempt>();
        // Omarchy's own lock (it also locks the password manager and stops the screensaver)
        if (has("omarchy-system-lock")) plan.Add(new("omarchy-system-lock", []));
        switch (session.Compositor)
        {
            case Compositor.Hyprland:
                if (has("hyprlock")) plan.Add(new("hyprlock", []));
                break;
            case Compositor.Sway:
                if (session.Wayland && has("swaylock")) plan.Add(new("swaylock", ["-f"]));
                if (!session.Wayland && has("i3lock")) plan.Add(new("i3lock", []));
                break;
            case Compositor.Gnome:
                plan.Add(new(null, [], "org.gnome.ScreenSaver", "/org/gnome/ScreenSaver", "org.gnome.ScreenSaver"));
                break;
        }
        // What KDE, Cinnamon, MATE, Xfce and others answer to
        plan.Add(new(null, [], "org.freedesktop.ScreenSaver", "/org/freedesktop/ScreenSaver", "org.freedesktop.ScreenSaver"));
        plan.Add(new(null, [], "org.freedesktop.ScreenSaver", "/ScreenSaver", "org.freedesktop.ScreenSaver"));
        // Asks whatever the session set up to listen (GNOME, KDE, hypridle, swayidle, xss-lock)
        if (has("loginctl")) plan.Add(new("loginctl", ["lock-session"]));
        if (has("xdg-screensaver")) plan.Add(new("xdg-screensaver", ["lock"]));
        return plan;
    }

    // ───────────────────────── Sleep

    /// <summary>Sleeps the PC after <paramref name="delay"/>, so the hub (which runs on it) can finish speaking the reply first.</summary>
    public void SleepAfter(TimeSpan delay, ILogger log) => _ = Task.Run(async () =>
    {
        await Task.Delay(delay).ConfigureAwait(false);
        try
        {
            if (Programs.Has("systemctl"))
            {
                var result = await Programs.RunAsync("systemctl", ["suspend"], TimeSpan.FromSeconds(30), CancellationToken.None).ConfigureAwait(false);
                if (result.ExitCode == 0) return;
                log.LogInformation("systemctl suspend failed ({Error}); asking logind directly", result.Error);
            }
            await Bus.SystemWide.CallAsync(Login1, "/org/freedesktop/login1", "org.freedesktop.login1.Manager", "Suspend", "b",
                (ref MessageWriter w) => w.WriteBool(false), TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            log.LogWarning(e, "Couldn't put the PC to sleep");
        }
    });

    // ───────────────────────── Shut down, restart

    /// <summary>
    /// Shuts down (or restarts) after <paramref name="minutes"/>. The hub counts down itself, so that
    /// when the time comes it can end the session the desktop's own tidy way; a notification on the
    /// PC says what's coming.
    /// </summary>
    public async Task ScheduleShutdownAsync(bool restart, int minutes, ILogger log, CancellationToken cancel)
    {
        string what = restart ? "restart" : "shut down";
        _countdown.Start(TimeSpan.FromMinutes(minutes), restart, async () =>
        {
            try
            {
                await PowerOffAsync(restart, log).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                log.LogWarning(e, "Couldn't {What} the PC", what);
                await DesktopNotifications.ShowAsync($"Pucksley couldn't {what} this PC", e.Message, _countdown.NotificationId, critical: true).ConfigureAwait(false);
            }
        });
        string when = minutes == 1 ? "1 minute" : $"{minutes} minutes";
        _countdown.NotificationId = await DesktopNotifications.ShowAsync($"This PC will {what} in {when}",
            "Pucksley was asked to. Say \"cancel the shutdown\" to stop it.", critical: true, timeoutMs: 0, cancel: cancel).ConfigureAwait(false);
    }

    /// <summary>False when nothing was counting down.</summary>
    public async Task<bool> CancelShutdownAsync(CancellationToken cancel)
    {
        if (!_countdown.Cancel(out bool restart)) return false;
        await DesktopNotifications.ShowAsync(restart ? "Restart cancelled" : "Shutdown cancelled", "This PC stays on.", _countdown.NotificationId, timeoutMs: 5000, cancel: cancel).ConfigureAwait(false);
        return true;
    }

    /// <summary>The ways to end the session and power off, the desktop's own first (it closes apps properly).</summary>
    internal static List<string[]> PowerOffPlan(DesktopSession session, bool restart, Func<string, bool> has)
    {
        var plan = new List<string[]>();
        if (has("omarchy-system")) plan.Add(["omarchy-system", restart ? "reboot" : "shutdown"]);
        if (session.Compositor == Compositor.Gnome && has("gnome-session-quit")) plan.Add(["gnome-session-quit", restart ? "--reboot" : "--power-off", "--no-prompt"]);
        if (has("systemctl")) plan.Add(["systemctl", restart ? "reboot" : "poweroff"]);
        if (has("loginctl")) plan.Add(["loginctl", restart ? "reboot" : "poweroff"]);
        return plan;
    }

    async Task PowerOffAsync(bool restart, ILogger log)
    {
        if (session.Compositor == Compositor.Kde)
        {
            try
            {
                // Plasma's own: saves the session and lets apps ask about unsaved work
                await Bus.Session.CallAsync("org.kde.Shutdown", "/Shutdown", "org.kde.Shutdown", restart ? "logoutAndReboot" : "logoutAndShutdown", timeout: Timeout).ConfigureAwait(false);
                return;
            }
            catch (Exception e) when (e is DBusExceptionBase or PluginException)
            {
                log.LogInformation("Plasma didn't take the shutdown ({Message}); asking systemd", e.Message);
            }
        }
        string complaint = "nothing on this PC can do it";
        foreach (var command in PowerOffPlan(session, restart, Programs.Has))
        {
            var result = await Programs.RunAsync(command[0], command.Skip(1), TimeSpan.FromSeconds(60), CancellationToken.None).ConfigureAwait(false);
            if (result.ExitCode == 0) return;
            complaint = result.Error.Length > 0 ? result.Error : $"{command[0]} gave exit code {result.ExitCode}";
            log.LogInformation("{Command} failed: {Complaint}", string.Join(' ', command), complaint);
        }
        throw new PluginException(Text.Clip(complaint, 200));
    }
}

/// <summary>One shutdown or restart counting down, which can be called off.</summary>
internal sealed class ShutdownCountdown(Func<TimeSpan, CancellationToken, Task>? delay = null)
{
    readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? Task.Delay;
    readonly object _gate = new();
    CancellationTokenSource? _pending;
    bool _restart;

    /// <summary>The desktop notification announcing it, so its end (done, cancelled, failed) can replace it.</summary>
    public uint NotificationId { get; set; }

    public bool Pending
    {
        get { lock (_gate) return _pending is not null; }
    }

    /// <summary>Runs <paramref name="whenDue"/> after <paramref name="after"/>, unless cancelled first.</summary>
    public void Start(TimeSpan after, bool restart, Func<Task> whenDue)
    {
        CancellationTokenSource mine;
        lock (_gate)
        {
            if (_pending is not null) throw new PluginException("A shutdown or restart is already counting down. Cancel it first to change it.");
            _pending = mine = new CancellationTokenSource();
            _restart = restart;
        }
        _ = Task.Run(async () =>
        {
            try
            {
                await _delay(after, mine.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            lock (_gate)
            {
                if (!ReferenceEquals(_pending, mine)) return; // cancelled at the last moment
                _pending = null;
            }
            await whenDue().ConfigureAwait(false);
        });
    }

    /// <summary>False when nothing was counting down.</summary>
    public bool Cancel(out bool restart)
    {
        CancellationTokenSource? pending;
        lock (_gate)
        {
            pending = _pending;
            _pending = null;
            restart = _restart;
        }
        if (pending is null) return false;
        pending.Cancel();
        return true;
    }
}
