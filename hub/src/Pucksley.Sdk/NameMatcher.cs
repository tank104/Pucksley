using System.Globalization;
using System.Text;

namespace Pucksley.Sdk;

/// <summary>
/// Matches what someone said ("the lounge", "kitchen speaker", "living room lamp") to the names
/// devices really have. Speech recognition is loose, so use this for every room/device lookup.
/// </summary>
public static class NameMatcher
{
    // Words that carry no identity at all
    static readonly HashSet<string> Articles = ["the", "a", "an", "my", "our", "in", "on", "of"];
    // ...and words that only say what kind of thing it is (dropped too, unless that's all there is)
    static readonly HashSet<string> Kinds = ["speaker", "speakers", "room", "sonos", "light", "lights", "lamp", "lamps", "plug", "plugs", "tv", "device", "socket", "switch", "folder", "app", "application", "program"];

    /// <summary>The best match for <paramref name="spoken"/> among <paramref name="candidates"/>, or default when nothing is close.</summary>
    public static T? Best<T>(string? spoken, IEnumerable<T> candidates, Func<T, string> nameOf, double threshold = 0.55)
    {
        if (string.IsNullOrWhiteSpace(spoken)) return default;
        T? best = default;
        double bestScore = 0;
        foreach (var c in candidates)
        {
            double s = Score(spoken, nameOf(c));
            if (s > bestScore)
            {
                bestScore = s;
                best = c;
            }
        }
        return bestScore >= threshold ? best : default;
    }

    public static string? Best(string? spoken, IEnumerable<string> candidates, double threshold = 0.55) =>
        Best(spoken, candidates, s => s, threshold);

    /// <summary>0..1: how well <paramref name="spoken"/> names <paramref name="actual"/>.</summary>
    public static double Score(string spoken, string actual)
    {
        string a = Normalize(spoken), b = Normalize(actual);
        if (a.Length == 0 || b.Length == 0) return 0;
        if (a == b) return 1;
        // Compare with articles gone ("the lamp" = "Lamp"), and with kind-words gone too
        // ("kitchen speaker" = "Kitchen"); take whichever agrees better.
        double light = Compare(Strip(a, Articles), Strip(b, Articles));
        string ka = Strip(Strip(a, Articles), Kinds), kb = Strip(Strip(b, Articles), Kinds);
        double heavy = ka.Length > 0 && kb.Length > 0 ? Compare(ka, kb) * 0.98 : 0;
        return Math.Max(light, heavy);
    }

    static double Compare(string a, string b)
    {
        if (a.Length == 0 || b.Length == 0) return 0;
        if (a == b) return 0.97;
        // word by word, plurals folded ("pendant" = "pendants")
        var ta = a.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(Stem).Distinct().ToArray();
        var tb = b.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(Stem).Distinct().ToArray();
        // The device's name contains everything said ("kitchen" → "Kitchen Pendant"): strong.
        // What was said contains the device's whole name ("kitchen pendant" → "Kitchen"): weaker,
        // so a device that matches more of the words wins.
        if (ContainsWords(tb, ta)) return 0.86;
        if (ContainsWords(ta, tb)) return 0.85 * (0.4 + 0.6 * tb.Length / ta.Length);
        double overlap = (double)ta.Intersect(tb).Count() / Math.Max(ta.Length, tb.Length);
        double lev = Similarity(a, b);
        return Math.Max(overlap * 0.9, lev * 0.9);
    }

    static bool ContainsWords(string[] haystack, string[] needles) => needles.Length > 0 && needles.All(haystack.Contains);

    /// <summary>A plural folded to its singular, roughly: "lights" → "light", "switches" → "switch", "batteries" → "battery".</summary>
    static string Stem(string w)
    {
        if (w.Length < 4 || !char.IsLetter(w[^1])) return w;
        if (w.EndsWith("ies", StringComparison.Ordinal) && w.Length > 4) return w[..^3] + "y";
        if (w.EndsWith("es", StringComparison.Ordinal) && w.Length > 4 &&
            (w[^3] is 's' or 'x' or 'z' || w.EndsWith("ches", StringComparison.Ordinal) || w.EndsWith("shes", StringComparison.Ordinal)))
            return w[..^2];
        if (w[^1] == 's' && w[^2] != 's' && w[^2] != 'u' && w[^2] != 'i') return w[..^1];
        return w;
    }

    /// <summary>Lower case, no accents, no punctuation, single spaces.</summary>
    public static string Normalize(string s)
    {
        var d = s.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(d.Length);
        foreach (var c in d)
        {
            var cat = CharUnicodeInfo.GetUnicodeCategory(c);
            if (cat == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
            else if (sb.Length > 0 && sb[^1] != ' ') sb.Append(' ');
        }
        return sb.ToString().Trim();
    }

    static string Strip(string normalized, HashSet<string> words) =>
        string.Join(' ', normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => !words.Contains(w)));

    static double Similarity(string a, string b)
    {
        int n = a.Length, m = b.Length;
        var prev = new int[m + 1];
        var cur = new int[m + 1];
        for (int j = 0; j <= m; j++) prev[j] = j;
        for (int i = 1; i <= n; i++)
        {
            cur[0] = i;
            for (int j = 1; j <= m; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
            }
            (prev, cur) = (cur, prev);
        }
        return 1.0 - (double)prev[m] / Math.Max(n, m);
    }
}
