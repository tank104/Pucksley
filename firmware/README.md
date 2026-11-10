# Pucksley firmware

ESP-IDF 5.5 project for the Waveshare ESP32-S3-Touch-AMOLED-1.75C (ESP32-S3R8: 8 MB octal PSRAM, 16 MB
flash). See the main README for what it does and docs/PROTOCOL.md for how it talks to the hub.

```powershell
idf.py set-target esp32s3      # once
idf.py build
idf.py -p COM5 flash           # hold BOOT while plugging in if it won't enter download mode
```

The native USB port carries the hub protocol (one JSON message per line), so ESP-IDF's own logs go to
UART0 instead. The board layer's own messages are kept and sent to the hub's log once it connects,
and `{"t":"diag"}` sent to the COM port answers with a line of board state, even before the puck is
approved.

| Folder | What |
|---|---|
| `main/hal` | The board: display + LVGL port, touch, motion, buttons, power, audio (ES8311 speaker, ES7210 mics) |
| `main/hub` | Wi-Fi, mDNS, WebSocket (esp_websocket_client), USB Serial/JTAG, setup portal (esp_http_server + captive DNS), NVS, protocol; and direct mode (`direct*.cpp`: Claude and cloud speech over HTTPS when no hub answers) |
| `main/ui` | The faces (LVGL 8.4) |
| `components/sensorlib`, `components/xpowerslib` | Vendored driver libraries (MIT) for touch, motion and the PMU, on the i2c_master driver |
| `tools/sim` | Desktop renderer of the faces (see its README) |
| `tools/test` | Desktop tests for the plain logic (text cleaning, sentences, end of speech, resampling): `./run.sh` |

**Things that are one constant each, if the board disagrees:**

| What | Where |
|---|---|
| Which I2S slot carries the mics | `MIC_CHANNELS` in `main/config.h` (0 = left, 1 = right, 2 = both) |
| Mic gain | The puck's settings in the hub (default 30 dB) |
| Speaker loudness curve | `setSpeakerVolume` in `main/hal/audio.cpp` |
| Speech pre-buffer | `SPK_PREBUFFER_MS` in `main/config.h` (default 180 ms) |
| Turn direction | `TURN_SIGN` in `main/config.h`, or *Flip turn direction* in the hub |
| Touch orientation | `TOUCH_MIRROR_X/Y` in `main/config.h` |
| Display offset | `LCD_COL_OFFSET` in `main/config.h` |
