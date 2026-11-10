using Pucksley.Sdk;

namespace Pucksley.Plugins.Shared;

/// <summary>
/// <see cref="NameMatcher"/> plus one fix: it ignores words like "lamp", "tv" and "plug", so a device
/// whose whole name is such a word ("Lamp") can't be matched when an article comes along ("the
/// lamp" scores 0.45). Scoring the article-free form as well fixes that.
/// </summary>
internal static class Names
{
    static readonly HashSet<string> Articles = ["the", "a", "an", "my", "our", "your", "this", "that"];

    /// <summary>"the lamp" → "lamp".</summary>
    public static string WithoutArticles(string spoken) =>
        string.Join(' ', NameMatcher.Normalize(spoken).Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => !Articles.Contains(w)));

    public static double Score(string spoken, string name)
    {
        double score = NameMatcher.Score(spoken, name);
        var bare = WithoutArticles(spoken);
        return bare.Length > 0 ? Math.Max(score, NameMatcher.Score(bare, name)) : score;
    }

    /// <summary>The best match scoring at least <paramref name="threshold"/>, or null.</summary>
    public static T? Best<T>(string? spoken, IEnumerable<T> items, Func<T, string> nameOf, double threshold = 0.55) where T : class
    {
        if (string.IsNullOrWhiteSpace(spoken)) return null;
        T? best = null;
        double bestScore = 0;
        foreach (var item in items)
        {
            double s = Score(spoken, nameOf(item));
            if (s > bestScore)
            {
                bestScore = s;
                best = item;
            }
        }
        return bestScore >= threshold ? best : null;
    }
}
