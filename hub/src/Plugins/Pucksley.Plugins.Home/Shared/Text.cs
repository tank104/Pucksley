using System.Globalization;

namespace Pucksley.Plugins.Shared;

/// <summary>Small formatting helpers for tool results (always invariant culture: results are read by Claude, not shown raw).</summary>
internal static class Text
{
    /// <summary>A number with up to <paramref name="decimals"/> decimals and no trailing zeros ("21", "20.5").</summary>
    public static string Num(double value, int decimals = 1) =>
        Math.Round(value, Math.Max(decimals, 0)).ToString(decimals <= 0 ? "0" : "0." + new string('#', decimals), CultureInfo.InvariantCulture);

    /// <summary>A number with exactly <paramref name="decimals"/> decimals ("21.0").</summary>
    public static string Fixed(double value, int decimals = 1) =>
        value.ToString("F" + decimals, CultureInfo.InvariantCulture);

    /// <summary>The puck's activity line holds about 24 characters.</summary>
    public static string Activity(string text) => text.Length <= 24 ? text : text[..23].TrimEnd() + "…";

    /// <summary>"A", "A and B", "A, B and C".</summary>
    public static string List(IEnumerable<string> items)
    {
        var list = items.ToList();
        return list.Count switch
        {
            0 => "",
            1 => list[0],
            _ => string.Join(", ", list.Take(list.Count - 1)) + " and " + list[^1],
        };
    }

    /// <summary>Lower-case id made of letters, digits and dashes ("Lounge Lamp" → "lounge-lamp").</summary>
    public static string Slug(string name)
    {
        var s = Pucksley.Sdk.NameMatcher.Normalize(name).Replace(' ', '-');
        return s.Length == 0 ? "item" : s;
    }

    public static string Capitalize(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    /// <summary>"1 plug", "3 plugs".</summary>
    public static string Count(int n, string singular, string? plural = null) =>
        $"{n} {(n == 1 ? singular : plural ?? singular + "s")}";
}
