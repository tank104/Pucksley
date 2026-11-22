using Pucksley.Sdk;
using static Pucksley.Plugins.Windows.Common.NativeMethods;

namespace Pucksley.Plugins.Windows.Pc;

/// <summary>Types text into the focused window as Unicode key events (any language, emoji included).</summary>
internal static class TextTyper
{
    public const int MaxLength = TypedText.MaxLength;
    const int EventsPerBatch = 64;

    /// <summary>
    /// The key events for <paramref name="text"/>, one group per typed unit: a character (or a
    /// surrogate pair, kept together), Enter for a line break, Tab for a tab.
    /// </summary>
    internal static List<INPUT[]> KeyStrokes(string text)
    {
        var strokes = new List<INPUT[]>(text.Length);
        foreach (var unit in TypedText.Units(text))
        {
            strokes.Add(unit.Kind switch
            {
                TypedKind.Enter => Press(VK_RETURN),
                TypedKind.Tab => Press(VK_TAB),
                _ => unit.Text.Length == 2 ? [.. Unicode(unit.Text[0]), .. Unicode(unit.Text[1])] : Unicode(unit.Text[0]),
            });
        }
        return strokes;
    }

    static INPUT[] Press(ushort vk) => [KeyInput(vk, 0, 0), KeyInput(vk, 0, KEYEVENTF_KEYUP)];

    static INPUT[] Unicode(char c) => [KeyInput(0, c, KEYEVENTF_UNICODE), KeyInput(0, c, KEYEVENTF_UNICODE | KEYEVENTF_KEYUP)];

    /// <summary>Blocking: sends the text in small batches (some apps drop keys when flooded).</summary>
    public static void Type(string text, CancellationToken cancel)
    {
        var batch = new List<INPUT>(EventsPerBatch + 4);
        foreach (var stroke in KeyStrokes(text))
        {
            batch.AddRange(stroke);
            if (batch.Count >= EventsPerBatch) Flush(batch, cancel);
        }
        if (batch.Count > 0) Flush(batch, cancel);
    }

    static void Flush(List<INPUT> batch, CancellationToken cancel)
    {
        cancel.ThrowIfCancellationRequested();
        if (Send(batch.ToArray()) == 0)
            throw new PluginException("Windows blocked the typing. The window in front may be running as administrator.");
        batch.Clear();
        Thread.Sleep(8);
    }
}
