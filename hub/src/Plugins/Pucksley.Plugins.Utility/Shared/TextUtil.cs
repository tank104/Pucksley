using System.Globalization;
using System.Text;

namespace Pucksley.Plugins;

/// <summary>String helpers shared by the utility plugins.</summary>
internal static class TextUtil
{
    /// <summary>Cuts <paramref name="text"/> to at most <paramref name="max"/> characters, ending with "…" when cut.</summary>
    public static string Truncate(string text, int max)
    {
        if (text.Length <= max) return text;
        if (max <= 1) return text[..max];
        int cut = max - 1;
        if (char.IsHighSurrogate(text[cut - 1])) cut--; // don't split a surrogate pair
        return text[..cut].TrimEnd() + "…";
    }

    /// <summary>Trimmed, non-empty lines of a multi-line setting.</summary>
    public static List<string> Lines(string? text) =>
        (text ?? "").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();

    /// <summary>Non-empty lines that aren't <c>#</c> comments.</summary>
    public static IEnumerable<string> ConfigLines(string? text) => Lines(text).Where(l => !l.StartsWith('#'));

    /// <summary><c>Name: value</c> lines (HTTP headers). Lines without a colon are ignored.</summary>
    public static List<KeyValuePair<string, string>> ParseHeaders(string? text)
    {
        var list = new List<KeyValuePair<string, string>>();
        foreach (var line in ConfigLines(text))
        {
            int colon = line.IndexOf(':');
            if (colon <= 0) continue;
            var name = line[..colon].Trim();
            if (name.Length == 0 || name.Any(char.IsWhiteSpace)) continue;
            list.Add(new(name, line[(colon + 1)..].Trim()));
        }
        return list;
    }

    /// <summary><c>KEY=VALUE</c> lines (environment variables). Later keys win.</summary>
    public static Dictionary<string, string> ParseKeyValues(string? text)
    {
        var map = new Dictionary<string, string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var line in ConfigLines(text))
        {
            int eq = line.IndexOf('=');
            if (eq <= 0) continue;
            map[line[..eq].Trim()] = line[(eq + 1)..].Trim();
        }
        return map;
    }

    /// <summary>Lower-case <c>[a-z0-9_]</c> with single underscores and none at the ends ("Garage Door!" → "garage_door").</summary>
    public static string Slug(string? text)
    {
        var d = (text ?? "").Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(d.Length);
        foreach (var c in d)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            char l = char.ToLowerInvariant(c);
            if (l is >= 'a' and <= 'z' or >= '0' and <= '9') sb.Append(l);
            else if (sb.Length > 0 && sb[^1] != '_') sb.Append('_');
        }
        return sb.ToString().Trim('_');
    }

    /// <summary>"1 item" / "3 items".</summary>
    public static string Plural(int count, string singular, string? plural = null) =>
        $"{count} {(count == 1 ? singular : plural ?? singular + "s")}";

    /// <summary>"milk", "milk and eggs", "milk, eggs and bread".</summary>
    public static string JoinAnd(IReadOnlyList<string> items) => items.Count switch
    {
        0 => "",
        1 => items[0],
        _ => string.Join(", ", items.Take(items.Count - 1)) + " and " + items[^1],
    };

    /// <summary>Upper-cases the first letter.</summary>
    public static string Capitalize(string text) =>
        text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    /// <summary>Text ending in a full stop (unless it already ends in punctuation).</summary>
    public static string Sentence(string text)
    {
        text = text.Trim();
        return text.Length == 0 || ".!?…".Contains(text[^1]) ? text : text + ".";
    }
}
