// The parts of direct mode that are plain logic, with nothing of ESP-IDF in them, so they can be
// tested on the desktop (tools/test): text cleaning, sentence and line splitting, end-of-speech
// detection, resampling and the little encoders the cloud requests need.
#pragma once
#include <stddef.h>
#include <stdint.h>
#include <string>
#include <vector>

namespace hub {
namespace direct {

// ---- text for the puck's fonts (printable ASCII plus • … ° – ‹ › × ·): curly quotes and dashes
// become plain ones, accents and macrons fold away (Tāmaki → Tamaki), anything else (emoji) goes.
std::string cleanForScreen(const std::string& s);

// ---- text for the voice: no markdown (stars, hashes, backticks, table bars), links read as their
// text, bare web addresses left out.
std::string cleanForSpeech(const std::string& s);

// ---- whole sentences out of streamed text, so speech can start after the first one (the first
// piece may be short; later ones are at least minChars long so the voice doesn't stutter)
class SentenceSplitter {
 public:
  explicit SentenceSplitter(size_t minChars = 40) : min_(minChars) {}
  void add(const std::string& delta, std::vector<std::string>& out);
  std::string flush();  // whatever is left ("" if nothing)
  void reset() {
    pending_.clear();
    first_ = true;
  }

 private:
  long findCut(size_t min) const;
  std::string pending_;
  size_t min_;
  bool first_ = true;
};

// ---- lines out of a byte stream (server-sent events). Lines longer than maxLine are dropped.
class LineSplitter {
 public:
  explicit LineSplitter(size_t maxLine) : max_(maxLine) {}
  template <class F>
  void feed(const char* p, size_t n, F&& onLine) {
    for (size_t i = 0; i < n; i++) {
      char c = p[i];
      if (c == '\n') {
        if (!overflow_) {
          if (!line_.empty() && line_.back() == '\r') line_.pop_back();
          onLine(line_);
        }
        line_.clear();
        overflow_ = false;
      } else if (line_.size() < max_) {
        line_.push_back(c);
      } else {
        overflow_ = true;
      }
    }
  }
  void reset() {
    line_.clear();
    overflow_ = false;
  }

 private:
  std::string line_;
  size_t max_;
  bool overflow_ = false;
};

// ---- someone starting and stopping talking, from the loudness of 20 ms frames against a running
// estimate of the room's noise (the same rules as the hub's VoiceActivityDetector)
class Vad {
 public:
  void reset(float sensitivity, int endSilenceMs);
  void process(const int16_t* samples, size_t n);
  bool speechStarted() const { return started_; }
  bool speechEnded() const { return ended_; }
  size_t speechStartSample() const { return startSample_; }  // with 200 ms kept before it
  size_t lastSpeechSample() const { return lastSample_; }

 private:
  void frame();
  static constexpr int kFrame = 320;
  int16_t buf_[kFrame];
  int fill_ = 0;
  float ratio_ = 3.0f;
  int endFrames_ = 45;
  float noise_ = 120, dc_ = 0;
  int loudRun_ = 0, quietRun_ = 0, frames_ = 0;
  bool started_ = false, ended_ = false;
  size_t startSample_ = 0, lastSample_ = 0;
};

// ---- any rate to another, mono 16-bit (the voices speak at 22.05 or 24 kHz; the puck plays
// 16 kHz): windowed sinc from a table of phases, low-passed when going down
class Resampler {
 public:
  void reset(int inRate, int outRate);
  bool passThrough() const { return in_ == out_; }
  void process(const int16_t* in, size_t n, std::vector<int16_t>& out);  // appends to out

 private:
  static constexpr int kHalf = 12;     // taps each side
  static constexpr int kPhases = 64;
  int in_ = 16000, out_ = 16000;
  double step_ = 1, pos_ = kHalf;
  std::vector<float> table_;           // kPhases x (2 * kHalf) weights
  std::vector<float> buf_;
};

// ---- encoders
void wavHeader(uint8_t out[44], uint32_t samples, uint32_t rate);
std::string jsonString(const std::string& s);  // a JSON string literal, quotes included
std::string xmlEscape(const std::string& s);
// Replaces every %TEXT% in a body template with the text, escaped the way the body needs.
std::string fillTemplate(const std::string& tpl, const std::string& text, bool xml);
bool endsWithQuestion(const std::string& s);
// At most max bytes, never cutting a UTF-8 character; from the end when tail is set.
std::string utf8Clip(const std::string& s, size_t max, bool tail = false);

}  // namespace direct
}  // namespace hub
