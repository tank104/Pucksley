using Pucksley.Plugins.Pc.Common;
using Pucksley.Sdk;

namespace Pucksley.Plugins.Pc.Desktop;

/// <summary>
/// A top-level window someone would see when switching apps (Alt+Tab). <see cref="Handle"/> is
/// whatever the system identifies it by: a window handle on Windows, the compositor's window
/// address or id on Linux.
/// </summary>
internal sealed record AppWindow(nint Handle, string Title, int ProcessId, string ProcessName, string AppName);

/// <summary>Finding a window by what someone calls it.</summary>
internal static class WindowMatcher
{
    /// <summary>
    /// The window someone means, by app name ("Outlook"), program ("chrome") or title words
    /// ("budget spreadsheet"). <paramref name="windows"/> is front-most first, so ties go to the
    /// most recently used.
    /// </summary>
    public static AppWindow? Match(string spoken, IReadOnlyList<AppWindow> windows)
    {
        var said = Text.StripAppWords(spoken);
        AppWindow? best = null;
        double bestScore = 0;
        foreach (var w in windows)
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
}
