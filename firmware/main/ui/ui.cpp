// The face manager: which face is up, the ones the hub brings up on its own (confirm, setup) and
// where they go back to, lift-to-talk, the picker and overlays, and when the screen dims and sleeps.
#include "../ui.h"
#include <stdio.h>
#include <string.h>
#include "ui_common.h"

namespace ui {

static const Face* const FACES[F_COUNT] = {
    &FACE_ASSISTANT, &FACE_HOME, &FACE_CONTROLS, &FACE_TIMER, &FACE_STATUS,
    &FACE_CONFIRM,   &FACE_SETUP, &FACE_SPIN,    &FACE_DICE,  &FACE_PONG,
};

static lv_obj_t* s_root[F_COUNT];
static FaceId s_cur = F_NONE;
static FaceId s_modalReturn = F_ASSISTANT;  // where confirm / setup go back to
static bool s_lowBatWarned = false;
static bool s_liftTalking = false;          // listening because the puck was picked up
static bool s_setupDismissed = false;       // the person left the setup face: don't push it again for this state
static NetState s_setupFor = NetState::Online;

// screen power
static uint32_t s_wakeMs = 0;
static bool s_dimmed = false;
static int s_brightApplied = -1;

static bool isModal(FaceId f) { return f == F_CONFIRM || f == F_SETUP; }

FaceId current() { return s_cur; }
const Face& face(FaceId f) { return *FACES[f]; }

void reportFace(const char* id) { hub::face(id); }

void restoreInputMode() { hal::setInputMode(InputMode::Ui); }

void show(FaceId f) {
  if (f >= F_COUNT || f == s_cur) return;
  FaceId prev = s_cur;
  if (prev != F_NONE) {
    if (FACES[prev]->leave) FACES[prev]->leave();
    lv_obj_add_flag(s_root[prev], LV_OBJ_FLAG_HIDDEN);
  }
  s_cur = f;
  lv_obj_t* r = s_root[f];
  lv_obj_clear_flag(r, LV_OBJ_FLAG_HIDDEN);
  reportFace(FACES[f]->id);
  if (FACES[f]->enter) FACES[f]->enter();
  // Arrive: a quick fade and a small rise, so a change of face reads as a change of mode
  if (prev != F_NONE) {
    fadeIn(r, 200);
    rise(r, 14, 260);
  }
}

static void showModal(FaceId f) {
  if (s_cur == f) return;
  if (!isModal(s_cur) && s_cur != F_NONE) s_modalReturn = s_cur;
  show(f);
}

static bool setupWanted() {
  NetState n = g_model.net;
  if (g_model.linked) return false;
  return n == NetState::Portal || n == NetState::Pending || n == NetState::NoWifi;
}

void back(FaceId from) {
  if (s_cur != from) return;
  FaceId to = isModal(s_modalReturn) ? F_ASSISTANT : s_modalReturn;
  if (to == from) to = F_ASSISTANT;
  if (to == F_CONFIRM && !g_model.confirmActive) to = F_ASSISTANT;
  show(to);
}

void startTalking(const char* via) {
  if (!model_can_talk()) return;
  wake();
  picker::close();
  if (s_cur != F_ASSISTANT && !isModal(s_cur)) show(F_ASSISTANT);
  hub::talk("start", via);
}

// ---- screen power

static void applyBrightness(int pct) {
  if (pct == s_brightApplied) return;
  s_brightApplied = pct;
  hal::setBrightness((uint8_t)pct);
}

void wake() {
  s_wakeMs = lv_tick_get();
  if (!hal::displayOn()) hal::setDisplayOn(true);
  s_dimmed = false;
  applyBrightness(g_model.bright);
}

static bool keepAwake() {
  if (s_cur == F_CONFIRM || s_cur == F_SETUP) return true;
  if (g_model.ai != AiState::Idle && g_model.ai != AiState::Error) return true;
  if (hal::speakerPlaying()) return true;
  return false;
}

static void powerPolicy(uint32_t now) {
  if (!hal::displayOn()) return;  // stays off until hal reports a Wake
  uint32_t last = hal::lastActivityMs();
  if ((int32_t)(s_wakeMs - last) > 0) last = s_wakeMs;
  // Signed: the input task can note activity after `now` was read
  int32_t sinceActivity = (int32_t)(now - last);
  uint32_t idle = sinceActivity > 0 ? (uint32_t)sinceActivity : 0;
  if (keepAwake()) idle = 0;
  if (g_model.offSec > 0 && idle > (uint32_t)g_model.offSec * 1000u) {
    hal::setDisplayOn(false);
    return;
  }
  if (g_model.dimSec > 0 && idle > (uint32_t)g_model.dimSec * 1000u) {
    int dim = g_model.bright / 4;
    if (dim < 4) dim = 4;
    s_dimmed = true;
    applyBrightness(dim);
  } else {
    s_dimmed = false;
    applyBrightness(g_model.bright);
  }
}

// ---- the connection coming and going

static void linkChanged() {
  if (g_model.net != s_setupFor) {
    s_setupFor = g_model.net;
    s_setupDismissed = false;
  }
  if (setupWanted() && !s_setupDismissed) {
    if (s_cur != F_SETUP && s_cur != F_CONFIRM) {
      wake();
      picker::close();
      showModal(F_SETUP);
    }
  } else if (s_cur == F_SETUP && (g_model.linked || !setupWanted())) {
    // Approved / found the hub: straight to the assistant
    if (g_model.linked) {
      hal::beep(Tone::Success);
      s_modalReturn = F_ASSISTANT;
    }
    back(F_SETUP);
  }
}

static FaceId byId(const char* id) {
  for (int i = 0; i < F_COUNT; i++)
    if (strcmp(FACES[i]->id, id) == 0) return (FaceId)i;
  return F_NONE;
}

}  // namespace ui

