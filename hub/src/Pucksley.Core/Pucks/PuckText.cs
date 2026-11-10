using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace Pucksley.Core.Pucks;

/// <summary>
/// The puck's fonts are small: printable ASCII plus a handful of symbols (• … ° – ‹ › × ·). Text
/// for the puck goes through here so it never shows empty boxes: curly quotes and dashes become
/// plain ones, accents and macrons fold away (Tāmaki → Tamaki), and anything else (emoji, CJK) is
/// dropped.
/// </summary>
public static class PuckText
{
    static readonly HashSet<char> Kept = ['•', '…', '°', '–', '‹', '›', '×', '·'];

    public static string Clean(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        bool plain = true;
        foreach (var c in s)
            if (c > '~' || (c < ' ' && c != '\n'))
            {
                plain = false;
                break;
            }
        if (plain) return s;

        var sb = new StringBuilder(s.Length);
        foreach (var ch in s.Normalize(NormalizationForm.FormD))
        {
            if (ch >= ' ' && ch <= '~') { sb.Append(ch); continue; }
            if (ch == '\n') { sb.Append(ch); continue; }
            if (Kept.Contains(ch)) { sb.Append(ch); continue; }
            switch (ch)
            {
                case '‘' or '’' or '‚' or '′' or '´': sb.Append('\''); continue;
                case '“' or '”' or '„' or '″': sb.Append('"'); continue;
                case '—' or '―' or '−': sb.Append('-'); continue;
                case ' ' or ' ' or ' ' or ' ' or '\t': sb.Append(' '); continue;
                case '«': sb.Append('‹'); continue;
                case '»': sb.Append('›'); continue;
                case 'ß': sb.Append("ss"); continue;
                case 'æ': sb.Append("ae"); continue;
                case 'Æ': sb.Append("AE"); continue;
                case 'ø': sb.Append('o'); continue;
                case 'Ø': sb.Append('O'); continue;
                case 'ł': sb.Append('l'); continue;
                case '€': sb.Append("EUR"); continue;
                case '£': sb.Append("GBP"); continue;
                case '™': sb.Append("TM"); continue;
                case '½': sb.Append("1/2"); continue;
                case '¼': sb.Append("1/4"); continue;
                case '¾': sb.Append("3/4"); continue;
            }
            // accents and macrons after FormD are combining marks: drop them
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
        }
        var result = sb.ToString();
        // dropped emoji can leave double spaces
        while (result.Contains("  ")) result = result.Replace("  ", " ");
        return result;
    }

    /// <summary>Cleans every string in a message (in place).</summary>
    public static void CleanStrings(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject o:
                foreach (var key in o.Select(kv => kv.Key).ToList())
                {
                    var v = o[key];
                    if (v is JsonValue jv && jv.TryGetValue<string>(out var s))
                    {
                        var c = Clean(s);
                        if (!ReferenceEquals(c, s)) o[key] = c;
                    }
                    else CleanStrings(v);
                }
                break;
            case JsonArray a:
                for (int i = 0; i < a.Count; i++)
                {
                    if (a[i] is JsonValue jv && jv.TryGetValue<string>(out var s))
                    {
                        var c = Clean(s);
                        if (!ReferenceEquals(c, s)) a[i] = c;
                    }
                    else CleanStrings(a[i]);
                }
                break;
        }
    }
}
