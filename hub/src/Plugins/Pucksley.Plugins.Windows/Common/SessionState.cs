using static Pucksley.Plugins.Windows.Common.NativeMethods;

namespace Pucksley.Plugins.Windows.Common;

/// <summary>Whether the person's desktop is usable right now.</summary>
internal static class SessionState
{
    /// <summary>
    /// True when the PC is locked (or showing another secure desktop, like a UAC prompt): the
    /// screen can't be captured or typed into then.
    /// </summary>
    public static unsafe bool IsLocked()
    {
        var desktop = OpenInputDesktop(0, false, DESKTOP_READOBJECTS);
        if (desktop == 0) return true; // the input desktop is Winlogon's, which we can't open
        try
        {
            var name = stackalloc char[64];
            if (!GetUserObjectInformation(desktop, UOI_NAME, name, 64 * sizeof(char), out _)) return false;
            if (!new string(name).Equals("Default", StringComparison.OrdinalIgnoreCase)) return true;
        }
        finally
        {
            CloseDesktop(desktop);
        }
        // The lock screen picture (before the password box) runs on the normal desktop
        var foreground = GetForegroundWindow();
        return foreground != 0 && AppNames.ForProcess(ProcessIdOf(foreground)).ProcessName.Equals("LockApp", StringComparison.OrdinalIgnoreCase);
    }
}
