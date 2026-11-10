// Everything the puck knows about the hub and the assistant. Written by the link (from the main
// loop), read by the faces. Fields that changed are reported through the dirty bits.
#pragma once
#include <stddef.h>
#include <stdint.h>
#include <stdbool.h>
#include "config.h"

// Where the connection stands, from no Wi-Fi to talking to a hub.
enum class NetState : uint8_t {
  NoWifi,         // no credentials saved
  Joining,        // joining the saved Wi-Fi
  Portal,         // the setup access point is open
  Searching,      // on Wi-Fi, looking for a hub (mDNS or the saved address)
  Connecting,     // found a hub, opening the WebSocket
  Pending,        // the hub is waiting for someone to approve this puck (pendingCode)
  Online,         // welcomed by a hub (Wi-Fi or USB)
};

enum class AiState : uint8_t { Idle, Listening, Thinking, Speaking, Error };
enum class CtlKind : uint8_t { Num, Bool, Act, Sel };
enum class NotifyKind : uint8_t { Info, Ai, Alert };

struct Control {
  char id[8] = "";        // short id from the hub ("c3")
  char group[24] = "";
  char name[28] = "";
  CtlKind kind = CtlKind::Num;
  float v = 0, min = 0, max = 100, step = 1;
  char unit[8] = "";
  bool on = false;
  char txt[28] = "";      // overrides the shown value when set
  char opts[8][22] = {};  // Sel
  uint8_t nopts = 0;
};

struct Timer {
  bool active = false;
  bool local = false;     // the puck's own (set with no hub around): it rings it itself
  bool rung = false;
  char id[26] = "";
  char label[26] = "";
  uint32_t endsAtMs = 0;  // millis() when it ends
  uint32_t totalMs = 0;   // for the ring
};

struct Activity {
  char icon[10] = "";
  char text[64] = "";
  char state = 'r';       // r = running, o = ok, e = error
};

struct Model {
  // ---- connection
  NetState net = NetState::NoWifi;
  bool linked = false;          // welcomed by a hub over some transport
  bool viaWifi = false;
  bool viaUsb = false;
  char hub[32] = "";            // the hub's name
  char pendingCode[8] = "";
  char linkError[64] = "";
  char ssid[33] = "";
  char ip[16] = "";
  int rssi = 0;
  char portalSsid[24] = "";     // the setup network's name while it's open
  char puckName[32] = "";       // what the hub calls this puck
  bool direct = false;          // no hub, but on Wi-Fi with keys: talking goes straight to Claude

  // ---- now playing (Home face)
  int vol = 50;
  bool hasVol = false;
  bool mute = false;
  bool playing = false;
  char title[96] = "";
  char artist[64] = "";
  char src[32] = "";

  // ---- time
  bool timeValid = false;
  int64_t epochAtSync = 0;      // unix seconds (UTC) at msAtSync
  uint32_t msAtSync = 0;
  int tzMin = 0;                // minutes east of UTC

  // ---- the assistant
  AiState ai = AiState::Idle;
  uint32_t aiSince = 0;         // millis() the state changed
  char heard[304] = "";         // what the person said
  char reply[704] = "";         // the answer (so far)
  bool replyDone = false;
  char aiError[204] = "";
  Activity acts[3];             // newest first
  uint8_t actCount = 0;
  bool listenRequest = false;   // the hub asked to listen (a follow-up question)

  // ---- dials from the plugins
  Control ctls[MAX_CONTROLS];
  uint8_t ctlCount = 0;

  // ---- timers the hub is running
  Timer timers[MAX_TIMERS];

  // ---- banner
  char notifyText[128] = "";
  NotifyKind notifyKind = NotifyKind::Info;
  bool notifyChime = false;
  uint32_t notifyAt = 0;

  // ---- a yes/no question
  bool confirmActive = false;
  char confirmId[16] = "";
  char confirmText[164] = "";

  // ---- device settings from the hub (saved in flash)
  int bright = 80;              // 10..100
  bool sound = true;
  int dimSec = 30;              // 0 = never
  int offSec = 300;             // 0 = never
  float sens = 1.0f;
  bool flip = false;
  int spk = 70;                 // speaker volume 0..100
  int micGain = 30;             // dB 0..37
  bool lift = true;             // lift to talk

  // ---- power
  int bat = -1;                 // percent, -1 = no battery
  bool chg = false;
  bool usb = false;

  // ---- a face the hub asked for (face id), consumed by the faces
  char faceRequest[16] = "";
};

extern Model g_model;

enum Dirty : uint32_t {
  D_LINK = 1u << 0,     // linked / net / hub / pending / wifi details
  D_AUDIO = 1u << 1,    // vol, mute
  D_MEDIA = 1u << 2,    // playing, title, artist, src
  D_TIME = 1u << 3,
  D_AI = 1u << 4,       // ai state, heard, reply, error
  D_ACT = 1u << 5,      // activity list
  D_CTLS = 1u << 6,     // the control list was replaced
  D_CTLV = 1u << 7,     // a control's value changed
  D_TIMERS = 1u << 8,
  D_NOTIFY = 1u << 9,
  D_CONFIRM = 1u << 10,
  D_CFG = 1u << 11,
  D_BAT = 1u << 12,
  D_FACE_REQ = 1u << 13,
  D_LISTEN = 1u << 14,  // listenRequest set
};

void model_mark(uint32_t bits);   // safe from any task
uint32_t model_take_dirty();      // returns and clears the dirty bits

// Local time now, from the last sync. Returns false if the hub never sent the time.
bool model_local_time(int* hour, int* minute, int* second);
// Local date now: year, month 1..12, day 1..31, weekday 0 = Sunday. False without a time sync.
bool model_local_date(int* year, int* month, int* day, int* weekday);

// Someone can talk to the assistant: a hub, or direct mode
inline bool model_can_talk() { return g_model.linked || g_model.direct; }

// UTF-8-safe bounded copy (never cuts a multi-byte character in half). Always NUL-terminates.
void model_copy(char* dst, size_t size, const char* src);
#define MODEL_SET(field, s) model_copy(field, sizeof(field), (s))
