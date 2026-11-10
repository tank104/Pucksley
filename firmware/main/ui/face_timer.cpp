// Timer: the timers the hub is running ("set a timer for ten minutes"), counting down round the
// rim. With none running: turn to set minutes, tap to start one. Hold to cancel the one showing.
#include "../platform.h"
#include <stdio.h>
#include <string.h>
#include "ui_common.h"

namespace ui {

// ---------------------------------------------------------------- timers:: (shared with Home)

namespace timers {

int remainingSec(const Timer& t) {
  int32_t ms = (int32_t)(t.endsAtMs - millis());
  return ms > 0 ? (ms + 999) / 1000 : 0;
}

float remainingFrac(const Timer& t) {
  if (!t.totalMs) return 0;
  int32_t ms = (int32_t)(t.endsAtMs - millis());
  return ms <= 0 ? 0 : clampf((float)ms / (float)t.totalMs, 0, 1);
}

int active() {
  int n = 0;
  for (auto& t : g_model.timers) n += t.active;
  return n;
}

const Timer* soonest() {
  const Timer* best = nullptr;
  for (auto& t : g_model.timers)
    if (t.active && (!best || (int32_t)(t.endsAtMs - best->endsAtMs) < 0)) best = &t;
  return best;
}

}  // namespace timers

// ---------------------------------------------------------------- the face
namespace {

lv_obj_t* root;
lv_obj_t* hdr;
lv_obj_t* ring;
lv_obj_t* timeLbl;
lv_obj_t* subLbl;
lv_obj_t* moreLbl;
lv_obj_t* hint;

int setMinutes = 10;
int showing = 0;           // which running timer (index among the active ones)
Detents det;
int lastSec = -1;

const Timer* nth(int i) {
  for (auto& t : g_model.timers)
    if (t.active && i-- == 0) return &t;
  return nullptr;
}

void fmt(int sec, char* buf, size_t n) {
  if (sec >= 3600) snprintf(buf, n, "%d:%02d:%02d", sec / 3600, sec / 60 % 60, sec % 60);
  else snprintf(buf, n, "%d:%02d", sec / 60, sec % 60);
}

void render(bool force) {
  int n = timers::active();
  if (showing >= n) showing = 0;
  const Timer* t = nth(showing);
  char buf[64];
  if (t) {
    int sec = timers::remainingSec(*t);
    if (!force && sec == lastSec) {
      arcSpan(ring, 0, 360.0f * timers::remainingFrac(*t));
      return;
    }
    lastSec = sec;
    setHeader(hdr, "Timer");
    fmt(sec, buf, sizeof buf);
    lv_obj_set_style_text_font(timeLbl, strlen(buf) <= 5 ? &nunito_digits_110 : &nunito_64, 0);
    setText(timeLbl, buf);
    lv_obj_set_style_text_color(timeLbl, rgb(sec == 0 ? COL_YELLOW : COL_WHITE), 0);
    setText(subLbl, t->label[0] ? t->label : (sec == 0 ? "Done" : ""));
    arcSpan(ring, 0, 360.0f * timers::remainingFrac(*t));
    hide(ring, false);
    if (n > 1) {
      snprintf(buf, sizeof buf, "%d of %d \xC2\xB7 turn for others", showing + 1, n);
      setText(moreLbl, buf);
    }
    hide(moreLbl, n <= 1);
    setText(hint, "Hold to cancel");
  } else {
    lastSec = -1;
    setHeader(hdr, "New timer");
    snprintf(buf, sizeof buf, "%d", setMinutes);
    lv_obj_set_style_text_font(timeLbl, &nunito_digits_150, 0);
    lv_obj_set_style_text_color(timeLbl, rgb(COL_WHITE), 0);
    setText(timeLbl, buf);
    setText(subLbl, setMinutes == 1 ? "minute" : "minutes");
    arcSpan(ring, 0, 360.0f * (setMinutes <= 60 ? setMinutes / 60.0f : 1.0f));
    hide(ring, false);
    hide(moreLbl, true);
    setText(hint, "Turn to set \xC2\xB7 Tap to start");
  }
}

void create(lv_obj_t* r) {
  root = r;
  ring = arc(root, 214, 12, COL_YELLOW);
  arcTrack(ring, COL_GREY4, 0, 360);
  hdr = header(root, "Timer");
  timeLbl = label(root, &nunito_digits_110, COL_WHITE, "");
  at(timeLbl, CX, CY - 6);
  subLbl = label(root, &nunito_28, COL_GREY1, "");
  at(subLbl, CX, CY + 82);
  moreLbl = label(root, &nunito_18, COL_GREY1, "");
  at(moreLbl, CX, CY + 122);
  hint = hintLabel(root, "");
}

void enter() {
  det.reset();
  render(true);
}

int stepFor(int m, int dir) {
  if (dir > 0) return m < 10 ? 1 : m < 60 ? 5 : 15;
  return m <= 10 ? 1 : m <= 60 ? 5 : 15;
}

void input(const InputEvent& e) {
  const Timer* t = nth(showing);
  switch (e.type) {
    case Ev::Turn: {
      int n = det.feed(e.value, t ? 30.0f : 10.0f);
      if (!n) break;
      if (t) {
        int count = timers::active();
        if (count > 1) {
          showing = (showing + (n > 0 ? 1 : -1) + count) % count;
          hal::beep(Tone::Tick);
          render(true);
        }
      } else {
        int dir = n > 0 ? 1 : -1;
        for (int i = 0; i < abs(n); i++) setMinutes += dir * stepFor(setMinutes, dir);
        setMinutes = setMinutes < 1 ? 1 : setMinutes > 180 ? 180 : setMinutes;
        hal::beep(Tone::Tick);
        render(true);
      }
      break;
    }
    case Ev::Tap:
      if (!t) {
        // with no hub the puck keeps the timer itself
        hub::timerStart(setMinutes * 60, nullptr);
        hal::beep(Tone::Success);
        pop(timeLbl);
      }
      break;
    case Ev::LongPress:
      if (t) {
        hub::timerCancel(t->id);
        hal::beep(Tone::Select);
      }
      break;
    default:
      break;
  }
}

void update(uint32_t dirty) {
  if (dirty & (D_TIMERS | D_LINK)) render(true);
}

void tick(uint32_t) {
  // a finished timer lingers at 0:00 for a few seconds, then goes (unless the hub says otherwise)
  for (auto& t : g_model.timers)
    if (t.active && (int32_t)(millis() - t.endsAtMs) > 8000) {
      t.active = false;
      model_mark(D_TIMERS);
    }
  render(false);
}

}  // namespace

const Face FACE_TIMER = {"timer", "Timer", create, enter, nullptr, input, update, tick};

}  // namespace ui
