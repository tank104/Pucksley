using Pucksley.Plugins.Windows.Common;
using Pucksley.Sdk;

namespace Pucksley.Plugins.Windows.Audio;

/// <summary>Finds the app someone means ("Spotify", "Chrome", "Teams") among the PC's audio sessions.</summary>
internal static class AppAudioMatcher
{
    public static AppAudio Find(string spoken, IReadOnlyList<AppAudio> apps)
    {
        if (apps.Count == 0) throw new PluginException("No apps are using sound on the PC right now.");
        var said = Text.StripAppWords(spoken);
        AppAudio? best = null;
        double bestScore = 0;
        // Ties go to an app that's playing
        foreach (var app in apps.OrderByDescending(a => a.Playing))
        {
            // The display name ("Google Chrome", "Microsoft Teams") or the exe ("chrome", "ms-teams")
            double score = Math.Max(NameMatcher.Score(said, app.Name), NameMatcher.Score(said, app.ProcessName));
            if (score > bestScore)
            {
                best = app;
                bestScore = score;
            }
        }
        if (best is null || bestScore < 0.55)
            throw new PluginException($"No app like '{spoken}' is using sound on the PC. Apps with sound: {Text.List(apps.Select(a => a.Name))}.");
        return best;
    }
}
