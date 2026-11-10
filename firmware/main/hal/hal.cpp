// Bringing the board up, the shared I2C bus, the boot log, and the display's public controls.
#include "../platform.h"
#include <stdarg.h>
#include <stdio.h>
#include <string.h>
#include <driver/usb_serial_jtag.h>
#include <esp_heap_caps.h>
#include <esp_log.h>
#include <esp_mac.h>
#include <esp_system.h>
#include <freertos/FreeRTOS.h>
#include <freertos/semphr.h>
#include "../config.h"
#include "hal_internal.h"

namespace hal {
namespace detail {

namespace {
SemaphoreHandle_t s_i2c = nullptr;
i2c_master_bus_handle_t s_bus = nullptr;

// One device handle per address for the code that talks to chips directly (the codecs)
struct Dev {
  uint8_t addr;
  i2c_master_dev_handle_t h;
};
Dev s_devs[6];
int s_ndevs = 0;

i2c_master_dev_handle_t device(uint8_t addr) {
  for (int i = 0; i < s_ndevs; i++)
    if (s_devs[i].addr == addr) return s_devs[i].h;
  if (!s_bus || s_ndevs >= (int)(sizeof s_devs / sizeof s_devs[0])) return nullptr;
  i2c_device_config_t cfg = {};
  cfg.dev_addr_length = I2C_ADDR_BIT_LEN_7;
  cfg.device_address = addr;
  cfg.scl_speed_hz = I2C_HZ;
  i2c_master_dev_handle_t h = nullptr;
  if (i2c_master_bus_add_device(s_bus, &cfg, &h) != ESP_OK) return nullptr;
  s_devs[s_ndevs++] = {addr, h};
  return h;
}
}  // namespace

bool i2cBegin() {
  if (!s_i2c) s_i2c = xSemaphoreCreateRecursiveMutex();
  i2c_master_bus_config_t cfg = {};
  cfg.i2c_port = I2C_NUM_0;
  cfg.sda_io_num = (gpio_num_t)PIN_I2C_SDA;
  cfg.scl_io_num = (gpio_num_t)PIN_I2C_SCL;
  cfg.clk_source = I2C_CLK_SRC_DEFAULT;
  cfg.glitch_ignore_cnt = 7;
  cfg.flags.enable_internal_pullup = true;
  if (i2c_new_master_bus(&cfg, &s_bus) != ESP_OK) {
    hal::logLine("[hal] I2C bus init failed");
    return false;
  }
  return true;
}

i2c_master_bus_handle_t i2cBus() { return s_bus; }

bool i2cWriteReg(uint8_t addr, uint8_t reg, uint8_t val) {
  auto h = device(addr);
  if (!h) return false;
  uint8_t buf[2] = {reg, val};
  return i2c_master_transmit(h, buf, 2, 20) == ESP_OK;
}

bool i2cReadReg(uint8_t addr, uint8_t reg, uint8_t* val) {
  auto h = device(addr);
  if (!h) return false;
  return i2c_master_transmit_receive(h, &reg, 1, val, 1, 20) == ESP_OK;
}

void i2cLock() {
  if (s_i2c) xSemaphoreTakeRecursive(s_i2c, portMAX_DELAY);
}

void i2cUnlock() {
  if (s_i2c) xSemaphoreGiveRecursive(s_i2c);
}

}  // namespace detail

// ---- the boot log: what the board layer had to say, kept until the hub can be told

namespace {
constexpr int kLogLines = 24;
char s_log[kLogLines][120];
int s_logHead = 0, s_logCount = 0;
portMUX_TYPE s_logMux = portMUX_INITIALIZER_UNLOCKED;
}  // namespace

void logLine(const char* fmt, ...) {
  char line[120];
  va_list ap;
  va_start(ap, fmt);
  vsnprintf(line, sizeof line, fmt, ap);
  va_end(ap);
  ESP_LOGW("puck", "%s", line);
  portENTER_CRITICAL(&s_logMux);
  int slot = (s_logHead + s_logCount) % kLogLines;
  if (s_logCount == kLogLines) s_logHead = (s_logHead + 1) % kLogLines;
  else s_logCount++;
  memcpy(s_log[slot], line, sizeof line);
  portEXIT_CRITICAL(&s_logMux);
}

bool takeLog(char* out, size_t len) {
  bool any = false;
  portENTER_CRITICAL(&s_logMux);
  if (s_logCount > 0) {
    snprintf(out, len, "%s", s_log[s_logHead]);
    s_logHead = (s_logHead + 1) % kLogLines;
    s_logCount--;
    any = true;
  }
  portEXIT_CRITICAL(&s_logMux);
  return any;
}

namespace {
bool s_displayOn = true;
uint8_t s_brightness = 80;  // percent
uint32_t s_displayToggles = 0;  // for diag(): a screen that keeps flicking on and off shows here
}

void begin() {
  // The native USB port carries the hub protocol. Big buffers: speech arrives over USB as base64
  // lines when there's no Wi-Fi.
  usb_serial_jtag_driver_config_t usb = {};
  usb.rx_buffer_size = 16384;
  usb.tx_buffer_size = 8192;
  usb_serial_jtag_driver_install(&usb);

  detail::inputBegin();
  detail::i2cBegin();

  detail::powerBegin();
  if (!detail::lcdBegin()) logLine("[hal] display init failed");
  detail::lvglBegin();
  detail::lvglRedrawNow();  // an all-black first frame before the light comes up
  setBrightness(s_brightness);

  detail::touchBegin();
  detail::motionBegin();
  detail::buttonsBegin();
  if (!detail::audioBegin()) logLine("[hal] sound is off");
  detail::startInputTask();
}

void setBrightness(uint8_t percent) {
  if (percent > 100) percent = 100;
  s_brightness = percent;
  if (s_displayOn) detail::lcdBrightness((uint8_t)((uint32_t)percent * 255 / 100));
}

void setDisplayOn(bool on) {
  if (on == s_displayOn) return;
  s_displayToggles++;
  if (on) {
    detail::lcdSleep(false);
    detail::lvglSetPanelAwake(true);
    detail::lvglRedrawNow();  // panel RAM went stale while asleep; repaint before it lights up
    detail::lcdCommand(0x29, nullptr, 0);  // DISPON
    s_displayOn = true;
    setBrightness(s_brightness);
  } else {
    detail::lcdBrightness(0);
    detail::lvglSetPanelAwake(false);
    detail::lcdDrain();
    detail::lcdSleep(true);
    s_displayOn = false;
  }
  detail::inputDisplayChanged(on);
}

bool displayOn() { return s_displayOn; }

uint64_t chipId() {
  uint8_t mac[6] = {};
  esp_efuse_mac_get_default(mac);
  uint64_t v = 0;
  for (int i = 5; i >= 0; i--) v = (v << 8) | mac[i];  // the same byte order as Arduino's getEfuseMac()
  return v;
}

void diag(char* buf, size_t len) {
  int n = snprintf(buf, len, "up=%lus reset=%d disp=%d bri=%u toggles=%lu wakes=%lu idle=%lums mode=%d touch=%d pmu=%d snd=%d bat=%d/%dmV chg=%d usb=%d heap=%u ",
                   (unsigned long)(millis() / 1000), (int)esp_reset_reason(),
                   (int)s_displayOn, (unsigned)s_brightness, (unsigned long)s_displayToggles,
                   (unsigned long)detail::wakesPosted(), (unsigned long)(millis() - lastActivityMs()),
                   (int)inputMode(), (int)detail::touchOk(), (int)detail::powerOk(), (int)detail::audioOk(),
                   batteryPercent(), batteryMv(), (int)charging(), (int)usbPowered(),
                   (unsigned)heap_caps_get_free_size(MALLOC_CAP_INTERNAL));
  if (n > 0 && (size_t)n < len) n += detail::touchDiag(buf + n, len - n);
  if (n > 0 && (size_t)n < len) n += detail::eventCounts(buf + n, len - n);
  if (n > 0 && (size_t)n < len) n += detail::motionDiag(buf + n, len - n);
  if (n > 0 && (size_t)n < len) n += snprintf(buf + n, len - n, " ");
  if (n > 0 && (size_t)n < len) detail::audioDiag(buf + n, len - n);
}

}  // namespace hal