// ---------------------------------------------------------------- ui.h

namespace ui {

void begin() {
  lv_obj_t* scr = lv_scr_act();
  lv_obj_remove_style_all(scr);
  lv_obj_set_style_bg_color(scr, lv_color_black(), 0);
  lv_obj_set_style_bg_opa(scr, LV_OPA_COVER, 0);
  lv_obj_clear_flag(scr, LV_OBJ_FLAG_SCROLLABLE);

  for (int i = 0; i < F_COUNT; i++) {
    lv_obj_t* r = plain(scr);
    lv_obj_set_size(r, LCD_W, LCD_H);
    lv_obj_set_pos(r, 0, 0);
    lv_obj_add_flag(r, LV_OBJ_FLAG_HIDDEN);
    s_root[i] = r;
    FACES[i]->create(r);
  }
  overlay::create();

  s_wakeMs = lv_tick_get();
  applyBrightness(g_model.bright);
  show(F_ASSISTANT);
  linkChanged();
}

void handleInput(const InputEvent& e) {
  // Anything the hal hands us is a person: the screen comes back to full brightness
  if (e.type == Ev::Wake || !hal::displayOn()) {
    wake();
    if (e.type == Ev::Wake) return;
  } else if (s_dimmed) {
    wake();
  }

  if (overlay::powerOpen()) {
    overlay::powerInput(e);
    return;
  }
  // Holding the button is power off, and that comes before everything else: the picker, a
  // question from the hub, whatever face is up, a reply being heard or spoken
  if (e.type == Ev::ButtonLong) {
    s_liftTalking = false;
    overlay::powerConfirm();
    return;
  }
  if (picker::isOpen()) {
    picker::input(e);
    return;
  }

  // Lift to talk: picking the puck up starts listening, putting it down sends
  if (e.type == Ev::Lifted) {
    if (g_model.lift && model_can_talk() && !isModal(s_cur) &&
        (s_cur == F_ASSISTANT || s_cur == F_HOME || s_cur == F_TIMER) &&
        (g_model.ai == AiState::Idle || g_model.ai == AiState::Error || g_model.ai == AiState::Speaking)) {
      s_liftTalking = true;
      startTalking("lift");
    }
    return;
  }
  if (e.type == Ev::Laid) {
    if (s_liftTalking && g_model.ai == AiState::Listening) hub::talk("stop");
    s_liftTalking = false;
    return;
  }

  // Picking the puck up to reach the side button isn't someone starting to talk: drop that listen
  if (e.type == Ev::Button && s_liftTalking && g_model.ai == AiState::Listening) {
    s_liftTalking = false;
    hub::talk("cancel");
  }
  // The side button: the picker everywhere (confirm ignores it so it can't be taken for an answer)
  if (e.type == Ev::Button && s_cur != F_CONFIRM) {
    if (s_cur == F_SETUP) s_setupDismissed = true;
    picker::open();
    return;
  }
  if (s_cur != F_NONE && FACES[s_cur]->input) FACES[s_cur]->input(e);
}

void update(uint32_t dirty) {
  if (dirty & D_CFG) {
    if (!s_dimmed && hal::displayOn()) applyBrightness(g_model.bright);
  }
  if (dirty & D_AUDIO) vol::syncFromModel();
  if (dirty & D_LINK) linkChanged();

  if (dirty & D_CONFIRM) {
    if (g_model.confirmActive && s_cur != F_CONFIRM) {
      wake();
      picker::close();
      showModal(F_CONFIRM);
    } else if (!g_model.confirmActive) {
      back(F_CONFIRM);
    }
  }
  if ((dirty & D_LISTEN) && g_model.listenRequest) {
    g_model.listenRequest = false;
    if (model_can_talk() && s_cur != F_CONFIRM && !overlay::powerOpen() && g_model.ai != AiState::Listening)
      startTalking("hub");
  }
  if (dirty & D_AI) {
    // the assistant is talking to someone: make sure they can see it
    if ((g_model.ai == AiState::Thinking || g_model.ai == AiState::Speaking) && s_cur != F_ASSISTANT && !isModal(s_cur) &&
        !picker::isOpen()) {
      wake();
      show(F_ASSISTANT);
    }
    if (g_model.ai != AiState::Listening) s_liftTalking = false;
  }
  if (dirty & D_BAT) {
    // Once, as it crosses 10 % on battery; again only after it has been charged past 15 %
    if (g_model.bat >= 0 && g_model.bat <= 10 && !g_model.chg && !s_lowBatWarned) {
      s_lowBatWarned = true;
      char msg[40];
      snprintf(msg, sizeof msg, "Battery low \xC2\xB7 %d%%", g_model.bat);
      overlay::banner(msg, false);
    } else if (g_model.chg || g_model.bat > 15) {
      s_lowBatWarned = false;
    }
  }
  if (dirty & D_NOTIFY) {
    if (g_model.notifyText[0]) {
      wake();
      overlay::banner(g_model.notifyText, g_model.notifyKind != NotifyKind::Info);
    }
  }
  if ((dirty & D_FACE_REQ) && g_model.faceRequest[0]) {
    FaceId f = byId(g_model.faceRequest);
    g_model.faceRequest[0] = 0;
    if (f != F_NONE && !isModal(s_cur) && !isModal(f)) {
      wake();
      picker::close();
      show(f);
    }
  }

  if (s_cur != F_NONE && FACES[s_cur]->update) FACES[s_cur]->update(dirty);
}

void tick(uint32_t now) {
  if (s_cur != F_NONE && FACES[s_cur]->tick) FACES[s_cur]->tick(now);
  picker::tick(now);
  overlay::tick(now);
  powerPolicy(now);
}

const char* currentFace() { return s_cur == F_NONE ? "assistant" : FACES[s_cur]->id; }

// The picker picked
void pickerChose(FaceId f) {
  if (f != s_cur) show(f);
  restoreInputMode();
}

}  // namespace ui
