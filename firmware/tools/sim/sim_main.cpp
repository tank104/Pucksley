// The face simulator: drives the real faces through a script of PC states and puck gestures, and
// writes what the glass shows to tools/sim/out/*.png (plus out/sheet.png with all of them).
#include <lvgl.h>
#include <stdio.h>
#include <string.h>
#include <string>
#include <vector>
#include "events.h"
#include "hal.h"
#include "model.h"
#include "sim.h"
#include "ui.h"


#define STB_IMAGE_WRITE_IMPLEMENTATION
#pragma GCC diagnostic push
#pragma GCC diagnostic ignored "-Wmissing-field-initializers"
#include "stb_image_write.h"
#pragma GCC diagnostic pop

namespace {

constexpr int CX = LCD_W / 2, CY = LCD_H / 2;
constexpr int THUMB = LCD_W / 2;
struct Shot {
  std::string name;
  std::vector<uint8_t> px;  // THUMB x THUMB RGB
};
std::vector<Shot> shots;

void run(uint32_t ms) {
  uint32_t end = g_now + ms;
  while (g_now < end) {
    g_now += 5;
    uint32_t d = model_take_dirty();
    if (d) ui::update(d);
    ui::tick(g_now);
    lv_timer_handler();
  }
}

void ev(Ev t, float v = 0, int x = CX, int y = CY, char dir = 0) {
  InputEvent e{t, v, (int16_t)x, (int16_t)y, dir, g_now};
  ui::handleInput(e);
  run(20);
}

void turn(float deg, int steps = 12) {
  for (int i = 0; i < steps; i++) ev(Ev::Turn, deg / steps);
}

void mark(uint32_t d) {
  model_mark(d);
  run(30);
}

void set(char* dst, size_t n, const char* s) {
  strncpy(dst, s, n - 1);
  dst[n - 1] = 0;
}
#define SET(field, s) set(field, sizeof field, s)

void request(const char* face) {
  SET(g_model.faceRequest, face);
  mark(D_FACE_REQ);
  run(400);
}

void snap(const char* name) {
  lv_refr_now(nullptr);
  static uint8_t out[LCD_W * LCD_H * 3];
  for (int y = 0; y < LCD_H; y++)
    for (int x = 0; x < LCD_W; x++) {
      int dx = x - CX, dy = y - CY;
      const uint8_t* s = &g_fb[(y * LCD_W + x) * 3];
      uint8_t* o = &out[(y * LCD_W + x) * 3];
      bool inside = dx * dx + dy * dy <= (CX - 0) * (CX - 0);
      // outside the glass: the aluminium case
      o[0] = inside ? s[0] : 38;
      o[1] = inside ? s[1] : 38;
      o[2] = inside ? s[2] : 41;
    }
  std::string path = std::string("out/") + name + ".png";
  stbi_write_png(path.c_str(), LCD_W, LCD_H, 3, out, LCD_W * 3);
  Shot sh{name, std::vector<uint8_t>(THUMB * THUMB * 3)};
  for (int y = 0; y < THUMB; y++)
    for (int x = 0; x < THUMB; x++)
      for (int c = 0; c < 3; c++) {
        int sum = out[((2 * y) * LCD_W + 2 * x) * 3 + c] + out[((2 * y) * LCD_W + 2 * x + 1) * 3 + c] +
                  out[((2 * y + 1) * LCD_W + 2 * x) * 3 + c] + out[((2 * y + 1) * LCD_W + 2 * x + 1) * 3 + c];
        sh.px[(y * THUMB + x) * 3 + c] = (uint8_t)(sum / 4);
      }
  shots.push_back(std::move(sh));
  printf("snap %s  (face: %s)\n", name, ui::currentFace());
}

void sheet() {
  const int cols = 6, pad = 8;
  int rows = ((int)shots.size() + cols - 1) / cols;
  int W = cols * (THUMB + pad) + pad, H = rows * (THUMB + pad) + pad;
  std::vector<uint8_t> img(W * H * 3, 20);
  for (size_t i = 0; i < shots.size(); i++) {
    int ox = pad + (int)(i % cols) * (THUMB + pad), oy = pad + (int)(i / cols) * (THUMB + pad);
    for (int y = 0; y < THUMB; y++) memcpy(&img[((oy + y) * W + ox) * 3], &shots[i].px[y * THUMB * 3], THUMB * 3);
  }
  stbi_write_png("out/sheet.png", W, H, 3, img.data(), W * 3);
}

}  // namespace


