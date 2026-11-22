namespace Pucksley.Plugins.Pc.Desktop;

/// <summary>What one press on a keyboard produces.</summary>
internal enum TypedKind
{
    /// <summary>A character (or a surrogate pair, kept together).</summary>
    Character,
    Enter,
    Tab,
}

/// <summary>One typed unit: a character, Enter or Tab.</summary>
internal readonly record struct TypedUnit(TypedKind Kind, string Text);

/// <summary>Text to type into the window in front, as the keys that produce it.</summary>
internal static class TypedText
{
    /// <summary>The longest text pc_type_text takes; beyond this the clipboard is the better way.</summary>
    public const int MaxLength = 5000;

    /// <summary>
    /// One unit per key: a character (a surrogate pair stays together), Enter for a line break
    /// (\r\n is one Enter, and so is a lone \r), Tab for a tab.
    /// </summary>
    public static IEnumerable<TypedUnit> Units(string text)
    {
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n') continue; // \r\n is one Enter
                c = '\n';
            }
            if (c == '\n') yield return new TypedUnit(TypedKind.Enter, "\n");
            else if (c == '\t') yield return new TypedUnit(TypedKind.Tab, "\t");
            else if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                yield return new TypedUnit(TypedKind.Character, text.Substring(i, 2));
                i++;
            }
            else yield return new TypedUnit(TypedKind.Character, c.ToString());
        }
    }

    /// <summary>
    /// The same, with neighbouring characters joined into runs of text and neighbouring Enters or
    /// Tabs counted: what a typing program takes as "this text, then these keys".
    /// </summary>
    public static List<(TypedKind Kind, string Text, int Count)> Runs(string text)
    {
        var runs = new List<(TypedKind Kind, string Text, int Count)>();
        foreach (var unit in Units(text))
        {
            if (runs.Count > 0 && runs[^1].Kind == unit.Kind)
            {
                var last = runs[^1];
                runs[^1] = unit.Kind == TypedKind.Character ? (last.Kind, last.Text + unit.Text, last.Count + 1) : (last.Kind, last.Text, last.Count + 1);
            }
            else runs.Add((unit.Kind, unit.Text, 1));
        }
        return runs;
    }
}
