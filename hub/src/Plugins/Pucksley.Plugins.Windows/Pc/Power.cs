using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Pucksley.Sdk;
using static Pucksley.Plugins.Windows.Common.NativeMethods;

namespace Pucksley.Plugins.Windows.Pc;

/// <summary>Lock, sleep, shut down and restart.</summary>
internal static partial class Power
{
    const uint SHUTDOWN_RESTART = 0x04, SHUTDOWN_POWEROFF = 0x08, SHUTDOWN_RESTARTAPPS = 0x80;
    const uint SHTDN_REASON_FLAG_PLANNED = 0x80000000; // major/minor "other"
    const int ERROR_SHUTDOWN_IN_PROGRESS = 1115, ERROR_NO_SHUTDOWN_IN_PROGRESS = 1116, ERROR_SHUTDOWN_IS_SCHEDULED = 1190,
        ERROR_SHUTDOWN_USERS_LOGGED_ON = 1191;

    [LibraryImport("advapi32.dll", EntryPoint = "InitiateShutdownW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint InitiateShutdown(string? machine, string? message, uint gracePeriodSeconds, uint flags, uint reason);

    [LibraryImport("advapi32.dll", EntryPoint = "AbortSystemShutdownW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AbortSystemShutdown(string? machine);

    public static void Lock()
    {
        if (!LockWorkStation()) throw new PluginException("Windows wouldn't lock the PC.");
    }

    /// <summary>Whether the PC has classic sleep (S3) and/or Modern Standby (S0 low-power idle).</summary>
    public static unsafe (bool S3, bool ModernStandby) SleepSupport()
    {
        var capabilities = stackalloc byte[SystemPowerCapabilitiesSize];
        if (!GetPwrCapabilities(capabilities)) return (true, false);
        return (capabilities[PowerCapS3Offset] != 0, capabilities[PowerCapAoAcOffset] != 0);
    }

    /// <summary>
    /// Sleeps the PC after <paramref name="delay"/>, so the hub (which runs on it) can finish
    /// speaking the reply first.
    /// </summary>
    public static void SleepAfter(TimeSpan delay, ILogger log) => _ = Task.Run(async () =>
    {
        await Task.Delay(delay).ConfigureAwait(false);
        try
        {
            EnablePrivilege("SeShutdownPrivilege");
            if (SetSuspendState(false, false, false)) return; // returns once the PC wakes again
            log.LogInformation("SetSuspendState failed (error {Error}); turning the display off instead", Marshal.GetLastPInvokeError());
            // On Modern Standby PCs (no S3), the display going off is what starts standby
            PostMessage(HWND_BROADCAST, WM_SYSCOMMAND, SC_MONITORPOWER, 2);
        }
        catch (Exception e)
        {
            log.LogWarning(e, "Couldn't put the PC to sleep");
        }
    });

    /// <summary>
    /// Shuts down (or restarts) after <paramref name="minutes"/>, cancellable with
    /// <see cref="CancelShutdown"/> or <c>shutdown /a</c>. Unlike <c>shutdown /t</c>, which implies
    /// /f, apps with unsaved work still get to ask before they close.
    /// </summary>
    public static void ScheduleShutdown(bool restart, int minutes)
    {
        EnablePrivilege("SeShutdownPrivilege");
        string what = restart ? "restart" : "shut down";
        uint flags = restart ? SHUTDOWN_RESTART | SHUTDOWN_RESTARTAPPS : SHUTDOWN_POWEROFF;
        uint error = InitiateShutdown(null, $"Pucksley will {what} this PC. Say \"cancel the shutdown\" to stop it.",
            (uint)minutes * 60, flags, SHTDN_REASON_FLAG_PLANNED);
        if (error is ERROR_SHUTDOWN_IN_PROGRESS or ERROR_SHUTDOWN_IS_SCHEDULED)
            throw new PluginException("A shutdown or restart is already counting down. Cancel it first to change it.");
        if (error == ERROR_SHUTDOWN_USERS_LOGGED_ON)
            throw new PluginException($"Someone else is signed in to this PC, so I won't {what} it.");
        if (error != 0) throw new PluginException($"Windows wouldn't {what} (error {error}).");
    }

    /// <summary>False when nothing was counting down.</summary>
    public static bool CancelShutdown()
    {
        EnablePrivilege("SeShutdownPrivilege");
        if (AbortSystemShutdown(null)) return true;
        int error = Marshal.GetLastPInvokeError();
        if (error == ERROR_NO_SHUTDOWN_IN_PROGRESS) return false;
        throw new PluginException($"Windows couldn't cancel the shutdown (error {error}).");
    }

    /// <summary>Shutting down and sleeping need SeShutdownPrivilege enabled (every user has it, disabled).</summary>
    static void EnablePrivilege(string name)
    {
        if (!OpenProcessToken(GetCurrentProcess(), TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out var token)) return;
        try
        {
            if (!LookupPrivilegeValue(null, name, out var luid)) return;
            var privileges = new TOKEN_PRIVILEGES { PrivilegeCount = 1, Luid = luid, Attributes = SE_PRIVILEGE_ENABLED };
            AdjustTokenPrivileges(token, false, ref privileges, (uint)Marshal.SizeOf<TOKEN_PRIVILEGES>(), 0, 0);
        }
        finally
        {
            CloseHandle(token);
        }
    }
}
