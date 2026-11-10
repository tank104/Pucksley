using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Pucksley.Core.Agent;
using Pucksley.Core.Settings;
using Pucksley.Sdk;

namespace Pucksley.Core.Pucks;

/// <summary>
/// Direct mode: what a puck needs to talk to Claude on its own when it can't reach the hub. The
/// Claude key and model, a system prompt, and the HTTP requests for a cloud speech-to-text service
/// and a cloud voice (from the speech plugins that can be called straight from the puck). The puck
/// keeps them in flash and uses them only while no hub answers. See docs/PROTOCOL.md ("direct").
/// </summary>
internal static class DirectSetup
{
    /// <summary>The messages for a puck (claude, stt, tts, listen), or a single "off".</summary>
    public static List<JsonObject> Build(HubSettings s, string? apiKey, IReadOnlyList<ISpeechToText> sttEngines,
        IReadOnlyList<ITextToSpeech> ttsEngines, TimeZoneInfo tz)
    {
        var d = s.Direct;
        if (!d.Enabled || string.IsNullOrWhiteSpace(apiKey)) return [Off()];
        var stt = Transcription(d, s.Voice, sttEngines);
        if (stt is null) return [Off()];   // the puck can't hear without one
        var voice = Voice(d, s.Voice, ttsEngines);

        var a = s.Assistant;
        var claude = new JsonObject
        {
            ["t"] = "direct",
            ["a"] = "claude",
            ["key"] = apiKey.Trim(),
            ["model"] = string.IsNullOrWhiteSpace(d.Model) ? a.Model.Trim() : d.Model.Trim(),
            ["effort"] = string.IsNullOrWhiteSpace(a.Effort) ? "low" : a.Effort.Trim(),
            ["max"] = Math.Clamp(d.MaxTokens, 512, 8000),
            ["search"] = d.WebSearch && a.WebSearch,
            ["keep"] = Math.Max(0, a.ConversationTimeoutMinutes),
            ["follow"] = a.FollowUpListening,
            ["sys"] = SystemPrompt(s),
        };
        if (Location(s.Household.Location, tz) is { } loc) claude["loc"] = loc;

        var sttMsg = new JsonObject
        {
            ["t"] = "direct",
            ["a"] = "stt",
            ["name"] = stt.Name,
            ["url"] = stt.Url,
            ["fmt"] = stt.Multipart ? "multipart" : "wav",
            ["hdr"] = new JsonArray(stt.Headers.Select(h => (JsonNode)h).ToArray()),
            ["fields"] = new JsonArray(stt.Fields.Select(f => (JsonNode)new JsonArray(f.Key, f.Value)).ToArray()),
            ["file"] = stt.FileField,
            ["text"] = stt.TextField,
        };

        var ttsMsg = voice is null
            ? new JsonObject { ["t"] = "direct", ["a"] = "tts", ["off"] = true }
            : new JsonObject
            {
                ["t"] = "direct",
                ["a"] = "tts",
                ["name"] = voice.Name,
                ["url"] = voice.Url,
                ["hdr"] = new JsonArray(voice.Headers.Select(h => (JsonNode)h).ToArray()),
                ["body"] = voice.Body,
                ["esc"] = voice.XmlBody ? "xml" : "json",
                ["rate"] = voice.SampleRate,
            };

        var v = s.Voice;
        var listen = new JsonObject
        {
            ["t"] = "direct",
            ["a"] = "listen",
            ["end"] = Math.Clamp(v.EndSilenceMs, 300, 3000),
            ["nospeech"] = Math.Clamp(v.NoSpeechTimeoutSeconds, 3, 30),
            ["max"] = Math.Clamp(v.MaxListenSeconds, 5, 30),
            ["sens"] = Math.Clamp(v.VadSensitivity, 1.5, 6),
        };
        return [claude, sttMsg, ttsMsg, listen];
    }

