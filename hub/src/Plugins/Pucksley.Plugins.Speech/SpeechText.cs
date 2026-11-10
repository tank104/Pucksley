using System.Text;
using System.Text.RegularExpressions;

namespace Pucksley.Plugins.Speech;

/// <summary>
/// Turns assistant text into something a voice should read: no markdown symbols, links and URLs replaced
/// with "link", emoji dropped, line breaks turned into sentence breaks, whitespace collapsed.
/// </summary>
public static partial class SpeechText
{
    /// <summary>Clean <paramref name="text"/> for text-to-speech. Returns "" when nothing speakable is left.</summary>
    public static string Clean(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var t = text.Replace("\r\n", "\n").Replace('\r', '\n');

        t = CodeFence().Replace(t, "");
        t = MarkdownLink().Replace(t, m => m.Groups["label"].Value.Trim() is { Length: > 0 } label ? label : "link");
        t = AngleUrl().Replace(t, "link");
        t = BareUrl().Replace(t, "link");
        t = Heading().Replace(t, "${text}");
        t = Blockquote().Replace(t, "");
        t = Bullet().Replace(t, "");
        t = Rule().Replace(t, "");
        t = TableDivider().Replace(t, "");
        t = t.Replace("*", "").Replace("`", "").Replace("~~", "");
        t = EmphasisUnderscore().Replace(t, "");
        t = TablePipe().Replace(t, ", ");
        t = StripEmoji(t);

        // Each line becomes a sentence, so headings and list items get a pause instead of running together.
        var sb = new StringBuilder(t.Length);
        foreach (var raw in t.Split('\n'))
        {
            var line = Whitespace().Replace(raw, " ").Trim().Trim(',').Trim();
            if (line.Length == 0) continue;
            if (sb.Length > 0)
            {
                if (!EndsClause(sb)) sb.Append('.');
                sb.Append(' ');
            }
            sb.Append(line);
        }
        return SpaceBeforePunctuation().Replace(sb.ToString(), "$1");
    }

    /// <summary>
    /// Splits text for engines with an input limit into pieces of at most <paramref name="maxLength"/> characters,
    /// breaking after a sentence where possible, else at a space.
    /// </summary>
    internal static IEnumerable<string> SplitForLimit(string text, int maxLength)
    {
        while (text.Length > maxLength)
        {
            int cut = BreakBefore(text, maxLength);
            yield return text[..cut].TrimEnd();
            text = text[cut..].TrimStart();
        }
        if (text.Length > 0) yield return text;
    }

    static int BreakBefore(string text, int maxLength)
    {
        // text is longer than maxLength, so text[i + 1] always exists here.
        for (int i = maxLength - 1; i > maxLength / 2; i--)
        {
            if ((text[i] is '.' or '!' or '?') && char.IsWhiteSpace(text[i + 1])) return i + 1;
        }
        int space = text.LastIndexOf(' ', maxLength - 1);
        return space > 0 ? space : maxLength;
    }

    static bool EndsClause(StringBuilder sb)
    {
        for (int i = sb.Length - 1; i >= 0; i--)
        {
            char c = sb[i];
            if (c is '"' or '\'' or '”' or '’' or ')' or ']') continue;
            return c is '.' or '!' or '?' or ':' or ';' or ',' or '…';
        }
        return true;
    }

    static string StripEmoji(string s)
    {
        StringBuilder? sb = null;
        int i = 0;
        foreach (var rune in s.EnumerateRunes())
        {
            int v = rune.Value;
            bool emoji = v is >= 0x1F000 and <= 0x1FAFF    // pictographs, emoticons, transport, flags
                or >= 0x2600 and <= 0x27BF                  // misc symbols and dingbats (☀ ✓ ❤)
                or >= 0x2B00 and <= 0x2BFF                  // arrows and stars (⭐)
                or 0xFE0F or 0x200D or 0x20E3;              // emoji presentation, zero-width joiner, keycap
            if (emoji) sb ??= new StringBuilder(s, 0, i, s.Length);
            else sb?.Append(rune.ToString());
            i += rune.Utf16SequenceLength;
        }
        return sb?.ToString() ?? s;
    }

    [GeneratedRegex(@"^[ \t]*(```|~~~).*$", RegexOptions.Multiline)]
    private static partial Regex CodeFence();

    // [label](url) and ![alt](url)
    [GeneratedRegex(@"!?\[(?<label>[^\]\n]*)\]\([^)\n]*\)")]
    private static partial Regex MarkdownLink();

    [GeneratedRegex(@"<(?:https?://|www\.)[^>\s]+>", RegexOptions.IgnoreCase)]
    private static partial Regex AngleUrl();

    // Doesn't swallow trailing punctuation: "see https://x.nz." → "see link."
    [GeneratedRegex(@"\b(?:https?://|www\.)[^\s<>()]*[^\s<>().,!?;:'""]", RegexOptions.IgnoreCase)]
    private static partial Regex BareUrl();

    [GeneratedRegex(@"^[ \t]{0,3}#{1,6}[ \t]+(?<text>.*?)(?:[ \t]+#+)?[ \t]*$", RegexOptions.Multiline)]
    private static partial Regex Heading();

    [GeneratedRegex(@"^[ \t]*>+[ \t]?", RegexOptions.Multiline)]
    private static partial Regex Blockquote();

    [GeneratedRegex(@"^[ \t]*[-*+•][ \t]+", RegexOptions.Multiline)]
    private static partial Regex Bullet();

    [GeneratedRegex(@"^[ \t]*([-*_])(?:[ \t]*\1){2,}[ \t]*$", RegexOptions.Multiline)]
    private static partial Regex Rule();

    [GeneratedRegex(@"^[ \t]*\|?(?:[ \t]*:?-+:?[ \t]*\|)+[ \t]*(?::?-+:?)?[ \t]*$", RegexOptions.Multiline)]
    private static partial Regex TableDivider();

    [GeneratedRegex(@"[ \t]*\|[ \t]*")]
    private static partial Regex TablePipe();

    // _emphasis_ and __strong__, but not snake_case
    [GeneratedRegex(@"(?<![\p{L}\p{N}])_{1,2}(?=\S)|(?<=\S)_{1,2}(?![\p{L}\p{N}])")]
    private static partial Regex EmphasisUnderscore();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"\s+([.,!?;:])")]
    private static partial Regex SpaceBeforePunctuation();
}
