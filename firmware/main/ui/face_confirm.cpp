// Confirm: the assistant wants a yes or no before something drastic ("Shut down the PC?").
// Nothing is chosen at first: turn clockwise for Yes, anticlockwise for No, then tap.
#include <string.h>
#include "ui_common.h"

namespace ui {
namespace {

lv_obj_t* root;
lv_obj_t* hdr;
Scroller question;
lv_obj_t* noArc;
lv_obj_t* yesArc;
lv_obj_t* noLbl;
lv_obj_t* yesLbl;
lv_obj_t* hint;

int choice = 0;  // -1 no, 0 none, 1 yes
float lean = 0;

void render() {
  lv_obj_set_style_arc_color(yesArc, rgb(choice > 0 ? COL_GREEN : COL_GREY3), LV_PART_INDICATOR);
  lv_obj_set_style_arc_color(noArc, rgb(choice < 0 ? COL_RED : COL_GREY3), LV_PART_INDICATOR);
  lv_obj_set_style_text_color(yesLbl, rgb(choice > 0 ? COL_GREEN : COL_GREY1), 0);
  lv_obj_set_style_text_color(noLbl, rgb(choice < 0 ? COL_RED : COL_GREY1), 0);
  lv_obj_set_style_text_font(yesLbl, choice > 0 ? &nunito_40 : &nunito_28, 0);
  lv_obj_set_style_text_font(noLbl, choice < 0 ? &nunito_40 : &nunito_28, 0);
  setText(hint, choice == 0 ? "Turn to choose" : "Tap to answer");
}

void create(lv_obj_t* r) {
  root = r;
  hdr = header(root, "Confirm", COL_CLAUDE);
  question.create(root, 320, 170, CX, CY - 28, &nunito_28, COL_WHITE, 4);
  yesArc = arc(root, 212, 14, COL_GREY3);
  arcSpan(yesArc, 50, 130);
  noArc = arc(root, 212, 14, COL_GREY3);
  arcSpan(noArc, 230, 310);
  yesLbl = label(root, &nunito_28, COL_GREY1, "Yes");
  at(yesLbl, CX + 120, CY + 104);
  noLbl = label(root, &nunito_28, COL_GREY1, "No");
  at(noLbl, CX - 120, CY + 104);
  hint = hintLabel(root, "Turn to choose");
}

void enter() {
  choice = 0;
  lean = 0;
  question.setText(g_model.confirmText);
  render();
}

void input(const InputEvent& e) {
  switch (e.type) {
    case Ev::Turn: {
      lean = clampf(lean + e.value, -40, 40);
      int c = lean > 12 ? 1 : lean < -12 ? -1 : choice;
      if (c != choice) {
        choice = c;
        hal::beep(Tone::Tick);
        render();
      }
      break;
    }
    case Ev::Tap:
      if (choice == 0) {
        shake(hint);
        hal::beep(Tone::Error);
        break;
      }
      hal::beep(choice > 0 ? Tone::Success : Tone::Select);
      hub::answer(g_model.confirmId, choice > 0);
      break;
    case Ev::Swipe:
      if (e.dir == 'u' || e.dir == 'd') question.scrollBy(e.dir == 'u' ? 60 : -60);
      break;
    default:
      break;
  }
}

void update(uint32_t dirty) {
  if (dirty & D_CONFIRM) {
    question.setText(g_model.confirmText);
    render();
  }
}

}  // namespace

const Face FACE_CONFIRM = {"confirm", "Confirm", create, enter, nullptr, input, update, nullptr};

}  // namespace ui
