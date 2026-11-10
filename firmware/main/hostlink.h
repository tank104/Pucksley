// Leftover from Puck for Windows: the board layer's trackpad mode sends mouse moves through these.
// Pucksley never turns trackpad mode on, so they do nothing; they're kept so src/hal stays the same
// code as Puck for Windows.
#pragma once

namespace hostlink {
inline void sendMove(char, float, float) {}
inline void sendScroll(float, float = 0) {}
inline void sendClick(char, int = 1) {}
inline void sendButton(char, bool) {}
}  // namespace hostlink
