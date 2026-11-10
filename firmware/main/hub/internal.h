// Shared between the link files; not part of the public interface (see ../hub.h).
//
// Threads:
//   main loop  hub::poll(): takes incoming messages off a queue, applies them to g_model, and
//              queues outgoing ones
//   net task   (core 0) Wi-Fi, mDNS, the WebSocket, USB serial and the setup portal; streams the
//              mic to the hub and the hub's speech into the speaker buffer; answers pings and
//              handles the "spk" messages itself so audio and its markers stay in order
//   direct + voice tasks (core 0) direct mode: with no hub around, the puck asks Claude itself
//              (direct.cpp); they report with the hub's own messages, tagged TP_DIRECT
#pragma once
#include <stddef.h>
#include <stdint.h>
#include <string>

namespace hub {
namespace detail {

// Transports (TP_DIRECT: messages from direct mode, which stands in for a hub)
enum : uint8_t { TP_NONE = 0, TP_WS = 1, TP_USB = 2, TP_DIRECT = 3 };

// ---- queues between the main loop and the net task (variable-length JSON lines)
void queuesBegin();
// main loop → net task. tp = TP_WS / TP_USB, or TP_NONE for "the preferred welcomed transport".
bool sendLine(const char* json, uint8_t tp = TP_NONE);
// net task → main loop. Returns a line (and its transport) or nullptr; call doneLine() after.
char* takeLine(uint8_t* tp, size_t* len);
void doneLine(char* line);
// net task side
bool pushIncoming(uint8_t tp, const char* data, size_t len);
char* takeOutgoing(uint8_t* tp, size_t* len);
void doneOutgoing(char* line);

// ---- net.cpp
void netBegin();
// The hub welcomed us on this transport (or not any more): decides where audio and messages go.
void setWelcomed(uint8_t tp, bool on);
bool welcomed(uint8_t tp);
uint8_t preferredTransport();      // WS if welcomed there, else USB if welcomed there, else none
// Wi-Fi credentials / hub address changed (store already updated): reconnect.
void netReconfigure();
void netForgetToken();
// The setup portal (portal.cpp, driven by the net task)
void netOpenPortal();
void netClosePortal();
bool netPortalOpen();
// For hello
const char* deviceId();            // 16 hex chars, from the MAC
const char* deviceName();          // "Puck-1D4D"
void setCurrentFace(const char* f);

// ---- portal.cpp (net task only)
void portalStart(const char* apName);
void portalLoop();
void portalStop();
bool portalRunning();
// The person saved new settings in the portal (consumed by the net task)
bool portalTakeSaved();

// ---- store.cpp: NVS ("pucksley" namespace). Safe from either task (Preferences is locked inside).
void storeBegin();
bool storeWifi(char* ssid, size_t ssidLen, char* pass, size_t passLen);
void storeSetWifi(const char* ssid, const char* pass);
void storeClearWifi();
void storeHubUrl(char* url, size_t len);       // "" = find by mDNS
void storeSetHubUrl(const char* url);
void storeToken(char* token, size_t len);
void storeSetToken(const char* token);
// Longer values (direct mode's setup, the time zone): "" erases. Stored as blobs (no 4000-byte limit).
bool storeGetString(const char* key, std::string& out);
void storeSetString(const char* key, const char* value);
void storeLoadCfg();                           // applies the saved cfg to g_model and hal
void storeSaveCfgSoon();                       // debounced (main loop)
void storePoll();                              // main loop

// ---- direct.cpp: talking to Claude without a hub
void directBegin();                            // at boot (main loop): starts its tasks
bool directConfigured();                       // the hub gave it keys for Claude and speech to text
int directTurn();                              // the turn its messages must carry to count
void directTalk(const char* action, bool lift); // main loop: "start" / "stop" / "cancel"
void directFromHub(const char* what, const char* json);  // the hub's "direct" message (saved, then used)
void directSetTzMin(int minutes);              // local time offset, for Claude's sense of "now"

// ---- proto.cpp (main loop)
void onLine(uint8_t tp, char* line, size_t len);
void protoTick(uint32_t now);
void applyCfgToHal();

}  // namespace detail
}  // namespace hub
