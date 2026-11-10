// Desktop tests for direct mode's plain logic (main/hub/direct_text.cpp): text for the screen and
// the voice, sentence and line splitting, end-of-speech detection, resampling and the encoders.
//
//   cd firmware/tools/test && ./run.sh
#include <math.h>
#include <stdio.h>
#include <string.h>
#include <string>
#include <vector>
#include "hub/direct_json.h"
#include "hub/direct_text.h"

using namespace hub::direct;

static int g_fail = 0, g_pass = 0;

#define CHECK(cond)                                                     \
  do {                                                                  \
    if (cond) {                                                         \
      g_pass++;                                                         \
    } else {                                                            \
      g_fail++;                                                         \
      printf("FAIL %s:%d  %s\n", __FILE__, __LINE__, #cond);            \
    }                                                                   \
  } while (0)

#define CHECK_EQ(a, b)                                                                  \
  do {                                                                                  \
    std::string _a = (a), _b = (b);                                                     \
    if (_a == _b) {                                                                     \
      g_pass++;                                                                         \
    } else {                                                                            \
      g_fail++;                                                                         \
      printf("FAIL %s:%d\n  got:  [%s]\n  want: [%s]\n", __FILE__, __LINE__, _a.c_str(), _b.c_str()); \
    }                                                                                   \
  } while (0)

static void screenText() {
  CHECK_EQ(cleanForScreen("plain text"), "plain text");
  // curly quotes, dashes, ellipsis kept, macrons folded
  CHECK_EQ(cleanForScreen("\xE2\x80\x9CKia ora\xE2\x80\x9D \xE2\x80\x94 T\xC4\x81maki Makaurau"), "\"Kia ora\" - Tamaki Makaurau");
  CHECK_EQ(cleanForScreen("it\xE2\x80\x99s 18\xC2\xB0" "C\xE2\x80\xA6"), "it's 18\xC2\xB0" "C\xE2\x80\xA6");
  CHECK_EQ(cleanForScreen("caf\xC3\xA9 na\xC3\xAFve Stra\xC3\x9F" "e \xC3\x86on"), "cafe naive Strasse AEon");
  // emoji dropped, double spaces collapsed
  CHECK_EQ(cleanForScreen("Sunny \xE2\x98\x80\xEF\xB8\x8F today \xF0\x9F\x98\x8E!"), "Sunny today !");
  // a decomposed accent (e + combining acute)
  CHECK_EQ(cleanForScreen("cafe\xCC\x81"), "cafe");
  CHECK_EQ(cleanForScreen("\xE2\x82\xAC" "5 and \xC2\xBD"), "EUR5 and 1/2");
  CHECK_EQ(cleanForScreen("line one\nline two"), "line one\nline two");
  // broken UTF-8 doesn't crash or leak bytes
  CHECK_EQ(cleanForScreen(std::string("ok\xC3", 3)), "ok");
}

static void speechText() {
  CHECK_EQ(cleanForSpeech("It's **really** warm, about *18 degrees*."), "It's really warm, about 18 degrees.");
  CHECK_EQ(cleanForSpeech("## Weather\n- Sunny\n- Light winds"), "Weather\nSunny\nLight winds");
  CHECK_EQ(cleanForSpeech("See [the forecast](https://metservice.com) for more."), "See the forecast for more.");
  CHECK_EQ(cleanForSpeech("Details at https://example.com/x?y=1 today."), "Details at today.");
  CHECK_EQ(cleanForSpeech("Use `snake_case` names."), "Use snake_case names.");
  CHECK_EQ(cleanForSpeech("| Day | Temp |\n|---|---|\n| Mon | 18 |"), ", Day , Temp ,\n, Mon , 18 ,");
  CHECK_EQ(cleanForSpeech("1. First\n2. Second"), "1. First\n2. Second");
}

static void sentences() {
  SentenceSplitter s;
  std::vector<std::string> out;
  s.add("It's 18 degrees", out);
  CHECK(out.empty());
  s.add(" and sunny. Tomorrow looks", out);
  CHECK(out.size() == 1);
  if (!out.empty()) CHECK_EQ(out[0], "It's 18 degrees and sunny.");
  s.add(" wetter, with rain arriving by the afternoon. Take a coat", out);
  CHECK(out.size() == 2);
  if (out.size() == 2) CHECK_EQ(out[1], "Tomorrow looks wetter, with rain arriving by the afternoon.");
  CHECK_EQ(s.flush(), "Take a coat");
  // numbers and abbreviations aren't sentence ends
  SentenceSplitter t;
  out.clear();
  t.add("Version 3.5 is out now, e.g. on the website, and Dr. Smith says it's good. Yes!", out);
  CHECK(out.size() == 1);
  if (!out.empty()) CHECK_EQ(out[0], "Version 3.5 is out now, e.g. on the website, and Dr. Smith says it's good.");
  CHECK_EQ(t.flush(), "Yes!");
}

static void lines() {
  LineSplitter ls(16);
  std::vector<std::string> got;
  auto on = [&](const std::string& l) { got.push_back(l); };
  auto feed = [&](const char* text) { ls.feed(text, strlen(text), on); };
  feed("data: {\"a\"");
  feed(":1}\r\n\nevent: x\n");
  CHECK(got.size() == 3);
  if (got.size() == 3) {
    CHECK_EQ(got[0], "data: {\"a\":1}");
    CHECK_EQ(got[1], "");
    CHECK_EQ(got[2], "event: x");
  }
  got.clear();
  feed("this line is far too long\nok\n");
  CHECK(got.size() == 1);
  if (got.size() == 1) CHECK_EQ(got[0], "ok");
}

static std::vector<int16_t> tone(size_t samples, float amp, float hz = 300, int rate = 16000) {
  std::vector<int16_t> v(samples);
  for (size_t i = 0; i < samples; i++) v[i] = (int16_t)(amp * sinf(2 * (float)M_PI * hz * i / rate));
  return v;
}

static void vad() {
  Vad v;
  v.reset(3.0f, 900);
  // 0.5 s of quiet room noise, 1 s of speech, then quiet
  auto quiet = tone(8000, 60, 50);
  auto speech = tone(16000, 6000, 300);
  v.process(quiet.data(), quiet.size());
  CHECK(!v.speechStarted());
  v.process(speech.data(), speech.size());
  CHECK(v.speechStarted());
  CHECK(!v.speechEnded());
  CHECK(v.speechStartSample() >= 8000 - 320 * 14 && v.speechStartSample() <= 8000);
  auto after = tone(16000, 60, 50);
  v.process(after.data(), 8000);  // 0.5 s of quiet: not yet
  CHECK(!v.speechEnded());
  v.process(after.data() + 8000, 8000);  // 1 s in all: ended
  CHECK(v.speechEnded());
  CHECK(v.lastSpeechSample() >= 24000 - 320 && v.lastSpeechSample() <= 24000 + 320);

  // a loud beep in the first 200 ms doesn't hide speech that follows
  Vad w;
  w.reset(3.0f, 900);
  auto beep = tone(1600, 9000, 1000);
  w.process(beep.data(), beep.size());
  w.process(quiet.data(), quiet.size());
  w.process(speech.data(), speech.size());
  CHECK(w.speechStarted());
}

static void resample() {
  // 24 kHz, 1 kHz sine → 16 kHz: same frequency, same loudness, 2/3 of the samples
  Resampler r;
  r.reset(24000, 16000);
  auto in = tone(24000, 10000, 1000, 24000);
  std::vector<int16_t> out;
  for (size_t i = 0; i < in.size(); i += 1000) r.process(in.data() + i, std::min<size_t>(1000, in.size() - i), out);
  CHECK(out.size() > 15900 && out.size() <= 16000);
  // count zero crossings in the middle: ~2 per cycle
  int crossings = 0;
  double peak = 0;
  for (size_t i = 2000; i < 14000; i++) {
    if ((out[i - 1] < 0) != (out[i] < 0)) crossings++;
    peak = fmax(peak, fabs((double)out[i]));
  }
  CHECK(crossings > 1480 && crossings < 1520);  // 12000 samples at 16 kHz = 0.75 s = 750 cycles
  CHECK(peak > 9000 && peak < 10500);
  // above the new Nyquist (10 kHz at 24 kHz in) is filtered out
  Resampler r2;
  r2.reset(24000, 16000);
  auto hi = tone(24000, 10000, 10000, 24000);
  std::vector<int16_t> out2;
  r2.process(hi.data(), hi.size(), out2);
  double hiPeak = 0;
  for (size_t i = 2000; i + 2000 < out2.size(); i++) hiPeak = fmax(hiPeak, fabs((double)out2[i]));
  CHECK(hiPeak < 1500);
  // the same rate passes through
  Resampler p;
  p.reset(16000, 16000);
  std::vector<int16_t> same;
  p.process(in.data(), 100, same);
  CHECK(same.size() == 100 && same[50] == in[50]);
}

static void encoders() {
  uint8_t h[44];
  wavHeader(h, 16000, 16000);
  CHECK(memcmp(h, "RIFF", 4) == 0 && memcmp(h + 8, "WAVEfmt ", 8) == 0 && memcmp(h + 36, "data", 4) == 0);
  CHECK(h[40] == 0x00 && h[41] == 0x7D && h[42] == 0 && h[43] == 0);  // 32000 bytes
  CHECK(h[24] == 0x80 && h[25] == 0x3E);                              // 16000 Hz

  CHECK_EQ(jsonString("say \"hi\"\n\\ok"), "\"say \\\"hi\\\"\\n\\\\ok\"");
  CHECK_EQ(xmlEscape("a < b & 'c'"), "a &lt; b &amp; &apos;c&apos;");
  CHECK_EQ(fillTemplate("{\"input\":%TEXT%,\"voice\":\"coral\"}", "It's \"18\"", false),
           "{\"input\":\"It's \\\"18\\\"\",\"voice\":\"coral\"}");
  CHECK_EQ(fillTemplate("<speak><voice>%TEXT%</voice></speak>", "Fish & chips", true),
           "<speak><voice>Fish &amp; chips</voice></speak>");

  CHECK(endsWithQuestion("Want me to set a timer?"));
  CHECK(endsWithQuestion("Shall I? "));
  CHECK(endsWithQuestion("(Is that right?)"));
  CHECK(!endsWithQuestion("Done."));
  CHECK(!endsWithQuestion(""));

  CHECK_EQ(utf8Clip("T\xC4\x81maki", 2), "T");          // never half of "ā"
  CHECK_EQ(utf8Clip("abcdef", 3, true), "def");
  CHECK_EQ(utf8Clip("\xC4\x81" "bc", 3, true), "bc");   // the tail doesn't start mid-character
}

void jsonPaths() {
  JsonDocument d;
  deserializeJson(d, R"({"text":" hi ","results":{"channels":[{"alternatives":[{"transcript":"Turn the lounge lights down.","words":[]}]}]},"DisplayText":"Azure"})");
  JsonVariantConst v = d.as<JsonVariantConst>();
  CHECK_EQ(std::string(jsonAt(v, "text") | ""), " hi ");
  CHECK_EQ(std::string(jsonAt(v, "DisplayText") | ""), "Azure");
  CHECK_EQ(std::string(jsonAt(v, "results.channels.0.alternatives.0.transcript") | ""), "Turn the lounge lights down.");
  CHECK(jsonAt(v, "results.channels.1.alternatives.0.transcript").isNull());   // no second channel
  CHECK(jsonAt(v, "results.channels.x").isNull());
  CHECK(jsonAt(v, "missing.deeper").isNull());
  CHECK(jsonAt(v, "text.0").isNull());                                          // a string isn't an array
  CHECK_EQ(std::string(jsonAt(v, "nothing") | "fallback"), "fallback");
}

int main() {
  screenText();
  speechText();
  sentences();
  lines();
  vad();
  resample();
  encoders();
  jsonPaths();
  printf("%d passed, %d failed\n", g_pass, g_fail);
  return g_fail ? 1 : 0;
}
