// The talking face: video frames the hub streams while the assistant speaks (from a face service
// such as Openspline), shown full screen in step with the speaker, then back to the usual face.
//
// Frames arrive over the WebSocket as binary messages: "PVF1", u32 pts (little-endian; samples of
// the current utterance at which the frame shows), then a baseline JPEG. Any size that's a multiple
// of 8 and at least 464 x 464; the centre is shown, cut to the round screen.
#pragma once

#include <stddef.h>
#include <stdint.h>

namespace face_video {

void begin();  // at start-up: the frame queue (PSRAM) and the player task

// From the WebSocket task, for one binary message: start (total bytes), then every piece of it.
// frameStart() returns false when the frame can't be taken (too big): skip its pieces then.
bool frameStart(size_t total);
void frameData(const uint8_t* data, size_t len);

void end();   // the reply is over: show what's left, then give the screen back
void stop();  // stop now (interrupted)

// While held off (the power-off question is up) frames are turned away, so a reply's last frames
// still on their way can't take the screen back from it.
void holdOff(bool on);

// The UI loop asks every round. True while the face has the screen: don't render then.
// It also does the hand-over (LVGL's last flush out first, the faces redrawn afterwards).
bool screenTaken();

}  // namespace face_video
