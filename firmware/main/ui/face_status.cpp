// Status: is it on Wi-Fi, has it found the hub, battery, and what it's called. Hold to open the
// Wi-Fi setup network (to move it to another network, or fix a wrong password).
#include <stdio.h>
#include <string.h>
#include "ui_common.h"

namespace ui {
namespace {

lv_obj_t* root;
lv_obj_t* hdr;
lv_obj_t* nameLbl;
lv_obj_t* wifiIcon;
lv_obj_t* wifiLbl;
lv_obj_t* hubLbl;
lv_obj_t* batLbl;
lv_obj_t* verLbl;
lv_obj_t* hint;

const char* netText() {
  switch (g_model.net) {
    case NetState::NoWifi: return "No Wi-Fi saved";
    case NetState::Joining: return "Joining Wi-Fi\xE2\x80\xA6";
    case NetState::Portal: return "Setup network open";
    case NetState::Searching: return "Looking for the hub";
    case NetState::Connecting: return "Connecting to the hub";
    case NetState::Pending: return "Waiting for approval";
    case NetState::Online: return "Connected";
  }
  return "";
}

void render() {
  char buf[96];
  setText(nameLbl, g_model.puckName[0] ? g_model.puckName : "Puck");

  bool onWifi = g_model.ip[0] != 0;
  if (onWifi) {
    int bars = g_model.rssi >= -55 ? 4 : g_model.rssi >= -65 ? 3 : g_model.rssi >= -75 ? 2 : 1;
    snprintf(buf, sizeof buf, "%s  \xC2\xB7  %s", g_model.ssid, bars >= 3 ? "strong" : bars == 2 ? "fair" : "weak");
    setText(wifiLbl, buf);
  } else {
    setText(wifiLbl, g_model.net == NetState::Portal ? "Setup network open" : (g_model.linkError[0] ? g_model.linkError : "Not on Wi-Fi"));
  }
  lv_obj_set_style_text_color(wifiIcon, rgb(onWifi ? COL_WHITE : COL_GREY2), 0);

  if (g_model.linked) {
    snprintf(buf, sizeof buf, "Hub: %s (%s)", g_model.hub[0] ? g_model.hub : "connected",
             g_model.viaWifi ? (g_model.viaUsb ? "Wi-Fi + USB" : "Wi-Fi") : "USB");
  } else if (g_model.net == NetState::Pending && g_model.pendingCode[0]) {
    snprintf(buf, sizeof buf, "Approve on the hub: code %s", g_model.pendingCode);
  } else if (g_model.direct) {
    snprintf(buf, sizeof buf, "No hub \xC2\xB7 talking to Claude directly");
  } else {
    snprintf(buf, sizeof buf, "%s", netText());
  }
  setText(hubLbl, buf);
  lv_obj_set_style_text_color(hubLbl, rgb(g_model.linked ? COL_GREEN : g_model.direct ? COL_CLAUDE : COL_GREY1), 0);

  int p = g_model.bat >= 0 ? g_model.bat : hal::batteryPercent();
  if (p >= 0) snprintf(buf, sizeof buf, "Battery %d%%%s", p, hal::charging() ? " \xC2\xB7 charging" : "");
  else snprintf(buf, sizeof buf, "On USB power");
  setText(batLbl, buf);

  snprintf(buf, sizeof buf, "%s%s%s  \xC2\xB7  v%s", g_model.ip[0] ? g_model.ip : "", g_model.ip[0] ? "  \xC2\xB7  " : "",
           "Pucksley", PUCK_FW_VERSION);
  setText(verLbl, buf);
  setText(hint, hub::portalOpen() ? "Hold to close Wi-Fi setup" : "Hold for Wi-Fi setup");
}

void create(lv_obj_t* r) {
  root = r;
  hdr = header(root, "Status");
  nameLbl = label(root, &nunito_40, COL_WHITE, "Puck");
  lv_obj_set_width(nameLbl, 320);
  lv_label_set_long_mode(nameLbl, LV_LABEL_LONG_DOT);
  lv_obj_set_style_text_align(nameLbl, LV_TEXT_ALIGN_CENTER, 0);
  at(nameLbl, CX, 128);
  wifiIcon = label(root, &lv_font_montserrat_48, COL_WHITE, LV_SYMBOL_WIFI);
  at(wifiIcon, CX, 196);
  wifiLbl = label(root, &nunito_22, COL_WHITE, "");
  lv_obj_set_width(wifiLbl, 340);
  lv_label_set_long_mode(wifiLbl, LV_LABEL_LONG_DOT);
  lv_obj_set_style_text_align(wifiLbl, LV_TEXT_ALIGN_CENTER, 0);
  at(wifiLbl, CX, 246);
  hubLbl = label(root, &nunito_22, COL_GREY1, "");
  lv_obj_set_width(hubLbl, 360);
  lv_label_set_long_mode(hubLbl, LV_LABEL_LONG_DOT);
  lv_obj_set_style_text_align(hubLbl, LV_TEXT_ALIGN_CENTER, 0);
  at(hubLbl, CX, 282);
  batLbl = label(root, &nunito_22, COL_GREY1, "");
  at(batLbl, CX, 318);
  verLbl = label(root, &nunito_18, COL_GREY2, "");
  at(verLbl, CX, 352);
  hint = hintLabel(root, "Hold for Wi-Fi setup");
}

void enter() { render(); }

void input(const InputEvent& e) {
  if (e.type == Ev::LongPress) {
    hal::beep(Tone::Select);
    if (hub::portalOpen()) hub::closeSetupPortal();
    else hub::openSetupPortal();
  }
}

void update(uint32_t dirty) {
  if (dirty & (D_LINK | D_BAT | D_CFG)) render();
}

void tick(uint32_t now) {
  static uint32_t last = 0;
  if (now - last > 2000) {
    last = now;
    render();
  }
}

}  // namespace

const Face FACE_STATUS = {"status", "Status", create, enter, nullptr, input, update, tick};

}  // namespace ui
