// The assistant: tap (or lift the puck), talk, and it answers out loud. The spark is Claude: it
// rests above the time while waiting, sits in the middle and listens (rings and a glow that follow
// your voice), spins while it thinks (what it heard underneath, and what it's doing), and shrinks
// to the top while it speaks so the answer has room (long answers scroll along as they're spoken).
// Tap while it thinks or talks to interrupt; hold to cancel.
#include <stdio.h>
#include <string.h>
#include "ui_common.h"

namespace ui {

// ---------------------------------------------------------------- the spark
// Eight rounded rays, long and short in turn: our mark for "the assistant is here".

void Spark::create(lv_obj_t* parent, uint32_t c) {
  for (int i = 0; i < 8; i++) rays[i] = line(parent, pts[i], 8, c);
}

void Spark::color(uint32_t c) {
  for (auto* r : rays) lv_obj_set_style_line_color(r, rgb(c), 0);
}

void Spark::draw(float cx, float cy, float size, float rotDeg, float breathe) {
  float w = size * 0.2f;
  if (w < 3) w = 3;
  lv_coord_t iw = (lv_coord_t)lroundf(w);
  float t = lv_tick_get() / 1000.0f;
  for (int i = 0; i < 8; i++) {
    float len = (i % 2 == 0 ? 1.0f : 0.7f) * size;
    if (breathe > 0) len *= 1.0f + breathe * 0.2f * sinf(t * 5.0f - i * 0.785f);
    float x1, y1;
    polar(rotDeg + i * 45.0f, len, &x1, &y1, 0, 0);
    if (lv_obj_get_style_line_width(rays[i], 0) != iw) lv_obj_set_style_line_width(rays[i], iw, 0);
    setLine(rays[i], pts[i], cx, cy, cx + x1, cy + y1);
  }
}

// ---------------------------------------------------------------- activity icons

const char* iconSymbol(const char* icon) {
  struct Map { const char* name; const char* sym; };
  static const Map kMap[] = {
      {"music", LV_SYMBOL_AUDIO},    {"volume", LV_SYMBOL_VOLUME_MAX}, {"heat", LV_SYMBOL_TINT},
      {"light", LV_SYMBOL_CHARGE},   {"tv", LV_SYMBOL_VIDEO},          {"plug", LV_SYMBOL_POWER},
      {"search", LV_SYMBOL_EYE_OPEN}, {"timer", LV_SYMBOL_BELL},       {"pc", LV_SYMBOL_KEYBOARD},
      {"home", LV_SYMBOL_HOME},      {"note", LV_SYMBOL_EDIT},         {"weather", LV_SYMBOL_GPS},
      {"wifi", LV_SYMBOL_WIFI},
  };
  for (auto& m : kMap)
    if (icon && !strcmp(icon, m.name)) return m.sym;
  return LV_SYMBOL_SETTINGS;
}

// ---------------------------------------------------------------- the face
namespace {

enum class View : uint8_t { Offline, Idle, Listening, Thinking, Answer, Error };

lv_obj_t* root;
lv_obj_t* halo;          // the soft glow behind the spark
Spark spark;
lv_obj_t* ring1;
lv_obj_t* ring2;
lv_obj_t* clockLbl;      // idle: the time, large; elsewhere hidden
lv_obj_t* dateLbl;       // idle: the date
lv_obj_t* topClock;      // the small time at the top (thinking, error)
lv_obj_t* status;        // centre line: "Listening…", the error, the offline state
lv_obj_t* heard;         // what the person said
Scroller reply;
lv_obj_t* actRow;
lv_obj_t* actIcon;
lv_obj_t* actText;
lv_obj_t* hint;
lv_obj_t* volArc;

View shown = (View)0xFF;
float sx = CX, sy = 150, ss = 56, srot = 0, haloD = 0, haloA = 0;
uint32_t lastFrame = 0;
uint32_t volShownAt = 0;
int lastMinute = -1;
int lastDay = -1;
char tail[220];
char quoted[330];

// auto-scroll while it speaks
uint32_t speakStart = 0;
bool manualScroll = false;
float autoY = 0;

constexpr uint32_t ANSWER_KEEP_MS = 120000;  // the last answer stays up this long
constexpr float SPOKEN_CHARS_PER_S = 14.5f;   // roughly how fast the voice reads

View currentView() {
  if (!model_can_talk()) return View::Offline;
  switch (g_model.ai) {
    case AiState::Listening: return View::Listening;
    case AiState::Thinking: return View::Thinking;
    case AiState::Speaking: return View::Answer;
    case AiState::Error: return View::Error;
    case AiState::Idle:
      if (g_model.reply[0] && lv_tick_elaps(g_model.aiSince) < ANSWER_KEEP_MS) return View::Answer;
      return View::Idle;
  }
  return View::Idle;
}

const char* offlineText() {
  switch (g_model.net) {
    case NetState::NoWifi: return "No Wi-Fi yet";
    case NetState::Joining: return "Joining Wi-Fi\xE2\x80\xA6";
    case NetState::Portal: return "Set up Wi-Fi on your phone";
    case NetState::Searching: return "Looking for the hub\xE2\x80\xA6";
    case NetState::Connecting: return "Connecting to the hub\xE2\x80\xA6";
    case NetState::Pending: return "Approve this puck on the hub";
    case NetState::Online: return "Connecting\xE2\x80\xA6";
  }
  return "Not connected";
}

// The last ~150 characters, from a word boundary
const char* tailOf(const char* text, size_t keep) {
  size_t n = strlen(text);
  if (n <= keep) return text;
  const char* p = text + n - keep;
  while (*p && *p != ' ') p++;
  snprintf(tail, sizeof tail, "\xE2\x80\xA6%s", p);
  return tail;
}

void refreshClock(bool force) {
  int h, m;
  if (!model_local_time(&h, &m, nullptr)) {
    setText(clockLbl, "");
    setText(dateLbl, "");
    setText(topClock, "");
    return;
  }
  if (force || m != lastMinute) {
    lastMinute = m;
    char buf[8];
    snprintf(buf, sizeof buf, "%d:%02d", h, m);
    setText(clockLbl, buf);
    setText(topClock, buf);
  }
  int y, mo, d, wd;
  if (model_local_date(&y, &mo, &d, &wd) && (force || d != lastDay)) {
    lastDay = d;
    static const char* const kDays[] = {"Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday"};
    static const char* const kMonths[] = {"January", "February", "March", "April", "May", "June",
                                          "July", "August", "September", "October", "November", "December"};
    char buf[40];
    snprintf(buf, sizeof buf, "%s %d %s", kDays[wd % 7], d, kMonths[(mo + 11) % 12]);
    setText(dateLbl, buf);
  }
}

void refreshActivity() {
  bool any = g_model.actCount > 0;
  View v = currentView();
  hide(actRow, !any || v == View::Idle || v == View::Offline || v == View::Listening || v == View::Error);
  if (!any) return;
  const Activity& a = g_model.acts[0];
  setText(actIcon, iconSymbol(a.icon));
  setText(actText, a.text);
  uint32_t c = a.state == 'e' ? COL_RED : a.state == 'o' ? COL_WHITE : COL_GREY1;
  lv_obj_set_style_text_color(actText, rgb(c), 0);
  lv_obj_set_style_text_color(actIcon, rgb(a.state == 'e' ? COL_RED : COL_CLAUDE), 0);
}

void refreshText() {
  View v = currentView();
  switch (v) {
    case View::Offline:
      setText(status, offlineText());
      break;
    case View::Idle:
      break;
    case View::Listening:
      // The words as they're recognised (live), or just "Listening..."
      if (g_model.heard[0]) {
        snprintf(quoted, sizeof quoted, "\"%s\"", tailOf(g_model.heard, 90));
        lv_obj_set_style_text_font(status, &nunito_22, 0);
        setText(status, quoted);
      } else {
        lv_obj_set_style_text_font(status, &nunito_28, 0);
        setText(status, "Listening\xE2\x80\xA6");
      }
      at(status, CX, 318);
      break;
    case View::Thinking:
      if (g_model.heard[0]) {
        snprintf(quoted, sizeof quoted, "\"%s\"", tailOf(g_model.heard, 150));
        setText(heard, quoted);
      } else {
        setText(heard, "\xE2\x80\xA6");
      }
      break;
    case View::Answer:
      setText(heard, g_model.heard[0] ? g_model.heard : "");
      reply.setText(g_model.reply);
      reply.refreshCue();
      break;
    case View::Error:
      setText(status, g_model.aiError[0] ? g_model.aiError : "Something went wrong");
      break;
  }
  refreshActivity();
}

// The line at the bottom: depends on the view and on whether speech is still playing
void refreshHint() {
  if (volShownAt) return;  // the volume readout has it for a moment
  switch (currentView()) {
    case View::Offline: setText(hint, g_model.net == NetState::Pending ? "" : "Side button: status"); break;
    case View::Idle:
      // no hub: Claude still answers, straight from the puck, but nothing at home can be controlled
      if (g_model.direct) setText(hint, "No hub \xC2\xB7 questions only");
      else setText(hint, g_model.lift ? "Tap or lift to talk" : "Tap to talk");
      break;
    case View::Listening: setText(hint, "Tap when done"); break;
    case View::Thinking: setText(hint, "Tap to interrupt"); break;
    case View::Answer: setText(hint, g_model.ai == AiState::Speaking ? "Tap to interrupt" : "Tap to reply"); break;
    case View::Error: setText(hint, "Tap to try again"); break;
  }
}

void layout(bool force) {
  View v = currentView();
  if (!force && v == shown) return;
  View prev = shown;
  shown = v;

  hide(ring1, v != View::Listening);
  hide(ring2, v != View::Listening);
  hide(clockLbl, v != View::Idle);
  hide(dateLbl, v != View::Idle);
  hide(topClock, !(v == View::Thinking || v == View::Error || v == View::Offline));
  hide(heard, !(v == View::Thinking || v == View::Answer));
  hide(reply.box, v != View::Answer);
  if (v != View::Answer) {
    hide(reply.track, true);
    hide(reply.thumb, true);
  }
  hide(status, !(v == View::Listening || v == View::Error || v == View::Offline));
  spark.color(v == View::Error || v == View::Offline ? COL_GREY2 : COL_CLAUDE);
  lv_obj_set_style_text_color(status, rgb(v == View::Error ? COL_RED : v == View::Listening ? COL_WHITE : COL_GREY1), 0);
  lv_obj_set_style_text_font(status, v == View::Listening ? &nunito_28 : &nunito_22, 0);
  // an error can be long (it says what went wrong): at most three lines, then "…"
  lv_label_set_long_mode(status, v == View::Listening ? LV_LABEL_LONG_WRAP : LV_LABEL_LONG_DOT);
  lv_obj_set_height(status, v == View::Listening ? LV_SIZE_CONTENT
                                                 : (lv_coord_t)(3 * lv_font_get_line_height(&nunito_22) + 8));
  at(status, CX, v == View::Listening ? 318 : 334);

  if (v == View::Thinking) {
    lv_obj_set_style_text_color(heard, rgb(COL_WHITE), 0);
    lv_obj_set_style_text_font(heard, &nunito_22, 0);
    lv_obj_set_width(heard, 330);
    lv_label_set_long_mode(heard, LV_LABEL_LONG_WRAP);
    lv_obj_set_height(heard, LV_SIZE_CONTENT);
    at(heard, CX, 292);
    at(actRow, CX, 354);
  } else if (v == View::Answer) {
    lv_obj_set_style_text_color(heard, rgb(COL_GREY1), 0);
    lv_obj_set_style_text_font(heard, &nunito_18, 0);
    lv_obj_set_width(heard, 300);
    lv_label_set_long_mode(heard, LV_LABEL_LONG_DOT);
    lv_obj_set_height(heard, lv_font_get_line_height(&nunito_18));
    at(heard, CX, 122);
    at(actRow, CX, 358);
  }

  refreshHint();
  if (v == View::Answer && prev != View::Answer) {
    reply.toTop();
    autoY = 0;
    fadeIn(reply.box, 220);
  }
  if (v == View::Idle && prev != View::Idle && prev != (View)0xFF) {
    fadeIn(clockLbl, 260);
    fadeIn(dateLbl, 320);
  }
  if (v == View::Listening && prev != View::Listening) fadeIn(status, 160);
  refreshText();
}

void showVolume() {
  volShownAt = lv_tick_get() | 1;
  hide(volArc, false);
  arcSpan(volArc, 210, 210 + 300.0f * g_model.spk / 100.0f);
  char buf[32];
  snprintf(buf, sizeof buf, "Voice volume %d%%", g_model.spk);
  setText(hint, buf);
}

void create(lv_obj_t* r) {
  root = r;
  halo = circle(root, 120, COL_CLAUDE, LV_OPA_TRANSP);
  ring1 = ringOutline(root, 100, 3, COL_CLAUDE);
  ring2 = ringOutline(root, 140, 2, COL_CLAUDE);
  lv_obj_set_style_border_opa(ring2, LV_OPA_40, 0);
  spark.create(root, COL_CLAUDE);

  clockLbl = label(root, &nunito_64, COL_WHITE, "");
  at(clockLbl, CX, 262);
  dateLbl = label(root, &nunito_22, COL_GREY1, "");
  at(dateLbl, CX, 314);
  topClock = label(root, &nunito_22, COL_GREY1, "");
  at(topClock, CX, 52);

  status = label(root, &nunito_22, COL_GREY1, "");
  lv_obj_set_width(status, 320);
  lv_label_set_long_mode(status, LV_LABEL_LONG_WRAP);
  lv_obj_set_style_text_align(status, LV_TEXT_ALIGN_CENTER, 0);
  at(status, CX, 330);

  heard = label(root, &nunito_22, COL_WHITE, "");
  lv_obj_set_width(heard, 330);
  lv_label_set_long_mode(heard, LV_LABEL_LONG_WRAP);
  lv_obj_set_style_text_line_space(heard, 4, 0);
  lv_obj_set_style_text_align(heard, LV_TEXT_ALIGN_CENTER, 0);
  at(heard, CX, 292);

  reply.create(root, 344, 200, CX, 244, &nunito_22, COL_WHITE, 5);
  hide(reply.box, true);

  actRow = plain(root);
  lv_obj_set_size(actRow, LV_SIZE_CONTENT, LV_SIZE_CONTENT);
  lv_obj_set_flex_flow(actRow, LV_FLEX_FLOW_ROW);
  lv_obj_set_flex_align(actRow, LV_FLEX_ALIGN_CENTER, LV_FLEX_ALIGN_CENTER, LV_FLEX_ALIGN_CENTER);
  lv_obj_set_style_pad_column(actRow, 8, 0);
  actIcon = label(actRow, &nunito_18, COL_CLAUDE, LV_SYMBOL_SETTINGS);
  actText = label(actRow, &nunito_18, COL_GREY1, "");
  lv_label_set_long_mode(actText, LV_LABEL_LONG_DOT);
  lv_obj_set_style_max_width(actText, 280, 0);
  hide(actRow, true);

  volArc = arc(root, 222, 6, COL_CLAUDE);
  arcTrack(volArc, COL_GREY4, 210, 510);
  hide(volArc, true);

  hint = hintLabel(root, "");
}

void enter() {
  lastFrame = 0;
  lastMinute = -1;
  lastDay = -1;
  shown = (View)0xFF;
  refreshClock(true);
  layout(true);
  View v = currentView();
  sx = CX;
  sy = v == View::Listening ? CY - 40 : v == View::Answer ? 80 : v == View::Idle ? 150 : CY - 40;
  ss = v == View::Listening ? 46 : v == View::Answer ? 22 : v == View::Idle ? 56 : 66;
}

void tapped() {
  View v = currentView();
  if (v == View::Offline) {
    shake(status);
    hal::beep(Tone::Error);
    return;
  }
  hal::beep(Tone::Tap);
  if (v == View::Listening) hub::talk("stop");
  else hub::talk("start", "tap");  // also interrupts thinking / speaking
  layout(false);
}

void input(const InputEvent& e) {
  View v = currentView();
  switch (e.type) {
    case Ev::Tap:
      tapped();
      break;
    case Ev::LongPress:
      if (v == View::Listening || v == View::Thinking || g_model.ai == AiState::Speaking) {
        hub::talk("cancel");
        hal::beep(Tone::Select);
      } else if (v == View::Answer) {
        // clear the answer away
        g_model.reply[0] = 0;
        g_model.heard[0] = 0;
        hal::beep(Tone::Select);
      }
      layout(false);
      break;
    case Ev::Turn: {
      if (v == View::Answer && g_model.ai != AiState::Speaking && reply.overflows()) {
        manualScroll = true;
        reply.scrollBy((int)lroundf(e.value * 3.0f), false);
        break;
      }
      static Detents det;
      int n = det.feed(e.value, 6.0f);
      if (n) {
        hub::speakerVolume(g_model.spk + n * 2);
        if ((g_model.spk % 10) == 0) hal::beep(Tone::Tick);
        showVolume();
      }
      break;
    }
    case Ev::Swipe:
      // a swipe up moves the text up, to what's below
      if (v == View::Answer && (e.dir == 'u' || e.dir == 'd')) {
        manualScroll = true;
        reply.scrollBy(e.dir == 'u' ? 84 : -84);
      }
      break;
    case Ev::TouchDown:
      if (v == View::Answer) manualScroll = true;
      break;
    default:
      break;
  }
}

void update(uint32_t dirty) {
  if (dirty & D_AI) {
    // speech starting: scroll along with it (until the person takes over)
    if (g_model.ai == AiState::Speaking && !speakStart) {
      speakStart = lv_tick_get() | 1;
      manualScroll = false;
    } else if (g_model.ai != AiState::Speaking) {
      speakStart = 0;
    }
  }
  if (dirty & (D_AI | D_LINK | D_CFG)) {
    layout(false);
    refreshText();
    refreshHint();
  }
  if (dirty & D_ACT) refreshActivity();
  if (dirty & D_TIME) refreshClock(true);
}

void tickScroll() {
  if (shown != View::Answer || !speakStart || manualScroll || !reply.overflows()) return;
  size_t total = strlen(g_model.reply);
  if (!total) return;
  // where the voice has got to, as a share of the text, kept a line behind so what's being
  // said stays in view
  float spoken = lv_tick_elaps(speakStart) / 1000.0f * SPOKEN_CHARS_PER_S;
  float frac = clampf((spoken - 40.0f) / (float)total, 0, 1);
  lv_obj_update_layout(reply.box);
  int range = (int)lv_obj_get_height(reply.text) - (int)lv_obj_get_height(reply.box);
  if (range <= 0) return;
  float target = frac * range;
  if (target > autoY + 1) {
    int step = (int)lroundf((target - autoY) * 0.08f + 0.5f);
    if (step < 1) step = 1;
    reply.scrollBy(step, false);
    autoY += step;
  }
}

void tick(uint32_t now) {
  // the last answer goes away after a while
  if (shown == View::Answer && currentView() != View::Answer) layout(false);
  refreshClock(false);

  if (volShownAt && lv_tick_elaps(volShownAt) > 1500) {
    volShownAt = 0;
    hide(volArc, true);
    layout(true);
  }
  tickScroll();

  View v = currentView();
  float t = now / 1000.0f;
  float dt = lastFrame ? (int32_t)(now - lastFrame) / 1000.0f : 0.016f;
  if (dt > 0.1f) dt = 0.1f;
  if (dt < 0) dt = 0;
  lastFrame = now;
  float tx = CX, ty, ts, breathe = 0, spin = 0;  // spin in degrees per second
  float hd = 0, ha = 0;                          // halo diameter and opacity (0..255)
  float mic = hal::micLevel();
  switch (v) {
    case View::Listening:
      ty = CY - 40;
      ts = 46;
      breathe = 0.6f;
      spin = 30;
      hd = 150 + mic * 120;
      ha = 30 + mic * 60;
      break;
    case View::Thinking:
      ty = CY - 60;
      ts = 60;
      breathe = 0.9f;
      spin = 200;
      hd = 150 + 14 * sinf(t * 4.0f);
      ha = 26;
      break;
    case View::Answer: {
      ty = 80;
      ts = 22;
      if (g_model.ai == AiState::Speaking) {
        float lvl = hal::speakerLevel();
        breathe = 0.4f + 1.6f * lvl;
        spin = 40;
        hd = 46 + lvl * 30;
        ha = 18 + lvl * 46;
      }
      break;
    }
    case View::Error: ty = CY - 50; ts = 56; break;
    case View::Offline: ty = CY - 40; ts = 64; spin = 6; break;
    default:
      // idle: resting above the time, breathing slowly
      ty = 150;
      ts = 56 + 3.0f * sinf(t * 1.6f);
      spin = 10;
      hd = 128 + 10 * sinf(t * 1.6f);
      ha = 16 + 8 * sinf(t * 1.6f);
      break;
  }
  srot = fmodf(srot + spin * dt, 360.0f);
  sx = ease(sx, tx, dt, 12.0f);
  sy = ease(sy, ty, dt, 12.0f);
  ss = ease(ss, ts, dt, 12.0f);
  haloD = ease(haloD, hd, dt, 10.0f);
  haloA = ease(haloA, ha, dt, 8.0f);
  spark.draw(sx, sy, ss, srot, breathe);

  int d = (int)lroundf(haloD);
  if (d < 4 || haloA < 3) {
    hide(halo, true);
  } else {
    hide(halo, false);
    lv_obj_set_size(halo, d, d);
    lv_obj_set_style_bg_opa(halo, (lv_opa_t)clampf(haloA, 0, 255), 0);
    at(halo, (int)sx, (int)sy);
  }

  if (v == View::Listening) {
    float lvl = mic;
    if (lvl <= 0.01f) lvl = 0.12f + 0.08f * sinf(t * 3.1f);  // no mic: a gentle pulse
    int d1 = (int)(110 + lvl * 100), d2 = (int)(d1 + 34 + 12 * sinf(t * 2.3f) + lvl * 44);
    lv_obj_set_size(ring1, d1, d1);
    lv_obj_set_size(ring2, d2, d2);
    at(ring1, (int)sx, (int)sy);
    at(ring2, (int)sx, (int)sy);
  }
}

}  // namespace

const Face FACE_ASSISTANT = {"assistant", "Assistant", create, enter, nullptr, input, update, tick};

}  // namespace ui
