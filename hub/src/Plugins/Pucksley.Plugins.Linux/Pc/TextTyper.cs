using System.Globalization;
using Pucksley.Plugins.Linux.Common;
using Pucksley.Sdk;

namespace Pucksley.Plugins.Linux.Pc;

/// <summary>
/// Types text into the focused window, through whichever typing program fits the desktop: wtype
/// (Hyprland, Sway and other Wayland compositors with a virtual keyboard), xdotool (X11), or
/// ydotool (anywhere, once its service is set up).
/// </summary>
internal sealed class TextTyper(DesktopSession session)
{
    /// <summary>Milliseconds between keys: some apps drop keys when flooded.</summary>
    const int KeyDelay = 4;

    /// <summary>One program to run, with what to feed it.</summary>
    internal sealed record Step(string Program, string[] Arguments, string? Input = null);

    public async Task TypeAsync(string text, CancellationToken cancel)
    {
        if (!session.Graphical) throw new PluginException("The hub isn't running inside a desktop session on this PC, so there's nothing to type into.");
        var programs = (session.Wayland ? new[] { "wtype", "ydotool" } : ["xdotool", "ydotool"]).Where(Programs.Has).ToList();
        if (programs.Count == 0)
            throw new PluginException(session.Wayland
                ? "Typing on this desktop needs wtype installed (or ydotool, with its service running)."
                : "Typing on this desktop needs xdotool installed.");

        PluginException? last = null;
        foreach (var program in programs)
        {
            try
            {
                foreach (var step in Steps(program, text))
                {
                    cancel.ThrowIfCancellationRequested();
                    var result = await Programs.RunAsync(step.Program, step.Arguments, TimeSpan.FromSeconds(60), cancel, step.Input).ConfigureAwait(false);
                    if (result.ExitCode != 0) throw new PluginException(Refusal(program, result.Error.Length > 0 ? result.Error : result.Output));
                }
                return;
            }
            catch (PluginException e)
            {
                // Nothing was typed by a program the desktop turned away: the next may be let in
                last = e;
            }
        }
        throw last!;
    }

    static string Refusal(string program, string complaint)
    {
        if (complaint.Contains("virtual keyboard", StringComparison.OrdinalIgnoreCase))
            return "This desktop doesn't let wtype type (GNOME and KDE don't offer a virtual keyboard). ydotool can, once its service is running.";
        if (program == "ydotool" && (complaint.Contains("socket", StringComparison.OrdinalIgnoreCase) || complaint.Contains("ydotoold", StringComparison.OrdinalIgnoreCase)))
            return "ydotool's service (ydotoold) isn't running, so it can't type.";
        var line = complaint.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);
        return line is null ? $"{program} couldn't type the text." : $"{program} couldn't type the text ({Text.Clip(line, 160)}).";
    }

    /// <summary>What to run to type <paramref name="text"/> with <paramref name="program"/>.</summary>
    internal static List<Step> Steps(string program, string text)
    {
        string delay = KeyDelay.ToString(CultureInfo.InvariantCulture);
        switch (program)
        {
            case "wtype":
                // "wtype [-k KEY]... -- TEXT": the keys given first are pressed, then the text is typed.
                // Text goes after "--" so that a line starting with "-" isn't taken for an option.
                var steps = new List<Step>();
                var keys = new List<string>();
                foreach (var (kind, run, count) in TypedText.Runs(text))
                {
                    if (kind == TypedKind.Character)
                    {
                        steps.Add(new Step("wtype", ["-d", delay, .. keys, "--", run]));
                        keys.Clear();
                    }
                    else
                    {
                        for (int i = 0; i < count; i++) keys.AddRange(["-k", kind == TypedKind.Enter ? "Return" : "Tab"]);
                    }
                }
                if (keys.Count > 0) steps.Add(new Step("wtype", ["-d", delay, .. keys]));
                return steps;
            case "xdotool":
                // It presses Return for a line break and Tab for a tab itself
                return [new Step("xdotool", ["type", "--clearmodifiers", "--delay", delay, "--file", "-"], Normalize(text))];
            default:
                return [new Step("ydotool", ["type", "--key-delay", delay, "--file", "-"], Normalize(text))];
        }
    }

    /// <summary>Line breaks as a single "\n" each (a Windows "\r\n" would press Enter twice).</summary>
    static string Normalize(string text) => string.Concat(TypedText.Units(text).Select(u => u.Text));
}
