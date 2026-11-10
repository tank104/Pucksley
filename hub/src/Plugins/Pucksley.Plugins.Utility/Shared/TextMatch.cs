using Pucksley.Sdk;

namespace Pucksley.Plugins;

/// <summary>
/// Picks a longer text out by a few spoken words ("wifi password" → "The wifi password is on the
/// fridge"). <see cref="NameMatcher"/> is built for short names; this adds word coverage on top.
/// </summary>
internal static class TextMatch
{
    static readonly HashSet<string> StopWords =
    [
        "the", "a", "an", "my", "our", "your", "his", "her", "their", "about", "that", "this", "these", "those",
        "is", "are", "was", "were", "be", "to", "of", "for", "in", "on", "at", "and", "or", "i", "we", "you",
        "it", "me", "us", "thing", "things", "something", "what", "one", "reminder", "reminders", "fact",
    ];

    /// <summary>0..1: how well <paramref name="query"/> picks out <paramref name="text"/>.</summary>
    public static double Score(string query, string text)
    {
        double best = NameMatcher.Score(query, text);
        var q = Words(query);
        if (q.Count == 0) return best;
        var t = Words(text);
        int hits = q.Count(w => t.Any(x => SameWord(w, x)));
        return Math.Max(best, 0.9 * hits / q.Count);
    }

    /// <summary>
    /// The best match at or above <paramref name="threshold"/>; on a tie the later item wins (lists are oldest
    /// first). The default accepts half of a two-word query ("wifi code" → "the wifi password…").
    /// </summary>
    public static T? Best<T>(string query, IReadOnlyList<T> items, Func<T, string> textOf, double threshold = 0.4) where T : class
    {
        T? best = null;
        double bestScore = 0;
        foreach (var item in items)
        {
            double s = Score(query, textOf(item));
            if (s > 0 && s >= bestScore)
            {
                best = item;
                bestScore = s;
            }
        }
        return bestScore >= threshold ? best : null;
    }

    static List<string> Words(string s) =>
        NameMatcher.Normalize(s).Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => !StopWords.Contains(w)).Distinct().ToList();

    // "password"/"passwords", "remind"/"reminder", "call"/"called" — but not "call"/"calendar".
    static bool SameWord(string a, string b) =>
        a == b || (Math.Min(a.Length, b.Length) >= 3 && Math.Abs(a.Length - b.Length) <= 3 &&
                   (a.StartsWith(b, StringComparison.Ordinal) || b.StartsWith(a, StringComparison.Ordinal)));
}
