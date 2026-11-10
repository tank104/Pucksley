# Pucksley link protocol (v1)

The puck (ESP32-S3 firmware) talks to the **Pucksley Hub** (the .NET server that runs Claude, speech
and the plugins). The puck is a voice terminal: it streams its microphone to the hub, plays the
hub's speech on its own speaker, and draws what the hub tells it to. It never needs a PC: the hub
can run on any always-on machine on the LAN (a PC, a NAS in Docker, a Raspberry Pi).

## Transports

Both transports carry the same JSON messages. Every message is a JSON object with a short type key
`"t"`. Unknown types and unknown fields **must be ignored** by both sides.

### Wi-Fi: WebSocket (primary)

- The puck connects to `ws://<hub>:<port>/puck` (default port **8765**).
- **Text frames** carry one JSON message each.
- **Binary frames** carry audio: raw PCM, signed 16-bit little-endian, mono, **16 000 Hz**.
  - puck → hub binary = microphone audio (only while listening)
  - hub → puck binary = speech to play (only between `spk start` and `spk end`)
  - Frames are any whole number of samples; the puck sends 20 ms (640 bytes), the hub sends 20–60 ms.
- Finding the hub: the puck browses mDNS for `_pucksley._tcp` (TXT `path=/puck`, `id=<hub id>`, `v=1`),
  unless a hub URL was set in its Wi-Fi setup page or sent with `hubaddr`.

### USB serial (setup, and a fallback transport)

- ESP32-S3 native USB CDC (VID `0x303A`, PID `0x1001`). DTR/RTS **off** (they can reset the chip).
- NDJSON: one JSON message per line, `\n` terminated, max **1024 bytes** per line. Lines that don't
  start with `{` are boot logs and are ignored.
- Audio travels base64-encoded: `{"t":"au","d":"<base64 PCM>"}` (≤ 640 PCM bytes per line), in both
  directions, with the same meaning as WebSocket binary frames.
- The hub opens the port and sends `hello` (every 2 s until answered); the puck answers with its own
  `hello`. A puck connected over USB is trusted without approval (someone has it in their hands).

When both transports are up, the hub prefers Wi-Fi for audio and keeps USB for setup.

## Handshake and approval

1. Puck → hub:
   ```json
   {"t":"hello","v":1,"fw":"1.0.0","id":"a1b2c3d4e5f60708","name":"Puck-1D4D","token":"<token or empty>",
    "bat":87,"chg":false,"usb":true,"ip":"192.168.1.50","rssi":-55,"ssid":"Home","face":"assistant","caps":["mic","spk"]}
   ```
   `id` is derived from the chip's MAC and never changes. `token` is what this hub gave the puck
   last time (empty on first contact).
2. Hub → puck, one of:
   - `{"t":"welcome","ok":true,"hub":"MY-PC","hubId":"9f3c…","name":"Kitchen","token":"<token>"}` —
     accepted. The puck stores `token` (and `hubId`) in flash. `name` is the puck's friendly name.
   - `{"t":"welcome","ok":false,"pending":true,"code":"482"}` — the hub doesn't know this puck. The
     puck shows the code; someone approves it in the hub's web page (which shows the same code).
     When they do, the hub sends `welcome ok:true` with a fresh token on the same connection.
   - `{"t":"welcome","ok":false,"error":"Rejected"}` — the puck shows the error and retries in 30 s.
3. After `welcome ok:true` the hub sends `time`, `cfg`, `ctls` and `state`.

Until welcomed, the hub ignores everything from the puck except `hello`, `pong`, `bat`, `log`,
`net` and `diag`.

## Keepalive

Hub → puck `{"t":"ping"}` every 5 s; puck → hub `{"t":"pong"}`. Either side drops the link after
15 s of silence. The puck reconnects with backoff (1 s, 2 s, 5 s, then every 10 s).

## Voice

```
puck  {"t":"talk","a":"start","via":"tap"}        person tapped (or lifted the puck: "via":"lift")
hub   {"t":"ai","s":"listening"}
puck  <binary mic frames …>
puck  {"t":"talk","a":"stop"}                      tapped again (or put down) — or the hub hears the end itself
hub   {"t":"ai","s":"thinking"}                    puck stops sending audio on any state but listening
hub   {"t":"heard","text":"turn the kitchen down a bit"}
hub   {"t":"act","icon":"music","text":"Kitchen 30%","s":"ok"}
hub   {"t":"reply","text":"Done — the kitchen is at 30.","done":false}
hub   {"t":"spk","a":"start","id":7}
hub   <binary speech frames …>
hub   {"t":"spk","a":"end","id":7}
hub   {"t":"reply","text":"Done — the kitchen is at 30.","done":true}
puck  {"t":"spk","s":"done","id":7}                the puck finished playing it
hub   {"t":"ai","s":"idle"}                         (or {"t":"listen"} for a follow-up question)
```

