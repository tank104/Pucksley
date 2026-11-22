namespace Pucksley.Plugins.Pc.Common;

/// <summary>Small formatting helpers for tool results.</summary>
internal static class Text
{
    /// <summary>The puck's activity line fits about 24 characters.</summary>
    public static string Activity(string text) => Clip(text, 24);

    /// <summary>Trims <paramref name="text"/> to at most <paramref name="max"/> characters, ending in "…" when cut.</summary>
    public static string Clip(string? text, int max)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        text = text.Trim();
        return text.Length <= max ? text : text[..(max - 1)].TrimEnd() + "…";
    }

    /// <summary>"Speakers (Realtek(R) Audio)" → "Speakers": the part people say.</summary>
    public static string ShortDeviceName(string name)
    {
        int paren = name.IndexOf(" (", StringComparison.Ordinal);
        return paren > 0 ? name[..paren] : name;
    }

    /// <summary>"the Spotify app" → "the Spotify": NameMatcher already ignores "the"/"my".</summary>
    public static string StripAppWords(string spoken)
    {
        var words = spoken.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => !w.Equals("app", StringComparison.OrdinalIgnoreCase)
                        && !w.Equals("application", StringComparison.OrdinalIgnoreCase)
                        && !w.Equals("program", StringComparison.OrdinalIgnoreCase)
                        && !w.Equals("window", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        return words.Length > 0 ? string.Join(' ', words) : spoken;
    }

    /// <summary>"3 days 4 hours", "5 hours 12 minutes", "12 minutes".</summary>
    public static string Duration(TimeSpan t)
    {
        static string Unit(int n, string unit) => $"{n} {unit}{(n == 1 ? "" : "s")}";
        if (t.TotalDays >= 1) return $"{Unit((int)t.TotalDays, "day")} {Unit(t.Hours, "hour")}";
        if (t.TotalHours >= 1) return $"{Unit(t.Hours, "hour")} {Unit(t.Minutes, "minute")}";
        return Unit(Math.Max(0, t.Minutes), "minute");
    }

    /// <summary>"a, b, c" — for listing what exists in an error message.</summary>
    public static string List(IEnumerable<string> items, int max = 12)
    {
        var all = items.Where(i => !string.IsNullOrWhiteSpace(i)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var shown = string.Join(", ", all.Take(max));
        return all.Count > max ? $"{shown} and {all.Count - max} more" : shown;
    }
}