    /// <summary>A fingerprint of the messages, to send them again only when something changed.</summary>
    public static string Fingerprint(IEnumerable<JsonObject> messages)
    {
        var sb = new StringBuilder();
        foreach (var m in messages) sb.Append(m.ToJsonString()).Append('\n');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    /// <summary>What the puck should say it uses, for the hub's pages ("Claude + OpenAI + ElevenLabs").</summary>
    public static string Describe(IReadOnlyList<JsonObject> messages)
    {
        if (messages.Count == 1) return "Off";
        string? Name(string what) => messages.FirstOrDefault(m => (string?)m["a"] == what)?["name"]?.GetValue<string>();
        var parts = new List<string> { "Claude" };
        if (Name("stt") is { } stt) parts.Add($"{stt} hearing");
        parts.Add(Name("tts") is { } tts ? $"{tts} voice" : "text only");
        return string.Join(" · ", parts);
    }

    static JsonObject Off() => new() { ["t"] = "direct", ["a"] = "off" };

    static DirectTranscription? Transcription(DirectSettings d, VoiceSettings v, IReadOnlyList<ISpeechToText> engines)
    {
        var direct = engines.OfType<IDirectSpeechToText>().ToList();
        DirectTranscription? From(string id) =>
            engines.FirstOrDefault(e => e.EngineId.Equals(id, StringComparison.OrdinalIgnoreCase)) is IDirectSpeechToText e
                ? e.GetDirectTranscription(v.Language) : null;
        var choice = d.SttEngine?.Trim() ?? "";
        if (choice.Length > 0 && !choice.Equals("auto", StringComparison.OrdinalIgnoreCase)) return From(choice);
        // the hub's own engine if it's a cloud one, else the first that can
        return (string.IsNullOrWhiteSpace(v.SttEngine) ? null : From(v.SttEngine.Trim()))
               ?? direct.Select(e => e.GetDirectTranscription(v.Language)).FirstOrDefault(t => t is not null);
    }

    static DirectVoice? Voice(DirectSettings d, VoiceSettings v, IReadOnlyList<ITextToSpeech> engines)
    {
        var choice = d.TtsEngine?.Trim() ?? "";
        if (choice.Equals("none", StringComparison.OrdinalIgnoreCase)) return null;
        DirectVoice? From(string id) =>
            engines.FirstOrDefault(e => e.EngineId.Equals(id, StringComparison.OrdinalIgnoreCase)) is IDirectTextToSpeech e
                ? e.GetDirectVoice() : null;
        if (choice.Length > 0 && !choice.Equals("auto", StringComparison.OrdinalIgnoreCase)) return From(choice);
        return (string.IsNullOrWhiteSpace(v.TtsEngine) ? null : From(v.TtsEngine.Trim()))
               ?? engines.OfType<IDirectTextToSpeech>().Select(e => e.GetDirectVoice()).FirstOrDefault(x => x is not null);
    }

    /// <summary>The prompt for a puck on its own: the hub's persona and household, without the tools.</summary>
    internal static string SystemPrompt(HubSettings s)
    {
        var a = s.Assistant;
        var name = string.IsNullOrWhiteSpace(a.AssistantName) ? "Claude" : a.AssistantName.Trim();
        var sb = new StringBuilder();
        sb.AppendLine($"You are {name}, a voice assistant that lives in the household's \"puck\": a small round device with a microphone, a little speaker and a round screen. People talk to you out loud and hear your answer through the puck's speaker while the screen shows your words.");
        sb.AppendLine();
        sb.AppendLine("How to answer:");
        sb.AppendLine("- Talk like a warm, capable person in a real conversation. Keep it short: usually one to three sentences, unless someone asks for more detail, a story or step-by-step help.");
        sb.AppendLine("- Everything you write is spoken aloud. Use plain sentences only: no markdown, bullet points, headings, tables or emoji. Don't read out URLs; name the site instead. Write numbers, times and units the way you'd say them.");
        sb.AppendLine("- What you hear comes from speech recognition, so words can be wrong or missing. Work out what was meant; if it's genuinely unclear, ask one short question.");
        sb.AppendLine("- For news, weather, prices, scores, opening hours or anything else that changes, search the web instead of guessing.");
        sb.AppendLine("- If the person says thanks or goodbye, reply briefly and don't ask a follow-up question.");
        var h = s.Household;
        if (!string.IsNullOrWhiteSpace(h.Location)) sb.AppendLine().AppendLine($"The household is in {h.Location.Trim()}.");
        if (!string.IsNullOrWhiteSpace(h.Notes)) sb.AppendLine().AppendLine("About the household:").AppendLine(Clip(h.Notes.Trim(), 1500));
        if (!string.IsNullOrWhiteSpace(a.ExtraInstructions)) sb.AppendLine().AppendLine("Extra instructions from the household:").AppendLine(Clip(a.ExtraInstructions.Trim(), 1500));
        return sb.ToString().TrimEnd();
    }

    /// <summary>The web search's user_location: city and country from "where you live", and the IANA zone.</summary>
    internal static JsonObject? Location(string location, TimeZoneInfo tz)
    {
        var city = AgentService.CityOf(location);
        var country = AgentService.CountryCode(location ?? "");
        string? zone = tz.Id.Contains('/') ? tz.Id : TimeZoneInfo.TryConvertWindowsIdToIanaId(tz.Id, out var iana) ? iana : null;
        if (city is null && country is null && zone is null) return null;
        var o = new JsonObject { ["type"] = "approximate" };
        if (city is not null) o["city"] = city;
        if (country is not null) o["country"] = country;
        if (zone is not null) o["timezone"] = zone;
        return o;
    }

    static string Clip(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";
}