| Message | Dir | Fields | Meaning |
|---|---|---|---|
| `talk` | P→H | `a`: `start`/`stop`/`cancel`; `via`: `tap`/`lift`/`hub` | Start listening (mic frames follow), end of the utterance, or throw it away. `start` while the hub is speaking interrupts it. |
| `ai` | H→P | `s`: `idle`/`listening`/`thinking`/`speaking`/`error`; `text` (error message) | The assistant's state. The puck stops streaming the mic on anything but `listening`. |
| `heard` | H→P | `text` (≤ 300) | What the person said (the transcript). |
| `reply` | H→P | `text` (≤ 700, the whole reply so far), `done` bool | The answer as it is written. |
| `act` | H→P | `icon`, `text` (≤ 60), `s`: `run`/`ok`/`err` | A tool is running / ran ("Kitchen 30%"). Icons: `music` `volume` `heat` `light` `tv` `plug` `search` `timer` `pc` `home` `note` `weather` `tool`. |
| `spk` | H→P | `a`: `start`/`end`/`stop`; `id` | Speech audio follows / no more audio for `id` (play out what's buffered) / stop now and flush. |
| `spk` | P→H | `s`: `done`; `id` | The puck finished playing utterance `id` (or it was stopped). |
| `listen` | H→P | | Start listening as if tapped (follow-up question). The puck replies with `talk start` `via:"hub"`. |

## Puck state and settings

| Message | Dir | Fields | Meaning |
|---|---|---|---|
| `time` | H→P | `epoch` (unix s, UTC), `tz` (minutes east of UTC), `tzp` (the zone as a POSIX TZ rule, e.g. `NZST-12NZDT,M9.5.0,M4.1.0/3`) | After welcome and every 10 min. The puck sets its own clock and zone from it, so it keeps local time (daylight saving included) with no hub. |
| `cfg` | H→P | any of `bright` 10–100, `sound` bool, `dim` s, `off` s (0 = never), `sens` 0.25–4, `flip` bool, `spk` 0–100 speaker volume, `mic` 0–37 mic gain dB, `lift` bool (lift to talk), `name` str | Device settings. Partial updates allowed. |
| `state` | H→P | any of `title`, `artist`, `src` (room/app), `play` bool, `vol` 0–100, `mute` bool | Now playing, for the home face. Partial updates allowed. |
| `notify` | H→P | `text` (≤ 120), `kind`: `info`/`ai`/`alert`, `chime` bool | A banner for ~4 s (timer done, reminder, …). |
| `face` | H→P | `f` face id | Show a face. |
| `face` | P→H | `f` | The face now showing. |
| `bat` | P→H | `p` 0–100 (-1 = none), `chg`, `usb`, `mv` | On change and every 60 s. |
| `net` | P→H | `ip`, `rssi`, `ssid` | After joining Wi-Fi and every 60 s. |
| `spkvol` | P→H | `v` 0–100 | The puck's own speaker volume was changed on the puck (throttled). |
| `log` | P→H | `m` | Debug text for the hub's log. |
| `diag` | both | `m` (P→H) | `{"t":"diag"}` asks the puck for one line of board state. |

## Controls (dials the plugins put on the puck)

The hub sends the list of controls; the puck's **Controls** face shows one at a time with a big
dial. The control marked `main` is also what turning the **Home** face changes.

```
hub   {"t":"ctls","n":3}                                     the list is replaced; n items follow
hub   {"t":"ctl","i":0,"id":"sonos.kitchen.vol","g":"Sonos","n":"Kitchen","k":"num","v":25,"min":0,"max":100,"step":1,"u":"%","main":true}
hub   {"t":"ctl","i":1,"id":"ufh.setpoint","g":"Heating","n":"House","k":"num","v":21,"min":10,"max":28,"step":0.5,"u":"°C"}
hub   {"t":"ctl","i":2,"id":"plug.lamp","g":"Plugs","n":"Lamp","k":"bool","on":true}
hub   {"t":"ctlv","id":"sonos.kitchen.vol","v":26}           one control's value changed (partial)
puck  {"t":"ctl","id":"sonos.kitchen.vol","v":27}           the person turned it (throttled ~8 Hz, last value always sent)
puck  {"t":"ctl","id":"plug.lamp","on":false}               toggled
puck  {"t":"ctl","id":"tv.off","a":"press"}                  pressed an action
puck  {"t":"media","a":"toggle"}                             home face: tap = play/pause, swipe = "next"/"prev"
puck  {"t":"media","a":"vol","v":32}                         home face: turned (throttled); "mute" toggles mute
```

Control kinds (`k`): `num` (a value with `min`/`max`/`step`/`u` unit), `bool` (`on`), `act` (a button),
`sel` (`opts` array of strings, `v` = index). `txt` overrides the displayed value. At most 16 controls.

## Timers and confirmations

| Message | Dir | Fields | Meaning |
|---|---|---|---|
| `timer` | H→P | `id`, `sec` (remaining), `label`; or `id` + `cancel:true` | A timer the hub is running (the hub is the source of truth; the puck counts down for display). When it ends the hub sends `notify` with `chime:true`. |
| `timer` | P→H | `a`: `start`, `sec`, `label`; or `a`: `cancel`, `id` | Set or cancel a timer from the puck's Timer face. |
| `confirm` | H→P | `id`, `text` (≤ 160) | Ask before doing something drastic ("Shut down MY-PC?"). The puck shows Yes/No: turn clockwise for Yes, anticlockwise for No, then tap. |
| `ans` | P→H | `id`, `ok` bool | The answer. |
| `confirm_end` | H→P | `id` | Answered elsewhere or timed out. |

## Wi-Fi and hub setup

| Message | Dir | Fields | Meaning |
|---|---|---|---|
| `wifi` | H→P | `ssid`, `pass` | Save Wi-Fi credentials and (re)connect. Normally sent over USB. |
| `wifi` | P→H | `s`: `saved`/`connecting`/`connected`/`failed` | Progress. |
| `hubaddr` | H→P | `url` (`ws://host:port/puck`, empty = find by mDNS) | Where to find the hub. |
| `forget` | H→P | | Forget this hub's token (unpair). The puck also wipes its direct mode setup. |
| `setup` | H→P | | Open the puck's Wi-Fi setup access point. |

Without Wi-Fi credentials (or when asked from the Status face) the puck opens an access point
`Puck-XXXX` with a setup page at `http://192.168.4.1/`: pick the network, type the password,
optionally the hub address.

## Direct mode (talking to Claude without the hub)

When a puck is on Wi-Fi but can't reach a hub (it's off, or the network between them is down), it
asks Claude itself: it records with its own end-of-speech detection, sends the recording to a
cloud speech-to-text service, streams Claude's answer (Messages API, with web search) and speaks it
through a cloud voice, sentence by sentence. Questions and conversation only: home control, music,
timers by voice and the PC need the hub. Timers set on the puck's Timer face work either way (with
no hub the puck keeps and rings them itself).

