// The picker: the side button brings it up over any face. Turn through the faces (one every 24°,
// stopping at the ends) or drag a finger across them, tap or press the button to open one, hold
// to back out.
#include "ui_common.h"

namespace ui {
namespace picker {

static constexpr float STEP_DEG = 24.0f;
static constexpr int STEP_PX = 70;  // a finger dragged this far moves one face

static lv_obj_t* s_box;
static lv_obj_t* s_dots;
static lv_obj_t* s_glyph;
static lv_obj_t* s_name;
static lv_obj_t* s_hint;
static FaceId s_items[F_COUNT];
static int s_n = 0;
static int s_sel = 0;
static bool s_open = false;
static uint32_t s_lastInput = 0;
static Detents s_det;
static int16_t s_dragX = 0, s_dragY = 0;  // where the finger was when it last moved the choice

// glyph parts that need to outlive the call that drew them
static Spark s_spark;
static lv_point_t s_needlePts[2];

void create() {
  s_box = box(lv_layer_top(), LCD_W, LCD_H, COL_BG, 0);
  lv_obj_set_pos(s_box, 0, 0);
  s_dots = plain(s_box);
  lv_obj_set_size(s_dots, LCD_W, LCD_H);
  s_glyph = plain(s_box);
  lv_obj_set_size(s_glyph, 180, 180);
  at(s_glyph, CX, CY - 26);
  s_name = label(s_box, &nunito_40, COL_WHITE, "");
  at(s_name, CX, 326);
  s_hint = hintLabel(s_box, "Turn or swipe \xC2\xB7 Tap: open", 380);
  hide(s_box, true);
}

bool isOpen() { return s_open; }

static void buildList() {
  s_n = 0;
  s_items[s_n++] = F_ASSISTANT;
  s_items[s_n++] = F_HOME;
  if (g_model.ctlCount > 0) s_items[s_n++] = F_CONTROLS;
  s_items[s_n++] = F_TIMER;
  s_items[s_n++] = F_STATUS;
  s_items[s_n++] = F_SPIN;
  s_items[s_n++] = F_DICE;
  s_items[s_n++] = F_PONG;
}

static void render(int dir) {
  lv_obj_clean(s_dots);
  const float spacing = 10.0f;
  float start = -spacing * (s_n - 1) / 2.0f;
  for (int i = 0; i < s_n; i++) {
    bool on = i == s_sel;
    lv_obj_t* d = circle(s_dots, on ? 14 : 8, on ? COL_YELLOW : COL_GREY2);
    float x, y;
    polar(start + i * spacing, 204, &x, &y);
    at(d, (int)x, (int)y);
  }
  lv_obj_clean(s_glyph);
  drawGlyph(s_items[s_sel], s_glyph);
  setText(s_name, face(s_items[s_sel]).name);
  if (dir) {
    lv_obj_set_style_translate_x(s_glyph, 0, 0);
    rise(s_glyph, dir > 0 ? 10 : -10, 180);
  }
}

void open() {
  if (s_open) return;
  buildList();
  s_sel = 0;
  for (int i = 0; i < s_n; i++)
    if (s_items[i] == current()) s_sel = i;
  s_det.reset();
  s_open = true;
  s_lastInput = lv_tick_get();
  hal::setInputMode(InputMode::Ui);
  render(0);
  fadeIn(s_box, 150);
  hal::beep(Tone::Tap);
}

void close() {
  if (!s_open) return;
  s_open = false;
  fadeOut(s_box, 140);
  restoreInputMode();
}

// Moves the choice by n, stopping at the ends; false if it was already there
static bool step(int n) {
  int sel = s_sel + n;
  if (sel < 0) sel = 0;
  if (sel > s_n - 1) sel = s_n - 1;
  if (sel == s_sel) return false;
  s_sel = sel;
  hal::beep(Tone::Tick);
  render(n);
  return true;
}

void input(const InputEvent& e) {
  s_lastInput = lv_tick_get();
  switch (e.type) {
    case Ev::Turn: {
      int n = s_det.feed(e.value, STEP_DEG);
      if (n && !step(n)) s_det.reset();  // at an end: it stops, it doesn't wind up
      break;
    }
    case Ev::TouchDown:
      s_dragX = e.x;
      s_dragY = e.y;
      break;
    case Ev::TouchMove: {
      // A finger dragged across: left or up is the next face, right or down the one before
      // (whichever way it moved most). A drag never counts as a tap, so nothing opens.
      const int dx = e.x - s_dragX, dy = e.y - s_dragY;
      const int d = abs(dx) >= abs(dy) ? -dx : -dy;
      if (abs(d) < STEP_PX) break;
      step(d > 0 ? 1 : -1);
      s_dragX = e.x;
      s_dragY = e.y;
      break;
    }
    case Ev::Tap:
    case Ev::Button: {
      FaceId f = s_items[s_sel];
      hal::beep(Tone::Select);
      s_open = false;
      fadeOut(s_box, 140);
      pickerChose(f);
      break;
    }
    case Ev::LongPress:  // (holding the button is power off: ui.cpp sends that there first)
      close();
      break;
    default:
      break;
  }
}

void tick(uint32_t now) {
  if (s_open && (int32_t)(now - s_lastInput) > 8000) close();
}

// ---------------------------------------------------------------- glyphs

void drawGlyph(FaceId f, lv_obj_t* p, float k) {
  lv_obj_update_layout(p);
  float cx = lv_obj_get_width(p) / 2.0f, cy = lv_obj_get_height(p) / 2.0f;
  auto S = [k](float v) { return (int)lroundf(v * k); };
  switch (f) {
    case F_ASSISTANT:
      s_spark.create(p, COL_CLAUDE);
      s_spark.draw(cx, cy, S(66), 0);
      break;
    case F_HOME: {
      lv_obj_t* l = box(p, S(34), S(84), COL_YELLOW, LV_RADIUS_CIRCLE);
      center(l, -S(36), 0);
      lv_obj_t* r = box(p, S(34), S(84), COL_YELLOW, LV_RADIUS_CIRCLE);
      center(r, S(36), 0);
      break;
    }
    case F_CONTROLS: {
      lv_obj_t* a = arc(p, S(60), S(12), COL_YELLOW);
      arcTrack(a, COL_GREY4, 210, 510);
      arcSpan(a, 210, 400);
      lv_obj_t* n = label(p, &nunito_28, COL_WHITE, "21");
      center(n, 0, 0);
      break;
    }
    case F_TIMER: {
      lv_obj_t* a = arc(p, S(58), S(14), COL_YELLOW);
      arcTrack(a, COL_GREY3, 0, 360);
      arcSpan(a, 0, 250);
      lv_obj_t* n = label(p, &nunito_28, COL_WHITE, "10");
      center(n, 0, 0);
      break;
    }
    case F_STATUS: {
      lv_obj_t* c = ringOutline(p, S(128), S(6), COL_GREY1);
      center(c, 0, 0);
      lv_obj_t* w = label(p, &lv_font_montserrat_48, COL_WHITE, LV_SYMBOL_WIFI);
      center(w, 0, 0);
      break;
    }
    case F_SPIN: {
      for (int i = 0; i < 8; i++) {
        lv_obj_t* a = arc(p, S(56), S(30), i % 2 == 0 ? COL_YELLOW : COL_GREY3, false);
        arcSpan(a, i * 45 + 2, i * 45 + 43);
      }
      lv_obj_t* n = line(p, s_needlePts, S(9), COL_WHITE);
      float x, y;
      polar(55, S(36), &x, &y, (int)cx, (int)cy);
      setLine(n, s_needlePts, cx, cy, x, y);
      lv_obj_t* hub = circle(p, S(18), COL_WHITE);
      center(hub, 0, 0);
      break;
    }
    case F_DICE: {
      lv_obj_t* d = box(p, S(108), S(108), COL_WHITE, S(22));
      center(d, 0, 0);
      const int pos[5][2] = {{-1, -1}, {1, -1}, {0, 0}, {-1, 1}, {1, 1}};
      for (auto& q : pos) {
        lv_obj_t* pip = circle(d, S(18), COL_BG);
        center(pip, q[0] * S(29), q[1] * S(29));
      }
      break;
    }
    case F_PONG: {
      lv_obj_t* t = arc(p, S(68), S(2), COL_GREY3);
      arcSpan(t, 0, 360);
      lv_obj_t* a = arc(p, S(68), S(10), COL_YELLOW);
      arcSpan(a, 40, 80);
      lv_obj_t* b = arc(p, S(68), S(10), COL_YELLOW);
      arcSpan(b, 220, 260);
      lv_obj_t* ball = circle(p, S(16), COL_WHITE);
      center(ball, -S(12), -S(18));
      break;
    }
    default:
      break;
  }
}

}  // namespace picker
}  // namespace ui