namespace {

void act(const char* icon, const char* text, char state) {
  for (int i = 2; i > 0; i--) g_model.acts[i] = g_model.acts[i - 1];
  SET(g_model.acts[0].icon, icon);
  SET(g_model.acts[0].text, text);
  g_model.acts[0].state = state;
  if (g_model.actCount < 3) g_model.actCount++;
  mark(D_ACT);
}

void ai(AiState s) {
  g_model.ai = s;
  g_model.aiSince = g_now;
  mark(D_AI);
}

void ctl(int i, const char* id, const char* g, const char* n, CtlKind k, float v, float mn, float mx, float step,
         const char* u, bool on = false) {
  Control& c = g_model.ctls[i];
  c = Control();
  SET(c.id, id);
  SET(c.group, g);
  SET(c.name, n);
  c.kind = k;
  c.v = v;
  c.min = mn;
  c.max = mx;
  c.step = step;
  SET(c.unit, u);
  c.on = on;
  if (i + 1 > g_model.ctlCount) g_model.ctlCount = (uint8_t)(i + 1);
}

}  // namespace

int main(int argc, char** argv) {
  g_quiet = argc > 1 && strcmp(argv[1], "-q") == 0;
  g_model.timeValid = true;
  g_model.epochAtSync = 1794212700;  // a Monday evening
  g_model.tzMin = 780;
  g_model.msAtSync = g_now;
  hal::begin();

  // ---- first boot: no Wi-Fi
  g_model.net = NetState::NoWifi;
  ui::begin();
  run(1200);
  snap("01_setup_no_wifi");

  // ---- the setup network
  g_model.net = NetState::Portal;
  SET(g_model.portalSsid, "Puck-1D4D");
  mark(D_LINK);
  run(800);
  snap("02_setup_portal");

  // ---- on Wi-Fi, looking for the hub
  g_model.net = NetState::Searching;
  SET(g_model.ssid, "Kohi");
  SET(g_model.ip, "192.168.1.57");
  g_model.rssi = -58;
  mark(D_LINK);
  run(800);
  snap("03_setup_searching");

  // ---- the hub wants this puck approved
  g_model.net = NetState::Pending;
  SET(g_model.pendingCode, "482");
  mark(D_LINK);
  run(800);
  snap("04_setup_pending");

  // ---- approved
  g_model.net = NetState::Online;
  g_model.linked = true;
  g_model.viaWifi = true;
  g_model.pendingCode[0] = 0;
  SET(g_model.hub, "MY-PC");
  SET(g_model.puckName, "Kitchen puck");
  mark(D_LINK | D_CFG);
  run(1500);
  snap("05_assistant_idle");

  // ---- a question
  ev(Ev::Tap);
  run(700);
  snap("06_assistant_listening");
  ev(Ev::Tap);
  SET(g_model.heard, "Play some Massive Attack in the kitchen and turn it up a bit");
  mark(D_AI);
  run(400);
  act("music", "Spotify: play", 'r');
  run(300);
  snap("07_assistant_thinking");
  act("music", "Teardrop - Kitchen", 'o');
  SET(g_model.reply, "Playing Teardrop by Massive Attack in the kitchen, and I've nudged the volume up to 35.");
  ai(AiState::Speaking);
  run(900);
  snap("08_assistant_speaking");

  // a long answer, scrolled
  ai(AiState::Idle);
  SET(g_model.heard, "Why is the sky blue?");
  SET(g_model.reply,
      "Sunlight is a mix of every colour. As it passes through the air, it bumps into molecules of nitrogen and oxygen "
      "that are much smaller than the light's wavelength, and they scatter short wavelengths, blue and violet, far "
      "more than long red ones. That scattered blue light reaches your eyes from every direction, so the whole sky "
      "looks blue. We see less violet because the sun gives off less of it and our eyes are less sensitive to it.");
  g_model.replyDone = true;
  g_model.actCount = 0;
  mark(D_AI | D_ACT);
  run(800);
  snap("09_assistant_answer");
  turn(120);
  run(500);
  snap("10_assistant_answer_scrolled");

  // the voice volume, turned while it speaks
  ai(AiState::Speaking);
  run(300);
  turn(-30);
  run(200);
  snap("11_assistant_voice_volume");
  run(2000);

  // an error
  SET(g_model.aiError, "I couldn't reach Claude. Check the internet connection.");
  ai(AiState::Error);
  run(800);
  snap("12_assistant_error");
  ai(AiState::Idle);
  g_model.reply[0] = 0;
  mark(D_AI);

  // ---- home: what's playing in the kitchen
  g_model.playing = true;
  SET(g_model.title, "Teardrop");
  SET(g_model.artist, "Massive Attack");
  SET(g_model.src, "Kitchen");
  g_model.vol = 35;
  g_model.hasVol = true;
  mark(D_MEDIA | D_AUDIO);
  request("home");
  run(800);
  snap("13_home_playing");
  turn(20);
  run(200);
  snap("14_home_volume");
  run(2500);

  // ---- controls from the plugins
  ctl(0, "c0", "Sonos", "Kitchen", CtlKind::Num, 35, 0, 100, 2, "%");
  ctl(1, "c1", "Heating", "House", CtlKind::Num, 21.5f, 10, 28, 0.5f, "\xC2\xB0" "C");
  ctl(2, "c2", "Plugs", "Lamp", CtlKind::Bool, 0, 0, 1, 1, "", true);
  ctl(3, "c3", "TV", "Lounge TV off", CtlKind::Act, 0, 0, 1, 1, "");
  mark(D_CTLS);
  request("controls");
  run(800);
  snap("15_controls_volume");
  ev(Ev::Swipe, 0, CX, CY, 'l');
  run(500);
  snap("16_controls_heating");
  ev(Ev::Swipe, 0, CX, CY, 'l');
  run(500);
  snap("17_controls_lamp");
  ev(Ev::Swipe, 0, CX, CY, 'l');
  run(500);
  snap("18_controls_action");

  // ---- timers
  request("timer");
  run(600);
  snap("19_timer_new");
  Timer& t = g_model.timers[0];
  t.active = true;
  SET(t.id, "t1");
  SET(t.label, "Pasta");
  t.totalMs = 600000;
  t.endsAtMs = g_now + 412000;
  mark(D_TIMERS);
  run(600);
  snap("20_timer_running");

  // ---- status
  request("status");
  run(600);
  snap("21_status");

  // ---- a yes/no question
  g_model.confirmActive = true;
  SET(g_model.confirmId, "c1");
  SET(g_model.confirmText, "Shut down or restart this PC?");
  mark(D_CONFIRM);
  run(800);
  snap("22_confirm");
  turn(30);
  run(300);
  snap("23_confirm_yes");
  ev(Ev::Tap);
  run(600);

  // ---- picker and banner
  request("assistant");
  ev(Ev::Button);
  run(500);
  snap("24_picker");
  ev(Ev::LongPress);
  run(500);
  SET(g_model.notifyText, "Your pasta timer is done");
  g_model.notifyKind = NotifyKind::Alert;
  g_model.notifyAt = g_now;
  mark(D_NOTIFY);
  run(500);
  snap("25_banner");
  run(4500);

  // ---- the hub goes away
  g_model.linked = false;
  g_model.net = NetState::Connecting;
  mark(D_LINK);
  run(1000);
  snap("26_assistant_offline");

  // ---- no hub, but on Wi-Fi with keys: direct mode, Claude straight from the puck
  g_model.direct = true;
  mark(D_LINK);
  run(1500);
  snap("27_direct_idle");
  request("status");
  run(600);
  snap("28_direct_status");

  sheet();
  printf("wrote out/*.png and out/sheet.png\n");
  return 0;
}
