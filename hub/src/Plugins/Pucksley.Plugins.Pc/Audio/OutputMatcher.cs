using Pucksley.Plugins.Pc.Common;
using Pucksley.Sdk;

namespace Pucksley.Plugins.Pc.Audio;

/// <summary>
/// Finds the output someone means: by name ("Realtek", "WH-1000XM4") or by kind ("headphones",
/// "speakers", "the monitor"). Windows names endpoints "Endpoint (Device)", e.g. "Speakers
/// (Realtek(R) Audio)", "Headphones (WH-1000XM4 Stereo)" or "DELL U2720Q (NVIDIA High Definition
/// Audio)", so a kind is recognised from words in that name.
/// </summary>
internal static class OutputMatcher
{
    sealed record Kind(string[] Spoken, string[] InName);

    // Order matters: "monitor speakers" and "TV speakers" mean the monitor, so it's checked before speakers
    static readonly Kind[] Kinds =
    [
        new(["headphone", "headset", "earphone", "earbud", "buds", "airpods"],
            ["headphone", "headset", "earphone", "earbud", "buds", "airpods", "hands free", "handsfree"]),
        new(["monitor", "screen", "display", "tv", "television", "hdmi", "displayport"],
            ["hdmi", "displayport", "display audio", "nvidia high definition audio", "amd high definition audio", "monitor", "tv", "television"]),
        new(["speaker", "soundbar"], ["speaker", "soundbar"]),
    ];

    public static AudioOutput Find(string spoken, IReadOnlyList<AudioOutput> outputs)
    {
        if (outputs.Count == 0) throw new PluginException("This PC has no audio outputs switched on.");

        // 1. By name. Several equally good ("speakers" against two speaker outputs): the one in use,
        // rather than hopping between similar devices.
        var scored = outputs.Select(o => (Output: o, Score: NameMatcher.Score(spoken, o.Name))).Where(x => x.Score >= 0.55)
            .OrderByDescending(x => x.Score).ToList();
        if (scored.Count > 0)
        {
            var top = scored.Where(x => x.Score >= scored[0].Score - 0.05).Select(x => x.Output).ToList();
            return top.FirstOrDefault(o => o.IsDefault)
                   ?? top.OrderBy(o => ContainsWord(o.Name, "hands free") || ContainsWord(o.Name, "handsfree") ? 1 : 0).First();
        }

        // 2. By kind
        var words = NameMatcher.Normalize(spoken).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        foreach (var kind in Kinds)
        {
            if (!words.Any(w => kind.Spoken.Any(k => w.StartsWith(k, StringComparison.Ordinal)))) continue;
            var matches = outputs
                .Where(o => kind.InName.Any(f => ContainsWord(o.Name, f)))
                // A Bluetooth headset's "Hands-Free" endpoint is the low-quality call mode: last resort
                .OrderBy(o => ContainsWord(o.Name, "hands free") || ContainsWord(o.Name, "handsfree") ? 1 : 0)
                .ToList();
            if (matches.Count == 0) continue;
            // Already on one of them: say so rather than hopping between similar devices
            return matches.FirstOrDefault(o => o.IsDefault) ?? matches[0];
        }

        throw new PluginException($"I can't find an audio output like '{spoken}'. Outputs: {Text.List(outputs.Select(o => o.Name))}.");
    }

    /// <summary>Whether a word (or phrase) of the name starts with <paramref name="fragment"/>.</summary>
    static bool ContainsWord(string name, string fragment) =>
        (" " + NameMatcher.Normalize(name) + " ").Contains(" " + fragment, StringComparison.Ordinal);
}
