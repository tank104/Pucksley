using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Pucksley.Plugins.Actions;

/// <summary><c>{{param}}</c> templating for HTTP actions (URL, headers, body) and response formatting.</summary>
internal static partial class ActionTemplates
{
    static readonly JsonSerializerOptions PrettyJson = new() { WriteIndented = true, NewLine = "\n", Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    static readonly JsonSerializerOptions CompactJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary><c>{{name}}</c> or <c>{{ name }}</c> (on one line).</summary>
    [GeneratedRegex(@"\{\{[ \t]*([A-Za-z_][A-Za-z0-9_]*)[ \t]*\}\}")]
    internal static partial Regex Placeholder();

    [GeneratedRegex(@"^-?(0|[1-9][0-9]*)(\.[0-9]+)?([eE][+-]?[0-9]+)?$")]
    private static partial Regex JsonNumber();

    /// <summary>Replaces each <c>{{name}}</c> with <paramref name="encode"/>(value). Names that aren't parameters are left alone.</summary>
    public static string Render(string template, IReadOnlyDictionary<string, string> values, Func<string, string> encode) =>
        Placeholder().Replace(template, m => values.TryGetValue(m.Groups[1].Value, out var v) ? encode(v) : m.Value);

    /// <summary>The URL with values URL-encoded.</summary>
    public static string Url(string template, IReadOnlyDictionary<string, string> values) =>
        Render(template.Trim(), values, Uri.EscapeDataString);

    public static bool LooksLikeJson(string text)
    {
        var t = text.TrimStart();
        return t.StartsWith('{') || t.StartsWith('[');
    }

    /// <summary>
    /// The request body. In a JSON body a value is JSON-escaped inside a string (<c>"{{msg}}"</c>); outside
    /// one (<c>{{level}}</c>) it goes in as-is when it's a number/true/false, as null when empty, and as a
    /// quoted string otherwise — so a value can't break the JSON's structure. A form body
    /// (<c>Content-Type: application/x-www-form-urlencoded</c>) is URL-encoded; anything else is raw.
    /// </summary>
    public static string Body(string template, IReadOnlyDictionary<string, string> values, string? contentType)
    {
        if (LooksLikeJson(template)) return JsonBody(template, values);
        if (contentType?.Contains("x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase) == true)
            return Render(template, values, v => Uri.EscapeDataString(v).Replace("%20", "+"));
        return Render(template, values, v => v);
    }

    static string JsonBody(string template, IReadOnlyDictionary<string, string> values)
    {
        var sb = new StringBuilder(template.Length + 64);
        bool inString = false;
        int pos = 0;
        foreach (Match m in Placeholder().Matches(template))
        {
            inString = CopyTrackingStrings(template, pos, m.Index, inString, sb);
            pos = m.Index + m.Length;
            if (!values.TryGetValue(m.Groups[1].Value, out var v)) sb.Append(m.Value);
            else sb.Append(inString ? JsonEscape(v) : JsonLiteral(v));
        }
        CopyTrackingStrings(template, pos, template.Length, inString, sb);
        return sb.ToString();
    }

    /// <summary>Copies <c>text[from..to)</c>, returning whether it ends inside a JSON string.</summary>
    static bool CopyTrackingStrings(string text, int from, int to, bool inString, StringBuilder sb)
    {
        for (int i = from; i < to; i++)
        {
            char c = text[i];
            sb.Append(c);
            if (inString && c == '\\' && i + 1 < to) sb.Append(text[++i]);
            else if (c == '"') inString = !inString;
        }
        return inString;
    }

    /// <summary>The inside of a JSON string literal (no surrounding quotes).</summary>
    public static string JsonEscape(string value) =>
        JsonEncodedText.Encode(value, JavaScriptEncoder.UnsafeRelaxedJsonEscaping).ToString();

    static string JsonLiteral(string value)
    {
        var t = value.Trim();
        if (t.Length == 0) return "null";
        if (t is "true" or "false" or "null" || JsonNumber().IsMatch(t)) return t;
        return "\"" + JsonEscape(value) + "\"";
    }

    /// <summary>Header lines with values filled in raw (line breaks removed, so a value can't add headers).</summary>
    public static List<KeyValuePair<string, string>> Headers(string? text, IReadOnlyDictionary<string, string> values) =>
        TextUtil.ParseHeaders(text)
            .Select(h => new KeyValuePair<string, string>(h.Key, Render(h.Value, values, v => v.Replace("\r", "").Replace("\n", " "))))
            .ToList();

    /// <summary>A response body for Claude: small JSON pretty-printed, bigger JSON compacted, everything capped.</summary>
    public static string ResponseBody(string body, int max = 2000)
    {
        body = body.Trim();
        if (body.Length == 0) return "";
        if (LooksLikeJson(body))
        {
            try
            {
                if (JsonNode.Parse(body) is { } node)
                {
                    var pretty = node.ToJsonString(PrettyJson);
                    if (pretty.Length <= max) return pretty;
                    body = node.ToJsonString(CompactJson);
                }
            }
            catch (JsonException)
            {
                // not JSON after all: show it as text
            }
        }
        return body.Length <= max ? body : TextUtil.Truncate(body, max) + string.Create(CultureInfo.InvariantCulture, $" ({body.Length} characters in all)");
    }
}
