// The side buttons. BOOT is a plain GPIO; the PWR key belongs to the AXP2101 and arrives through
// power.cpp as already-classified short/long presses. Both end up as Button / ButtonLong.
#include "../platform.h"
#include "../config.h"
#include "hal_internal.h"

namespace hal {
namespace detail {

namespace {

constexpr uint32_t kDebounceMs = 20;
constexpr uint32_t kLongMs = 800;

bool s_down = false;          // debounced state
bool s_raw = false;
uint32_t s_rawSince = 0;
uint32_t s_pressedAt = 0;
bool s_longFired = false;
bool s_swallowed = false;     // pressed while the display was off: that press only wakes it

}  // namespace

void buttonEvent(bool longPress) {
  // The PMU reports a finished press, so there's no "rest of the gesture" to swallow. A short
  // press on a dark screen only wakes it; a long one is power off, which always goes through.
  if (!activityBegin(Src::Button) && !longPress) return;
  post(longPress ? Ev::ButtonLong : Ev::Button);
}

void buttonsBegin() { pinInputPullup(PIN_BOOT_BTN); }

void buttonsPoll(uint32_t now) {
  const bool raw = pinRead(PIN_BOOT_BTN) == 0;
  if (raw != s_raw) {
    s_raw = raw;
    s_rawSince = now;
  }
  if (raw != s_down && now - s_rawSince >= kDebounceMs) {
    s_down = raw;
    if (s_down) {
      s_pressedAt = now;
      s_longFired = false;
      s_swallowed = !activityBegin(Src::Button);
    } else if (!s_swallowed && !s_longFired) {
      activityContinue();
      post(Ev::Button);
    }
  }
  // Held long it's power off, even if the press began on a dark screen
  if (s_down && !s_longFired && now - s_pressedAt >= kLongMs) {
    s_longFired = true;
    activityContinue();
    post(Ev::ButtonLong);
  }
}

}  // namespace detail
}  // namespace hal
