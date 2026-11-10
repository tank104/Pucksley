// Controls: the dials the hub's plugins put on the puck, one per page: a Sonos room's volume, the
// heating setpoint, a lamp. Turn to change a value, tap to switch or press, swipe for the next.
#include <stdio.h>
#include <string.h>
#include "ui_common.h"

namespace ui {
namespace {

constexpr float RING_FROM = 210.0f;
constexpr float RING_SPAN = 300.0f;

lv_obj_t* root;
lv_obj_t* groupLbl;
lv_obj_t* nameLbl;
lv_obj_t* ring;
lv_obj_t* valueLbl;
lv_obj_t* unitLbl;
lv_obj_t* toggle;
lv_obj_t* toggleLbl;
lv_obj_t* dots;
lv_obj_t* hint;
lv_obj_t* empty;

int page = 0;
float localV = 0;          // the value as the person turns it
uint32_t localAt = 0;      // when they last turned it
Detents det;
Detents pageDet;

Control* cur() { return page < g_model.ctlCount ? &g_model.ctls[page] : nullptr; }

float shownValue(const Control& c) { return localAt && lv_tick_elaps(localAt) < 1500 ? localV : c.v; }

void fmtValue(const Control& c, float v, char* buf, size_t n) {
  if (c.txt[0] && !(localAt && lv_tick_elaps(localAt) < 1500)) {
    snprintf(buf, n, "%s", c.txt);
    return;
  }
  if (c.step < 1.0f) snprintf(buf, n, "%.1f", (double)v);
  else snprintf(buf, n, "%d", (int)lroundf(v));
}

void renderDots() {
  lv_obj_clean(dots);
  int n = g_model.ctlCount;
  if (n <= 1) return;
  const float spacing = 9.0f;
  float start = 180.0f + spacing * (n - 1) / 2.0f;  // bottom of the rim, left to right
  for (int i = 0; i < n; i++) {
    bool on = i == page;
    lv_obj_t* d = circle(dots, on ? 12 : 7, on ? COL_YELLOW : COL_GREY2);
    float x, y;
    polar(start - i * spacing, 214, &x, &y);
    at(d, (int)x, (int)y);
  }
}

void render() {
  Control* c = cur();
  hide(empty, c != nullptr);
  hide(groupLbl, !c);
  hide(nameLbl, !c);
  if (!c) {
    hide(ring, true);
    hide(valueLbl, true);
    hide(unitLbl, true);
    hide(toggle, true);
    setText(hint, "");
    lv_obj_clean(dots);
    return;
  }
  setHeader(groupLbl, c->group);
  setText(nameLbl, c->name);
  bool num = c->kind == CtlKind::Num, sel = c->kind == CtlKind::Sel;
  hide(ring, !num);
  hide(valueLbl, !(num || sel));
  hide(unitLbl, !num || !c->unit[0]);
  hide(toggle, num || sel);
  char buf[32];
  if (num) {
    float v = shownValue(*c);
    fmtValue(*c, v, buf, sizeof buf);
    // the big digits font has 0-9, minus and colon only (no decimal point)
    bool digitsOnly = strspn(buf, "0123456789-") == strlen(buf);
    lv_obj_set_style_text_font(valueLbl, digitsOnly && strlen(buf) <= 4 ? &nunito_digits_110 : &nunito_64, 0);
    setText(valueLbl, buf);
    setText(unitLbl, c->unit);
    float span = c->max - c->min;
    float f = span > 0 ? clampf((v - c->min) / span, 0, 1) : 0;
    arcSpan(ring, RING_FROM, RING_FROM + RING_SPAN * f);
    // warm for temperatures, yellow for levels
    bool temp = strstr(c->unit, "\xC2\xB0") != nullptr;  // a degree sign
    lv_obj_set_style_arc_color(ring, rgb(temp ? COL_CLAUDE : COL_YELLOW), LV_PART_INDICATOR);
    setText(hint, g_model.ctlCount > 1 ? "Swipe for more" : "Turn to change");
  } else if (sel) {
    int i = (int)lroundf(shownValue(*c));
    lv_obj_set_style_text_font(valueLbl, &nunito_40, 0);
    setText(valueLbl, i >= 0 && i < c->nopts ? c->opts[i] : (c->txt[0] ? c->txt : "-"));
    setText(hint, g_model.ctlCount > 1 ? "Swipe for more" : "Turn to choose");
  } else if (c->kind == CtlKind::Bool) {
    lv_obj_set_style_bg_color(toggle, rgb(c->on ? COL_YELLOW : COL_GREY4), 0);
    lv_obj_set_style_border_color(toggle, rgb(c->on ? COL_YELLOW : COL_GREY2), 0);
    lv_obj_set_style_text_color(toggleLbl, rgb(c->on ? COL_BG : COL_GREY1), 0);
    setText(toggleLbl, c->txt[0] ? c->txt : (c->on ? "On" : "Off"));
    setText(hint, "Tap to switch");
  } else {
    lv_obj_set_style_bg_color(toggle, rgb(COL_CLAUDE), 0);
    lv_obj_set_style_border_color(toggle, rgb(COL_CLAUDE), 0);
    lv_obj_set_style_text_color(toggleLbl, rgb(COL_WHITE), 0);
    setText(toggleLbl, c->txt[0] ? c->txt : "Go");
    setText(hint, "Tap to press");
  }
  renderDots();
}

void create(lv_obj_t* r) {
  root = r;
  ring = arc(root, 200, 16, COL_YELLOW);
  arcTrack(ring, COL_GREY4, RING_FROM, RING_FROM + RING_SPAN);
  groupLbl = header(root, "");
  nameLbl = label(root, &nunito_28, COL_WHITE, "");
  lv_obj_set_width(nameLbl, 300);
  lv_label_set_long_mode(nameLbl, LV_LABEL_LONG_DOT);
  lv_obj_set_style_text_align(nameLbl, LV_TEXT_ALIGN_CENTER, 0);
  at(nameLbl, CX, 116);
  valueLbl = label(root, &nunito_digits_110, COL_WHITE, "");
  at(valueLbl, CX, CY + 4);
  unitLbl = label(root, &nunito_28, COL_GREY1, "");
  at(unitLbl, CX, CY + 82);
  toggle = box(root, 170, 170, COL_GREY4, LV_RADIUS_CIRCLE);
  lv_obj_set_style_border_width(toggle, 4, 0);
  lv_obj_set_style_border_opa(toggle, LV_OPA_COVER, 0);
  at(toggle, CX, CY + 14);
  toggleLbl = label(toggle, &nunito_40, COL_GREY1, "Off");
  lv_obj_center(toggleLbl);
  dots = plain(root);
  lv_obj_set_size(dots, LCD_W, LCD_H);
  empty = label(root, &nunito_22, COL_GREY1, "No controls yet.\nPlugins on the hub add dials here.");
  lv_obj_set_style_text_align(empty, LV_TEXT_ALIGN_CENTER, 0);
  at(empty, CX, CY);
  hint = hintLabel(root, "", 372, 210);
}

void enter() {
  if (page >= g_model.ctlCount) page = 0;
  localAt = 0;
  det.reset();
  pageDet.reset();
  render();
}

void go(int dir) {
  int n = g_model.ctlCount;
  if (n <= 1) return;
  page = (page + dir + n) % n;
  localAt = 0;
  det.reset();
  hal::beep(Tone::Select);
  render();
  rise(valueLbl, dir > 0 ? 10 : -10, 180);
}

void input(const InputEvent& e) {
  Control* c = cur();
  if (!c) return;
  switch (e.type) {
    case Ev::Turn:
      if (c->kind == CtlKind::Num) {
        int n = det.feed(e.value, 8.0f);
        if (!n) break;
        float base = shownValue(*c);
        float v = clampf(base + n * c->step, c->min, c->max);
        if (c->step >= 1.0f) v = roundf(v / c->step) * c->step;
        if (v != base) {
          localV = v;
          localAt = lv_tick_get();
          hub::controlValue(c->id, v);
          hal::beep(Tone::Tick);
          render();
        }
      } else if (c->kind == CtlKind::Sel) {
        int n = det.feed(e.value, 24.0f);
        if (!n || c->nopts == 0) break;
        int i = (int)lroundf(shownValue(*c)) + n;
        i = i < 0 ? 0 : i >= c->nopts ? c->nopts - 1 : i;
        localV = (float)i;
        localAt = lv_tick_get();
        hub::controlValue(c->id, (float)i);
        hal::beep(Tone::Tick);
        render();
      } else {
        int n = pageDet.feed(e.value, 40.0f);
        if (n) go(n > 0 ? 1 : -1);
      }
      break;
    case Ev::Tap:
      if (c->kind == CtlKind::Bool) {
        c->on = !c->on;  // shown now; the hub confirms
        hub::controlToggle(c->id, c->on);
        hal::beep(Tone::Tap);
        render();
        pop(toggle);
      } else if (c->kind == CtlKind::Act) {
        hub::controlPress(c->id);
        hal::beep(Tone::Success);
        pop(toggle);
      }
      break;
    case Ev::Swipe:
      if (e.dir == 'l') go(1);
      else if (e.dir == 'r') go(-1);
      break;
    default:
      break;
  }
}

void update(uint32_t dirty) {
  if (dirty & D_CTLS) {
    if (page >= g_model.ctlCount) page = 0;
    render();
  } else if (dirty & D_CTLV) {
    render();
  }
}

void tick(uint32_t) {
  if (localAt && lv_tick_elaps(localAt) >= 1500) {
    localAt = 0;
    render();
  }
}

}  // namespace

const Face FACE_CONTROLS = {"controls", "Controls", create, enter, nullptr, input, update, tick};

}  // namespace ui
