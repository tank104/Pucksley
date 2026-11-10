// The connection to the Pucksley hub: Wi-Fi (WebSocket, found by mDNS) and USB serial, both
// carrying the JSON messages of docs/PROTOCOL.md, plus the voice audio in both directions.
// Implemented in src/hub/.
//
// poll() runs in the main loop: it applies whatever arrived to g_model (marking dirty bits). The
// network work itself (Wi-Fi, WebSocket, serial, audio streaming) runs in a task of its own, so a
// slow screen redraw never stalls the speech. Everything here is safe to call from the main loop.
#pragma once
#include <stdint.h>
#include "model.h"

namespace hub {

void begin();
void poll();

bool online();                               // welcomed by a hub (same as g_model.linked)

// ---- the assistant
void talk(const char* action, const char* via = "tap");   // "start" / "stop" / "cancel"

// ---- Home face
void media(const char* action, int value = -1);          // "toggle" "next" "prev" "mute" / "vol" + 0..100

// ---- dials
void controlValue(const char* id, float v);               // throttled; the last value always goes out
void controlToggle(const char* id, bool on);
void controlPress(const char* id);

// ---- timers and questions
void timerStart(int seconds, const char* label);
void timerCancel(const char* id);
void answer(const char* id, bool ok);

// ---- the puck itself
void face(const char* f);                    // the face now showing (only sent on change)
void speakerVolume(int v);                   // the person changed it on the puck
void log(const char* fmt, ...);

// ---- Wi-Fi setup
void openSetupPortal();                      // the "Puck-XXXX" access point with a setup page
void closeSetupPortal();
bool portalOpen();
void forgetWifi();

}  // namespace hub
