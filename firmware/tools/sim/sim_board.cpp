// A pretend board and link for the simulator: the display is a framebuffer, the link prints what
// the faces would send to the PC.
#include <lvgl.h>
#include <stdarg.h>
#include <stdio.h>
#include <string.h>
#include "config.h"
#include "hal.h"
#include "hub.h"
#include <math.h>
#include "sim.h"

uint32_t g_now = 1000;
uint8_t g_fb[LCD_W * LCD_H * 3];
bool g_lifted = false;
bool g_usb = false;
bool g_quiet = false;
static bool s_displayOn = true;
static int s_bright = 80;

extern "C" uint32_t millis(void) { return g_now; }

static void flush(lv_disp_drv_t* d, const lv_area_t* a, lv_color_t* px) {
  for (int y = a->y1; y <= a->y2; y++)
    for (int x = a->x1; x <= a->x2; x++) {
      lv_color32_t c;
      c.full = lv_color_to32(*px++);
      uint8_t* o = &g_fb[(y * LCD_W + x) * 3];
      o[0] = c.ch.red;
      o[1] = c.ch.green;
      o[2] = c.ch.blue;
    }
  lv_disp_flush_ready(d);
}

static void log(const char* fmt, ...) {
  if (g_quiet) return;
  va_list ap;
  va_start(ap, fmt);
  printf("  [%6u] ", (unsigned)g_now);
  vprintf(fmt, ap);
  printf("\n");
  va_end(ap);
}

namespace hal {
static lv_disp_draw_buf_t s_buf;
static lv_disp_drv_t s_drv;
static InputMode s_mode = InputMode::Ui;

void begin() {
  lv_init();
  static lv_color_t px[LCD_W * LCD_H];
  lv_disp_draw_buf_init(&s_buf, px, nullptr, LCD_W * LCD_H);
  lv_disp_drv_init(&s_drv);
  s_drv.hor_res = LCD_W;
  s_drv.ver_res = LCD_H;
  s_drv.flush_cb = flush;
  s_drv.draw_buf = &s_buf;
  lv_disp_drv_register(&s_drv);
}
void setBrightness(uint8_t p) {
  if (p != s_bright) log("hal brightness %d", p);
  s_bright = p;
}
void setDisplayOn(bool on) {
  if (on != s_displayOn) log("hal display %s", on ? "on" : "off");
  s_displayOn = on;
}
bool displayOn() { return s_displayOn; }
bool pollEvent(InputEvent&) { return false; }
void setInputMode(InputMode m) {
  if (m != s_mode) log("hal input mode %s", m == InputMode::Trackpad ? "trackpad" : "ui");
  s_mode = m;
}
InputMode inputMode() { return s_mode; }
float heading() { return 0; }
bool lifted() { return g_lifted; }
uint32_t lastActivityMs() { return g_now; }
void setTurnSensitivity(float) {}
void setTurnFlip(bool) {}
int batteryPercent() { return 72; }
int batteryMv() { return 3900; }
bool charging() { return false; }
bool usbPowered() { return g_usb; }
void powerOff() { log("hal POWER OFF"); }
void beep(Tone t) {
  static const char* names[] = {"tick", "tap", "select", "success", "error", "chime"};
  if (t != Tone::Tick) log("beep %s", names[(int)t]);
}
void setSoundEnabled(bool) {}
uint64_t chipId() { return 0xC0FFEE; }
static bool s_mic = false;
void micStart() { log("hal mic on"); s_mic = true; }
void micStop() { log("hal mic off"); s_mic = false; }
bool micActive() { return s_mic; }
size_t micRead(int16_t*, size_t) { return 0; }
float micLevel() { return s_mic ? 0.35f + 0.25f * sinf(g_now / 180.0f) : 0; }  // someone talking
void setMicGain(int) {}
void speakerBegin(int) {}
void speakerWrite(const int16_t*, size_t) {}
void speakerEnd(int) {}
void speakerStop() {}
bool speakerPlaying() { return g_model.ai == AiState::Speaking; }
size_t speakerFree() { return 16000; }
int speakerTakeDone() { return -1; }
float speakerLevel() { return g_model.ai == AiState::Speaking ? 0.3f + 0.2f * sinf(g_now / 90.0f) : 0; }
void setSpeakerVolume(int v) { log("hal speaker volume %d", v); }
}  // namespace hal

namespace hub {
void begin() {}
void poll() {}
bool online() { return g_model.linked; }
void talk(const char* a, const char* via) {
  log("-> talk %s (%s)", a, via ? via : "");
  if (!strcmp(a, "start")) {
    g_model.ai = AiState::Listening;
    g_model.heard[0] = 0;
    g_model.reply[0] = 0;
    g_model.actCount = 0;
  } else if (!strcmp(a, "stop")) {
    g_model.ai = AiState::Thinking;
  } else if (!strcmp(a, "cancel")) {
    g_model.ai = AiState::Idle;
  }
  g_model.aiSince = g_now;
  model_mark(D_AI | D_ACT);
}
void media(const char* a, int v) { log("-> media %s %d", a, v); }
void controlValue(const char* id, float v) { log("-> ctl %s %.1f", id, v); }
void controlToggle(const char* id, bool on) { log("-> ctl %s on=%d", id, on); }
void controlPress(const char* id) { log("-> ctl %s press", id); }
void timerStart(int s, const char* l) { log("-> timer start %d %s", s, l ? l : ""); }
void timerCancel(const char* id) { log("-> timer cancel %s", id); }
void answer(const char* id, bool ok) {
  log("-> ans %s %s", id, ok ? "yes" : "no");
  g_model.confirmActive = false;
  model_mark(D_CONFIRM);
}
void face(const char* f) { log("-> face %s", f); }
void speakerVolume(int v) {
  g_model.spk = v < 0 ? 0 : v > 100 ? 100 : v;
  log("-> spkvol %d", g_model.spk);
}
void log(const char*, ...) {}
static bool s_portal = false;
void openSetupPortal() {
  s_portal = true;
  g_model.net = NetState::Portal;
  model_mark(D_LINK);
  log("-> portal open");
}
void closeSetupPortal() {
  s_portal = false;
  log("-> portal close");
}
bool portalOpen() { return s_portal; }
void forgetWifi() {}
}  // namespace hub
