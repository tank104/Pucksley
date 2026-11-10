using Pucksley.Plugins.Windows.Common;
using Pucksley.Sdk;
using static Pucksley.Plugins.Windows.Common.NativeMethods;

namespace Pucksley.Plugins.Windows.Pc;

/// <summary>A top-level window someone would see in Alt+Tab.</summary>
internal sealed record AppWindow(nint Handle, string Title, int ProcessId, string ProcessName, string AppName);

/// <summary>The PC's app windows: listing, finding by name, bringing to the front.</summary>
internal static class WindowList
{
    const nint MIN_ALL = 419; // the taskbar's "minimise all windows" command (Win+M)
    static readonly HashSet<string> ShellClasses = ["Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd"];

    /// <summary>Windowed apps, front-most first.</summary>
    public static List<AppWindow> AppWindows()
    {
        var handles = new List<nint>();
        EnumWindows((hwnd, _) =>
        {
            if (IsAltTabWindow(hwnd)) handles.Add(hwnd);
            return true;
        }, 0);
        int self = Environment.ProcessId;
        return handles.Select(Describe).Where(w => w.ProcessId != self).ToList();
    }

    /// <summary>The window in front, or null for the desktop/taskbar/nothing.</summary>
    public static AppWindow? Foreground()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == 0 || ShellClasses.Contains(ClassName(hwnd))) return null;
        return Describe(hwnd);
    }

    static bool IsAltTabWindow(nint hwnd)
    {
        if (!IsWindowVisible(hwnd) || GetWindowTextLength(hwnd) == 0) return false;
        if (GetWindow(hwnd, GW_OWNER) != 0) return false;
        long exStyle = GetWindowLongPtr(hwnd, GWL_EXSTYLE);
        bool appWindow = (exStyle & WS_EX_APPWINDOW) != 0;
        if (!appWindow && (exStyle & (WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE)) != 0) return false;
        if (IsCloaked(hwnd)) return false; // suspended Store apps, other virtual desktops
        return !ShellClasses.Contains(ClassName(hwnd));
    }

    static AppWindow Describe(nint hwnd)
    {
        string title = WindowText(hwnd);
        int pid = ProcessIdOf(hwnd);
        // Store apps draw inside ApplicationFrameHost; the app's own process owns a child window
        bool frame = ClassName(hwnd) == "ApplicationFrameWindow";
        if (frame && HostedProcess(hwnd, pid) is int hosted) pid = hosted;
        var (processName, appName) = AppNames.ForProcess(pid);
        // A minimised Store app has no child to tell: its title is its name ("Settings", "Calculator")
        if (frame && processName.Equals("ApplicationFrameHost", StringComparison.OrdinalIgnoreCase)) appName = title;
        return new AppWindow(hwnd, title, pid, processName, appName);
    }

    static int? HostedProcess(nint frame, int framePid)
    {
        int? found = null;
        EnumChildWindows(frame, (child, _) =>
        {
            int pid = ProcessIdOf(child);
            if (pid == framePid) return true;
            found = pid;
            return false;
        }, 0);
        return found;
    }

    /// <summary>The window someone means, by app name ("Outlook"), exe ("chrome") or title words ("budget spreadsheet").</summary>
    public static AppWindow? Match(string spoken, IReadOnlyList<AppWindow> windows)
    {
        var said = Text.StripAppWords(spoken);
        AppWindow? best = null;
        double bestScore = 0;
        foreach (var w in windows) // front-most first, so ties go to the most recently used
        {
            double score = Math.Max(
                Math.Max(NameMatcher.Score(said, w.AppName), NameMatcher.Score(said, w.ProcessName)),
                NameMatcher.Score(said, w.Title) * 0.95);
            if (score > bestScore)
            {
                best = w;
                bestScore = score;
            }
        }
        return bestScore >= 0.55 ? best : null;
    }

    /// <summary>
    /// Restores and activates a window. Windows only lets the process that received the last input
    /// move the foreground, so first send an empty mouse event (as PowerToys does), then fall back
    /// to borrowing the foreground thread's input state.
    /// </summary>
    public static bool BringToFront(nint hwnd)
    {
        if (IsIconic(hwnd)) ShowWindow(hwnd, SW_RESTORE);
        Send([new INPUT { type = INPUT_MOUSE }]);
        if (SetForegroundWindow(hwnd) && GetForegroundWindow() == hwnd) return true;

        var foreground = GetForegroundWindow();
        uint foregroundThread = GetWindowThreadProcessId(foreground, out _);
        uint thisThread = GetCurrentThreadId();
        if (foregroundThread != 0 && foregroundThread != thisThread && AttachThreadInput(thisThread, foregroundThread, true))
        {
            try
            {
                BringWindowToTop(hwnd);
                SetForegroundWindow(hwnd);
            }
            finally
            {
                AttachThreadInput(thisThread, foregroundThread, false);
            }
        }
        return GetForegroundWindow() == hwnd;
    }

    /// <summary>Minimises every window (the taskbar's Win+M command); Win+D if there's no taskbar.</summary>
    public static void MinimizeAll()
    {
        var taskbar = FindWindow("Shell_TrayWnd", null);
        if (taskbar != 0 && PostMessage(taskbar, WM_COMMAND, MIN_ALL, 0)) return;
        Send([KeyInput(VK_LWIN, 0, 0), KeyInput(VK_D, 0, 0), KeyInput(VK_D, 0, KEYEVENTF_KEYUP), KeyInput(VK_LWIN, 0, KEYEVENTF_KEYUP)]);
    }
}
