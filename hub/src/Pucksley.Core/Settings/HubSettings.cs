using System.Text.Json.Nodes;
using Pucksley.Sdk;

namespace Pucksley.Core.Settings;

/// <summary>
/// Everything the hub remembers, saved as <c>settings.json</c> in the data folder. Only ever add
/// properties (with defaults); never rename them, so old files keep loading.
/// </summary>
public sealed class HubSettings
{
    /// <summary>Random id (16 hex), made on first run; pucks remember which hub they belong to.</summary>
    public string HubId { get; set; } = "";

    /// <summary>Shown on the pucks and the web page. Defaults to the machine name.</summary>
    public string HubName { get; set; } = "";

    /// <summary>HTTP + WebSocket port (pucks and the web page).</summary>
    public int Port { get; set; } = 8765;

    /// <summary>Key for the web page from other machines (the page is open without it on this machine).</summary>
    public string AdminToken { get; set; } = "";

    public AssistantSettings Assistant { get; set; } = new();
    public VoiceSettings Voice { get; set; } = new();
    public HouseholdSettings Household { get; set; } = new();
    public DirectSettings Direct { get; set; } = new();
    public SharingSettings Sharing { get; set; } = new();
    public List<PuckRecord> Pucks { get; set; } = [];

    /// <summary>Per plugin: enabled + its settings (secrets inside are encrypted).</summary>
    public Dictionary<string, PluginState> Plugins { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class PluginState
{
    /// <summary>Null = the plugin's default.</summary>
    public bool? Enabled { get; set; }
    public JsonObject? Settings { get; set; }
}

/// <summary>The conversation: model, personality, behaviour.</summary>
public sealed class AssistantSettings
{
    [Setting("Anthropic API key", Secret = true, Order = 0,
        Help = "From console.anthropic.com → API keys. Falls back to the ANTHROPIC_API_KEY environment variable.")]
    public string AnthropicApiKey { get; set; } = "";

    [Setting("Model", Order = 1, Help = "Claude model id for everyday answers. claude-haiku-5-5 (the default) answers fastest; any current model id works.")]
    public string Model { get; set; } = "claude-haiku-5-5";

    [Setting("Model for harder questions", Order = 1, Placeholder = "none: the model above answers everything",
        Help = "When a question needs real thought (reasoning, planning, advice, explaining, writing), the everyday model hands it to this one, which answers in its place. claude-opus-5-5 is the default; empty turns it off.")]
    public string DeepModel { get; set; } = "claude-opus-5-5";

    [Setting("Effort", Order = 2, Options = ["low", "medium", "high"],
        Help = "How hard Claude thinks before answering. Low keeps spoken answers quick.")]
    public string Effort { get; set; } = "low";

    [Setting("Assistant's name", Order = 3, Help = "What the assistant calls itself.")]
    public string AssistantName { get; set; } = "Claude";

    [Setting("Personality and extra instructions", Order = 4, Multiline = true,
        Placeholder = "e.g. Be cheeky. Use metric units. Our dog is called Mabel.",
        Help = "Added to the system prompt.")]
    public string ExtraInstructions { get; set; } = "";

    [Setting("Web search", Order = 5, Help = "Let Claude search the web and read pages for up-to-date answers.")]
    public bool WebSearch { get; set; } = true;

    [Setting("Keep listening after a question", Order = 6,
        Help = "When Claude ends with a question, the puck listens for your answer without a tap.")]
    public bool FollowUpListening { get; set; } = true;

    [Setting("Forget the conversation after (minutes)", Order = 7,
        Help = "After this long without talking, the next question starts a fresh conversation.")]
    public int ConversationTimeoutMinutes { get; set; } = 5;

    [Setting("Say what it's doing between tools", Order = 8, Advanced = true,
        Help = "Speak short progress lines (\"Let me check…\") while tools run.")]
    public bool SpeakProgress { get; set; } = true;

    [Setting("Maximum tool rounds per answer", Order = 9, Advanced = true)]
    public int MaxToolRounds { get; set; } = 10;

    [Setting("Maximum output tokens", Order = 10, Advanced = true)]
    public int MaxTokens { get; set; } = 16000;
}

/// <summary>
/// Direct mode: what the pucks do when they can't reach the hub (it's off, or the network is down
/// between them). On Wi-Fi they ask Claude themselves, with the keys the hub gave them.
/// </summary>
public sealed class DirectSettings
{
    [Setting("Pucks talk to Claude when the hub is off", Order = 0,
        Help = "A puck that can't reach the hub but is on Wi-Fi asks Claude itself: questions, conversation and web search, but no home control. The hub gives each puck a copy of the Claude key and the speech service's key, kept in the puck's flash. Use keys you can revoke.")]
    public bool Enabled { get; set; } = true;

    [Setting("Speech recognition", Order = 1, Options = ["auto", "deepgram", "openai", "elevenlabs", "azure"],
        Help = "The cloud service a puck sends what you say to. Auto: the hub's own engine if it's a cloud one, else the first one set up. Local Whisper only runs on the hub.")]
    public string SttEngine { get; set; } = "auto";

    [Setting("Voice", Order = 2, Options = ["auto", "openai", "elevenlabs", "azure", "none"],
        Help = "The cloud voice a puck answers with. Auto: the hub's own voice if it's a cloud one, else the first one set up. None: the answer only shows on the screen.")]
    public string TtsEngine { get; set; } = "auto";

    [Setting("Web search", Order = 3, Help = "Let Claude search the web when a puck asks it directly.")]
    public bool WebSearch { get; set; } = true;

    [Setting("Model", Order = 4, Advanced = true, Placeholder = "same as the assistant", Help = "The Claude model the pucks use on their own.")]
    public string Model { get; set; } = "";

    [Setting("Longest answer (tokens)", Order = 5, Advanced = true, Help = "Thinking counts too. Spoken answers are short; 2048 leaves room.")]
    public int MaxTokens { get; set; } = 2048;
}

/// <summary>
/// This hub's part when there's more than one: the main hub (on the NAS, say) takes the pucks and
/// runs Claude; a hub on the PC can lend it this PC's controls over MCP instead of taking pucks.
/// </summary>
public sealed class SharingSettings
{
    [Setting("Pucks connect to this hub", Order = 0,
        Help = "Turn off on a PC whose hub only lends its controls to the main hub: it stops announcing itself on the network, so pucks find the main hub instead.")]
    public bool AcceptPucks { get; set; } = true;

    [Setting("Share this hub's tools", Order = 1,
        Help = "Another hub (or any MCP client, such as Claude Desktop) can use what the plugins here can do, such as this PC's volume, apps and media, at the address and key shown below.")]
    public bool ShareTools { get; set; }

    [Setting("Only these plugins", Order = 2, Placeholder = "pc-audio, pc",
        Help = "Plugin ids, one per line (they're on the Plugins page). Empty shares them all. On a PC that lends its controls to a hub on the NAS: pc-audio and pc, so the NAS doesn't get a second copy of Sonos and the rest.")]
    public List<string> SharedPlugins { get; set; } = [];

    /// <summary>What MCP clients send as "Authorization: Bearer …". Made on first run.</summary>
    [Secret]
    public string ShareKey { get; set; } = "";
}

/// <summary>Speech in and out.</summary>
public sealed class VoiceSettings
{
    [Setting("Speech recognition engine", Order = 0,
        Help = "Which engine turns speech into text. Engines come from the speech plugins (set them up on the Plugins page).")]
    public string SttEngine { get; set; } = "whisper-local";

    [Setting("Voice (text to speech) engine", Order = 1,
        Help = "Which engine speaks the answers on the puck. \"none\" shows text only.")]
    public string TtsEngine { get; set; } = "";

    [Setting("Language", Order = 2, Help = "BCP-47 tag for recognition, e.g. en-NZ. Empty = automatic.")]
    public string Language { get; set; } = "en-NZ";

    [Setting("Stop listening by itself", Order = 3,
        Help = "Notice when you've finished talking (otherwise tap the puck when done).")]
    public bool AutoEndOfSpeech { get; set; } = true;

    [Setting("Pause that ends a sentence (ms)", Order = 4, Advanced = true)]
    public int EndSilenceMs { get; set; } = 900;

    [Setting("Give up if nothing is said within (s)", Order = 5, Advanced = true)]
    public int NoSpeechTimeoutSeconds { get; set; } = 7;

    [Setting("Longest question (s)", Order = 6, Advanced = true)]
    public int MaxListenSeconds { get; set; } = 30;

    [Setting("Speech sensitivity", Order = 7, Advanced = true,
        Help = "How much louder than the room speech must be (1.5 – 6). Lower hears quieter voices; higher ignores more noise.")]
    public double VadSensitivity { get; set; } = 3.0;
}

/// <summary>Facts about the home, for context and plugins.</summary>
public sealed class HouseholdSettings
{
    [Setting("Where you live", Order = 0, Placeholder = "Wellington, New Zealand", Help = "For weather and local answers.")]
    public string Location { get; set; } = "";

    [Setting("Latitude", Order = 1, Advanced = true)]
    public double? Latitude { get; set; }

    [Setting("Longitude", Order = 2, Advanced = true)]
    public double? Longitude { get; set; }

    [Setting("Time zone", Order = 3, Placeholder = "Pacific/Auckland", Help = "IANA or Windows id. Empty = this computer's.")]
    public string TimeZone { get; set; } = "";

    [Setting("About your home", Order = 4, Multiline = true,
        Placeholder = "Who lives here, room names, which speaker is where, preferences…",
        Help = "Added to the system prompt so Claude knows the household.")]
    public string Notes { get; set; } = "";

    [Setting("Default media source", Order = 5,
        Help = "What the puck's Home face shows and controls, e.g. sonos:Kitchen, spotify:active or pc-audio:pc. Empty = automatic.")]
    public string DefaultMediaSource { get; set; } = "";
}

/// <summary>A puck the hub knows (approved).</summary>
public sealed class PuckRecord
{
    public string Id { get; set; } = "";

    /// <summary>The hardware name it reports (Puck-1D4D).</summary>
    public string DeviceName { get; set; } = "";

    [Setting("Name", Order = 0, Placeholder = "Kitchen puck")]
    public string Name { get; set; } = "";

    [Setting("Room", Order = 1, Placeholder = "Kitchen",
        Help = "The room it's in: \"turn the music down\" means this room's speaker.")]
    public string Room { get; set; } = "";

    [Setting("Media source", Order = 2, Help = "What its Home face shows and controls. Empty = the household default.")]
    public string MediaSource { get; set; } = "";

    [Setting("Speaker volume", Order = 3)]
    public int SpeakerVolume { get; set; } = 70;

    [Setting("Microphone gain (dB)", Order = 4, Advanced = true, Help = "0–37. Raise it if the puck hears you poorly.")]
    public int MicGain { get; set; } = 30;

    [Setting("Lift to talk", Order = 5, Help = "Pick the puck up to talk, put it down when done.")]
    public bool LiftToTalk { get; set; } = true;

    [Setting("Screen brightness", Order = 6)]
    public int Brightness { get; set; } = 80;

    [Setting("Dim after (s)", Order = 7, Advanced = true, Help = "0 = never.")]
    public int DimSeconds { get; set; } = 30;

    [Setting("Screen off after (s)", Order = 8, Advanced = true, Help = "0 = never.")]
    public int OffSeconds { get; set; } = 300;

    [Setting("Sounds", Order = 9, Advanced = true, Help = "Clicks and chimes.")]
    public bool Sounds { get; set; } = true;

    [Setting("Turn sensitivity", Order = 10, Advanced = true)]
    public double TurnSensitivity { get; set; } = 1.0;

    [Setting("Flip turn direction", Order = 11, Advanced = true)]
    public bool FlipTurn { get; set; }

    /// <summary>The token this puck presents (secret).</summary>
    public string Token { get; set; } = "";

    public DateTimeOffset ApprovedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastSeen { get; set; }

    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? (string.IsNullOrEmpty(DeviceName) ? Id : DeviceName) : Name;
}
