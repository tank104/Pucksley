// Wi-Fi setup without a PC: the puck opens an access point named after itself ("Puck-1D4D") with a
// captive page at http://192.168.4.1/ where you pick the network, type its password and (if mDNS
// can't find it) the hub's address. Started and stopped by the net task; the web server and the
// little DNS server (every name → 192.168.4.1, which makes phones pop the page up) run in tasks
// of their own.
#include "../platform.h"
#include <ctype.h>
#include <string.h>
#include <string>
#include <esp_http_server.h>
#include <esp_wifi.h>
#include <lwip/sockets.h>
#include "../config.h"
#include "../hal.h"
#include "../model.h"
#include "internal.h"

namespace hub {
namespace detail {

namespace {

httpd_handle_t s_web = nullptr;
TaskHandle_t s_dnsTask = nullptr;
volatile bool s_dnsRun = false;
int s_dnsSock = -1;
bool s_running = false;
volatile bool s_saved = false;
std::string s_networks;  // <option>s from the last scan
char s_apName[24] = "";

std::string esc(const char* s) {
  std::string o;
  for (; *s; s++) {
    switch (*s) {
      case '&': o += "&amp;"; break;
      case '<': o += "&lt;"; break;
      case '>': o += "&gt;"; break;
      case '"': o += "&quot;"; break;
      default: o += *s;
    }
  }
  return o;
}

void scan() {
  wifi_scan_config_t cfg = {};
  cfg.show_hidden = false;
  s_networks.clear();
  if (esp_wifi_scan_start(&cfg, true) != ESP_OK) return;
  uint16_t n = 20;
  static wifi_ap_record_t recs[20];
  if (esp_wifi_scan_get_ap_records(&n, recs) != ESP_OK) return;
  // the driver returns them strongest first; drop duplicates and hidden ones
  for (int i = 0; i < n; i++) {
    const char* ssid = (const char*)recs[i].ssid;
    if (!ssid[0]) continue;
    std::string opt = "<option>" + esc(ssid) + "</option>";
    if (s_networks.find(opt) != std::string::npos) continue;
    s_networks += opt;
  }
}

const char kStyle[] = R"(<meta name=viewport content="width=device-width,initial-scale=1"><style>
body{font-family:system-ui,sans-serif;background:#111;color:#f5f5f7;margin:0;padding:24px;max-width:460px}
h1{font-size:22px;margin:0 0 4px}p{color:#aaa;margin:0 0 18px;line-height:1.4}
label{display:block;font-size:14px;color:#ccc;margin:14px 0 6px}
input,select{width:100%;box-sizing:border-box;font-size:17px;padding:11px;border-radius:10px;border:1px solid #444;background:#1c1c1e;color:#f5f5f7}
button{margin-top:22px;width:100%;font-size:17px;padding:13px;border:0;border-radius:12px;background:#d97757;color:#fff;font-weight:600}
.dot{display:inline-block;width:12px;height:12px;border-radius:50%;background:#d97757;margin-right:8px}
small{color:#888}a{color:#d97757}</style>)";

esp_err_t handleRoot(httpd_req_t* req) {
  std::string page;
  page.reserve(3200);
  page += "<!doctype html><html><head><title>Puck setup</title>";
  page += kStyle;
  page += "</head><body><h1><span class=dot></span>Set up your Puck</h1><p>Choose the Wi-Fi network the puck should use. "
          "It finds your Pucksley hub on that network by itself.</p><form method=post action=/save>"
          "<label>Network</label><select name=ssid>";
  page += s_networks;
  page += "<option value=''>Other&hellip;</option></select>"
          "<label>Or type its name</label><input name=ssid2 placeholder='Network name (if not listed)' autocapitalize=off>"
          "<label>Password</label><input name=pass type=password autocomplete=off>"
          "<label>Hub address <small>(optional)</small></label><input name=hub placeholder='e.g. 192.168.1.10:8765' autocapitalize=off>"
          "<button>Save and connect</button></form><p style='margin-top:18px'><small><a href=/rescan>Scan again</a> &middot; ";
  page += esc(s_apName);
  page += "</small></p></body></html>";
  httpd_resp_set_type(req, "text/html");
  return httpd_resp_send(req, page.c_str(), page.size());
}

// application/x-www-form-urlencoded value of `key`, decoded
std::string formValue(const std::string& body, const char* key) {
  std::string k = std::string(key) + "=";
  size_t pos = 0;
  while ((pos = body.find(k, pos)) != std::string::npos) {
    if (pos == 0 || body[pos - 1] == '&') break;
    pos += k.size();
  }
  if (pos == std::string::npos) return "";
  pos += k.size();
  size_t end = body.find('&', pos);
  std::string raw = body.substr(pos, end == std::string::npos ? std::string::npos : end - pos);
  std::string out;
  for (size_t i = 0; i < raw.size(); i++) {
    if (raw[i] == '+') out += ' ';
    else if (raw[i] == '%' && i + 2 < raw.size() && isxdigit((unsigned char)raw[i + 1]) && isxdigit((unsigned char)raw[i + 2])) {
      out += (char)strtol(raw.substr(i + 1, 2).c_str(), nullptr, 16);
      i += 2;
    } else out += raw[i];
  }
  return out;
}

std::string trim(std::string s) {
  while (!s.empty() && isspace((unsigned char)s.back())) s.pop_back();
  size_t i = 0;
  while (i < s.size() && isspace((unsigned char)s[i])) i++;
  return s.substr(i);
}

esp_err_t handleSave(httpd_req_t* req) {
  std::string body;
  int left = req->content_len;
  if (left > 2048) left = 2048;
  char buf[256];
  while (left > 0) {
    int r = httpd_req_recv(req, buf, left < (int)sizeof buf ? left : (int)sizeof buf);
    if (r <= 0) break;
    body.append(buf, r);
    left -= r;
  }
  std::string ssid = trim(formValue(body, "ssid2"));
  if (ssid.empty()) ssid = formValue(body, "ssid");
  std::string pass = formValue(body, "pass");
  std::string hubAddr = trim(formValue(body, "hub"));
  httpd_resp_set_type(req, "text/html");
  if (ssid.empty()) {
    const char* msg = "<meta name=viewport content='width=device-width'><p>Pick or type a network name. <a href=/>Back</a>";
    httpd_resp_set_status(req, "400 Bad Request");
    return httpd_resp_send(req, msg, HTTPD_RESP_USE_STRLEN);
  }
  storeSetWifi(ssid.c_str(), pass.c_str());
  if (!hubAddr.empty()) {
    if (hubAddr.rfind("ws://", 0) != 0) hubAddr = "ws://" + hubAddr;
    if (hubAddr.find(':', 5) == std::string::npos) hubAddr += ":" + std::to_string(HUB_DEFAULT_PORT);
    if (hubAddr.find('/', 5) == std::string::npos) hubAddr += HUB_PATH;
    storeSetHubUrl(hubAddr.c_str());
  } else {
    storeSetHubUrl("");
  }
  std::string page = "<!doctype html><html><head><title>Puck setup</title>";
  page += kStyle;
  page += "</head><body><h1><span class=dot></span>Saved</h1><p>Your puck is joining <b>";
  page += esc(ssid.c_str());
  page += "</b>. This setup network will close. Watch the puck: it shows when it has found your hub.</p></body></html>";
  httpd_resp_send(req, page.c_str(), page.size());
  s_saved = true;
  return ESP_OK;
}

esp_err_t redirect(httpd_req_t* req) {
  httpd_resp_set_status(req, "302 Found");
  httpd_resp_set_hdr(req, "Location", "http://192.168.4.1/");
  return httpd_resp_send(req, nullptr, 0);
}

esp_err_t handleRescan(httpd_req_t* req) {
  scan();
  return redirect(req);
}

esp_err_t notFound(httpd_req_t* req, httpd_err_code_t) { return redirect(req); }

// ---- every DNS question gets 192.168.4.1
void dnsTask(void*) {
  uint8_t buf[512];
  while (s_dnsRun) {
    sockaddr_in from = {};
    socklen_t fromLen = sizeof from;
    int n = recvfrom(s_dnsSock, buf, sizeof buf, 0, (sockaddr*)&from, &fromLen);
    if (n < 12) continue;
    if (buf[2] & 0x80) continue;          // a response, not a question
    int qd = (buf[4] << 8) | buf[5];
    if (qd < 1) continue;
    // find the end of the first question (name + type + class)
    int p = 12;
    while (p < n && buf[p] != 0) p += buf[p] + 1;
    p += 5;
    if (p > n || p + 16 > (int)sizeof buf) continue;
    buf[2] = 0x84;                         // response, authoritative
    buf[3] = 0x00;
    buf[4] = 0; buf[5] = 1;                // one question
    buf[6] = 0; buf[7] = 1;                // one answer
    buf[8] = buf[9] = buf[10] = buf[11] = 0;
    const uint8_t answer[16] = {0xC0, 0x0C, 0x00, 0x01, 0x00, 0x01, 0, 0, 0, 30, 0, 4, 192, 168, 4, 1};
    memcpy(buf + p, answer, sizeof answer);
    sendto(s_dnsSock, buf, p + sizeof answer, 0, (sockaddr*)&from, fromLen);
  }
  s_dnsTask = nullptr;
  vTaskDelete(nullptr);
}

void startDns() {
  s_dnsSock = socket(AF_INET, SOCK_DGRAM, IPPROTO_UDP);
  if (s_dnsSock < 0) return;
  sockaddr_in addr = {};
  addr.sin_family = AF_INET;
  addr.sin_port = htons(53);
  addr.sin_addr.s_addr = htonl(INADDR_ANY);
  if (bind(s_dnsSock, (sockaddr*)&addr, sizeof addr) != 0) {
    close(s_dnsSock);
    s_dnsSock = -1;
    return;
  }
  timeval tv = {1, 0};
  setsockopt(s_dnsSock, SOL_SOCKET, SO_RCVTIMEO, &tv, sizeof tv);
  s_dnsRun = true;
  xTaskCreatePinnedToCore(dnsTask, "dns", 3072, nullptr, 3, &s_dnsTask, 0);
}

void stopDns() {
  s_dnsRun = false;
  for (int i = 0; i < 30 && s_dnsTask; i++) vTaskDelay(pdMS_TO_TICKS(50));  // it wakes within 1 s
  if (s_dnsSock >= 0) close(s_dnsSock);
  s_dnsSock = -1;
}

}  // namespace

void portalStart(const char* apName) {
  if (s_running) return;
  model_copy(s_apName, sizeof s_apName, apName);
  esp_wifi_set_mode(WIFI_MODE_APSTA);
  wifi_config_t ap = {};
  size_t n = strnlen(apName, sizeof ap.ap.ssid);
  memcpy(ap.ap.ssid, apName, n);
  ap.ap.ssid_len = (uint8_t)n;
  ap.ap.channel = 6;
  ap.ap.authmode = WIFI_AUTH_OPEN;
  ap.ap.max_connection = 4;
  esp_wifi_set_config(WIFI_IF_AP, &ap);  // the AP's netif is 192.168.4.1 with DHCP by default
  scan();
  startDns();

  httpd_config_t cfg = HTTPD_DEFAULT_CONFIG();
  cfg.max_uri_handlers = 8;
  cfg.lru_purge_enable = true;
  cfg.stack_size = 6144;
  if (httpd_start(&s_web, &cfg) == ESP_OK) {
    httpd_uri_t root = {"/", HTTP_GET, handleRoot, nullptr};
    httpd_uri_t save = {"/save", HTTP_POST, handleSave, nullptr};
    httpd_uri_t rescan = {"/rescan", HTTP_GET, handleRescan, nullptr};
    httpd_register_uri_handler(s_web, &root);
    httpd_register_uri_handler(s_web, &save);
    httpd_register_uri_handler(s_web, &rescan);
    // everything else (what phones fetch to detect a captive portal) goes to the page
    httpd_register_err_handler(s_web, HTTPD_404_NOT_FOUND, notFound);
  }
  s_saved = false;
  s_running = true;
  hal::logLine("[link] setup network %s is open at 192.168.4.1", apName);
}

void portalLoop() {}  // the servers run in their own tasks

void portalStop() {
  if (!s_running) return;
  if (s_web) httpd_stop(s_web);
  s_web = nullptr;
  stopDns();
  esp_wifi_set_mode(WIFI_MODE_STA);
  s_running = false;
}

bool portalRunning() { return s_running; }

bool portalTakeSaved() {
  bool s = s_saved;
  s_saved = false;
  return s;
}

}  // namespace detail
}  // namespace hub
