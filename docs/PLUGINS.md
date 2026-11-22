# Writing Pucksley plugins

A plugin gives Claude **tools** (things it can do), and optionally live **context** for each turn,
**dials** on the puck, **media sources** for the Home face, **speech engines**, **buttons** on the
hub's web page and **HTTP endpoints**. Everything a plugin needs is in `Pucksley.Sdk`
(`hub/src/Pucksley.Sdk`); read `IPlugin.cs` and `Tools.cs` first, they're short.

There are three ways to add abilities, from least to most work:

1. **No code: HTTP and script actions.** In the hub's web page, *Plugins → Custom actions*: name a
   tool, describe it, give its parameters, and say what to call (a URL, or a PowerShell/shell
   command). Good for webhooks (IFTTT, Node-RED, Home Assistant webhooks) and one-off scripts.
2. **No code: MCP servers.** *Plugins → MCP servers*: add any Model Context Protocol server (a
   command to run, or an HTTP URL). Its tools appear to Claude as `mcp_<server>_<tool>`.
3. **A C# plugin.** A class deriving from `PuckPlugin<TSettings>`, either built in (in
   `hub/src/Plugins/*`) or compiled into a DLL and dropped into the hub's `plugins` folder
   (`%AppData%\Pucksley\plugins` on Windows, `~/.config/pucksley/plugins` on Linux, `/data/plugins`
   in Docker). Reference only `Pucksley.Sdk`.

## A whole plugin

```csharp
using Pucksley.Sdk;

namespace Pucksley.Plugins.Garage;

public sealed class GarageSettings
{
    [Setting("Controller URL", Placeholder = "http://192.168.1.40")]
    public string Url { get; set; } = "";

    [Setting("API key", Secret = true)]
    public string ApiKey { get; set; } = "";
}

public sealed class GaragePlugin : PuckPlugin<GarageSettings>
{
    public override string Id => "garage";
    public override string Name => "Garage door";
    public override string Description => "Open, close and check the garage door.";
    public override string Icon => "home";

    public override string? Instructions => "Always confirm before opening the garage door at night.";

    [Tool("garage_status", "Whether the garage door is open or closed.")]
    public async Task<string> Status(CancellationToken cancel)
    {
        var state = await Context.Http.GetStringAsync($"{Settings.Url}/state", cancel);
        return $"The garage door is {state}.";
    }

    [Tool("garage_set", "Open or close the garage door.", Confirm = true)]
    public async Task<ToolResult> Set([Param("open or close", Values = ["open", "close"])] string action, CancellationToken cancel)
    {
        using var r = await Context.Http.PostAsync($"{Settings.Url}/{action}", null, cancel);
        if (!r.IsSuccessStatusCode) throw new PluginException($"The garage controller said {(int)r.StatusCode}.");
        return ToolResult.Ok($"Garage door: {action}.", activity: $"Garage {action}");
    }

    public override Task StartAsync(CancellationToken cancel)
    {
        if (string.IsNullOrEmpty(Settings.Url)) SetStatus(StatusLevel.Warning, "Set the controller URL");
        else SetStatus(StatusLevel.Ok, "Ready");
        return Task.CompletedTask;
    }
}
```

## Conventions (please follow them: they decide how good the assistant feels)

**Tool names** are `snake_case`, start with the plugin id (`sonos_set_volume`), and are unique
across all plugins. **Descriptions** are what Claude reads to choose a tool: say what it does, what
the defaults are ("room defaults to the room the puck is in"), and anything surprising. Keep them
to one or two sentences.

**People speak, they don't type.** Names come from speech recognition ("the lounge", "kitchen
speaker"). Match them with `NameMatcher.Best(...)`. When nothing matches, throw a
`PluginException` that lists what does exist ("I can't find 'den'. Rooms: Kitchen, Lounge,
Office.") so Claude can recover in one step.

**Use the caller's room.** Tools can take a `ToolCaller` parameter (filled in by the hub). Its
`Room` is the room of the puck that was spoken to. When a tool targets a place and none was
named, default to `caller.Room`.

**Results are short.** Claude reads them (tokens and time) and usually speaks a summary. Return
one line of facts ("Kitchen volume is now 30."), or compact JSON for lists. Set
`ToolResult.Activity` to ≤ 24 characters for the puck's activity line ("Kitchen 30%").

**Errors are for people.** `throw new PluginException("Spotify isn't connected. Connect it on the
hub's Plugins page.")`. Other exceptions become a generic tool error.

**Be quick.** Tools should answer in a few seconds: use timeouts on network calls (the shared
`Context.Http` has 30 s; pass a shorter `CancellationToken` for LAN devices). `GetContextAsync`
runs before every turn with a ~1 s budget: serve it from a cache, never from a slow call.

**Confirm drastic things.** `[Tool(..., Confirm = true)]` asks on the puck first (turn for yes or
no, tap). Use it for shutdowns, unlocking, buying, deleting.

**Status.** Call `SetStatus(...)` with something useful: "3 rooms found", "Not connected — click
Connect", "Wrong API key".

**Thread safety.** Several tools can run at once. Lock shared state.

**Secrets** go in `[Setting(..., Secret = true)]` properties: encrypted at rest, never sent back
to the browser. Tokens the plugin manages itself (OAuth refresh tokens) can live in settings
properties without `[Setting]` (saved with `SaveSettings()`) or in `Context.DataDirectory`.

## Other things a plugin can offer

| Interface / override | What for |
|---|---|
| `GetContextAsync(caller)` | Live state for the system prompt on each turn ("Kitchen: playing Teardrop, vol 25"). |
| `Instructions` | Static guidance for the system prompt. |
| `GetControls()` / `OnControlAsync()` | Dials on the puck's Controls face (volume, setpoint, a lamp switch). Call `Context.ControlsChanged()` / `ControlValueChanged()` when they change. |
| `IMediaSourceProvider` | Now playing + media keys + volume for the puck's Home face (one source per Sonos room, Spotify, this PC). |
| `ISpeechToText` / `ITextToSpeech` | Speech engines, selectable in the hub's Voice settings. |
| `IDirectSpeechToText` / `IDirectTextToSpeech` | The same engines called straight from the puck when the hub is away (direct mode): return the HTTPS request to make (URL, headers, form fields or a body template with `%TEXT%`), or null when the puck couldn't make it (no key, a server only the hub can reach). |
| `GetActions()` / `RunActionAsync()` | Buttons on the plugin's card (Connect, Discover, Pair). Return `OpenUrl` to open a sign-in page. |
| `HandleHttpAsync()` | Requests to `http://<hub>/plugins/<id>/<path>` (OAuth callbacks). |
| `IPuckTimerHandler` | Timers set from the puck's Timer face (the Timers plugin). |
| `Context.SayAsync()` / `NotifyAsync()` | Speak or show a banner on the pucks (reminders, alerts). |
| `Context.ConfirmAsync()` | Ask a yes/no question on the puck from inside a tool. |
| `Context.SynthesizeWavAsync()` + `PublishClip()` | Make speech and serve it to a LAN speaker (Sonos announcements). |
| `Context.GetPlugin<T>()` | Use another plugin (Spotify playing on a Sonos room). |

## Testing

`hub/tests/Pucksley.TestSupport` has `FakePluginContext` (records notifications, speech, timers)
and `FakePluginContext.CallToolAsync(plugin, "tool_name", "{json args}")`, which runs a tool the
way the hub does.
