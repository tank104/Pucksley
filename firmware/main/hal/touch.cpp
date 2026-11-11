// The glass: CST9217 touch controller, and the gestures read from it. In trackpad mode the glass
// also drives the PC's mouse directly (see InputMode::Trackpad).
#include "../platform.h"
#include <atomic>
#include <math.h>
#include <TouchDrvCSTXXX.hpp>
#include "../config.h"
#include "../hostlink.h"
#include "hal_internal.h"

namespace hal {
namespace detail {

namespace {

constexpr int kTapSlopPx = 14;            // more than this and it's a drag, not a tap
constexpr uint32_t kTapMaxMs = 450;          // up to long-press territory: a slow, deliberate tap still counts
constexpr uint32_t kDoubleTapMs = 300;    // between the first release and the second tap
constexpr int kDoubleTapSlopPx = 40;
constexpr uint32_t kLongPressMs = 600;
constexpr uint32_t kSwipeMaxMs = 400;
constexpr int kSwipeMinPx = 60;
constexpr uint32_t kMoveEventMs = 16;     // TouchMove events to the faces at most ~60 Hz
constexpr float kTwoFingerScrollPx = 30;  // trackpad: two-finger drag, px per scroll notch
constexpr uint32_t kQuietReleaseMs = 1200;  // a finger down but no fresh report for this long: take it as lifted

TouchDrvCST92xx s_touch;
bool s_ok = false;
InputMode s_mode = InputMode::Ui;

struct Gesture {
  bool down = false;
  bool swallowed = false;   // started while the display was off: ignore until lifted
  int16_t x0 = 0, y0 = 0;   // where it started
  int16_t x = 0, y = 0;     // latest
  uint32_t t0 = 0;
  bool moved = false;
  bool longFired = false;
  bool second = false;      // started right after a tap: a double tap, or tap-and-drag
  bool dragging = false;    // trackpad: left button held for a tap-and-drag
  uint8_t maxFingers = 0;
  uint32_t lastMoveEvent = 0;
  int16_t lastEvX = -1, lastEvY = -1;
} g;

uint32_t s_lastTapUpMs = 0;
uint8_t s_emptyReads = 0;  // a lone empty report mid-drag is a glitch, not a release
uint32_t s_lastReportMs = 0;  // the chip's last fresh report (points or not) while a finger was down
int16_t s_lastTapX = 0, s_lastTapY = 0;

inline int16_t clampCoord(int v, int max) { return (int16_t)(v < 0 ? 0 : (v > max - 1 ? max - 1 : v)); }
inline int dist2(int dx, int dy) { return dx * dx + dy * dy; }

bool trackpad() { return s_mode == InputMode::Trackpad; }

// Turn a drag on the glass into pointer motion that keeps "up" pointing away from the user even
// after the puck has been turned on the desk: rotate by how far it turned since trackpad mode began.
float s_headingAtEntry = 0;
void sendTrackpadMove(float dx, float dy) {
  const float th = (motionHeading() - s_headingAtEntry) * (float)M_PI / 180.0f;
  const float c = cosf(th), s = sinf(th);
  hostlink::sendMove('t', dx * c - dy * s, dx * s + dy * c);
}

void onPress(int16_t x, int16_t y, uint8_t fingers, uint32_t now) {
  g = Gesture();
  g.down = true;
  g.x0 = g.x = x;
  g.y0 = g.y = y;
  g.t0 = now;
  g.maxFingers = fingers;
  if (!activityBegin(Src::Touch)) {
    g.swallowed = true;
    return;
  }
  g.second = (now - s_lastTapUpMs) < kDoubleTapMs &&
             dist2(x - s_lastTapX, y - s_lastTapY) < kDoubleTapSlopPx * kDoubleTapSlopPx;
  post(Ev::TouchDown, 0, x, y);
  g.lastEvX = x;
  g.lastEvY = y;
  g.lastMoveEvent = now;
}

void onHold(int16_t x, int16_t y, uint8_t fingers, uint32_t now) {
  if (fingers > g.maxFingers) g.maxFingers = fingers;
  const int16_t px = g.x, py = g.y;
  g.x = x;
  g.y = y;
  if (g.swallowed) return;
  activityContinue();

  if (!g.moved && dist2(x - g.x0, y - g.y0) > kTapSlopPx * kTapSlopPx) g.moved = true;

  if (trackpad() && !motionLifted() && g.moved) {
    const float dx = (float)(x - px), dy = (float)(y - py);
    if (g.maxFingers >= 2) {
      // two fingers: scroll (drag down = content down = scroll up, like a touchpad)
      if (dy != 0) hostlink::sendScroll(dy / kTwoFingerScrollPx);
    } else if (dx != 0 || dy != 0) {
      if (g.second && !g.dragging) {
        hostlink::sendButton('l', true);
        g.dragging = true;
      }
      sendTrackpadMove(dx, dy);
    }
  }

  if (!g.moved && !g.longFired && now - g.t0 >= kLongPressMs) {
    g.longFired = true;
    post(Ev::LongPress, 0, x, y);
    // Held up as a pointer, a long press is a thumb resting on the glass, not a right-click
    if (trackpad() && !motionLifted()) hostlink::sendClick('r');
  }

  if ((x != g.lastEvX || y != g.lastEvY) && now - g.lastMoveEvent >= kMoveEventMs) {
    post(Ev::TouchMove, 0, x, y);
    g.lastEvX = x;
    g.lastEvY = y;
    g.lastMoveEvent = now;
  }
}

void onRelease(uint32_t now) {
  const Gesture done = g;
  g.down = false;
  if (done.swallowed) return;
  activityContinue();
  post(Ev::TouchUp, 0, done.x, done.y);

  if (done.dragging) {
    hostlink::sendButton('l', false);
    return;
  }
  const int32_t dur = (int32_t)(now - done.t0);
  const int dx = done.x - done.x0, dy = done.y - done.y0;
  if (!done.moved && !done.longFired && dur < (int32_t)kTapMaxMs) {
    post(Ev::Tap, 0, done.x, done.y);
    if (trackpad()) hostlink::sendClick(done.maxFingers >= 2 ? 'r' : 'l');
    if (done.second) {
      post(Ev::DoubleTap, 0, done.x, done.y);
      s_lastTapUpMs = 0;  // a third tap starts over
    } else {
      s_lastTapUpMs = now;
      s_lastTapX = done.x;
      s_lastTapY = done.y;
    }
    return;
  }
  if (dur <= (int32_t)kSwipeMaxMs && dist2(dx, dy) >= kSwipeMinPx * kSwipeMinPx) {
    char dir = abs(dx) >= abs(dy) ? (dx > 0 ? 'r' : 'l') : (dy > 0 ? 'd' : 'u');
    post(Ev::Swipe, 0, done.x, done.y, dir);
  }
}

}  // namespace

bool touchOk() { return s_ok; }

namespace {
std::atomic<bool> s_fingerDown{false};
std::atomic<uint32_t> s_fingerDownMs{0};
std::atomic<uint32_t> s_fingerUpMs{0};
}  // namespace

bool tapInProgress(uint32_t nowMs, uint32_t afterMs) {
  if (s_fingerDown.load()) return (int32_t)(nowMs - s_fingerDownMs.load()) < (int32_t)kTapMaxMs;
  return (int32_t)(nowMs - s_fingerUpMs.load()) <= (int32_t)afterMs;
}

// SensorLib's ESP-IDF begin() doesn't talk to the chip (adding an I2C device never fails), so
// check it here: which addresses answer on the bus, and what a touch-report read returns (byte 6
// is the chip's 0xAB ack when it's in its normal reporting mode).
void probeTouch() {
  char found[96] = "";
  size_t n = 0;
  for (uint8_t a = 0x08; a < 0x78 && n + 6 < sizeof found; a++)
    if (i2c_master_probe(i2cBus(), a, 20) == ESP_OK) n += snprintf(found + n, sizeof found - n, " %02X", a);
  hal::logLine("[hal] i2c answers:%s", n ? found : " none");

  i2c_device_config_t cfg = {};
  cfg.dev_addr_length = I2C_ADDR_BIT_LEN_7;
  cfg.device_address = I2C_ADDR_TOUCH;
  cfg.scl_speed_hz = 400000;
  i2c_master_dev_handle_t h = nullptr;
  if (i2c_master_bus_add_device(i2cBus(), &cfg, &h) != ESP_OK) return;
  const uint8_t cmd[2] = {0xD0, 0x00};
  uint8_t r[8] = {};
  esp_err_t e = i2c_master_transmit_receive(h, cmd, 2, r, sizeof r, 50);
  hal::logLine("[hal] touch read: %s %02X %02X %02X %02X %02X %02X %02X %02X", esp_err_to_name(e), r[0], r[1], r[2], r[3],
               r[4], r[5], r[6], r[7]);
  i2c_master_bus_rm_device(h);
}

std::atomic<uint32_t> s_touchReads{0}, s_touchPoints{0};
// What the reads came back as: points, bus error, no fresh report, no points, release
std::atomic<uint32_t> s_readKinds[5] = {};

bool touchBegin() {
  // Reset pulse first: the controller may be wedged from before a soft reboot.
  pinOutput(PIN_TP_RST);
  pinWrite(PIN_TP_RST, 0);
  delay(20);
  pinWrite(PIN_TP_RST, 1);
  delay(60);

  I2cGuard lock;
  probeTouch();
  s_touch.setPins(PIN_TP_RST, PIN_TP_INT);
  if (!s_touch.begin(i2cBus(), I2C_ADDR_TOUCH)) {
    hal::logLine("[hal] touch: CST9217 not found");
    s_ok = false;
    return false;
  }
  s_touch.setMaxCoordinates(LCD_W, LCD_H);
  s_touch.setMirrorXY(TOUCH_MIRROR_X, TOUCH_MIRROR_Y);
  s_ok = true;
  return true;
}

int touchDiag(char* buf, size_t len) {
  return snprintf(buf, len, "treads=%lu tpts=%lu tkinds=%lu/%lu/%lu/%lu/%lu ", (unsigned long)s_touchReads.load(),
                  (unsigned long)s_touchPoints.load(), (unsigned long)s_readKinds[0].load(), (unsigned long)s_readKinds[1].load(),
                  (unsigned long)s_readKinds[2].load(), (unsigned long)s_readKinds[3].load(), (unsigned long)s_readKinds[4].load());
}

void touchModeChanged(InputMode mode) {
  if (s_mode == InputMode::Trackpad && mode != InputMode::Trackpad && g.dragging) {
    hostlink::sendButton('l', false);
    g.dragging = false;
  }
  s_mode = mode;
  if (mode == InputMode::Trackpad) s_headingAtEntry = motionHeading();
}

void touchPoll(uint32_t now) {
  if (!s_ok) return;
  // The CST9217 pulses its interrupt line rather than holding it, so it's polled; a read while
  // nothing touches it is short and harmless.
  int16_t xs[2] = {0, 0}, ys[2] = {0, 0};
  uint8_t n;
  TouchDrvCST92xx::ReadStatus kind;
  {
    I2cGuard lock;
    n = s_touch.getPoint(xs, ys, 2);
    kind = s_touch.lastRead;
  }
  s_touchReads++;
  if (kind < 5) s_readKinds[kind]++;
  auto release = [&] {
    s_emptyReads = 0;
    onRelease(now);
    s_fingerDown = false;
    s_fingerUpMs = now;
  };
  if (n > 0) {
    if (s_touchPoints++ == 0) hal::logLine("[hal] touch: first touch at %d,%d", xs[0], ys[0]);
    s_emptyReads = 0;
    s_lastReportMs = now;
    if (!s_fingerDown.exchange(true)) s_fingerDownMs = now;
    const int16_t x = clampCoord(xs[0], LCD_W), y = clampCoord(ys[0], LCD_H);
    if (!g.down) onPress(x, y, n, now);
    else onHold(x, y, n, now);
    return;
  }
  if (!g.down) return;
  switch (kind) {
    case TouchDrvCST92xx::ReadRelease:
      // The chip says the finger came up (it says so once)
      release();
      break;
    case TouchDrvCST92xx::ReadNoPoints:
      s_lastReportMs = now;
      if (++s_emptyReads >= 2) release();
      break;
    default:
      // No fresh report (the chip only has one when something changed) or a bus hiccup: that's
      // not a lift. A finger resting still is still down, and still makes a long press.
      if (now - s_lastReportMs >= kQuietReleaseMs) release();
      else if (!g.moved && !g.longFired && !g.swallowed) onHold(g.x, g.y, g.maxFingers, now);
      break;
  }
}

}  // namespace detail
}  // namespace hal
