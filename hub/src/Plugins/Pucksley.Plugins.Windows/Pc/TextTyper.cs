using Pucksley.Sdk;
using static Pucksley.Plugins.Windows.Common.NativeMethods;

namespace Pucksley.Plugins.Windows.Pc;

/// <summary>Types text into the focused window as Unicode key events (any language, emoji included).</summary>
internal static class TextTyper
{
    public const int MaxLength = 5000;
    const int EventsPerBatch = 64;

    /// <summary>
    /// The key events for <paramref name="text"/>, one group per typed unit: a character (or a
    /// surrogate pair, kept together), Enter for a line break, Tab for a tab.
    /// </summary>
    internal static List<INPUT[]> KeyStrokes(string text)
    {
        var strokes = new List<INPUT[]>(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n') continue; // \r\n is one Enter
                c = '\n';
            }
            if (c == '\n') strokes.Add(Press(VK_RETURN));
            else if (c == '\t') strokes.Add(Press(VK_TAB));
            else if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                strokes.Add([.. Unicode(c), .. Unicode(text[i + 1])]);
                i++;
            }
            else strokes.Add(Unicode(c));
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
