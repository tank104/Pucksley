// Pucksley: a voice assistant you hold in your hand. The board (hal/), the connection to the Puck
// AI hub over Wi-Fi or USB (hub/), and the faces (ui/). ESP-IDF; the UI runs in a task of its own
// on core 1 (core 0 has the audio, input and network tasks).
#include "platform.h"
#include <lvgl.h>
#include "hal.h"
#include "hub.h"
#include "model.h"
#include "ui.h"
#include "face_video.h"

namespace {

void uiTask(void*) {
  hal::begin();
  face_video::begin();
  hub::begin();
  ui::begin();

  uint32_t lastRender = 0;
  for (;;) {
    hub::poll();
    if (face_video::screenTaken()) {
      // The talking face has the panel: no rendering, but a tap or the button still stops the reply
      InputEvent e;
      while (hal::pollEvent(e))
        if (e.type == Ev::Tap || e.type == Ev::Button || e.type == Ev::LongPress || e.type == Ev::DoubleTap) {
          face_video::stop();
          hub::talk("cancel");
        }
      delay(5);
      continue;
    }

    InputEvent e;
    while (hal::pollEvent(e)) ui::handleInput(e);

    uint32_t dirty = model_take_dirty();
    if (dirty) ui::update(dirty);

    uint32_t now = millis();
    ui::tick(now);

    // While the panel sleeps its frames are thrown away, so render (and run LVGL's timers) a few
    // times a second instead of 60: animations keep their place without burning the battery.
    if (!hal::displayOn() && (int32_t)(now - lastRender) < 100) {
      delay(5);
      continue;
    }
    lastRender = now;
    uint32_t wait = lv_timer_handler();
    delay(wait < 5 ? (wait ? wait : 1) : 5);
  }
}

}  // namespace

extern "C" void app_main(void) {
  // A roomy stack: LVGL's layout and the faces' builders recurse a little
  xTaskCreatePinnedToCore(uiTask, "ui", 16384, nullptr, 4, nullptr, 1);
}
