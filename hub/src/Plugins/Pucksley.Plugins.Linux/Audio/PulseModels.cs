using System.Globalization;
using System.Text.Json;

namespace Pucksley.Plugins.Linux.Audio;

/// <summary>One way out of a sound card: its speakers, its headphone socket, an HDMI connector.</summary>
internal sealed record PulsePort(string Name, string Description, string Type, bool Available);

/// <summary>An output of the sound server (a "sink"), as <c>pactl -f json list sinks</c> describes it.</summary>
internal sealed record PulseSink(int Index, string Name, string Description, int Volume, bool Muted, string? ActivePort,
    IReadOnlyList<PulsePort> Ports, IReadOnlyDictionary<string, string> Properties)
{
    public PulsePort? Port => Ports.FirstOrDefault(p => p.Name == ActivePort);

    /// <summary>False when it's a socket with nothing plugged in (every port says so).</summary>
    public bool Usable => Ports.Count == 0 || Ports.Any(p => p.Available);

    public string? Property(string key) => Properties.TryGetValue(key, out var v) && v.Length > 0 ? v : null;
}

/// <summary>One app's stream into an output (a "sink input").</summary>
internal sealed record PulseStream(int Index, int Sink, int Volume, bool Muted, bool Corked, IReadOnlyDictionary<string, string> Properties)
{
    public string? Property(string key) => Properties.TryGetValue(key, out var v) && v.Length > 0 ? v : null;
}

/// <summary>
/// Reads what <c>pactl -f json</c> prints. PipeWire (through pipewire-pulse) and PulseAudio answer
/// the same way, so one reader serves both.
/// </summary>
internal static class PulseJson
{
    public static List<PulseSink> Sinks(string json)
    {
        var sinks = new List<PulseSink>();
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return sinks;
        foreach (var e in doc.RootElement.EnumerateArray())
        {
            if (e.ValueKind != JsonValueKind.Object) continue;
            var ports = new List<PulsePort>();
            if (e.TryGetProperty("ports", out var portList) && portList.ValueKind == JsonValueKind.Array)
            {
                foreach (var p in portList.EnumerateArray())
                {
                    if (p.ValueKind != JsonValueKind.Object) continue;
                    // "available", "not available", or "availability unknown" (a card that can't tell)
                    bool available = !String(p, "availability").Equals("not available", StringComparison.OrdinalIgnoreCase);
                    ports.Add(new PulsePort(String(p, "name"), String(p, "description"), String(p, "type"), available));
                }
            }
            string name = String(e, "name");
            if (name.Length == 0) continue;
            string description = String(e, "description");
            sinks.Add(new PulseSink(Int(e, "index"), name, description.Length > 0 ? description : name, Volume(e), Bool(e, "mute"),
                String(e, "active_port") is { Length: > 0 } port ? port : null, ports, Properties(e)));
        }
        return sinks;
    }

    public static List<PulseStream> Streams(string json)
    {
        var streams = new List<PulseStream>();
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return streams;
        foreach (var e in doc.RootElement.EnumerateArray())
        {
            if (e.ValueKind != JsonValueKind.Object) continue;
            streams.Add(new PulseStream(Int(e, "index"), Int(e, "sink"), Volume(e), Bool(e, "mute"), Bool(e, "corked"), Properties(e)));
        }
        return streams;
    }

    /// <summary>
    /// The loudest channel, 0–100, as volume sliders show it. Above 100 % (software boost) reads as 100.
    /// </summary>
    static int Volume(JsonElement owner)
    {
        if (!owner.TryGetProperty("volume", out var volume) || volume.ValueKind != JsonValueKind.Object) return 0;
        int loudest = 0;
        foreach (var channel in volume.EnumerateObject())
        {
            if (channel.Value.ValueKind != JsonValueKind.Object) continue;
            int percent = -1;
            if (channel.Value.TryGetProperty("value_percent", out var text) && text.ValueKind == JsonValueKind.String &&
                int.TryParse(text.GetString()!.Trim().TrimEnd('%'), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
                percent = parsed;
            else if (channel.Value.TryGetProperty("value", out var raw) && raw.TryGetInt64(out long value))
                percent = (int)Math.Round(value * 100.0 / 65536, MidpointRounding.AwayFromZero); // PA_VOLUME_NORM
            loudest = Math.Max(loudest, percent);
        }
        return Math.Clamp(loudest, 0, 100);
    }

    static Dictionary<string, string> Properties(JsonElement owner)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!owner.TryGetProperty("properties", out var properties) || properties.ValueKind != JsonValueKind.Object) return result;
        foreach (var p in properties.EnumerateObject())
        {
            result[p.Name] = p.Value.ValueKind switch
            {
                JsonValueKind.String => p.Value.GetString() ?? "",
                JsonValueKind.Null or JsonValueKind.Undefined => "",
                _ => p.Value.GetRawText(),
            };
        }
        return result;
    }

