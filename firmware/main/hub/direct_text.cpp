// Direct mode's plain logic (see direct_text.h). No ESP-IDF here: tools/test builds it on the desktop.
#include "direct_text.h"
#include <ctype.h>
#include <math.h>
#include <stdio.h>
#include <string.h>
#include <algorithm>

namespace hub {
namespace direct {

namespace {

// U+00C0..U+00FF and U+0100..U+017F folded to one ASCII letter ('*': handled on its own)
const char kLatin1[] = "AAAAAA*CEEEEIIII" "DNOOOOO*OUUUUY**" "aaaaaa*ceeeeiiii" "dnooooo/ouuuuy*y";
const char kExtA[] = "AaAaAaCcCcCcCcDd" "DdEeEeEeEeEeGgGg" "GgGgHhHhIiIiIiIi" "Ii**JjKkkLlLlLlL"
                     "lLlNnNnNnnNnOoOo" "Oo**RrRrRrSsSsSs" "SsTtTtTtUuUuUuUu" "UuUuWwYyYZzZzZzs";

void putUtf8(std::string& o, uint32_t cp) {
  if (cp < 0x80) {
    o += (char)cp;
  } else if (cp < 0x800) {
    o += (char)(0xC0 | (cp >> 6));
    o += (char)(0x80 | (cp & 0x3F));
  } else if (cp < 0x10000) {
    o += (char)(0xE0 | (cp >> 12));
    o += (char)(0x80 | ((cp >> 6) & 0x3F));
    o += (char)(0x80 | (cp & 0x3F));
  } else {
    o += (char)(0xF0 | (cp >> 18));
    o += (char)(0x80 | ((cp >> 12) & 0x3F));
    o += (char)(0x80 | ((cp >> 6) & 0x3F));
    o += (char)(0x80 | (cp & 0x3F));
  }
}

// One UTF-8 character at s[i] (i moves past it). Broken bytes come out as U+FFFD.
uint32_t nextUtf8(const std::string& s, size_t& i) {
  uint8_t b = (uint8_t)s[i++];
  if (b < 0x80) return b;
  int extra = (b & 0xE0) == 0xC0 ? 1 : (b & 0xF0) == 0xE0 ? 2 : (b & 0xF8) == 0xF0 ? 3 : -1;
  if (extra < 0) return 0xFFFD;
  uint32_t cp = b & (0x3F >> extra);
  for (int k = 0; k < extra; k++) {
    if (i >= s.size() || ((uint8_t)s[i] & 0xC0) != 0x80) return 0xFFFD;
    cp = (cp << 6) | ((uint8_t)s[i++] & 0x3F);
  }
  return cp;
}

void collapseSpaces(std::string& s) {
  std::string o;
  o.reserve(s.size());
  for (size_t i = 0; i < s.size(); i++) {
    if (s[i] == ' ' && (o.empty() || o.back() == ' ' || o.back() == '\n')) continue;
    if (s[i] == '\n' && !o.empty() && o.back() == ' ') o.pop_back();
    o += s[i];
  }
  while (!o.empty() && (o.back() == ' ' || o.back() == '\n')) o.pop_back();
  s.swap(o);
}

std::string trim(const std::string& s) {
  size_t b = 0, e = s.size();
  while (b < e && isspace((unsigned char)s[b])) b++;
  while (e > b && isspace((unsigned char)s[e - 1])) e--;
  return s.substr(b, e - b);
}

bool startsWith(const std::string& s, size_t at, const char* p) { return s.compare(at, strlen(p), p) == 0; }

// One line of markdown, as it should be said
std::string speakLine(const std::string& line) {
  size_t i = 0;
  while (i < line.size() && (line[i] == ' ' || line[i] == '\t')) i++;
  // headings, quotes and bullets: the marker goes (numbered lists keep their numbers)
  while (i < line.size() && line[i] == '#') i++;
  if (i < line.size() && line[i] == '>') i++;
  if (i + 1 < line.size() && (line[i] == '-' || line[i] == '*' || line[i] == '+') && line[i + 1] == ' ') i += 2;
  if (startsWith(line, i, "\xE2\x80\xA2")) i += 3;  // •

  std::string o;
  o.reserve(line.size());
  while (i < line.size()) {
    char c = line[i];
    // [text](url) → text
    if (c == '[') {
      size_t close = line.find(']', i + 1);
      if (close != std::string::npos && close + 1 < line.size() && line[close + 1] == '(') {
        size_t paren = line.find(')', close + 2);
        if (paren != std::string::npos) {
          o += line.substr(i + 1, close - i - 1);
          i = paren + 1;
          continue;
        }
      }
    }
    // bare web addresses: left out
    if (startsWith(line, i, "http://") || startsWith(line, i, "https://") || startsWith(line, i, "www.")) {
      while (i < line.size() && !isspace((unsigned char)line[i])) i++;
      continue;
    }
    if (c == '*' || c == '`') {
      i++;
      continue;
    }
    // _emphasis_ (but not snake_case)
    if (c == '_' && (i == 0 || !isalnum((unsigned char)line[i - 1]) || i + 1 >= line.size() || !isalnum((unsigned char)line[i + 1]))) {
      i++;
      continue;
    }
    if (c == '|') {
      o += ", ";
      i++;
      continue;
    }
    o += c;
    i++;
  }
  return trim(o);
}

}  // namespace

// ---------------------------------------------------------------- text

std::string cleanForScreen(const std::string& s) {
  std::string o;
  o.reserve(s.size());
  for (size_t i = 0; i < s.size();) {
    uint32_t c = nextUtf8(s, i);
    if (c == '\n') {
      o += '\n';
      continue;
    }
    if (c == '\t') {
      o += ' ';
      continue;
    }
    if (c >= 0x20 && c <= 0x7E) {
      o += (char)c;
      continue;
    }
    switch (c) {
      case 0x2022: case 0x2026: case 0x00B0: case 0x2013: case 0x2039: case 0x203A: case 0x00D7: case 0x00B7:
        putUtf8(o, c);  // the fonts have these
        continue;
      case 0x2018: case 0x2019: case 0x201A: case 0x2032: case 0x00B4: o += '\''; continue;
      case 0x201C: case 0x201D: case 0x201E: case 0x2033: o += '"'; continue;
      case 0x2010: case 0x2011: case 0x2014: case 0x2015: case 0x2212: o += '-'; continue;
      case 0x00A0: case 0x2002: case 0x2003: case 0x2009: case 0x200A: case 0x202F: o += ' '; continue;
      case 0x00AB: putUtf8(o, 0x2039); continue;
      case 0x00BB: putUtf8(o, 0x203A); continue;
      case 0x20AC: o += "EUR"; continue;
      case 0x00A3: o += "GBP"; continue;
      case 0x2122: o += "TM"; continue;
      case 0x00BD: o += "1/2"; continue;
      case 0x00BC: o += "1/4"; continue;
      case 0x00BE: o += "3/4"; continue;
      case 0x00C6: o += "AE"; continue;
      case 0x00E6: o += "ae"; continue;
      case 0x00DE: o += "Th"; continue;
      case 0x00FE: o += "th"; continue;
      case 0x00DF: o += "ss"; continue;
      case 0x0132: o += "IJ"; continue;
      case 0x0133: o += "ij"; continue;
      case 0x0152: o += "OE"; continue;
      case 0x0153: o += "oe"; continue;
      default: break;
    }
    if (c >= 0xC0 && c <= 0xFF) {
      char f = kLatin1[c - 0xC0];
      if (f != '*') o += f;
    } else if (c >= 0x100 && c <= 0x17F) {
      char f = kExtA[c - 0x100];
      if (f != '*') o += f;
    }
    // combining accents, emoji, other scripts: dropped
  }
  collapseSpaces(o);
  return o;
}

std::string cleanForSpeech(const std::string& s) {
  std::string o;
  size_t start = 0;
  while (start <= s.size()) {
    size_t nl = s.find('\n', start);
    if (nl == std::string::npos) nl = s.size();
    std::string line = speakLine(s.substr(start, nl - start));
    // a table's divider row (|---|---|) says nothing
    bool rule = !line.empty() && line.find_first_not_of("-:, ") == std::string::npos;
    if (!line.empty() && !rule) {
      if (!o.empty()) o += '\n';
      o += line;
    }
    start = nl + 1;
  }
  collapseSpaces(o);
  return o;
}

// ---------------------------------------------------------------- sentences

void SentenceSplitter::add(const std::string& delta, std::vector<std::string>& out) {
  pending_ += delta;
  for (;;) {
    long cut = findCut(first_ ? 12 : min_);
    if (cut < 0) break;
    std::string piece = trim(pending_.substr(0, (size_t)cut));
    pending_.erase(0, (size_t)cut);
    if (!piece.empty()) {
      out.push_back(piece);
      first_ = false;
    }
  }
}

std::string SentenceSplitter::flush() {
  std::string rest = trim(pending_);
  pending_.clear();
  return rest;
}

long SentenceSplitter::findCut(size_t min) const {
  const std::string& s = pending_;
  for (size_t i = std::max<size_t>(min, 1); i < s.size(); i++) {
    char c = s[i - 1];
    bool end = c == '.' || c == '!' || c == '?' || c == ';' || c == ':' || c == '\n';
    if (!end) continue;
    // the next character must be a space ("3.5" and "e.g.x" aren't the end of anything)
    if (c != '\n' && !isspace((unsigned char)s[i])) continue;
    if (c == '.') {
      size_t e = i - 1;
      size_t b = e == 0 ? std::string::npos : s.rfind(' ', e - 1);
      std::string word = s.substr(b == std::string::npos ? 0 : b + 1, e - (b == std::string::npos ? 0 : b + 1));
      static const char* const kAbbrev[] = {"Mr", "Mrs", "Ms", "Dr", "St", "vs", "e.g", "i.e", "approx"};
      bool abbrev = false;
      for (const char* a : kAbbrev)
        if (word == a) abbrev = true;
      if (abbrev) continue;
    }
    return (long)i;
  }
  // a very long run without an ending: cut at a comma
  if (s.size() > 220) {
    size_t comma = s.rfind(", ", 200);
    if (comma != std::string::npos && comma > min) return (long)comma + 1;
  }
  return -1;
}

// ---------------------------------------------------------------- voice activity

void Vad::reset(float sensitivity, int endSilenceMs) {
  ratio_ = std::min(6.0f, std::max(1.5f, sensitivity));
  endFrames_ = std::max(10, endSilenceMs / 20);
  fill_ = 0;
  noise_ = 120;
  dc_ = 0;
  loudRun_ = quietRun_ = frames_ = 0;
  started_ = ended_ = false;
  startSample_ = lastSample_ = 0;
}

void Vad::process(const int16_t* samples, size_t n) {
  for (size_t i = 0; i < n; i++) {
    buf_[fill_++] = samples[i];
    if (fill_ == kFrame) {
      frame();
      fill_ = 0;
    }
  }
}

void Vad::frame() {
  float sum = 0;
  for (int i = 0; i < kFrame; i++) {
    dc_ += (buf_[i] - dc_) * 0.005f;
    float v = buf_[i] - dc_;
    sum += v * v;
  }
  float rms = sqrtf(sum / kFrame);
  frames_++;
  // The first 200 ms set the noise floor from their quietest frame (the "listening" beep can't
  // inflate it); after that it adapts only while it's quiet: slowly up, quicker down.
  if (frames_ <= 10) noise_ = frames_ == 1 ? rms : std::min(noise_, rms);
  bool loud = frames_ > 10 && rms > std::max(noise_ * ratio_, 250.0f);
  if (!loud && frames_ > 10)
    noise_ = rms < noise_ ? noise_ * 0.9f + rms * 0.1f : noise_ * 0.98f + std::min(rms, noise_ * 2) * 0.02f;
  if (loud) {
    loudRun_++;
    quietRun_ = 0;
    if (!started_ && loudRun_ >= 3) {
      started_ = true;
      int from = frames_ - 3 - 10;  // keep 200 ms before
      startSample_ = (size_t)std::max(0, from) * kFrame;
    }
    if (started_) lastSample_ = (size_t)frames_ * kFrame;
  } else {
    loudRun_ = 0;
    if (started_ && ++quietRun_ >= endFrames_) ended_ = true;
  }
}

// ---------------------------------------------------------------- resampling

void Resampler::reset(int inRate, int outRate) {
  in_ = inRate > 0 ? inRate : 16000;
  out_ = outRate > 0 ? outRate : 16000;
  step_ = (double)in_ / out_;
  buf_.assign(kHalf, 0.0f);
  pos_ = kHalf;
  table_.clear();
  if (passThrough()) return;
  table_.assign((size_t)kPhases * 2 * kHalf, 0.0f);
  double cutoff = std::min(1.0, (double)out_ / in_) * 0.95;
  for (int p = 0; p < kPhases; p++) {
    double frac = (double)p / kPhases;
    double norm = 0;
    float* w = &table_[(size_t)p * 2 * kHalf];
    for (int j = 0; j < 2 * kHalf; j++) {
      double x = frac + kHalf - 1 - j;  // distance from the output position to tap j
      double a = fabs(x);
      double v = 0;
      if (a < kHalf) {
        double sinc = a < 1e-9 ? 1.0 : sin(M_PI * cutoff * x) / (M_PI * cutoff * x);
        double window = 0.5 * (1 + cos(M_PI * x / kHalf));
        v = cutoff * sinc * window;
      }
      w[j] = (float)v;
      norm += v;
    }
    if (norm != 0)
      for (int j = 0; j < 2 * kHalf; j++) w[j] = (float)(w[j] / norm);
  }
}

void Resampler::process(const int16_t* in, size_t n, std::vector<int16_t>& out) {
  if (passThrough()) {
    out.insert(out.end(), in, in + n);
    return;
  }
  buf_.reserve(buf_.size() + n);
  for (size_t i = 0; i < n; i++) buf_.push_back(in[i]);
  while (pos_ + kHalf + 1 < (double)buf_.size()) {
    int center = (int)floor(pos_);
    int p = (int)lround((pos_ - center) * kPhases);
    if (p >= kPhases) {
      p = 0;
      center++;
    }
    const float* w = &table_[(size_t)p * 2 * kHalf];
    int k0 = center - kHalf + 1;
    float sum = 0;
    for (int j = 0; j < 2 * kHalf; j++) {
      int k = k0 + j;
      if (k >= 0 && k < (int)buf_.size()) sum += buf_[(size_t)k] * w[j];
    }
    long v = lroundf(sum);
    out.push_back((int16_t)(v > 32767 ? 32767 : v < -32768 ? -32768 : v));
    pos_ += step_;
  }
  // drop what's been used, keeping enough history for the filter
  int drop = (int)floor(pos_) - kHalf;
  if (drop > 0) {
    buf_.erase(buf_.begin(), buf_.begin() + drop);
    pos_ -= drop;
  }
}

// ---------------------------------------------------------------- encoders

void wavHeader(uint8_t out[44], uint32_t samples, uint32_t rate) {
  auto u32 = [&](int at, uint32_t v) {
    out[at] = (uint8_t)v;
    out[at + 1] = (uint8_t)(v >> 8);
    out[at + 2] = (uint8_t)(v >> 16);
    out[at + 3] = (uint8_t)(v >> 24);
  };
  auto u16 = [&](int at, uint16_t v) {
    out[at] = (uint8_t)v;
    out[at + 1] = (uint8_t)(v >> 8);
  };
  uint32_t data = samples * 2;
  memcpy(out, "RIFF", 4);
  u32(4, 36 + data);
  memcpy(out + 8, "WAVEfmt ", 8);
  u32(16, 16);
  u16(20, 1);         // PCM
  u16(22, 1);         // mono
  u32(24, rate);
  u32(28, rate * 2);  // bytes a second
  u16(32, 2);         // block align
  u16(34, 16);        // bits
  memcpy(out + 36, "data", 4);
  u32(40, data);
}

std::string jsonString(const std::string& s) {
  std::string o;
  o.reserve(s.size() + 2);
  o += '"';
  for (unsigned char c : s) {
    switch (c) {
      case '"': o += "\\\""; break;
      case '\\': o += "\\\\"; break;
      case '\n': o += "\\n"; break;
      case '\r': o += "\\r"; break;
      case '\t': o += "\\t"; break;
      default:
        if (c < 0x20) {
          char u[8];
          snprintf(u, sizeof u, "\\u%04x", c);
          o += u;
        } else {
          o += (char)c;
        }
    }
  }
  o += '"';
  return o;
}

std::string xmlEscape(const std::string& s) {
  std::string o;
  o.reserve(s.size());
  for (char c : s) {
    switch (c) {
      case '&': o += "&amp;"; break;
      case '<': o += "&lt;"; break;
      case '>': o += "&gt;"; break;
      case '"': o += "&quot;"; break;
      case '\'': o += "&apos;"; break;
      default: o += c;
    }
  }
  return o;
}

std::string fillTemplate(const std::string& tpl, const std::string& text, bool xml) {
  const std::string value = xml ? xmlEscape(text) : jsonString(text);
  static const char kMark[] = "%TEXT%";
  std::string o;
  size_t at = 0;
  for (;;) {
    size_t m = tpl.find(kMark, at);
    if (m == std::string::npos) break;
    o.append(tpl, at, m - at);
    o += value;
    at = m + sizeof kMark - 1;
  }
  o.append(tpl, at, std::string::npos);
  return o;
}

bool endsWithQuestion(const std::string& s) {
  size_t e = s.size();
  while (e > 0 && isspace((unsigned char)s[e - 1])) e--;
  if (e == 0) return false;
  if (s[e - 1] == '?') return true;
  return e >= 2 && s[e - 2] == '?' && (s[e - 1] == '"' || s[e - 1] == ')');
}

std::string utf8Clip(const std::string& s, size_t max, bool tail) {
  if (s.size() <= max) return s;
  if (!tail) {
    size_t n = max;
    while (n > 0 && ((uint8_t)s[n] & 0xC0) == 0x80) n--;
    return s.substr(0, n);
  }
  size_t from = s.size() - max;
  while (from < s.size() && ((uint8_t)s[from] & 0xC0) == 0x80) from++;
  return s.substr(from);
}

}  // namespace direct
}  // namespace hub
