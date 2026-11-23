using System.Text.RegularExpressions;

namespace Pucksley.Plugins.Pc.Speech;

/// <summary>Splits a reply into sentences, so the first can play while the rest are synthesised.</summary>
internal static partial class SentenceSplitter
{
    // After . ! ? … (and any closing quote or bracket), or at a line break. "3.5" and "e.g.x" don't split
    [GeneratedRegex(@"(?<=[.!?…][""'”’)\]]*)\s+|\s*\n\s*")]
    private static partial Regex Boundary();

    /// <summary>Pieces shorter than <paramref name="minLength"/> join the next one ("Dr. Smith", "Yes. ").</summary>
    public static List<string> Split(string text, int minLength = 40)
    {
        var pieces = new List<string>();
        var current = "";
        foreach (var part in Boundary().Split(text.Trim()))
        {
            if (part.Length == 0) continue;
            current = current.Length == 0 ? part : current + " " + part;
            if (current.Length >= minLength)
            {
                pieces.Add(current);
                current = "";
            }
        }
        if (current.Length > 0)
        {
            // A short tail joins the previous sentence rather than becoming its own clip
            if (pieces.Count > 0 && current.Length < minLength / 2) pieces[^1] += " " + current;
            else pieces.Add(current);
        }
        return pieces;
    }
}
