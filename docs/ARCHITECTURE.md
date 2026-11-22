# How Pucksley works

## The puck (firmware/, ESP-IDF 5.5)

Three layers, each behind a small header:

| Layer | Header | What |
|---|---|---|
| Board | `hal.h` | Display (CO5300 over QSPI) + LVGL port, touch (CST9217), motion (QMI8658: turning, lifting, shaking), buttons, power (AXP2101), sound (ES8311 out, ES7210 mics in). |
| Connection | `hub.h` | Wi-Fi, mDNS, the WebSocket to the hub, USB serial, the Wi-Fi setup portal, NVS storage, the protocol. |
| Faces | `ui.h` | LVGL faces: assistant, home, controls, timer, status, confirm, setup, picker, plus toys. |

`model.h` is the shared state between the connection and the faces: the connection writes it (and
sets dirty bits), the faces read it.

**Tasks.**

| Core | Task | What |
|---|---|---|
| 1 | `ui` (app_main) | LVGL, the faces, and the protocol's main-loop side (`hub::poll()` applies incoming messages to the model). |
| 0 | `input` | Samples touch, motion, buttons and power at 200 Hz, and posts input events. |
| 0 | `audio` | Full-duplex I2S at 16 kHz. Each 10 ms round it reads the mics (into the mic ring buffer while listening) and writes the speaker: streamed speech from the speaker ring buffer, mixed with UI tones. Only that task touches the speaker amplifier. |
| 0 | `net` | Wi-Fi state machine, mDNS discovery, USB serial reading and writing, outgoing message queue, mic audio to the hub, speaker-done reports. |
| 0 | WebSocket client task | Incoming text goes to a queue for the main loop, incoming binary audio straight into the speaker ring buffer. Speech start/end markers (`spk`) are handled in this task as well, so they stay in order with the audio. |
| 0 | `direct`, `voice` | Direct mode, only once the hub has given the puck keys (see below). |

**Audio.** 16 kHz mono 16-bit in both directions. Mic frames go out as 20 ms (640-byte) binary
WebSocket frames, or base64 `au` lines over USB. Speech is buffered: playback starts once 180 ms has
arrived, so Wi-Fi jitter doesn't cause gaps, and the puck reports `spk done` when an utterance has
finished playing. The hub paces speech roughly 700 ms ahead of real time.

**Direct mode (no hub).** When the puck is on Wi-Fi but no hub answers, a tap goes to
`hub/direct.cpp` instead: the `direct` task records (with the same end-of-speech detection as the
hub, `direct_text.cpp`), POSTs the WAV to a cloud speech-to-text service, and streams Claude's
answer (`/v1/messages`, server-sent events, web search) over HTTPS with the certificate bundle; the
`voice` task fetches each finished sentence from a cloud voice as raw PCM, resamples it to 16 kHz
and feeds the speaker. Both report with the hub's own messages (`ai`, `heard`, `reply`, `act`,
`listen`) on the main loop's queue, so the faces don't know the difference. Each turn has a number;
a tap, a cancel or the hub coming back moves it on and anything older is dropped. Keys, requests
and listening settings come from the hub (`direct` messages) and live in NVS. The time comes from
SNTP and the hub's POSIX time zone rule. mbedTLS buffers and LVGL's widgets are in PSRAM so the TLS
connections fit next to Wi-Fi and the display's DMA buffers.

## The hub (hub/, .NET 10)

```
 PuckConnection (WebSocket / Serial) ─▶ PuckManager ─▶ PuckSession ─▶ VoiceSession
                                         (approval,     (messages,     listen → VAD → STT → AgentService → sentences → TTS → audio
                                          state push)    keepalive)                            │
                                                                                               ▼
                                         PluginManager ◀── tools / context / controls ── AnthropicChatModel (official SDK, streaming)
```

- **PuckManager**: handshake and approval. USB pucks are trusted; Wi‑Fi pucks show a code to approve
  in the web page, then present a token. It also pushes each puck's settings, controls, timers and the
  now-playing state of the media source it follows.
- **VoiceSession** (one per puck):
  1. Audio arrives into a buffer, and an energy-based voice-activity detector (adaptive noise floor)
     decides when you've stopped talking.
  2. The clip is trimmed and sent to the selected speech-to-text engine.
  3. The transcript goes to the agent. Its streamed text is cut into sentences, each sentence is
     synthesized as soon as it's complete, resampled to 16 kHz, and streamed to the puck.
  4. If the answer ends with a question, the hub asks the puck to listen again.
  5. A tap while it's thinking or speaking cancels the turn and flushes the speaker.
- **AgentService**:
  - **System prompt** (stable, cached by the API): persona, spoken-style rules, the household notes,
    and each plugin's guidance, including remembered facts.
  - **Context block** on each turn: time, which puck and room is asking, and each plugin's live state
    (what's playing, the heating, running timers). It's gathered in parallel within about 1 s.
  - **Conversations**: one per puck, reset after 5 quiet minutes (configurable).
- **AnthropicChatModel**: the beta Messages API, streamed. It sends client tools from the plugins
  plus server-side web search and web fetch, top-level prompt caching, low effort for speed, and the
  server-side refusal fallback. History is append-only, so thinking blocks stay valid. The tool loop
  runs tools in parallel and returns every result in one message.
- **PluginManager**:
  - Discovers plugins by reflection, from the built-in assemblies and the `plugins` folder.
  - Stores their settings, encrypting secrets: DPAPI on Windows, AES-GCM with a key file elsewhere.
  - Runs tools with timeouts, asks the puck to confirm the ones marked `Confirm`, and gathers
    controls, media sources and speech engines.
- **Direct mode setup** (`DirectSetup`): after a puck's welcome, over Wi-Fi, the hub sends it the
  Claude key and model, a system prompt without tools, and the requests for a cloud speech-to-text
  service and voice, built by the speech plugins (`IDirectSpeechToText`, `IDirectTextToSpeech`). It
  checks every 15 s and resends when anything changed, or sends `off` (the puck wipes its copy).
- **Sharing tools** (`ToolSharing`): a hub can serve its plugins' tools over MCP (Streamable HTTP
  at `/mcp`, bearer key). Another hub's MCP plugin connects to it, so the hub on the NAS gets the
  PC's tools from a hub on the PC that doesn't take pucks itself (it stops announcing itself over
  mDNS and refuses `/puck`). Tools that ask first are marked destructive, so the calling hub asks.
- **Hosts** (`hub/src/Hosts`): the hub is a library (`HubHost.Build`) that three small programs
  start: the Windows tray app, the Linux desktop app, and the console for servers and Docker. Each
  ships the plugins that fit it; what they do alike (loading the plugin assemblies, the status
  line, announcing a puck that's waiting) is in `DesktopHost`.
- **The PC plugins** (`pc`, `pc-audio`) are the same on Windows and Linux: their tools are in
  `Pucksley.Plugins.Pc`, and each system brings what's behind them (see [LINUX.md](LINUX.md)).
- **Web page** (`wwwroot/`, embedded): setup, plugins, pucks, chat, a voice test using the browser
  mic, and a live log over Server-Sent Events.
  - Open without a key from the hub's own machine; from other machines it needs the admin key.
  - Changes require an `X-Puck-Ui` header, so other websites can't make them.

## Text on the puck

The puck's fonts cover ASCII plus a few symbols, so the hub cleans every string it sends:
curly quotes and dashes become plain ones, accents and macrons fold away (Tāmaki → Tamaki), and
emoji are dropped (`PuckText`).
