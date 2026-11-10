// The protocol, main-loop side: applies the hub's messages to g_model, and the hub:: API the faces
// use to talk to the hub. See docs/PROTOCOL.md.
#include "../platform.h"
#include <ArduinoJson.h>
#include <stdarg.h>
#include <stdio.h>
#include <stdlib.h>
#include <sys/time.h>
#include <time.h>
#include <algorithm>
#include <string.h>
#include <string>
#include "../config.h"
#include "../hal.h"
#include "../face_video.h"
#include "../hub.h"
#include "../model.h"
#include "internal.h"

namespace hub {
namespace detail {

namespace {

uint32_t s_lastBat = 0;
int s_sentBat = -2;
bool s_sentChg = false;
char s_lastFace[16] = "";

// a dial being turned: at most ~8 messages a second, and the last value always goes out
char s_ctlId[8] = "";
float s_ctlV = 0;
bool s_ctlPending = false;
uint32_t s_ctlSentAt = 0;

int s_volPending = -1;
uint32_t s_volSentAt = 0;

int s_spkPending = -1;
uint32_t s_spkSentAt = 0;

uint32_t s_lastClock = 0;     // the clock was last set from the system time (no hub) at this millis()
bool s_haveTz = false;        // a POSIX time zone from the hub is in effect (TZ)
int s_localTimers = 0;

// The hub's time zone, as a POSIX rule ("NZST-12NZDT,M9.5.0,M4.1.0/3"): with it the puck keeps
// local time (daylight saving included) when there's no hub to send it.
void applyTz(const char* tzp, bool save) {
  if (!tzp || !tzp[0]) return;
  const char* cur = getenv("TZ");
  if (cur && !strcmp(cur, tzp) && s_haveTz) return;
  setenv("TZ", tzp, 1);
  tzset();
  s_haveTz = true;
  if (save) storeSetString("tzp", tzp);
}

// Minutes east of UTC at t, from the time zone in effect
int tzMinutesAt(time_t t) {
  struct tm lt, gt;
  localtime_r(&t, &lt);
  gmtime_r(&t, &gt);
  int days = lt.tm_year != gt.tm_year ? (lt.tm_year > gt.tm_year ? 1 : -1) : lt.tm_yday - gt.tm_yday;
  return days * 1440 + (lt.tm_hour - gt.tm_hour) * 60 + (lt.tm_min - gt.tm_min);
}

void send(JsonDocument& d, uint8_t tp = TP_NONE) {
  char buf[LINK_MAX_LINE];
  size_t n = serializeJson(d, buf, sizeof buf);
  if (n > 0 && n < sizeof buf) sendLine(buf, tp);
}

AiState parseAi(const char* s) {
  if (!strcmp(s, "listening")) return AiState::Listening;
  if (!strcmp(s, "thinking")) return AiState::Thinking;
  if (!strcmp(s, "speaking")) return AiState::Speaking;
  if (!strcmp(s, "error")) return AiState::Error;
  return AiState::Idle;
}

void setAi(AiState s) {
  AiState before = g_model.ai;
  if (s == before) return;
  g_model.ai = s;
  g_model.aiSince = millis();
  if (s == AiState::Listening) {
    if (!hal::micActive()) {
      hal::micStart();
      hal::beep(Tone::Listen);
    }
  } else if (before == AiState::Listening && hal::micActive()) {
    hal::micStop();
    hal::beep(Tone::ListenEnd);
  }
  model_mark(D_AI);
}

void refreshLinked() {
  bool ws = welcomed(TP_WS), usb = welcomed(TP_USB);
  bool linked = ws || usb;
  bool changed = linked != g_model.linked || ws != g_model.viaWifi || usb != g_model.viaUsb;
  g_model.viaWifi = ws;
  g_model.viaUsb = usb;
  g_model.linked = linked;
  if (linked) g_model.net = NetState::Online;
  if (changed) {
    if (!linked || g_model.direct) {
      // the hub went (or came back while direct mode was talking): whatever was going on stops
      if (linked) directTalk("cancel", false);
      if (hal::micActive()) hal::micStop();
      hal::speakerStop();
      g_model.ai = AiState::Idle;
      g_model.confirmActive = false;
      model_mark(D_AI | D_CONFIRM);
    }
    if (linked && g_model.direct) g_model.direct = false;
    model_mark(D_LINK);
  }
}

void sendBat(bool force) {
  int p = hal::batteryPercent();
  bool chg = hal::charging();
  uint32_t now = millis();
  bool changed = p != s_sentBat || chg != s_sentChg;
  if (!force && !changed && now - s_lastBat < 60000) return;
  if (!force && changed && p >= 0 && s_sentBat >= 0 && abs(p - s_sentBat) < 1 && chg == s_sentChg) return;
  s_lastBat = now;
  s_sentBat = p;
  s_sentChg = chg;
  g_model.bat = p;
  g_model.chg = chg;
  g_model.usb = hal::usbPowered();
  model_mark(D_BAT);
  JsonDocument d;
  d["t"] = "bat";
  d["p"] = p;
  d["chg"] = chg;
  d["usb"] = g_model.usb;
  d["mv"] = hal::batteryMv();
  send(d);
}

void sendNet() {
  if (!g_model.ip[0]) return;
  JsonDocument d;
  d["t"] = "net";
  d["ip"] = g_model.ip;
  d["rssi"] = g_model.rssi;
  d["ssid"] = g_model.ssid;
  send(d);
}

void onNetNote(JsonDocument& doc) {
  const char* s = doc["s"] | "";
  if (doc["ssid"].is<const char*>()) MODEL_SET(g_model.ssid, doc["ssid"].as<const char*>());
  if (doc["ip"].is<const char*>()) MODEL_SET(g_model.ip, doc["ip"].as<const char*>());
  if (doc["rssi"].is<int>()) g_model.rssi = doc["rssi"].as<int>();
  if (doc["err"].is<const char*>()) MODEL_SET(g_model.linkError, doc["err"].as<const char*>());
  if (!g_model.linked) {
    if (!strcmp(s, "nowifi")) g_model.net = NetState::NoWifi;
    else if (!strcmp(s, "joining")) g_model.net = NetState::Joining;
    else if (!strcmp(s, "failed")) g_model.net = NetState::Joining;
    else if (!strcmp(s, "portal")) g_model.net = NetState::Portal;
    else if (!strcmp(s, "online") || !strcmp(s, "searching")) g_model.net = g_model.net == NetState::Pending ? NetState::Pending : NetState::Searching;
    else if (!strcmp(s, "connecting")) g_model.net = NetState::Connecting;
  }
  if (!strcmp(s, "portal")) {
    MODEL_SET(g_model.portalSsid, deviceName());
  } else if (strcmp(s, "portal") != 0 && !netPortalOpen()) {
    g_model.portalSsid[0] = 0;
  }
  if (!strcmp(s, "online") || !strcmp(s, "joining") || !strcmp(s, "failed")) {
    // nothing of the old address is right any more
    if (strcmp(s, "online") != 0) g_model.ip[0] = 0;
  }
  if (!strcmp(s, "online") && g_model.linked) sendNet();
  model_mark(D_LINK);
}

void pushActivity(const char* icon, const char* text, char state) {
  // A finished tool replaces its own "running" line (same icon, still running)
  if (state != 'r') {
    for (int i = 0; i < g_model.actCount; i++) {
      Activity& a = g_model.acts[i];
      if (a.state == 'r' && !strcmp(a.icon, icon)) {
        MODEL_SET(a.text, text);
        a.state = state;
        model_mark(D_ACT);
        return;
      }
    }
  }
  for (int i = 2; i > 0; i--) g_model.acts[i] = g_model.acts[i - 1];
  MODEL_SET(g_model.acts[0].icon, icon);
  MODEL_SET(g_model.acts[0].text, text);
  g_model.acts[0].state = state;
  if (g_model.actCount < 3) g_model.actCount++;
  model_mark(D_ACT);
}

void onControl(JsonDocument& doc) {
  int i = doc["i"] | -1;
  if (i < 0 || i >= MAX_CONTROLS) return;
  Control& c = g_model.ctls[i];
  c = Control();
  MODEL_SET(c.id, doc["id"] | "");
  MODEL_SET(c.group, doc["g"] | "");
  MODEL_SET(c.name, doc["n"] | "");
  const char* k = doc["k"] | "num";
  c.kind = !strcmp(k, "bool") ? CtlKind::Bool : !strcmp(k, "act") ? CtlKind::Act : !strcmp(k, "sel") ? CtlKind::Sel : CtlKind::Num;
  c.v = doc["v"] | 0.0f;
  c.min = doc["min"] | 0.0f;
  c.max = doc["max"] | 100.0f;
  c.step = doc["step"] | 1.0f;
  if (c.step <= 0) c.step = 1;
  MODEL_SET(c.unit, doc["u"] | "");
  c.on = doc["on"] | false;
  MODEL_SET(c.txt, doc["txt"] | "");
  JsonArray opts = doc["opts"].as<JsonArray>();
  c.nopts = 0;
  for (JsonVariant o : opts) {
    if (c.nopts >= 8) break;
    MODEL_SET(c.opts[c.nopts], o.as<const char*>());
    c.nopts++;
  }
  if (i + 1 > g_model.ctlCount) g_model.ctlCount = (uint8_t)(i + 1);
  model_mark(D_CTLS);
}

void onControlValue(JsonDocument& doc) {
  const char* id = doc["id"] | "";
  for (int i = 0; i < g_model.ctlCount; i++) {
    Control& c = g_model.ctls[i];
    if (strcmp(c.id, id) != 0) continue;
    // while the person is turning this one, the puck owns its value
    if (s_ctlPending && !strcmp(s_ctlId, id)) return;
    if (millis() - s_ctlSentAt < 600 && !strcmp(s_ctlId, id)) return;
    if (doc["v"].is<float>()) c.v = doc["v"].as<float>();
    if (doc["on"].is<bool>()) c.on = doc["on"].as<bool>();
    if (doc["txt"].is<const char*>()) MODEL_SET(c.txt, doc["txt"].as<const char*>());
    model_mark(D_CTLV);
    return;
  }
}

void onTimer(JsonDocument& doc) {
  const char* id = doc["id"] | "";
  if (!id[0]) return;
  int slot = -1, free = -1;
  for (int i = 0; i < MAX_TIMERS; i++) {
    if (g_model.timers[i].active && !strcmp(g_model.timers[i].id, id)) slot = i;
    if (!g_model.timers[i].active && free < 0) free = i;
  }
  if (doc["cancel"] | false) {
    if (slot >= 0) g_model.timers[slot].active = false;
  } else {
    if (slot < 0) slot = free;
    if (slot < 0) slot = 0;  // full: the oldest goes
    Timer& t = g_model.timers[slot];
    uint32_t ms = (uint32_t)(doc["sec"] | 0) * 1000u;
    bool fresh = !t.active || strcmp(t.id, id) != 0;
    t.active = true;
    MODEL_SET(t.id, id);
    MODEL_SET(t.label, doc["label"] | "");
    t.endsAtMs = millis() + ms;
    if (fresh || ms > t.totalMs) t.totalMs = ms > 0 ? ms : 1;
  }
  model_mark(D_TIMERS);
}

void replyDiag(uint8_t tp) {
  static char m[700];
  hal::diag(m, sizeof m);
  size_t n = strlen(m);
  snprintf(m + n, sizeof m - n, " net=%d ws=%d usb=%d ip=%s rssi=%d", (int)g_model.net, (int)welcomed(TP_WS),
           (int)welcomed(TP_USB), g_model.ip, g_model.rssi);
  JsonDocument d;
  d["t"] = "diag";
  d["m"] = m;
  send(d, tp);
}

}  // namespace

void applyCfgToHal() {
  hal::setSoundEnabled(g_model.sound);
  hal::setTurnSensitivity(g_model.sens);
  hal::setTurnFlip(g_model.flip);
  hal::setSpeakerVolume(g_model.spk);
  hal::setMicGain(g_model.micGain);
}

void onLine(uint8_t tp, char* line, size_t len) {
  JsonDocument doc;
  if (deserializeJson(doc, line, len)) return;
  const char* t = doc["t"] | "";

  // ---- notes from the net task
  if (!strcmp(t, "_net")) return onNetNote(doc);
  if (!strcmp(t, "_ws") || !strcmp(t, "_usb")) {
    if (!strcmp(doc["s"] | "", "down")) {
      setWelcomed(!strcmp(t, "_ws") ? TP_WS : TP_USB, false);
      if (!welcomed(TP_WS) && !welcomed(TP_USB) && g_model.net == NetState::Online) g_model.net = NetState::Connecting;
    } else if (!g_model.linked) {
      g_model.net = NetState::Connecting;
    }
    refreshLinked();
    model_mark(D_LINK);
    return;
  }

  // ---- direct mode stands in for a hub: only its current turn counts, and never over a real hub
  if (tp == TP_DIRECT) {
    if (g_model.linked || (doc["turn"] | -1) != directTurn()) return;
    if (strcmp(t, "ai") && strcmp(t, "heard") && strcmp(t, "reply") && strcmp(t, "act") && strcmp(t, "listen")) return;
  }

  // ---- the handshake
  if (!strcmp(t, "hello")) return;  // the hub on USB (the net task answered)
  if (!strcmp(t, "welcome")) {
    if (doc["ok"] | false) {
      const char* token = doc["token"] | "";
      if (token[0]) {
        char cur[40];
        storeToken(cur, sizeof cur);
        if (strcmp(cur, token) != 0) storeSetToken(token);
      }
      MODEL_SET(g_model.hub, doc["hub"] | "");
      if (doc["name"].is<const char*>()) MODEL_SET(g_model.puckName, doc["name"].as<const char*>());
      g_model.pendingCode[0] = 0;
      g_model.linkError[0] = 0;
      setWelcomed(tp, true);
      refreshLinked();
      hal::logLine("[link] welcomed over %s", tp == TP_USB ? "USB" : tp == TP_WS ? "Wi-Fi" : "?");
      sendBat(true);
      sendNet();
      replyDiag(tp);  // the hub logs it: a snapshot of the board on every connect
      if (s_lastFace[0]) {
        JsonDocument f;
        f["t"] = "face";
        f["f"] = s_lastFace;
        send(f, tp);
      }
    } else if (doc["pending"] | false) {
      g_model.net = NetState::Pending;
      MODEL_SET(g_model.pendingCode, doc["code"] | "");
    } else {
      MODEL_SET(g_model.linkError, doc["error"] | "The hub said no");
    }
    model_mark(D_LINK);
    return;
  }

  // ---- setup and debugging: allowed on USB before (or without) a welcome
  if (!strcmp(t, "diag")) return replyDiag(tp);
  if (!strcmp(t, "wifi")) {
    storeSetWifi(doc["ssid"] | "", doc["pass"] | "");
    netReconfigure();
    JsonDocument r;
    r["t"] = "wifi";
    r["s"] = "saved";
    send(r, tp);
    return;
  }
  if (!strcmp(t, "hubaddr")) {
    storeSetHubUrl(doc["url"] | "");
    netReconfigure();
    return;
  }
  if (!strcmp(t, "setup")) {
    netOpenPortal();
    return;
  }

  if (tp != TP_DIRECT && !welcomed(tp)) return;

  if (!strcmp(t, "pong")) return;
  if (!strcmp(t, "direct")) {
    // keys and settings for talking to Claude without the hub (kept in flash)
    directFromHub(doc["a"] | "", line);
    return;
  }
  if (!strcmp(t, "time")) {
    g_model.epochAtSync = doc["epoch"] | 0LL;
    g_model.tzMin = doc["tz"] | 0;
    g_model.msAtSync = millis();
    g_model.timeValid = g_model.epochAtSync > 0;
    // the system clock and zone too, so the time stays right without the hub
    if (g_model.timeValid) {
      struct timeval tv = {(time_t)g_model.epochAtSync, 0};
      settimeofday(&tv, nullptr);
    }
    applyTz(doc["tzp"] | "", true);
    static int savedTz = 0x7FFF;
    if (g_model.tzMin != savedTz) {
      savedTz = g_model.tzMin;
      char v[12];
      snprintf(v, sizeof v, "%d", g_model.tzMin);
      storeSetString("tzm", v);
    }
    directSetTzMin(g_model.tzMin);
    model_mark(D_TIME);
  } else if (!strcmp(t, "cfg")) {
    if (doc["bright"].is<int>()) g_model.bright = clampv(doc["bright"].as<int>(), 10, 100);
    if (doc["sound"].is<bool>()) g_model.sound = doc["sound"].as<bool>();
    if (doc["dim"].is<int>()) g_model.dimSec = std::max(0, doc["dim"].as<int>());
    if (doc["off"].is<int>()) g_model.offSec = std::max(0, doc["off"].as<int>());
    if (doc["sens"].is<float>()) g_model.sens = clampv(doc["sens"].as<float>(), 0.25f, 4.0f);
    if (doc["flip"].is<bool>()) g_model.flip = doc["flip"].as<bool>();
    if (doc["spk"].is<int>()) g_model.spk = clampv(doc["spk"].as<int>(), 0, 100);
    if (doc["mic"].is<int>()) g_model.micGain = clampv(doc["mic"].as<int>(), 0, 37);
    if (doc["lift"].is<bool>()) g_model.lift = doc["lift"].as<bool>();
    if (doc["name"].is<const char*>()) MODEL_SET(g_model.puckName, doc["name"].as<const char*>());
    applyCfgToHal();
    storeSaveCfgSoon();
    model_mark(D_CFG);
  } else if (!strcmp(t, "state")) {
    uint32_t d = 0;
    if (doc["vol"].is<int>()) { g_model.vol = clampv(doc["vol"].as<int>(), 0, 100); g_model.hasVol = true; d |= D_AUDIO; }
    if (doc["mute"].is<bool>()) { g_model.mute = doc["mute"].as<bool>(); d |= D_AUDIO; }
    if (doc["play"].is<bool>()) { g_model.playing = doc["play"].as<bool>(); d |= D_MEDIA; }
    if (doc["title"].is<const char*>()) { MODEL_SET(g_model.title, doc["title"].as<const char*>()); d |= D_MEDIA; }
    if (doc["artist"].is<const char*>()) { MODEL_SET(g_model.artist, doc["artist"].as<const char*>()); d |= D_MEDIA; }
    if (doc["src"].is<const char*>()) { MODEL_SET(g_model.src, doc["src"].as<const char*>()); d |= D_MEDIA; }
    model_mark(d);
  } else if (!strcmp(t, "ai")) {
    AiState s = parseAi(doc["s"] | "idle");
    if (s == AiState::Error) MODEL_SET(g_model.aiError, doc["text"] | "Something went wrong");
    if (s == AiState::Listening && g_model.ai != AiState::Listening) {
      // a new question: clear the last one
      g_model.heard[0] = 0;
      g_model.reply[0] = 0;
      g_model.replyDone = false;
      g_model.actCount = 0;
      model_mark(D_ACT);
    }
    setAi(s);
    model_mark(D_AI);
  } else if (!strcmp(t, "heard")) {
    MODEL_SET(g_model.heard, doc["text"] | "");
    model_mark(D_AI);
  } else if (!strcmp(t, "reply")) {
    MODEL_SET(g_model.reply, doc["text"] | "");
    g_model.replyDone = doc["done"] | false;
    model_mark(D_AI);
  } else if (!strcmp(t, "act")) {
    const char* s = doc["s"] | "run";
    pushActivity(doc["icon"] | "tool", doc["text"] | "", s[0] == 'o' ? 'o' : s[0] == 'e' ? 'e' : 'r');
  } else if (!strcmp(t, "listen")) {
    g_model.listenRequest = true;
    model_mark(D_LISTEN);
  } else if (!strcmp(t, "ctls")) {
    int n = doc["n"] | 0;
    g_model.ctlCount = 0;
    for (int i = 0; i < MAX_CONTROLS; i++) g_model.ctls[i] = Control();
    (void)n;
    model_mark(D_CTLS);
  } else if (!strcmp(t, "ctl")) {
    onControl(doc);
  } else if (!strcmp(t, "ctlv")) {
    onControlValue(doc);
  } else if (!strcmp(t, "timer")) {
    onTimer(doc);
  } else if (!strcmp(t, "notify")) {
    MODEL_SET(g_model.notifyText, doc["text"] | "");
    const char* k = doc["kind"] | "info";
    g_model.notifyKind = !strcmp(k, "ai") ? NotifyKind::Ai : !strcmp(k, "alert") ? NotifyKind::Alert : NotifyKind::Info;
    g_model.notifyChime = doc["chime"] | false;
    g_model.notifyAt = millis();
    if (g_model.notifyChime) hal::beep(Tone::Chime);
    model_mark(D_NOTIFY);
  } else if (!strcmp(t, "fv")) {
    // the talking face: the reply is over (what's queued still plays)
    if (!strcmp(doc["a"] | "", "end")) face_video::end();
  } else if (!strcmp(t, "confirm")) {
    g_model.confirmActive = true;
    MODEL_SET(g_model.confirmId, doc["id"] | "");
    MODEL_SET(g_model.confirmText, doc["text"] | "Are you sure?");
    hal::beep(Tone::Select);
    model_mark(D_CONFIRM);
  } else if (!strcmp(t, "confirm_end")) {
    if (!strcmp(g_model.confirmId, doc["id"] | "")) {
      g_model.confirmActive = false;
      model_mark(D_CONFIRM);
    }
  } else if (!strcmp(t, "face")) {
    MODEL_SET(g_model.faceRequest, doc["f"] | "");
    model_mark(D_FACE_REQ);
  } else if (!strcmp(t, "forget")) {
    // the hub let this puck go: its keys go too
    directFromHub("off", "");
    storeSetToken("");
    setWelcomed(TP_WS, false);
    setWelcomed(TP_USB, false);
    refreshLinked();
  }
}

void protoTick(uint32_t now) {
  sendBat(false);

  // Direct mode: no hub, but on Wi-Fi with keys from one. Talking then goes straight to Claude.
  bool direct = !g_model.linked && g_model.ip[0] && !netPortalOpen() && directConfigured();
  if (direct != g_model.direct) {
    g_model.direct = direct;
    if (!direct && !g_model.linked && g_model.ai != AiState::Idle) {
      // lost Wi-Fi in the middle of a question
      directTalk("cancel", false);
      hal::speakerStop();
      setAi(AiState::Idle);
    }
    model_mark(D_LINK | D_AI);
  }

  // The clock with no hub: the system time (from the internet, or the hub's last word) in the
  // hub's time zone
  if (!g_model.linked && (!s_lastClock || now - s_lastClock > 30000)) {
    s_lastClock = now | 1;
    time_t t = time(nullptr);
    if (t > 1700000000) {
      g_model.epochAtSync = t;
      g_model.msAtSync = millis();
      if (s_haveTz) g_model.tzMin = tzMinutesAt(t);
      g_model.timeValid = true;
      directSetTzMin(g_model.tzMin);
      model_mark(D_TIME);
    }
  }

  // The puck's own timers ring here (the hub rings its own)
  for (auto& tm : g_model.timers) {
    if (!tm.active || !tm.local || tm.rung || (int32_t)(now - tm.endsAtMs) < 0) continue;
    tm.rung = true;
    char msg[64];
    snprintf(msg, sizeof msg, "%s is done", tm.label[0] ? tm.label : "Your timer");
    MODEL_SET(g_model.notifyText, msg);
    g_model.notifyKind = NotifyKind::Alert;
    g_model.notifyChime = true;
    g_model.notifyAt = now;
    hal::beep(Tone::Chime);
    model_mark(D_NOTIFY | D_TIMERS);
  }

  // what the board layer logged (chips that didn't answer at boot…) goes to the hub's log
  static uint32_t lastLog = 0;
  if (g_model.linked && now - lastLog > 50) {
    lastLog = now;
    char line[120];
    if (hal::takeLog(line, sizeof line)) {
      JsonDocument d;
      d["t"] = "log";
      d["m"] = line;
      send(d);
    }
  }
  if (s_ctlPending && now - s_ctlSentAt >= 120) {
    JsonDocument d;
    d["t"] = "ctl";
    d["id"] = s_ctlId;
    d["v"] = s_ctlV;
    send(d);
    s_ctlPending = false;
    s_ctlSentAt = now;
  }
  if (s_volPending >= 0 && now - s_volSentAt >= 80) {
    JsonDocument d;
    d["t"] = "media";
    d["a"] = "vol";
    d["v"] = s_volPending;
    send(d);
    s_volPending = -1;
    s_volSentAt = now;
  }
  if (s_spkPending >= 0 && now - s_spkSentAt >= 500) {
    JsonDocument d;
    d["t"] = "spkvol";
    d["v"] = s_spkPending;
    send(d);
    s_spkPending = -1;
    s_spkSentAt = now;
  }
}

}  // namespace detail

// ---------------------------------------------------------------- link.h

using namespace detail;

void begin() {
  storeBegin();
  queuesBegin();
  storeLoadCfg();
  std::string tz;
  if (storeGetString("tzm", tz)) g_model.tzMin = atoi(tz.c_str());
  if (storeGetString("tzp", tz)) applyTz(tz.c_str(), false);
  directSetTzMin(g_model.tzMin);
  char ssid[33], pass[65];
  g_model.net = storeWifi(ssid, sizeof ssid, pass, sizeof pass) ? NetState::Joining : NetState::NoWifi;
  netBegin();
  directBegin();
  model_mark(D_LINK);
}

void poll() {
  uint8_t tp = 0;
  size_t len = 0;
  for (int i = 0; i < 32; i++) {
    char* line = takeLine(&tp, &len);
    if (!line) break;
    onLine(tp, line, len);
    doneLine(line);
  }
  protoTick(millis());
  storePoll();
}

bool online() { return g_model.linked; }

void talk(const char* action, const char* via) {
  bool direct = !g_model.linked && g_model.direct;
  if (!strcmp(action, "start")) {
    if (!g_model.linked && !direct) return;
    // Start the mic now (don't wait a round trip): the hub's "listening" confirms it
    g_model.heard[0] = 0;
    g_model.reply[0] = 0;
    g_model.replyDone = false;
    g_model.actCount = 0;
    hal::speakerStop();
    detail::setAi(AiState::Listening);
    model_mark(D_ACT);
  } else if (!strcmp(action, "stop")) {
    if (g_model.ai == AiState::Listening) detail::setAi(AiState::Thinking);
  } else if (!strcmp(action, "cancel")) {
    hal::speakerStop();
    detail::setAi(AiState::Idle);
  }
  if (direct) {
    directTalk(action, via && !strcmp(via, "lift"));
    return;
  }
  JsonDocument d;
  d["t"] = "talk";
  d["a"] = action;
  if (via) d["via"] = via;
  send(d);
}

void media(const char* action, int value) {
  if (!strcmp(action, "vol")) {
    s_volPending = clampv(value, 0, 100);
    return;
  }
  JsonDocument d;
  d["t"] = "media";
  d["a"] = action;
  send(d);
}

void controlValue(const char* id, float v) {
  if (s_ctlPending && strcmp(s_ctlId, id) != 0) {
    // a different dial: flush the old one first
    JsonDocument d;
    d["t"] = "ctl";
    d["id"] = s_ctlId;
    d["v"] = s_ctlV;
    send(d);
  }
  MODEL_SET(s_ctlId, id);
  s_ctlV = v;
  s_ctlPending = true;
}

void controlToggle(const char* id, bool on) {
  JsonDocument d;
  d["t"] = "ctl";
  d["id"] = id;
  d["on"] = on;
  send(d);
}

void controlPress(const char* id) {
  JsonDocument d;
  d["t"] = "ctl";
  d["id"] = id;
  d["a"] = "press";
  send(d);
}

void timerStart(int seconds, const char* label) {
  if (!g_model.linked) {
    // no hub: the puck keeps (and rings) this one itself
    int slot = 0;
    for (int i = 0; i < MAX_TIMERS; i++)
      if (!g_model.timers[i].active) {
        slot = i;
        break;
      }
    Timer& t = g_model.timers[slot];
    t = Timer();
    t.active = true;
    t.local = true;
    snprintf(t.id, sizeof t.id, "L%d", ++s_localTimers);
    MODEL_SET(t.label, label ? label : "");
    t.totalMs = (uint32_t)(seconds > 0 ? seconds : 1) * 1000u;
    t.endsAtMs = millis() + t.totalMs;
    model_mark(D_TIMERS);
    return;
  }
  JsonDocument d;
  d["t"] = "timer";
  d["a"] = "start";
  d["sec"] = seconds;
  if (label && label[0]) d["label"] = label;
  send(d);
}

void timerCancel(const char* id) {
  for (auto& t : g_model.timers)
    if (t.active && t.local && !strcmp(t.id, id)) {
      t.active = false;
      model_mark(D_TIMERS);
      return;
    }
  JsonDocument d;
  d["t"] = "timer";
  d["a"] = "cancel";
  d["id"] = id;
  send(d);
}

void answer(const char* id, bool ok) {
  JsonDocument d;
  d["t"] = "ans";
  d["id"] = id;
  d["ok"] = ok;
  send(d);
  g_model.confirmActive = false;
  model_mark(D_CONFIRM);
}

void face(const char* f) {
  if (!strcmp(s_lastFace, f)) return;
  MODEL_SET(s_lastFace, f);
  setCurrentFace(f);
  JsonDocument d;
  d["t"] = "face";
  d["f"] = f;
  send(d);
}

void speakerVolume(int v) {
  g_model.spk = clampv(v, 0, 100);
  hal::setSpeakerVolume(g_model.spk);
  storeSaveCfgSoon();
  s_spkPending = g_model.spk;
}

void log(const char* fmt, ...) {
  char m[200];
  va_list ap;
  va_start(ap, fmt);
  vsnprintf(m, sizeof m, fmt, ap);
  va_end(ap);
  JsonDocument d;
  d["t"] = "log";
  d["m"] = m;
  send(d);
}

void openSetupPortal() { netOpenPortal(); }
void closeSetupPortal() { netClosePortal(); }
bool portalOpen() { return netPortalOpen(); }

void forgetWifi() {
  storeClearWifi();
  netReconfigure();
}

}  // namespace hub