The hub gives each puck what it needs after the welcome, **over Wi-Fi only** (the messages are
longer than USB's line limit, and direct mode needs Wi-Fi anyway), and again whenever it changes.
The puck saves them in flash and uses them only while no hub answers:

| Message | Fields | Meaning |
|---|---|---|
| `direct` `a:"claude"` | `key`, `model`, `effort`, `max` (tokens), `search` bool, `loc` (web search `user_location`), `keep` (minutes a conversation lasts), `follow` bool (listen again after a question), `sys` (system prompt) | Claude. The puck adds the time and "the hub isn't reachable" to each request. |
| `direct` `a:"stt"` | `name`, `url`, `fmt`: `multipart` or `wav`, `hdr` (["Name: value", …]), `fields` ([[name, value], …]), `file` (the form field for the WAV), `text` (where the transcript is in the JSON answer: a field name, or a dotted path where numbers index arrays, e.g. `results.channels.0.alternatives.0.transcript`) | How to transcribe: POST the recording (16 kHz mono WAV) as multipart form data, or as the whole body. |
| `direct` `a:"tts"` | `name`, `url`, `hdr`, `body` (with `%TEXT%` where the sentence goes), `esc`: `json` (the sentence as a JSON string literal) or `xml`, `rate` (Hz of the raw 16-bit PCM that comes back); or `off:true` | How to speak a sentence. Without it the answer only shows on the screen. |
| `direct` `a:"listen"` | `end` (ms of silence that ends a question), `nospeech` (s), `max` (s), `sens` | End-of-speech detection, as the hub's Voice settings. |
| `direct` `a:"off"` | | Direct mode is off (or the hub lost its key): wipe the saved setup. |

The requests come from the speech plugins (`IDirectSpeechToText`, `IDirectTextToSpeech` in the
SDK): Deepgram, OpenAI (or an OpenAI-compatible cloud server such as Groq), ElevenLabs and Azure
can be called from the puck; local Whisper and Piper can't (they only run on the hub). The keys travel in
the clear on the local network (like everything else on the `ws://` link) and sit in the puck's
flash: use keys you can revoke, ideally with spending limits.

## Face ids

`assistant`, `home`, `controls`, `timer`, `status`, `confirm`, `setup`, `picker`, `spin`, `dice`, `pong`.
