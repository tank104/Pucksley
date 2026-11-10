// Setup: getting the puck onto Wi-Fi and approved by the hub. Shows the setup network to join from
// a phone (with a QR code), or the code to compare when approving the puck in the hub's web page.
#include <stdio.h>
#include <string.h>
#include "ui_common.h"

namespace ui {
namespace {

lv_obj_t* root;
lv_obj_t* hdr;
lv_obj_t* line1;
lv_obj_t* big;
lv_obj_t* line2;
lv_obj_t* qr;
lv_obj_t* hint;
char qrText[64] = "";

enum class Mode : uint8_t { Portal, Pending, NoWifi, Other };
Mode shown = (Mode)0xFF;

Mode mode() {
  if (g_model.net == NetState::Pending && g_model.pendingCode[0]) return Mode::Pending;
  if (g_model.net == NetState::Portal || hub::portalOpen()) return Mode::Portal;
  if (g_model.net == NetState::NoWifi) return Mode::NoWifi;
  return Mode::Other;
}

void render(bool force) {
  Mode m = mode();
  if (!force && m == shown) {
    if (m == Mode::Pending) setText(big, g_model.pendingCode);
    return;
  }
  shown = m;
#if LV_USE_QRCODE
  hide(qr, m != Mode::Portal);
#endif
  switch (m) {
    case Mode::Portal: {
      const char* ssid = g_model.portalSsid[0] ? g_model.portalSsid : "Puck";
      setHeader(hdr, "Wi-Fi setup");
      setText(line1, "On your phone, join");
      lv_obj_set_style_text_font(big, &nunito_40, 0);
      setText(big, ssid);
      setText(line2, "then open 192.168.4.1");
      setText(hint, "Hold to cancel");
#if LV_USE_QRCODE
      snprintf(qrText, sizeof qrText, "WIFI:T:nopass;S:%s;;", ssid);
      lv_qrcode_update(qr, qrText, strlen(qrText));
      at(big, CX, 196);
      at(line1, CX, 146);
      at(line2, CX, 236);
#endif
      break;
    }
    case Mode::Pending:
      setHeader(hdr, "New puck");
      setText(line1, "Approve it in the hub's page");
      lv_obj_set_style_text_font(big, &nunito_digits_150, 0);
      setText(big, g_model.pendingCode);
      setText(line2, "and check the code matches");
      setText(hint, "");
      at(line1, CX, 128);
      at(big, CX, CY + 4);
      at(line2, CX, CY + 96);
      break;
    case Mode::NoWifi:
      setHeader(hdr, "Hello");
      setText(line1, "This puck isn't on Wi-Fi yet");
      lv_obj_set_style_text_font(big, &nunito_40, 0);
      setText(big, "Hold to set up");
      setText(line2, "or plug it into the hub's computer");
      setText(hint, "");
      at(line1, CX, 160);
      at(big, CX, CY);
      at(line2, CX, CY + 60);
      break;
    case Mode::Other:
      setHeader(hdr, "Connecting");
      setText(line1, g_model.ssid[0] ? g_model.ssid : "");
      lv_obj_set_style_text_font(big, &nunito_28, 0);
      setText(big, g_model.net == NetState::Joining ? "Joining Wi-Fi\xE2\x80\xA6" : "Looking for the hub\xE2\x80\xA6");
      setText(line2, g_model.linkError);
      setText(hint, "Hold for Wi-Fi setup");
      at(line1, CX, 160);
      at(big, CX, CY);
      at(line2, CX, CY + 60);
      break;
  }
}

void create(lv_obj_t* r) {
  root = r;
  hdr = header(root, "Setup");
  line1 = label(root, &nunito_22, COL_GREY1, "");
  at(line1, CX, 146);
  big = label(root, &nunito_40, COL_WHITE, "");
  at(big, CX, 196);
  line2 = label(root, &nunito_22, COL_GREY1, "");
  lv_obj_set_width(line2, 330);
  lv_label_set_long_mode(line2, LV_LABEL_LONG_DOT);
  lv_obj_set_style_text_align(line2, LV_TEXT_ALIGN_CENTER, 0);
  at(line2, CX, 236);
#if LV_USE_QRCODE
  qr = lv_qrcode_create(root, 116, lv_color_black(), lv_color_white());
  lv_obj_set_style_border_color(qr, lv_color_white(), 0);
  lv_obj_set_style_border_width(qr, 6, 0);
  at(qr, CX, 326);
#else
  qr = nullptr;
#endif
  hint = hintLabel(root, "", 412);
}

void enter() {
  shown = (Mode)0xFF;
  render(true);
}

void input(const InputEvent& e) {
  if (e.type != Ev::LongPress) return;
  hal::beep(Tone::Select);
  if (hub::portalOpen()) hub::closeSetupPortal();
  else hub::openSetupPortal();
}

void update(uint32_t dirty) {
  if (dirty & D_LINK) render(false);
}

void tick(uint32_t) { render(false); }

}  // namespace

const Face FACE_SETUP = {"setup", "Setup", create, enter, nullptr, input, update, tick};

}  // namespace ui