    static string String(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    static int Int(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int n) ? n : -1;

    // pactl has printed booleans both as true/false and as "yes"/"no"
    static bool Bool(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.String => v.GetString() is "yes" or "true" or "1",
        _ => false,
    };
}

/// <summary>Names to say for outputs and apps.</summary>
internal static class PulseNames
{
    /// <summary>
    /// An output's name the way Windows writes them, "Endpoint (Device)": "Speakers (Built-in Audio
    /// Analog Stereo)", "Headphones (WH-1000XM4)", "HDMI / DisplayPort 2 (Radeon High Definition
    /// Audio)". The part in front is what people call it; <see cref="OutputMatcher"/> also reads its
    /// kind ("headphones", "the monitor") from those words.
    /// </summary>
    public static string Output(PulseSink sink)
    {
        string description = sink.Description;
        // The first label that says something the description doesn't already
        string? label = Labels(sink).FirstOrDefault(l => !description.Contains(l, StringComparison.OrdinalIgnoreCase));
        return label is null ? description : $"{label} ({description})";
    }

    static IEnumerable<string> Labels(PulseSink sink)
    {
        // 1. What's plugged into it, when the card knows: "Speakers", "Headphones", "HDMI / DisplayPort 2"
        if (sink.Port is { Description.Length: > 0 } port &&
            port.Type.ToLowerInvariant() is "speaker" or "headphones" or "headset" or "handset" or "earpiece" or "hdmi" or "spdif" or "tv" or "handsfree" or "hifi" or "car")
            yield return port.Description;

        // 2. A Bluetooth headset in its call mode sounds poor: say so, like Windows' "Hands-Free" endpoints
        string? profile = sink.Property("api.bluez5.profile");
        if (profile is not null && (profile.StartsWith("headset", StringComparison.OrdinalIgnoreCase) || profile.StartsWith("hfp", StringComparison.OrdinalIgnoreCase)
                                    || profile.StartsWith("hsp", StringComparison.OrdinalIgnoreCase)))
            yield return "Headset Hands-Free";

        // 3. What kind of thing it says it is, and the card's own short name ("Audioengine 2+", often
        // what's printed on it). For USB the name goes first: a kind taken from a USB id is sometimes
        // plain wrong (speakers that claim to be a headset).
        string? kind = sink.Property("device.form_factor")?.ToLowerInvariant() switch
        {
            "headphone" => "Headphones",
            "headset" => "Headset",
            "hands-free" => "Headset Hands-Free",
            "speaker" => "Speakers",
            "tv" => "TV",
            "hifi" => "Hi-Fi",
            "car" => "Car",
            _ => null,
        };
        // (A built-in card's short name is its codec chip, "ALC3204 Analog": no use to anyone.)
        string? nick = sink.Property("node.nick") ?? sink.Property("alsa.card_name") ?? sink.Property("device.nick");
        string? bus = sink.Property("device.bus");
        if (bus == "usb" && nick is not null) yield return nick;
        if (kind is not null) yield return kind;
        if (bus == "bluetooth" && nick is not null) yield return nick;
    }

    /// <summary>
    /// The app behind a stream: a key that groups its streams (a browser has several) and the
    /// names people use for it.
    /// </summary>
    public static (string Key, string Name, string ProcessName) App(PulseStream stream)
    {
        string? binary = stream.Property("application.process.binary");
        string? name = stream.Property("application.name");
        // Flatpak and portal apps: "com.spotify.Client"
        string? id = stream.Property("pipewire.access.portal.app_id") ?? stream.Property("application.id");
        string process = binary is not null ? Path.GetFileName(binary) : IdTail(id) ?? name ?? $"stream{stream.Index}";
        string display = name ?? Capitalize(process);
        // Players that only say what they're built on
        if (display is "Chromium" or "ALSA plug-in" or "WEBRTC VoiceEngine" or "PipeWire ALSA" or "Playback" or "audio stream" || display.StartsWith("ALSA plug-in", StringComparison.Ordinal))
            display = Friendly(process);
        else if (display.Equals(process, StringComparison.OrdinalIgnoreCase)) display = Friendly(process);
        return (process.ToLowerInvariant(), display, process);
    }

    static string? IdTail(string? id) => string.IsNullOrEmpty(id) ? null : id.Split('.').LastOrDefault(part => part.Length > 0 && !part.Equals("Client", StringComparison.OrdinalIgnoreCase));

    /// <summary>"google-chrome-stable" → "Google Chrome"; "spotify" → "Spotify".</summary>
    internal static string Friendly(string process)
    {
        string p = process.ToLowerInvariant();
        if (p.StartsWith("google-chrome", StringComparison.Ordinal) || p == "chrome") return "Google Chrome";
        if (p.StartsWith("chromium", StringComparison.Ordinal)) return "Chromium";
        if (p.StartsWith("firefox", StringComparison.Ordinal)) return "Firefox";
        if (p.StartsWith("brave", StringComparison.Ordinal)) return "Brave";
        if (p.StartsWith("microsoft-edge", StringComparison.Ordinal) || p == "msedge") return "Microsoft Edge";
        if (p.StartsWith("vivaldi", StringComparison.Ordinal)) return "Vivaldi";
        if (p is "vlc") return "VLC";
        if (p is "mpv") return "mpv";
        if (p.StartsWith("teams", StringComparison.Ordinal)) return "Microsoft Teams";
        var words = p.Split(['-', '_'], StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w is not ("bin" or "stable" or "beta" or "wrapped" or "real"))
            .Select(Capitalize);
        string joined = string.Join(' ', words);
        return joined.Length > 0 ? joined : Capitalize(process);
    }

    static string Capitalize(string s) => s.Length > 0 ? char.ToUpperInvariant(s[0]) + s[1..] : s;
}
