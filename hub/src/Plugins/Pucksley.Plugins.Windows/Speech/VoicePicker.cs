namespace Pucksley.Plugins.Windows.Speech;

/// <summary>Chooses a Windows voice and a dictation recognizer (pure logic, unit-tested).</summary>
internal static class VoicePicker
{
    // The household is in NZ: these English accents first, in this order
    static readonly string[] Regional = ["en-NZ", "en-AU", "en-GB"];

    /// <summary>
    /// The index of the voice to use, or -1 when there are none. A <paramref name="wanted"/> name
    /// (any part of it) wins; otherwise the best English voice: natural-sounding ones first, then
    /// Windows' own languages, then NZ/AU/GB English, then Windows' default voice.
    /// </summary>
    public static int Pick(IReadOnlyList<(string Name, string Language)> voices, string? wanted, IReadOnlyList<string> systemLanguages, string? defaultVoiceName)
    {
        if (voices.Count == 0) return -1;
        if (!string.IsNullOrWhiteSpace(wanted))
        {
            for (int i = 0; i < voices.Count; i++)
                if (voices[i].Name.Contains(wanted.Trim(), StringComparison.OrdinalIgnoreCase)) return i;
        }

        int best = -1;
        double bestScore = double.MinValue;
        for (int i = 0; i < voices.Count; i++)
        {
            double score = Score(voices[i], systemLanguages, defaultVoiceName);
            if (score > bestScore)
            {
                best = i;
                bestScore = score;
            }
        }
        if (bestScore >= 0) return best;
        // No English voice at all: Windows' default, else the first
        int byDefault = IndexOf(voices, v => v.Name == defaultVoiceName);
        return byDefault >= 0 ? byDefault : 0;
    }

    static double Score((string Name, string Language) voice, IReadOnlyList<string> systemLanguages, string? defaultVoiceName)
    {
        if (!voice.Language.StartsWith("en", StringComparison.OrdinalIgnoreCase)) return -1;
        double score = 0;
        if (IsNatural(voice.Name)) score += 4;
        if (systemLanguages.Any(l => l.Equals(voice.Language, StringComparison.OrdinalIgnoreCase))) score += 3;
        int regional = Array.FindIndex(Regional, r => r.Equals(voice.Language, StringComparison.OrdinalIgnoreCase));
        if (regional >= 0) score += 2.3 - regional * 0.1;
        if (voice.Name == defaultVoiceName) score += 0.5;
        return score;
    }

    internal static bool IsNatural(string name) =>
        name.Contains("Natural", StringComparison.OrdinalIgnoreCase) || name.Contains("Neural", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The installed recognizer (by culture name) closest to <paramref name="language"/>: the same
    /// culture; for NZ/AU/IE/ZA English, British then Australian; the same language; any English.
    /// -1 when none are installed.
    /// </summary>
    public static int PickRecognizer(IReadOnlyList<string> installed, string? language)
    {
        if (installed.Count == 0) return -1;
        if (!string.IsNullOrWhiteSpace(language))
        {
            int exact = IndexOf(installed, c => c.Equals(language, StringComparison.OrdinalIgnoreCase));
            if (exact >= 0) return exact;
            string lang = language.Split('-')[0];
            if (lang.Equals("en", StringComparison.OrdinalIgnoreCase) && !language.Equals("en-US", StringComparison.OrdinalIgnoreCase) && !language.Equals("en-CA", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var near in (string[])["en-GB", "en-AU"])
                {
                    int i = IndexOf(installed, c => c.Equals(near, StringComparison.OrdinalIgnoreCase));
                    if (i >= 0) return i;
                }
            }
            int sameLanguage = IndexOf(installed, c => c.Equals(lang, StringComparison.OrdinalIgnoreCase) || c.StartsWith(lang + "-", StringComparison.OrdinalIgnoreCase));
            if (sameLanguage >= 0) return sameLanguage;
        }
        int english = IndexOf(installed, c => c.StartsWith("en", StringComparison.OrdinalIgnoreCase));
        return english >= 0 ? english : 0;
    }

    static int IndexOf<T>(IReadOnlyList<T> items, Func<T, bool> predicate)
    {
        for (int i = 0; i < items.Count; i++)
            if (predicate(items[i])) return i;
        return -1;
    }
}
