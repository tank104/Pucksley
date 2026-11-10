// What every face shares: the face table, the manager's API, drawing helpers and the palette.
#pragma once
#include <lvgl.h>
#include <math.h>
#include <stdint.h>
#include "../config.h"
#include "../events.h"
#include "../hal.h"
#include "../hub.h"
#include "../model.h"

LV_FONT_DECLARE(nunito_digits_150)
LV_FONT_DECLARE(nunito_digits_110)
LV_FONT_DECLARE(nunito_64)
LV_FONT_DECLARE(nunito_40)
LV_FONT_DECLARE(nunito_28)
LV_FONT_DECLARE(nunito_22)
LV_FONT_DECLARE(nunito_18)

namespace ui {

constexpr int CX = LCD_W / 2;
constexpr int CY = LCD_H / 2;
constexpr float PI_F = 3.14159265f;

inline lv_color_t rgb(uint32_t c) { return lv_color_hex(c); }

enum FaceId : uint8_t {
  F_ASSISTANT, F_HOME, F_CONTROLS, F_TIMER, F_STATUS, F_CONFIRM, F_SETUP,
  F_SPIN, F_DICE, F_PONG,
  F_COUNT,
  F_NONE = 0xFF,
};

// One face. Every hook is optional except create.
struct Face {
  const char* id;    // as in docs/PROTOCOL.md
  const char* name;  // in the picker
  void (*create)(lv_obj_t* root);
  void (*enter)();
  void (*leave)();
  void (*input)(const InputEvent& e);
  void (*update)(uint32_t dirty);
  void (*tick)(uint32_t now);
};

extern const Face FACE_ASSISTANT, FACE_HOME, FACE_CONTROLS, FACE_TIMER, FACE_STATUS, FACE_CONFIRM, FACE_SETUP,
    FACE_SPIN, FACE_DICE, FACE_PONG;

// ---- the manager (ui.cpp)
FaceId current();
const Face& face(FaceId f);
void show(FaceId f);          // switch now (a person chose it, or the manager decided)
void back(FaceId from);       // leave a face the host brought up, if it's showing
void reportFace(const char* id);  // tell the app which face is up (only sends on change)
void wake();                  // screen on at full brightness, and count as activity
void restoreInputMode();      // trackpad mode back on if the trackpad face is up (after an overlay)
void pickerChose(FaceId f);   // the picker's choice (a person picked it)
void startTalking(const char* via);  // start listening (any face): switches to the assistant face

// ---- drawing helpers (ui_util.cpp)
lv_obj_t* plain(lv_obj_t* parent);                     // a bare container: no style, no scroll, no click
lv_obj_t* box(lv_obj_t* parent, int w, int h, uint32_t color, int radius = 0, lv_opa_t opa = LV_OPA_COVER);
lv_obj_t* circle(lv_obj_t* parent, int d, uint32_t color, lv_opa_t opa = LV_OPA_COVER);
lv_obj_t* ringOutline(lv_obj_t* parent, int d, int border, uint32_t color);
lv_obj_t* label(lv_obj_t* parent, const lv_font_t* font, uint32_t color, const char* text = "");
// An lv_arc with no knob and no background track; `radius` is to the middle of the stroke.
// Angles are set with arcSpan() (clockwise degrees from 12 o'clock).
lv_obj_t* arc(lv_obj_t* parent, int radius, int width, uint32_t color, bool rounded = true);
void arcSpan(lv_obj_t* a, float fromDeg, float toDeg);  // clockwise from 12 o'clock
void arcTrack(lv_obj_t* a, uint32_t color, float fromDeg, float toDeg);  // show the background track
void center(lv_obj_t* o, int dx, int dy);               // o's center at parent center + (dx, dy)
void at(lv_obj_t* o, int x, int y);                     // o's center at (x, y) in a full-screen parent
void polar(float degCw, float r, float* x, float* y, int cx = CX, int cy = CY);
void setLine(lv_obj_t* line, lv_point_t pts[2], float x0, float y0, float x1, float y1);  // keeps the line's box tight
lv_obj_t* line(lv_obj_t* parent, lv_point_t pts[2], int width, uint32_t color);
void hide(lv_obj_t* o, bool hidden);
void setText(lv_obj_t* label, const char* text);  // only invalidates when it changed

// ---- the words every face shares: a small-caps title at the top and an instruction at the
// bottom, in the same places and sizes everywhere (22 px grey-1 is the smallest text a person is
// asked to read on the 1.75" glass)
constexpr int HEADER_Y = 72;
constexpr int HINT_Y = 392;
lv_obj_t* header(lv_obj_t* parent, const char* text, uint32_t color = COL_GREY1);  // upper-cased
void setHeader(lv_obj_t* header, const char* text);                               // upper-cased
lv_obj_t* hintLabel(lv_obj_t* parent, const char* text, int y = HINT_Y, int width = 300);

// Text too long for its place: a scrolling column whose edges fade where there's more to read,
// with a thin mark on the right-hand rim showing where you are in it.
struct Scroller {
  lv_obj_t* box = nullptr;
  lv_obj_t* text = nullptr;
  lv_obj_t* track = nullptr;
  lv_obj_t* thumb = nullptr;
  lv_draw_mask_fade_param_t fadeTop, fadeBottom;
  int16_t maskTop = -1, maskBottom = -1;
  void create(lv_obj_t* parent, int w, int h, int cx, int cy, const lv_font_t* font, uint32_t color, int lineSpace = 4);
  void setText(const char* t);  // back to the top when the text changes
  void toTop();
  bool scrollBy(int dy, bool animate = true);  // false when it's already at that end
  bool overflows();
  void refreshCue();
};

void fadeIn(lv_obj_t* o, uint32_t ms = 200, uint32_t delay = 0);
void fadeOut(lv_obj_t* o, uint32_t ms = 200, bool hideWhenDone = true);
void rise(lv_obj_t* o, int fromDy, uint32_t ms = 260, uint32_t delay = 0);
void shake(lv_obj_t* o);
void pop(lv_obj_t* o, uint32_t ms = 320);  // zoom in with a little overshoot

uint32_t rnd();
int rndRange(int lo, int hi);  // inclusive
float clampf(float v, float lo, float hi);
float approach(float v, float target, float rate);  // eases v toward target (rate 0..1 per frame)
float ease(float v, float target, float dt, float speed);  // the same, by time: speed ~ 1 / time constant (s)
float angleDiff(float a, float b);                  // shortest signed a - b in degrees

// Turns degrees into whole steps, keeping the remainder.
struct Detents {
  float acc = 0;
  int feed(float deg, float perStep) {
    acc += deg;
    int n = (int)(acc / perStep);
    acc -= n * perStep;
    return n;
  }
  void reset() { acc = 0; }
};

// ---- the volume that home and call share (volume.cpp)
namespace vol {
void turn(float deg);     // 0.35 volume per degree; sends to the app
float value();
bool muted();             // what the screen should show (local action wins for a moment)
void toggleMute();
void syncFromModel();     // takes the app's volume unless the dial was just turned
bool recentlyTurned(uint32_t ms);
}  // namespace vol

// The big number with the rim arc, shown while turning.
struct VolumeView {
  lv_obj_t* box = nullptr;
  lv_obj_t* ring = nullptr;
  lv_obj_t* num = nullptr;
  lv_obj_t* sub = nullptr;
  bool shown = false;
  uint32_t lastShowMs = 0;
  int lastNum = -1;
  int lastMuted = -1;
  void create(lv_obj_t* parent);
  void show();               // fades in, keeps it up for 1.5 s after the last call
  void refresh();
  bool tick(uint32_t now);   // returns true while it's showing
  void hideNow();
};

// ---- timers the hub runs (face_timer.cpp)
namespace timers {
int active();                        // how many are running
const Timer* soonest();              // the one ending first, or nullptr
int remainingSec(const Timer& t);    // seconds left (0 when done)
float remainingFrac(const Timer& t); // 0..1 of its length left
}  // namespace timers

// ---- activity icons (an LV_SYMBOL for the hub's icon names)
const char* iconSymbol(const char* icon);

// ---- the spark: the assistant's mark, also drawn small by the picker (face_assistant.cpp)
struct Spark {
  lv_obj_t* rays[8] = {};
  lv_point_t pts[8][2] = {};
  void create(lv_obj_t* parent, uint32_t color);
  void draw(float cx, float cy, float size, float rotDeg, float breathe = 0);  // size = long ray length
  void color(uint32_t c);
};

// ---- overlays on lv_layer_top (picker.cpp, overlay.cpp)
namespace picker {
void create();
void open();
bool isOpen();
void input(const InputEvent& e);
void tick(uint32_t now);
void close();
void drawGlyph(FaceId f, lv_obj_t* parent, float scale = 1.0f);  // the face's icon, centered in parent
}  // namespace picker

namespace overlay {
void create();
void banner(const char* text, bool accent);
void powerConfirm();
bool powerOpen();
void powerInput(const InputEvent& e);
void tick(uint32_t now);
}  // namespace overlay

}  // namespace ui
