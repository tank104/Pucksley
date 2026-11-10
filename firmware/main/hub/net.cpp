// The network task: Wi-Fi, finding the hub (mDNS), the WebSocket, USB serial, and the voice audio.
// Runs on core 0 so the screen never stalls the speech. It owns every transport; the main loop
// only exchanges JSON lines with it through two ring buffers (queues.cpp).
//
// The WebSocket client runs in a task of its own (esp_websocket_client): its events push incoming
// lines and speech audio straight into the queues/speaker buffer, and set flags this task acts on.
#include "../platform.h"
#include <ArduinoJson.h>
#include <atomic>
#include <ctype.h>
#include <string.h>
#include <driver/usb_serial_jtag.h>
#include <esp_event.h>
#include <esp_mac.h>
#include <esp_netif.h>
#include <esp_netif_sntp.h>
#include <esp_websocket_client.h>
#include <esp_wifi.h>
#include <mbedtls/base64.h>
#include <mdns.h>
#include "../config.h"
#include "../hal.h"
#include "../model.h"
#include "internal.h"

namespace hub {
namespace detail {

namespace {

enum class W : uint8_t { Idle, Joining, Online, Portal };

esp_websocket_client_handle_t s_ws = nullptr;
std::atomic<bool> s_wsUp{false};
std::atomic<bool> s_wsHello{false};      // connected: say hello (from the net task)
std::atomic<int> s_wsPongs{0};           // pings to answer on the WebSocket
std::atomic<bool> s_wsDropped{false};    // disconnected since the net task last looked
std::atomic<bool> s_welcomedWs{false};
std::atomic<bool> s_welcomedUsb{false};

char s_id[17] = "";
char s_name[16] = "";
char s_face[16] = "assistant";
portMUX_TYPE s_faceMux = portMUX_INITIALIZER_UNLOCKED;

// Wi-Fi
esp_netif_t* s_sta = nullptr;
W s_w = W::Idle;
char s_ssid[33] = "";
char s_pass[65] = "";
uint32_t s_joinStarted = 0;
uint32_t s_nextJoin = 0;
int s_joinFails = 0;
bool s_autoPortalDone = false;
bool s_sntpStarted = false;   // the clock from the internet (for direct mode and the time with no hub)
uint32_t s_lastNetReport = 0;
std::atomic<bool> s_gotIp{false};
std::atomic<int> s_lastReason{0};
char s_ipText[16] = "";

// the hub
char s_hubUrl[128] = "";
char s_hubHost[64] = "";
uint16_t s_hubPort = HUB_DEFAULT_PORT;
char s_hubPath[32] = HUB_PATH;
bool s_hubKnown = false;
bool s_hubFromMdns = false;
uint32_t s_nextDiscovery = 0;
uint32_t s_wsStartedAt = 0;
int s_wsFails = 0;

std::atomic<bool> s_reconfigure{false};
std::atomic<bool> s_openPortal{false};
std::atomic<bool> s_closePortal{false};
std::atomic<bool> s_portalOpen{false};

// USB
char s_usbLine[LINK_MAX_LINE + 1];
size_t s_usbLen = 0;
bool s_usbOverflow = false;
uint32_t s_usbLastHub = 0;   // millis() of the last line from a hub on USB (0 = never)

// incoming WebSocket text being reassembled, and an odd byte of audio between chunks
char* s_wsText = nullptr;
size_t s_wsTextLen = 0;
uint8_t s_oddByte = 0;
bool s_haveOdd = false;

// audio out to the hub
int16_t s_mic[AUDIO_FRAME];
size_t s_micFill = 0;
bool s_micWasOn = false;

// an outgoing line that didn't fit in the USB buffer yet
char* s_pendingOut = nullptr;
uint8_t s_pendingTp = TP_NONE;
size_t s_pendingLen = 0;

// ---------------------------------------------------------------- helpers

// A status note for the main loop (it owns g_model)
void note(const char* json) { pushIncoming(TP_NONE, json, strlen(json)); }

int rssiNow() {
  wifi_ap_record_t ap;
  return esp_wifi_sta_get_ap_info(&ap) == ESP_OK ? ap.rssi : 0;
}

void noteNet(const char* state, const char* err = nullptr) {
  JsonDocument d;
  d["t"] = "_net";
  d["s"] = state;
  if (s_w == W::Online || s_w == W::Joining) d["ssid"] = s_ssid;
  if (s_gotIp.load()) {
    d["ip"] = s_ipText;
    d["rssi"] = rssiNow();
  }
  if (err) d["err"] = err;
  char buf[256];
  size_t n = serializeJson(d, buf, sizeof buf);
  pushIncoming(TP_NONE, buf, n);
}

bool usbHostThere() { return usb_serial_jtag_is_connected(); }

// One whole line or nothing (the driver's ring buffer takes all of it or none)
bool writeUsb(const char* data, size_t len) {
  if (!usbHostThere()) return false;
  static char line[LINK_MAX_LINE + 2];
  if (len + 1 > sizeof line) return false;
  memcpy(line, data, len);
  line[len] = '\n';
  return usb_serial_jtag_write_bytes(line, len + 1, 0) == (int)(len + 1);
}

bool usbHubPresent() { return s_usbLastHub && millis() - s_usbLastHub < LINK_HOST_TIMEOUT_MS; }

size_t buildHello(char* buf, size_t len) {
  char token[40];
  storeToken(token, sizeof token);
  JsonDocument d;
  d["t"] = "hello";
  d["v"] = PUCK_PROTOCOL_VERSION;
  d["fw"] = PUCK_FW_VERSION;
  d["id"] = s_id;
  d["name"] = s_name;
  d["token"] = token;
  d["bat"] = hal::batteryPercent();
  d["chg"] = hal::charging();
  d["usb"] = hal::usbPowered();
  if (s_gotIp.load()) {
    d["ip"] = s_ipText;
    d["rssi"] = rssiNow();
    d["ssid"] = s_ssid;
  }
  char face[16];
  portENTER_CRITICAL(&s_faceMux);
  memcpy(face, s_face, sizeof face);
  portEXIT_CRITICAL(&s_faceMux);
  d["face"] = face;
  JsonArray caps = d["caps"].to<JsonArray>();
  caps.add("mic");
  caps.add("spk");
  return serializeJson(d, buf, len);
}

bool sendWs(const char* json, size_t len) {
  if (!s_ws || !s_wsUp.load()) return false;
  return esp_websocket_client_send_text(s_ws, json, (int)len, pdMS_TO_TICKS(1000)) >= 0;
}

void sendOn(uint8_t tp, const char* json, size_t len) {
  if (tp == TP_WS) sendWs(json, len);
  else if (tp == TP_USB) writeUsb(json, len);
}

// ---------------------------------------------------------------- incoming

bool startsWith(const char* data, size_t len, const char* prefix) {
  size_t n = strlen(prefix);
  return len >= n && memcmp(data, prefix, n) == 0;
}

// The few messages handled here; everything else goes to the main loop. Called from the net task
// (USB) and from the WebSocket client's task (Wi-Fi).
void handleIncoming(uint8_t tp, const char* data, size_t len) {
  if (len == 0 || data[0] != '{') return;  // boot noise on USB
  if (tp == TP_USB) s_usbLastHub = millis() | 1;

  if (startsWith(data, len, "{\"t\":\"ping\"")) {
    static const char pong[] = "{\"t\":\"pong\"}";
    if (tp == TP_WS) s_wsPongs++;
    else writeUsb(pong, sizeof pong - 1);
    return;
  }
  if (startsWith(data, len, "{\"t\":\"au\"")) {
    // speech over USB, base64
    const char* d = strstr(data, "\"d\":\"");
    if (!d) return;
    d += 5;
    const char* e = (const char*)memchr(d, '"', len - (d - data));
    if (!e) return;
    static uint8_t pcm[LINK_MAX_LINE];
    size_t out = 0;
    if (mbedtls_base64_decode(pcm, sizeof pcm, &out, (const uint8_t*)d, e - d) == 0 && out >= 2)
      hal::speakerWrite((const int16_t*)pcm, out / 2);
    return;
  }
  if (startsWith(data, len, "{\"t\":\"spk\"")) {
    JsonDocument doc;
    if (deserializeJson(doc, data, len)) return;
    const char* a = doc["a"] | "";
    int id = doc["id"] | -1;
    if (strcmp(a, "start") == 0) {
      s_haveOdd = false;
      hal::speakerBegin(id);
    } else if (strcmp(a, "end") == 0) {
      hal::speakerEnd(id);
    } else if (strcmp(a, "stop") == 0) {
      hal::speakerStop();
    }
    return;
  }
  if (tp == TP_USB && startsWith(data, len, "{\"t\":\"hello\"")) {
    // the hub says hello first on USB: answer, then let the main loop know it's there
    char buf[512];
    size_t n = buildHello(buf, sizeof buf);
    writeUsb(buf, n);
  }
  pushIncoming(tp, data, len);
}

// ---------------------------------------------------------------- WebSocket (its own task)

void speechBytes(const uint8_t* p, size_t n) {
  static int16_t pcm[1024];
  size_t i = 0;
  if (s_haveOdd && n > 0) {
    uint8_t two[2] = {s_oddByte, p[0]};
    int16_t s;
    memcpy(&s, two, 2);
    hal::speakerWrite(&s, 1);
    s_haveOdd = false;
    i = 1;
  }
  while (i + 1 < n) {
    size_t samples = std::min((n - i) / 2, sizeof(pcm) / sizeof(pcm[0]));
    memcpy(pcm, p + i, samples * 2);  // aligned copy
    hal::speakerWrite(pcm, samples);
    i += samples * 2;
  }
  if (i < n) {
    s_oddByte = p[i];
    s_haveOdd = true;
  }
}

void onWsEvent(void*, esp_event_base_t, int32_t id, void* event) {
  auto* d = (esp_websocket_event_data_t*)event;
  switch (id) {
    case WEBSOCKET_EVENT_CONNECTED:
      s_wsUp = true;
      s_wsFails = 0;
      s_wsHello = true;
      break;
    case WEBSOCKET_EVENT_DISCONNECTED:
    case WEBSOCKET_EVENT_CLOSED:
      if (s_wsUp.exchange(false)) s_wsDropped = true;
      else s_wsFails++;
      s_welcomedWs = false;
      break;
    case WEBSOCKET_EVENT_ERROR:
      if (!s_wsUp.load()) s_wsFails++;
      break;
    case WEBSOCKET_EVENT_DATA: {
      if (!d || d->data_len <= 0) break;
      if (d->op_code == 0x2) {
        speechBytes((const uint8_t*)d->data_ptr, d->data_len);
      } else if (d->op_code == 0x1 || (d->op_code == 0x0 && s_wsText)) {
        // text: may arrive in pieces (payload_offset / payload_len)
        size_t total = d->payload_len > 0 ? (size_t)d->payload_len : (size_t)d->data_len;
        if (total > 16384) break;
        if (d->payload_offset == 0) {
          free(s_wsText);
          s_wsText = (char*)malloc(total + 1);
          s_wsTextLen = 0;
        }
        if (!s_wsText || s_wsTextLen + d->data_len > total) break;
        memcpy(s_wsText + s_wsTextLen, d->data_ptr, d->data_len);
        s_wsTextLen += d->data_len;
        if (s_wsTextLen >= total) {
          s_wsText[s_wsTextLen] = 0;
          handleIncoming(TP_WS, s_wsText, s_wsTextLen);
          free(s_wsText);
          s_wsText = nullptr;
          s_wsTextLen = 0;
        }
      }
      break;
    }
    default:
      break;
  }
}

bool parseHubUrl(const char* url) {
  // ws://host[:port][/path]
  const char* p = url;
  if (strncmp(p, "ws://", 5) == 0) p += 5;
  else if (strncmp(p, "http://", 7) == 0) p += 7;
  const char* slash = strchr(p, '/');
  const char* colon = strchr(p, ':');
  size_t hostLen = colon && (!slash || colon < slash) ? (size_t)(colon - p) : slash ? (size_t)(slash - p) : strlen(p);
  if (hostLen == 0 || hostLen >= sizeof s_hubHost) return false;
  memcpy(s_hubHost, p, hostLen);
  s_hubHost[hostLen] = 0;
  s_hubPort = colon && (!slash || colon < slash) ? (uint16_t)atoi(colon + 1) : HUB_DEFAULT_PORT;
  model_copy(s_hubPath, sizeof s_hubPath, slash && slash[1] ? slash : HUB_PATH);
  return s_hubPort != 0;
}

bool discoverHub() {
  mdns_result_t* results = nullptr;
  if (mdns_query_ptr("_" HUB_MDNS_SERVICE, "_tcp", 2500, 4, &results) != ESP_OK || !results) return false;
  bool found = false;
  for (mdns_result_t* r = results; r && !found; r = r->next) {
    for (mdns_ip_addr_t* a = r->addr; a; a = a->next) {
      if (a->addr.type != ESP_IPADDR_TYPE_V4) continue;
      snprintf(s_hubHost, sizeof s_hubHost, IPSTR, IP2STR(&a->addr.u_addr.ip4));
      s_hubPort = r->port;
      model_copy(s_hubPath, sizeof s_hubPath, HUB_PATH);
      for (size_t i = 0; i < r->txt_count; i++)
        if (r->txt[i].key && !strcmp(r->txt[i].key, "path") && r->txt[i].value) model_copy(s_hubPath, sizeof s_hubPath, r->txt[i].value);
      found = true;
      break;
    }
  }
  mdns_query_results_free(results);
  return found;
}

void stopWs() {
  if (s_ws) {
    esp_websocket_client_stop(s_ws);
    esp_websocket_client_destroy(s_ws);
    s_ws = nullptr;
  }
  if (s_wsUp.exchange(false) || s_wsDropped.exchange(false)) note("{\"t\":\"_ws\",\"s\":\"down\"}");
  s_welcomedWs = false;
}

void startWs() {
  static char uri[160];
  snprintf(uri, sizeof uri, "ws://%s:%u%s", s_hubHost, (unsigned)s_hubPort, s_hubPath);
  esp_websocket_client_config_t cfg = {};
  cfg.uri = uri;
  cfg.reconnect_timeout_ms = 3000;
  cfg.network_timeout_ms = 6000;
  cfg.buffer_size = 4096;
  cfg.task_stack = 6144;
  cfg.task_prio = 5;
  cfg.ping_interval_sec = 15;
  cfg.pingpong_timeout_sec = 30;
  s_ws = esp_websocket_client_init(&cfg);
  if (!s_ws) return;
  esp_websocket_register_events(s_ws, WEBSOCKET_EVENT_ANY, onWsEvent, nullptr);
  esp_websocket_client_start(s_ws);
  s_wsStartedAt = millis();
  s_wsFails = 0;
}

void hubStep(uint32_t now) {
  if (!s_hubKnown) {
    if (s_hubUrl[0] && parseHubUrl(s_hubUrl)) {
      s_hubKnown = true;
      s_hubFromMdns = false;
    } else if ((int32_t)(now - s_nextDiscovery) >= 0) {
      noteNet("searching");
      if (discoverHub()) {
        s_hubKnown = true;
        s_hubFromMdns = true;
      } else {
        s_nextDiscovery = millis() + 4000;
      }
    }
    if (s_hubKnown) {
      noteNet("connecting");
      startWs();
    }
    return;
  }
  if (s_wsHello.exchange(false)) {
    char buf[512];
    size_t n = buildHello(buf, sizeof buf);
    sendWs(buf, n);
    note("{\"t\":\"_ws\",\"s\":\"up\"}");
  }
  for (int n = s_wsPongs.exchange(0); n > 0; n--) {
    static const char pong[] = "{\"t\":\"pong\"}";
    sendWs(pong, sizeof pong - 1);
  }
  if (s_wsDropped.exchange(false)) note("{\"t\":\"_ws\",\"s\":\"down\"}");
  // An mDNS-found hub that keeps refusing may have moved: look again
  if (s_hubFromMdns && !s_wsUp.load() && (s_wsFails >= 4 || (s_wsStartedAt && now - s_wsStartedAt > 30000))) {
    stopWs();
    s_hubKnown = false;
    s_wsStartedAt = 0;
    s_nextDiscovery = now;
  }
}

// ---------------------------------------------------------------- Wi-Fi

void onWifiEvent(void*, esp_event_base_t base, int32_t id, void* data) {
  if (base == WIFI_EVENT && id == WIFI_EVENT_STA_DISCONNECTED) {
    s_gotIp = false;
    s_lastReason = ((wifi_event_sta_disconnected_t*)data)->reason;
  } else if (base == IP_EVENT && id == IP_EVENT_STA_GOT_IP) {
    auto* e = (ip_event_got_ip_t*)data;
    snprintf(s_ipText, sizeof s_ipText, IPSTR, IP2STR(&e->ip_info.ip));
    s_gotIp = true;
  }
}

void wifiInit() {
  esp_netif_init();
  esp_event_loop_create_default();
  s_sta = esp_netif_create_default_wifi_sta();
  esp_netif_create_default_wifi_ap();
  wifi_init_config_t cfg = WIFI_INIT_CONFIG_DEFAULT();
  esp_wifi_init(&cfg);
  esp_wifi_set_storage(WIFI_STORAGE_RAM);  // credentials live in our own NVS namespace
  esp_event_handler_register(WIFI_EVENT, WIFI_EVENT_STA_DISCONNECTED, onWifiEvent, nullptr);
  esp_event_handler_register(IP_EVENT, IP_EVENT_STA_GOT_IP, onWifiEvent, nullptr);
  esp_netif_set_hostname(s_sta, s_name);
  esp_wifi_set_mode(WIFI_MODE_STA);
  esp_wifi_start();
  esp_wifi_set_ps(WIFI_PS_NONE);  // modem sleep adds latency to every audio frame

  char host[24];
  snprintf(host, sizeof host, "%s", s_name);
  for (char* c = host; *c; c++) *c = (char)tolower((unsigned char)*c);
  if (mdns_init() == ESP_OK) mdns_hostname_set(host);
}

const char* reasonText(int reason) {
  switch (reason) {
    case WIFI_REASON_NO_AP_FOUND: return "Network not found";
    case WIFI_REASON_AUTH_FAIL:
    case WIFI_REASON_4WAY_HANDSHAKE_TIMEOUT:
    case WIFI_REASON_HANDSHAKE_TIMEOUT: return "Wrong Wi-Fi password?";
    default: return "Couldn't join the network";
  }
}

void startJoin(uint32_t now) {
  if (!storeWifi(s_ssid, sizeof s_ssid, s_pass, sizeof s_pass)) {
    s_w = W::Idle;
    return;
  }
  wifi_config_t wc = {};
  memcpy(wc.sta.ssid, s_ssid, strnlen(s_ssid, sizeof wc.sta.ssid));
  memcpy(wc.sta.password, s_pass, strnlen(s_pass, sizeof wc.sta.password));
  wc.sta.threshold.authmode = WIFI_AUTH_OPEN;  // accept whatever the network uses
  wc.sta.pmf_cfg.capable = true;
  wc.sta.scan_method = WIFI_ALL_CHANNEL_SCAN;
  wc.sta.sort_method = WIFI_CONNECT_AP_BY_SIGNAL;
  esp_wifi_set_mode(WIFI_MODE_STA);
  esp_wifi_set_config(WIFI_IF_STA, &wc);
  esp_wifi_disconnect();
  s_gotIp = false;
  s_lastReason = 0;
  esp_wifi_connect();
  s_joinStarted = now;
  s_w = W::Joining;
  noteNet("joining");
}

void wifiStep(uint32_t now) {
  switch (s_w) {
    case W::Idle: {
      char ssid[33], pass[65];
      if (storeWifi(ssid, sizeof ssid, pass, sizeof pass)) {
        if ((int32_t)(now - s_nextJoin) >= 0) startJoin(now);
      } else if (!s_autoPortalDone && now > 6000 && !usbHubPresent()) {
        // First boot: nothing saved and no hub on USB to send Wi-Fi details: open the setup network
        s_autoPortalDone = true;
        s_openPortal = true;
      } else if (now - s_lastNetReport > 5000) {
        s_lastNetReport = now;
        noteNet("nowifi");
      }
      break;
    }
    case W::Joining:
      if (s_gotIp.load()) {
        s_w = W::Online;
        s_joinFails = 0;
        if (!s_sntpStarted) {
          s_sntpStarted = true;
          esp_sntp_config_t sntp = ESP_NETIF_SNTP_DEFAULT_CONFIG("pool.ntp.org");
          esp_netif_sntp_init(&sntp);
        }
        s_hubKnown = false;
        s_nextDiscovery = now;
        storeHubUrl(s_hubUrl, sizeof s_hubUrl);
        noteNet("online");
      } else if (now - s_joinStarted > 20000) {
        s_joinFails++;
        esp_wifi_disconnect();
        s_w = W::Idle;
        s_nextJoin = now + (s_joinFails < 3 ? 3000 : 30000);
        noteNet("failed", reasonText(s_lastReason.load()));
      } else if (s_lastReason.load() && now - s_joinStarted > 4000 && now - s_joinStarted < 19000) {
        // a quick failure (no network / wrong password): try again soon, a few times
        int r = s_lastReason.exchange(0);
        esp_wifi_connect();
        if (r == WIFI_REASON_AUTH_FAIL || r == WIFI_REASON_NO_AP_FOUND) noteNet("joining", reasonText(r));
      }
      break;
    case W::Online:
      if (!s_gotIp.load()) {
        stopWs();
        s_w = W::Joining;
        s_joinStarted = now;
        esp_wifi_connect();
        noteNet("joining");
        break;
      }
      hubStep(now);
      if (now - s_lastNetReport > 60000) {
        s_lastNetReport = now;
        noteNet("online");
      }
      break;
    case W::Portal:
      portalLoop();
      if (portalTakeSaved()) {
        s_closePortal = true;
        s_nextJoin = now + 1500;  // let the "saved" page reach the phone first
      }
      break;
  }
}

// ---------------------------------------------------------------- USB

void usbRead() {
  uint8_t chunk[256];
  for (int budget = 8192; budget > 0;) {
    int got = usb_serial_jtag_read_bytes(chunk, sizeof chunk, 0);
    if (got <= 0) break;
    budget -= got;
    for (int i = 0; i < got; i++) {
      char c = (char)chunk[i];
      if (c == '\n') {
        if (!s_usbOverflow && s_usbLen) {
          s_usbLine[s_usbLen] = 0;
          handleIncoming(TP_USB, s_usbLine, s_usbLen);
        }
        s_usbLen = 0;
        s_usbOverflow = false;
      } else if (c == '\r') {
      } else if (s_usbLen < LINK_MAX_LINE) {
        s_usbLine[s_usbLen++] = c;
      } else {
        s_usbOverflow = true;
      }
    }
  }
  if (s_welcomedUsb.load() && !usbHubPresent()) {
    s_welcomedUsb = false;
    note("{\"t\":\"_usb\",\"s\":\"down\"}");
  }
}

// ---------------------------------------------------------------- outgoing

bool allowedBeforeWelcome(const char* json, size_t len) {
  return startsWith(json, len, "{\"t\":\"bat\"") || startsWith(json, len, "{\"t\":\"log\"") ||
         startsWith(json, len, "{\"t\":\"net\"") || startsWith(json, len, "{\"t\":\"diag\"") ||
         startsWith(json, len, "{\"t\":\"wifi\"") || startsWith(json, len, "{\"t\":\"hello\"");
}

// Where a line goes: the transport it was addressed to, else the preferred welcomed one, else
// (for the few messages allowed before a welcome) any connected one.
uint8_t routeFor(uint8_t tp, const char* json, size_t len) {
  if (tp != TP_NONE) return tp;
  uint8_t pref = preferredTransport();
  if (pref != TP_NONE) return pref;
  if (allowedBeforeWelcome(json, len)) {
    if (s_wsUp.load()) return TP_WS;
    if (usbHubPresent()) return TP_USB;
  }
  return TP_NONE;
}

void drainOutgoing() {
  for (int i = 0; i < 16; i++) {
    if (!s_pendingOut) {
      s_pendingOut = takeOutgoing(&s_pendingTp, &s_pendingLen);
      if (!s_pendingOut) return;
    }
    uint8_t dest = routeFor(s_pendingTp, s_pendingOut, s_pendingLen);
    if (dest == TP_WS) {
      sendWs(s_pendingOut, s_pendingLen);
    } else if (dest == TP_USB) {
      if (!writeUsb(s_pendingOut, s_pendingLen) && usbHostThere()) return;  // full: try again next round
    }
    // no route: dropped (the hub resyncs everything on welcome anyway)
    doneOutgoing(s_pendingOut);
    s_pendingOut = nullptr;
  }
}

// ---------------------------------------------------------------- audio

void sendMicFrame(const int16_t* pcm, size_t samples) {
  uint8_t dest = preferredTransport();
  if (dest == TP_WS && s_ws && s_wsUp.load()) {
    esp_websocket_client_send_bin(s_ws, (const char*)pcm, (int)(samples * 2), pdMS_TO_TICKS(500));
  } else if (dest == TP_USB) {
    static char line[LINK_MAX_LINE];
    int head = snprintf(line, sizeof line, "{\"t\":\"au\",\"d\":\"");
    size_t out = 0;
    if (mbedtls_base64_encode((uint8_t*)line + head, sizeof line - head - 4, &out, (const uint8_t*)pcm, samples * 2) != 0) return;
    size_t n = head + out;
    line[n++] = '"';
    line[n++] = '}';
    writeUsb(line, n);
  }
}

void pumpMic() {
  // Only toward a hub: in direct mode the mic is the direct task's to read
  bool on = hal::micActive() && preferredTransport() != TP_NONE;
  if (!on) {
    if (s_micWasOn && s_micFill) sendMicFrame(s_mic, s_micFill);  // the tail
    s_micFill = 0;
    s_micWasOn = false;
    return;
  }
  s_micWasOn = true;
  for (int i = 0; i < 8; i++) {
    size_t got = hal::micRead(s_mic + s_micFill, AUDIO_FRAME - s_micFill);
    if (!got) break;
    s_micFill += got;
    if (s_micFill == AUDIO_FRAME) {
      sendMicFrame(s_mic, s_micFill);
      s_micFill = 0;
    }
  }
}

void reportSpeakerDone() {
  int id = hal::speakerTakeDone();
  if (id < 0) return;
  char buf[48];
  int n = snprintf(buf, sizeof buf, "{\"t\":\"spk\",\"s\":\"done\",\"id\":%d}", id);
  uint8_t dest = preferredTransport();
  if (dest != TP_NONE) sendOn(dest, buf, n);
}

// ---------------------------------------------------------------- the task

void netTask(void*) {
  wifiInit();
  storeHubUrl(s_hubUrl, sizeof s_hubUrl);
  for (;;) {
    uint32_t now = millis();

    if (s_openPortal.exchange(false) && s_w != W::Portal) {
      stopWs();
      esp_wifi_disconnect();
      portalStart(s_name);
      s_w = W::Portal;
      s_portalOpen = true;
      note("{\"t\":\"_net\",\"s\":\"portal\"}");
    }
    if (s_closePortal.exchange(false) && s_w == W::Portal) {
      portalStop();
      s_portalOpen = false;
      s_w = W::Idle;
      if ((int32_t)(s_nextJoin - now) < 0) s_nextJoin = now;
    }
    if (s_reconfigure.exchange(false)) {
      stopWs();
      storeHubUrl(s_hubUrl, sizeof s_hubUrl);
      s_hubKnown = false;
      if (s_w == W::Portal) {
        s_closePortal = true;
      } else {
        esp_wifi_disconnect();
        s_gotIp = false;
        s_w = W::Idle;
        s_nextJoin = now;
        s_joinFails = 0;
      }
    }

    wifiStep(now);
    usbRead();
    drainOutgoing();
    pumpMic();
    reportSpeakerDone();
    vTaskDelay(pdMS_TO_TICKS(2));
  }
}

}  // namespace

void netBegin() {
  uint8_t mac[6] = {};
  esp_efuse_mac_get_default(mac);
  uint64_t v = 0;
  for (int i = 5; i >= 0; i--) v = (v << 8) | mac[i];
  // A stable id from the MAC (mixed so it doesn't read as the MAC itself), and the name every
  // Puck shows: the last two bytes of the MAC.
  uint64_t mixed = v * 0x9E3779B97F4A7C15ull;
  snprintf(s_id, sizeof s_id, "%08lx%08lx", (unsigned long)(mixed >> 32), (unsigned long)(mixed & 0xFFFFFFFF));
  snprintf(s_name, sizeof s_name, "Puck-%02X%02X", mac[4], mac[5]);
  xTaskCreatePinnedToCore(netTask, "net", 12288, nullptr, 5, nullptr, 0);
}

void setWelcomed(uint8_t tp, bool on) {
  if (tp == TP_WS) s_welcomedWs = on;
  else if (tp == TP_USB) s_welcomedUsb = on;
}

bool welcomed(uint8_t tp) { return tp == TP_WS ? s_welcomedWs.load() : tp == TP_USB ? s_welcomedUsb.load() : false; }

uint8_t preferredTransport() {
  if (s_welcomedWs.load() && s_wsUp.load()) return TP_WS;
  if (s_welcomedUsb.load()) return TP_USB;
  return TP_NONE;
}

void netReconfigure() { s_reconfigure = true; }
void netForgetToken() { storeSetToken(""); }
void netOpenPortal() { s_openPortal = true; }
void netClosePortal() { s_closePortal = true; }
bool netPortalOpen() { return s_portalOpen.load(); }
const char* deviceId() { return s_id; }
const char* deviceName() { return s_name; }

void setCurrentFace(const char* f) {
  portENTER_CRITICAL(&s_faceMux);
  model_copy(s_face, sizeof s_face, f);
  portEXIT_CRITICAL(&s_faceMux);
}

}  // namespace detail
}  // namespace hub
