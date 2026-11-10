namespace Pucksley.Plugins.Speech;

/// <summary>Language tag conversions. The hub speaks BCP-47 ("en-NZ"); speech APIs want ISO-639 codes.</summary>
internal static class Languages
{
    /// <summary>
    /// The primary language subtag, lower case: "en-NZ" → "en", "fil-PH" → "fil", "zh_Hant_TW" → "zh".
    /// Null for null, empty, "auto", "und" and anything that isn't a two- or three-letter code.
    /// </summary>
    public static string? Primary(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        var t = tag.Trim();
        int cut = t.IndexOfAny(['-', '_']);
        var primary = (cut < 0 ? t : t[..cut]).ToLowerInvariant();
        if (primary is "auto" or "und" or "mul" or "zxx") return null;
        if (primary.Length is < 2 or > 3) return null;
        foreach (char c in primary)
            if (!char.IsAsciiLetterLower(c)) return null;
        return primary;
    }

    /// <summary>
    /// ISO-639-1 (two letters), as OpenAI-style APIs want: "en-NZ" → "en", "mi-NZ" → "mi". Null when the language
    /// has no two-letter code (BCP-47 always uses the two-letter code when one exists), so the caller can omit it.
    /// </summary>
    public static string? Iso6391(string? tag) => Primary(tag) is { Length: 2 } p ? p : null;
}
