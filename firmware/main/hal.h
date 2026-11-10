// The board: display (with the LVGL port), touch, motion, buttons, power and sound.
// Implemented in src/hal/. Everything here is called from the main loop unless noted.
#pragma once
#include <stdint.h>
#include "events.h"

enum class Tone : uint8_t {
  Tick,     // a detent: very short, quiet click
  Tap,      // a tap landed
  Select,   // a face picked / an option chosen
  Success,  // rising two notes
  Error,    // low buzz
  Chime,    // a timer is done / a reminder: a short gentle melody
  Listen,   // the puck starts listening: a soft rising pair
  ListenEnd,// it stopped listening: the same, falling
};

namespace hal {

// Power, I2C, display + LVGL (lv_init, draw buffers, flush, 2-px rounder), touch, IMU, buttons,
// audio, and the input task (pinned to core 0) that samples touch/IMU/buttons/PMU.
void begin();

// ---- display
void setBrightness(uint8_t percent);    // 0..100, applied immediately
void setDisplayOn(bool on);             // panel sleep / wake (LVGL keeps running)
bool displayOn();

// ---- input
bool pollEvent(InputEvent& out);        // non-blocking; pops the next event for the faces
void setInputMode(InputMode mode);      // faces call this when the trackpad face is entered/left
InputMode inputMode();
float heading();                        // total degrees turned since boot (clockwise +), drift-corrected
bool lifted();                          // true when the puck is held up (screen not facing up)
uint32_t lastActivityMs();              // millis() of the last touch, turn, button or motion
void setTurnSensitivity(float k);       // multiplies Turn values (from cfg.sens)
void setTurnFlip(bool flip);            // reverses Turn direction (from cfg.flip)

// ---- power (values refreshed by the input task every ~2 s; reads are cheap)
int batteryPercent();                   // 0..100, or -1 when no battery is attached
int batteryMv();
bool charging();
bool usbPowered();
void powerOff();                        // AXP2101 shutdown (does nothing useful on USB power)

// ---- sound (ES8311 + speaker amp); non-blocking, plays from a small audio task
void beep(Tone t);
void setSoundEnabled(bool on);

// ---- voice audio: 16 kHz mono 16-bit, mics (ES7210) in and speech out, full duplex on one I2S.
// The mic side has a single reader and the speaker side a single writer (the link's task).
void micStart();                                   // start filling the mic buffer
void micStop();
bool micActive();
size_t micRead(int16_t* dst, size_t maxSamples);   // non-blocking: what's waiting (0 if none)
float micLevel();                                  // 0..1, recent loudness (for the face's rings)
void setMicGain(int db);                           // 0..37 (the ES7210's PGA, 3 dB steps)

void speakerBegin(int id);                         // a new utterance (drops anything left of the last one)
void speakerWrite(const int16_t* pcm, size_t samples);  // queue audio (drops what doesn't fit)
void speakerEnd(int id);                           // nothing more for this one: play out what's queued
void speakerStop();                                // stop now and flush
bool speakerPlaying();                             // speech is playing or queued
uint32_t speakerPosition();                        // samples of the current utterance played so far
size_t speakerFree();                              // samples that fit in the speech buffer right now
int speakerTakeDone();                             // an utterance id that finished playing since last asked, or -1
float speakerLevel();                              // 0..1, loudness of what's playing (for the face)
void setSpeakerVolume(int percent);                // 0..100

// ---- misc
uint64_t chipId();                      // from the eFuse MAC
// A line for the log (UART, and kept until the hub can be sent it as a "log" message: the board
// layer's complaints at boot end up in the hub's log page). Safe from any task.
void logLine(const char* fmt, ...) __attribute__((format(printf, 1, 2)));
bool takeLog(char* out, size_t len);    // the oldest line not yet taken
// One line of live state for debugging on a real board (which chips answered, raw motion,
// display/brightness, activity): hostlink answers {"t":"diag"} with it.
void diag(char* buf, size_t len);
}  // namespace hal
