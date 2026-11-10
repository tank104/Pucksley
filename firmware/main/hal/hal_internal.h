// What the pieces of the board layer share with each other (not part of the hal.h API).
#pragma once
#include "../platform.h"
#include <driver/gpio.h>
#include <driver/i2c_master.h>
#include "../events.h"
#include "../hal.h"

namespace hal {
namespace detail {

// ---- GPIO
inline void pinOutput(int pin, int level = 0) {
  gpio_reset_pin((gpio_num_t)pin);
  gpio_set_direction((gpio_num_t)pin, GPIO_MODE_OUTPUT);
  gpio_set_level((gpio_num_t)pin, level);
}
inline void pinInputPullup(int pin) {
  gpio_reset_pin((gpio_num_t)pin);
  gpio_set_direction((gpio_num_t)pin, GPIO_MODE_INPUT);
  gpio_set_pull_mode((gpio_num_t)pin, GPIO_PULLUP_ONLY);
}
inline void pinWrite(int pin, int level) { gpio_set_level((gpio_num_t)pin, level); }
inline int pinRead(int pin) { return gpio_get_level((gpio_num_t)pin); }

// ---- I2C: one bus for touch, IMU, PMU and the audio codec. The input task and the main loop
// both reach it, so every transaction runs under this lock.
bool i2cBegin();
i2c_master_bus_handle_t i2cBus();          // for the driver libraries (they add their own devices)
bool i2cWriteReg(uint8_t addr, uint8_t reg, uint8_t val);
bool i2cReadReg(uint8_t addr, uint8_t reg, uint8_t* val);
void i2cLock();
void i2cUnlock();
struct I2cGuard {
  I2cGuard() { i2cLock(); }
  ~I2cGuard() { i2cUnlock(); }
  I2cGuard(const I2cGuard&) = delete;
  I2cGuard& operator=(const I2cGuard&) = delete;
};

// ---- display (display.cpp) and its LVGL port (lvgl_port.cpp)
bool lcdBegin();                            // SPI bus, reset, init sequence, black frame, panel on
void lcdSetWindow(int x1, int y1, int x2, int y2);   // inclusive, in LVGL coordinates
// Queue pixels for the window just set (bytes already in panel order). `onDone` runs in the SPI
// ISR once the last byte is out. Returns false if the transfer couldn't be queued.
bool lcdQueuePixels(const void* data, size_t bytes, void (*onDone)(void*), void* arg);
void lcdDrain();                            // wait for queued transfers to finish
void lcdCommand(uint8_t cmd, const uint8_t* params, size_t len);
void lcdSleep(bool sleep);
void lcdBrightness(uint8_t value);          // 0..255
void lvglBegin();
void lvglSetPanelAwake(bool awake);         // while asleep, flushes are dropped (panel RAM is redrawn on wake)
void lvglRedrawNow();                       // invalidate everything and render it synchronously
// LVGL's two DMA draw buffers, for code that has the panel to itself while LVGL is paused
void* lvglDrawBuffer(int which, size_t* bytes);

// ---- input plumbing (input.cpp)
void inputBegin();                          // the queue; call before any post
bool post(Ev type, float value = 0, int16_t x = 0, int16_t y = 0, char dir = 0);  // false: queue full
enum class Src : uint8_t { Touch, Turn, Button, Motion };
// A gesture is starting. Notes the activity; when the display is off it posts a single Wake and
// returns false, and the caller must swallow the rest of that gesture.
bool activityBegin(Src src);
void activityContinue();                    // a gesture in progress: just keeps the screen awake
bool displayIsOff();
void inputDisplayChanged(bool on);         // hal::setDisplayOn tells the input side
float turnScale();                          // sensitivity * (flip ? -1 : 1)
void startInputTask();

// ---- the sources, polled from the input task
bool touchBegin();
void touchPoll(uint32_t nowMs);
void touchModeChanged(InputMode mode);
// A tap may be happening: a finger down for less than a tap's length, or lifted within afterMs
bool tapInProgress(uint32_t nowMs, uint32_t afterMs);
bool motionBegin();
void motionPoll(uint32_t nowUs);
void motionModeChanged(InputMode mode);
float motionHeading();
bool motionLifted();
void buttonsBegin();
void buttonsPoll(uint32_t nowMs);
void buttonEvent(bool longPress);           // from the BOOT pin or the PMU's power key
bool powerBegin();
void powerPoll(uint32_t nowMs);             // battery every ~2 s, power key IRQs every ~50 ms

// ---- diagnostics (hal::diag)
bool touchOk();
int touchDiag(char* buf, size_t len);          // reads and reads-with-a-finger since boot
bool powerOk();
bool audioOk();
int motionDiag(char* buf, size_t len);      // snprintf-style: chars it wanted to write
uint32_t wakesPosted();
int eventCounts(char* buf, size_t len);     // events posted since boot, by type

// ---- sound (audio.cpp, es8311.cpp, es7210.cpp)
bool es8311Begin(uint32_t sampleRate, uint32_t mclkHz);
void es8311SetVolume(uint8_t reg32);       // 0x00 = -95.5 dB, 0xBF = 0 dB
bool es7210Begin(int gainDb);              // the mic ADC; call with MCLK running
void es7210SetGain(int gainDb);
bool es7210Ok();
bool audioBegin();
int audioDiag(char* buf, size_t len);      // mic/speaker state for hal::diag

}  // namespace detail
}  // namespace hal
