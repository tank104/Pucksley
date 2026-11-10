// What survives a reboot (NVS, namespace "pucksley"): Wi-Fi credentials, where the hub is, the hub's
// token for this puck, and the last device settings the hub sent (so brightness and volume are
// right before it connects).
#include "../platform.h"
#include <nvs.h>
#include <nvs_flash.h>
#include <string.h>
#include <freertos/FreeRTOS.h>
#include <freertos/semphr.h>
#include "../config.h"
#include "../hal.h"
#include "../model.h"
#include "internal.h"

namespace hub {
namespace detail {

namespace {

struct CfgBlob {
  uint8_t version;
  uint8_t bright;
  uint8_t sound;
  uint8_t flip;
  uint16_t dimSec;
  uint16_t offSec;
  float sens;
  uint8_t spk;
  uint8_t micGain;
  uint8_t lift;
  uint8_t pad;
  char name[32];
};

constexpr uint8_t kCfgVersion = 2;
constexpr uint32_t kCfgSaveDelayMs = 2000;

nvs_handle_t s_nvs = 0;
bool s_open = false;
SemaphoreHandle_t s_lock = nullptr;
uint32_t s_cfgDirtyAt = 0;

struct Lock {
  Lock() { if (s_lock) xSemaphoreTake(s_lock, portMAX_DELAY); }
  ~Lock() { if (s_lock) xSemaphoreGive(s_lock); }
};

void getStr(const char* key, char* out, size_t len) {
  if (!len) return;
  out[0] = 0;
  Lock l;
  if (!s_open) return;
  size_t n = len;
  if (nvs_get_str(s_nvs, key, out, &n) != ESP_OK) out[0] = 0;
}

void putStr(const char* key, const char* v) {
  Lock l;
  if (!s_open) return;
  if (v && v[0]) nvs_set_str(s_nvs, key, v);
  else nvs_erase_key(s_nvs, key);
  nvs_commit(s_nvs);
}

void saveCfg() {
  CfgBlob b = {};
  b.version = kCfgVersion;
  b.bright = (uint8_t)g_model.bright;
  b.sound = g_model.sound;
  b.flip = g_model.flip;
  b.dimSec = (uint16_t)g_model.dimSec;
  b.offSec = (uint16_t)g_model.offSec;
  b.sens = g_model.sens;
  b.spk = (uint8_t)g_model.spk;
  b.micGain = (uint8_t)g_model.micGain;
  b.lift = g_model.lift;
  model_copy(b.name, sizeof b.name, g_model.puckName);
  Lock l;
  if (!s_open) return;
  nvs_set_blob(s_nvs, "cfg", &b, sizeof b);
  nvs_commit(s_nvs);
}

}  // namespace

void storeBegin() {
  s_lock = xSemaphoreCreateMutex();
  esp_err_t err = nvs_flash_init();
  if (err == ESP_ERR_NVS_NO_FREE_PAGES || err == ESP_ERR_NVS_NEW_VERSION_FOUND) {
    nvs_flash_erase();
    err = nvs_flash_init();
  }
  s_open = err == ESP_OK && nvs_open("pucksley", NVS_READWRITE, &s_nvs) == ESP_OK;
  if (!s_open) hal::logLine("[link] NVS failed to open; settings won't be remembered");
}

bool storeWifi(char* ssid, size_t ssidLen, char* pass, size_t passLen) {
  getStr("ssid", ssid, ssidLen);
  getStr("pass", pass, passLen);
  return ssid[0] != 0;
}

void storeSetWifi(const char* ssid, const char* pass) {
  putStr("ssid", ssid);
  putStr("pass", pass);
}

void storeClearWifi() {
  putStr("ssid", nullptr);
  putStr("pass", nullptr);
}

void storeHubUrl(char* url, size_t len) { getStr("hub", url, len); }
void storeSetHubUrl(const char* url) { putStr("hub", url); }
void storeToken(char* token, size_t len) { getStr("token", token, len); }
void storeSetToken(const char* token) { putStr("token", token); }

bool storeGetString(const char* key, std::string& out) {
  out.clear();
  Lock l;
  if (!s_open) return false;
  size_t n = 0;
  if (nvs_get_blob(s_nvs, key, nullptr, &n) != ESP_OK || n == 0) return false;
  out.resize(n);
  if (nvs_get_blob(s_nvs, key, &out[0], &n) != ESP_OK) {
    out.clear();
    return false;
  }
  out.resize(n);
  return true;
}

void storeSetString(const char* key, const char* value) {
  Lock l;
  if (!s_open) return;
  if (value && value[0]) nvs_set_blob(s_nvs, key, value, strlen(value));
  else nvs_erase_key(s_nvs, key);
  nvs_commit(s_nvs);
}

void storeLoadCfg() {
  CfgBlob b = {};
  bool ok = false;
  {
    Lock l;
    size_t n = sizeof b;
    ok = s_open && nvs_get_blob(s_nvs, "cfg", &b, &n) == ESP_OK && n == sizeof b && b.version == kCfgVersion;
  }
  if (ok) {
    g_model.bright = b.bright < 10 ? 10 : b.bright > 100 ? 100 : b.bright;
    g_model.sound = b.sound;
    g_model.flip = b.flip;
    g_model.dimSec = b.dimSec;
    g_model.offSec = b.offSec;
    g_model.sens = b.sens > 0.2f && b.sens < 5.0f ? b.sens : 1.0f;
    g_model.spk = b.spk > 100 ? 70 : b.spk;
    g_model.micGain = b.micGain > 37 ? 30 : b.micGain;
    g_model.lift = b.lift;
    model_copy(g_model.puckName, sizeof g_model.puckName, b.name);
  }
  applyCfgToHal();
  model_mark(D_CFG);
}

void storeSaveCfgSoon() { s_cfgDirtyAt = millis() | 1; }

void storePoll() {
  if (s_cfgDirtyAt && millis() - s_cfgDirtyAt > kCfgSaveDelayMs) {
    s_cfgDirtyAt = 0;
    saveCfg();
  }
}

}  // namespace detail
}  // namespace hub
