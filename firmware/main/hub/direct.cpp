// Direct mode: no hub on the network, but the puck is on Wi-Fi and the hub once gave it keys for
// Claude and for speech services. The puck then does the hub's job itself: it listens (with its
// own end-of-speech detection), sends the recording to a speech-to-text service, asks Claude
// (streaming, with web search) and speaks the answer through a text-to-speech service as the
// sentences arrive. Questions and conversation only: home control, music and the PC need the hub.
//
// Two tasks on core 0: "direct" (listen → transcribe → ask Claude) and "voice" (fetch the speech
// for each sentence → the speaker). They report to the main loop with the hub's own protocol
// messages ("ai", "heard", "reply", "act", "listen") on the incoming queue (TP_DIRECT), so the
// faces can't tell the difference. Every turn has a number: the main loop moves it on to
// interrupt (a tap, a cancel, the hub coming back) and both tasks drop whatever belongs to an
// older one, so a late message never lands on a new question.
#include "../platform.h"
#include <ArduinoJson.h>
#include <esp_heap_caps.h>
#include <esp_random.h>
#include <freertos/FreeRTOS.h>
#include <freertos/queue.h>
#include <freertos/semphr.h>
#include <freertos/task.h>
#include <ctype.h>
#include <string.h>
#include <time.h>
#include <algorithm>
#include <atomic>
#include <string>
#include <vector>
#include "../config.h"
#include "../hal.h"
#include "../model.h"
#include "direct_http.h"
#include "direct_text.h"
#include "direct_json.h"
#include "internal.h"

namespace hub {
namespace detail {

namespace {

using namespace hub::direct;

constexpr const char* kApiVersion = "2026-10-21";
constexpr size_t kMaxHistory = 12;          // messages (six questions and answers)
constexpr size_t kMaxSseLine = 256 * 1024;  // a search result's block arrives as one line
constexpr uint32_t kStreamIdleMs = 60000;   // Claude says nothing for this long: give up
constexpr uint32_t kReplyEveryMs = 250;     // how often the growing answer goes to the screen

const char* const kDefaultSystem =
    "You are Claude, a voice assistant on a small round puck in someone's home. People talk to you out "
    "loud and hear your answers through its little speaker, with the words on its round screen. Keep "
    "answers short and easy to listen to: usually one to three sentences, with no markdown, lists, "
    "headings, emoji or web addresses. Say numbers, times, dates and units the way people say them. "
    "Search the web when a question needs current or local information. If a request is unclear, ask "
    "one short question.";

// Big JSON documents (the request, with the conversation) go to PSRAM
struct PsramAllocator : ArduinoJson::Allocator {
  void* allocate(size_t n) override { return heap_caps_malloc(n, MALLOC_CAP_SPIRAM | MALLOC_CAP_8BIT); }
  void deallocate(void* p) override { heap_caps_free(p); }
  void* reallocate(void* p, size_t n) override { return heap_caps_realloc(p, n, MALLOC_CAP_SPIRAM | MALLOC_CAP_8BIT); }
};
PsramAllocator s_psram;

// ---------------------------------------------------------------- what the hub gave us

struct Voice {
  std::string url, body, name;
  std::vector<std::string> headers;
  bool xml = false;
  int rate = 16000;
};

struct Config {
  bool claude = false, stt = false;
  // Claude
  std::string key, model = "claude-opus-5-5", url = "https://api.anthropic.com/v1/messages", effort = "low", sys;
  std::string loc;  // the web search's user_location object, as JSON
  int maxTokens = 2048, keepMin = 5;
  bool search = true, followUp = true;
  // speech to text
  std::string sttUrl, sttFmt = "multipart", sttFile = "file", sttText = "text", sttName = "Speech to text";
  std::vector<std::string> sttHeaders;
  std::vector<std::pair<std::string, std::string>> sttFields;
  // listening
  int endSilenceMs = 900, noSpeechSec = 7, maxListenSec = 30;
  float vadSens = 3.0f;
};

Config s_cfg;                    // the direct task's
Voice s_voice;                   // the voice task reads it under s_voiceLock
bool s_hasVoice = false;
SemaphoreHandle_t s_voiceLock = nullptr;

std::atomic<bool> s_ready{false};   // Claude and speech to text are set up
std::atomic<int> s_turn{0};         // the turn that counts
std::atomic<int> s_stopTurn{-1};    // stop listening in this turn (tapped, or put down)
std::atomic<int> s_tzMin{0};        // the main loop's idea of local time, for Claude's context
int s_turnMain = 0;                 // main loop

enum class Op : uint8_t { Start, Reload };
struct Cmd {
  Op op;
  int turn;
  bool lift;
};
QueueHandle_t s_cmds = nullptr;

struct Say {
  int turn;
  std::string text;
  bool last;
  bool followUp;
};
QueueHandle_t s_says = nullptr;

int16_t* s_rec = nullptr;           // the recording (PSRAM)
size_t s_recCap = 0;
char* s_sseBuf = nullptr;           // reading Claude's stream (PSRAM)
char* s_voiceBuf = nullptr;         // reading the voice (PSRAM)
int16_t* s_voiceSamples = nullptr;
constexpr size_t kSseBuf = 2048, kVoiceBuf = 4096;
bool s_started = false;             // the tasks exist (only once direct mode is set up)

Https s_claudeHttp, s_sttHttp;      // the direct task's connections
Https s_voiceHttp;                  // the voice task's
std::atomic<bool> s_voiceReset{false};

struct Msg {
  bool user;
  std::string text;
};
std::vector<Msg> s_history;
uint32_t s_lastTurnAt = 0;

int speakerId(int turn) { return 0x4000 | (turn & 0x3FFF); }  // never one of the hub's ids

// ---------------------------------------------------------------- telling the main loop

void emit(JsonDocument& d, int turn) {
  d["turn"] = turn;
  std::string s;
  serializeJson(d, s);
  pushIncoming(TP_DIRECT, s.data(), s.size());
}

void emitAi(int turn, const char* state, const std::string& text = "") {
  JsonDocument d;
  d["t"] = "ai";
  d["s"] = state;
  if (!text.empty()) d["text"] = cleanForScreen(utf8Clip(text, 200));
  emit(d, turn);
}

void emitHeard(int turn, const std::string& text) {
  JsonDocument d;
  d["t"] = "heard";
  d["text"] = cleanForScreen(utf8Clip(text, 300));
  emit(d, turn);
}

void emitReply(int turn, const std::string& text, bool done) {
  JsonDocument d;
  d["t"] = "reply";
  d["text"] = utf8Clip(cleanForScreen(text), 700);
  d["done"] = done;
  emit(d, turn);
}

void emitAct(int turn, const char* icon, const std::string& text, const char* state) {
  JsonDocument d;
  d["t"] = "act";
  d["icon"] = icon;
  d["text"] = utf8Clip(cleanForScreen(text), 60);
  d["s"] = state;
  emit(d, turn);
}

void emitListen(int turn) {
  JsonDocument d;
  d["t"] = "listen";
  emit(d, turn);
}

// ---------------------------------------------------------------- setup

std::string str(JsonVariantConst v, const char* fallback = "") {
  const char* s = v.as<const char*>();
  return s ? std::string(s) : std::string(fallback);
}

void loadConfig() {
  Config c;
  Voice voice;
  bool hasVoice = false;
  std::string j;
  JsonDocument d;

  if (storeGetString("d_claude", j) && !deserializeJson(d, j)) {
    c.key = str(d["key"]);
    c.model = str(d["model"], c.model.c_str());
    c.url = str(d["url"], c.url.c_str());
    c.effort = str(d["effort"], c.effort.c_str());
    c.sys = str(d["sys"]);
    c.maxTokens = std::min(16000, std::max(256, d["max"] | c.maxTokens));
    c.keepMin = std::max(0, d["keep"] | c.keepMin);
    c.search = d["search"] | true;
    c.followUp = d["follow"] | true;
    if (d["loc"].is<JsonObjectConst>()) serializeJson(d["loc"], c.loc);
    c.claude = !c.key.empty();
  }
  if (storeGetString("d_stt", j) && !deserializeJson(d, j)) {
    c.sttUrl = str(d["url"]);
    c.sttFmt = str(d["fmt"], "multipart");
    c.sttFile = str(d["file"], "file");
    c.sttText = str(d["text"], "text");
    c.sttName = str(d["name"], "Speech to text");
    for (JsonVariantConst h : d["hdr"].as<JsonArrayConst>()) c.sttHeaders.push_back(str(h));
    for (JsonVariantConst f : d["fields"].as<JsonArrayConst>())
      if (f.is<JsonArrayConst>() && f.size() == 2) c.sttFields.emplace_back(str(f[0]), str(f[1]));
    c.stt = c.sttUrl.rfind("http", 0) == 0;
  }
  if (storeGetString("d_tts", j) && !deserializeJson(d, j) && !(d["off"] | false)) {
    voice.url = str(d["url"]);
    voice.body = str(d["body"]);
    voice.name = str(d["name"], "The voice");
    voice.xml = !strcmp(d["esc"] | "json", "xml");
    voice.rate = d["rate"] | 16000;
    for (JsonVariantConst h : d["hdr"].as<JsonArrayConst>()) voice.headers.push_back(str(h));
    hasVoice = voice.url.rfind("http", 0) == 0 && voice.body.find("%TEXT%") != std::string::npos;
  }
  if (storeGetString("d_listen", j) && !deserializeJson(d, j)) {
    c.endSilenceMs = std::min(3000, std::max(300, d["end"] | c.endSilenceMs));
    c.noSpeechSec = std::min(30, std::max(3, d["nospeech"] | c.noSpeechSec));
    c.maxListenSec = std::min(30, std::max(5, d["max"] | c.maxListenSec));
    c.vadSens = d["sens"] | c.vadSens;
  }

  s_cfg = std::move(c);
  xSemaphoreTake(s_voiceLock, portMAX_DELAY);
  s_voice = std::move(voice);
  s_hasVoice = hasVoice;
  xSemaphoreGive(s_voiceLock);
  s_ready = s_cfg.claude && s_cfg.stt;
  // new keys or servers: no connection made with the old ones carries on
  s_claudeHttp.end();
  s_sttHttp.end();
  s_voiceReset = true;
  s_history.clear();
}

// ---------------------------------------------------------------- errors, in words

std::string errorMessage(JsonDocument& d) {
  // Anthropic / OpenAI: {"error":{"message":…}}; ElevenLabs: {"detail":{"message":…}} or {"detail":"…"};
  // Deepgram: {"err_msg":…}
  const char* m = d["error"]["message"] | (const char*)nullptr;
  if (!m) m = d["err_msg"] | (const char*)nullptr;
  if (!m) m = d["detail"]["message"] | (const char*)nullptr;
  if (!m) m = d["detail"] | (const char*)nullptr;
  if (!m) m = d["message"] | (const char*)nullptr;
  if (!m) m = d["error"] | (const char*)nullptr;
  return m ? std::string(m) : std::string();
}

std::string describe(int status, const std::string& body, const std::string& service) {
  JsonDocument d;
  std::string detail;
  if (!deserializeJson(d, body)) detail = errorMessage(d);
  const char* type = d["error"]["type"] | "";
  if (status == 401 || status == 403) return service + " didn't accept its key. Check it on the hub.";
  if (status == 429) return service + " is busy with too many requests. Try again in a minute.";
  if (status == 529 || !strcmp(type, "overloaded_error")) return service + " is overloaded right now. Try again shortly.";
  if (status >= 500) return service + " had a problem (" + std::to_string(status) + "). Try again.";
  if (!detail.empty()) return service + ": " + utf8Clip(detail, 140);
  return service + " said no (" + std::to_string(status) + ").";
}

// ---------------------------------------------------------------- speaking (the voice task)

void say(int turn, const std::string& text, bool last, bool followUp = false) {
  if (!s_hasVoice) return;
  auto* s = new Say{turn, text, last, followUp};
  if (xQueueSend(s_says, &s, pdMS_TO_TICKS(2000)) != pdTRUE) delete s;
}

// Puts speech on the speaker, waiting for room (the voice arrives faster than it's played)
bool toSpeaker(int turn, const std::vector<int16_t>& pcm, bool& spoke) {
  size_t i = 0;
  while (i < pcm.size()) {
    if (s_turn.load() != turn) return false;
    size_t room = hal::speakerFree();
    if (room < 320) {
      vTaskDelay(pdMS_TO_TICKS(20));
      continue;
    }
    size_t n = std::min(room, pcm.size() - i);
    hal::speakerWrite(pcm.data() + i, n);
    i += n;
    if (!spoke) {
      spoke = true;
      emitAi(turn, "speaking");
    }
  }
  return true;
}

// One sentence: the voice service's raw 16-bit PCM, resampled to the speaker's 16 kHz
void speakOne(int turn, const std::string& text, bool& spoke) {
  std::string words = cleanForSpeech(text);
  if (words.empty()) return;
  xSemaphoreTake(s_voiceLock, portMAX_DELAY);
  Voice v = s_voice;
  xSemaphoreGive(s_voiceLock);
  if (v.url.empty()) return;

  std::string body = fillTemplate(v.body, words, v.xml);
  Piece p{body.data(), body.size()};
  int status = s_voiceHttp.post(v.url, v.headers, &p, 1);
  if (s_turn.load() != turn) {
    s_voiceHttp.close();
    return;
  }
  if (status != 200) {
    std::string err;
    if (status > 0) s_voiceHttp.readAll(err, 2048);
    s_voiceHttp.close();
    emitAct(turn, "volume", status < 0 ? std::string(s_voiceHttp.err()) : describe(status, err, v.name), "err");
    hal::logLine("[direct] voice failed: %d %s", status, s_voiceHttp.err());
    return;
  }

  Resampler rs;
  rs.reset(v.rate, AUDIO_RATE);
  char* buf = s_voiceBuf;
  int16_t* samples = s_voiceSamples;
  std::vector<int16_t> out;
  out.reserve(4096);
  uint8_t odd = 0;
  bool haveOdd = false;
  uint32_t lastData = millis();
  for (;;) {
    if (s_turn.load() != turn) {
      s_voiceHttp.close();
      return;
    }
    int n = s_voiceHttp.read(buf, (int)kVoiceBuf);
    if (n == -2) {
      if (millis() - lastData > 15000) {
        s_voiceHttp.close();  // stalled: this connection can't be trusted for the next sentence
        break;
      }
      continue;
    }
    if (n < 0) {
      s_voiceHttp.close();
      break;
    }
    if (n == 0) break;
    lastData = millis();
    // bytes → samples, carrying an odd byte to the next read
    size_t count = 0, i = 0;
    if (haveOdd) {
      uint8_t two[2] = {odd, (uint8_t)buf[0]};
      memcpy(&samples[count++], two, 2);
      haveOdd = false;
      i = 1;
    }
    for (; i + 1 < (size_t)n; i += 2) memcpy(&samples[count++], buf + i, 2);
    if (i < (size_t)n) {
      odd = (uint8_t)buf[i];
      haveOdd = true;
    }
    out.clear();
    rs.process(samples, count, out);
    if (!toSpeaker(turn, out, spoke)) {
      s_voiceHttp.close();
      return;
    }
  }
}

void voiceTask(void*) {
  int open = -1;  // the turn whose speech is open on the speaker
  bool spoke = false;
  for (;;) {
    Say* s = nullptr;
    if (xQueueReceive(s_says, &s, pdMS_TO_TICKS(5000)) != pdTRUE || !s) {
      if (s_voiceReset.exchange(false) || s_voiceHttp.idleFor(60000)) s_voiceHttp.end();
      continue;
    }
    if (s_voiceReset.exchange(false)) s_voiceHttp.end();
    int turn = s->turn;
    if (turn == s_turn.load()) {
      if (open != turn) {
        open = turn;
        spoke = false;
        hal::speakerBegin(speakerId(turn));
      }
      if (!s->text.empty()) speakOne(turn, s->text, spoke);
      if (s->last) {
        hal::speakerEnd(speakerId(turn));
        vTaskDelay(pdMS_TO_TICKS(40));
        while (turn == s_turn.load() && hal::speakerPlaying()) vTaskDelay(pdMS_TO_TICKS(30));
        if (turn == s_turn.load()) {
          if (s->followUp) emitListen(turn);
          else emitAi(turn, "idle");
        }
        open = -1;
      }
    }
    delete s;
  }
}

// ---------------------------------------------------------------- listening

// Records until they stop talking (or tap, or put the puck down). Returns false if the turn is
// over (interrupted, or nobody spoke); else [from, to) is the part worth transcribing.
bool listen(int turn, bool lift, size_t& from, size_t& to) {
  Vad vad;
  vad.reset(s_cfg.vadSens, s_cfg.endSilenceMs);
  size_t n = 0;
  bool tapped = false;
  uint32_t t0 = millis();
  // holding the puck up to talk: give them longer to start
  uint32_t quietLimit = (uint32_t)(s_cfg.noSpeechSec + (lift ? 8 : 0)) * 1000u;
  for (;;) {
    if (s_turn.load() != turn) return false;
    size_t room = s_recCap - n;
    size_t got = room ? hal::micRead(s_rec + n, std::min<size_t>(room, 1600)) : 0;
    if (got) {
      vad.process(s_rec + n, got);
      n += got;
    }
    if (s_stopTurn.load() == turn) {
      tapped = true;
      break;
    }
    if (vad.speechEnded()) break;
    uint32_t elapsed = millis() - t0;
    if (!vad.speechStarted() && elapsed > quietLimit) {
      emitAi(turn, "idle");
      return false;
    }
    if (room == 0 || elapsed > (uint32_t)s_cfg.maxListenSec * 1000u) break;
    if (!got) vTaskDelay(pdMS_TO_TICKS(15));
  }
  if (vad.speechStarted()) {
    from = vad.speechStartSample();
    to = std::min(n, vad.lastSpeechSample() + AUDIO_RATE * 3 / 10);  // and 300 ms after
    if (to <= from) to = n;
  } else if (tapped && n > AUDIO_RATE / 2) {
    from = 0;  // a quiet voice the detector missed: send it all
    to = n;
  } else {
    from = to = 0;
  }
  return true;
}

// ---------------------------------------------------------------- speech to text

bool transcribe(int turn, size_t from, size_t to, std::string& text, std::string& err) {
  size_t samples = to - from;
  uint8_t wav[44];
  wavHeader(wav, (uint32_t)samples, AUDIO_RATE);
  std::vector<std::string> headers = s_cfg.sttHeaders;
  std::string head, tail;
  std::vector<Piece> pieces;
  if (s_cfg.sttFmt == "multipart") {
    char b[40];
    snprintf(b, sizeof b, "----Pucksley%08lx%08lx", (unsigned long)esp_random(), (unsigned long)esp_random());
    for (auto& f : s_cfg.sttFields)
      head += std::string("--") + b + "\r\nContent-Disposition: form-data; name=\"" + f.first + "\"\r\n\r\n" + f.second + "\r\n";
    head += std::string("--") + b + "\r\nContent-Disposition: form-data; name=\"" + s_cfg.sttFile +
            "\"; filename=\"speech.wav\"\r\nContent-Type: audio/wav\r\n\r\n";
    tail = std::string("\r\n--") + b + "--\r\n";
    headers.push_back(std::string("Content-Type: multipart/form-data; boundary=") + b);
    pieces = {{head.data(), head.size()}, {wav, sizeof wav}, {s_rec + from, samples * 2}, {tail.data(), tail.size()}};
  } else {
    // the WAV is the whole body (Azure, Deepgram); the hub sent its Content-Type
    pieces = {{wav, sizeof wav}, {s_rec + from, samples * 2}};
  }
  int status = s_sttHttp.post(s_cfg.sttUrl, headers, pieces.data(), pieces.size());
  if (s_turn.load() != turn) {
    s_sttHttp.close();
    return false;
  }
  if (status < 0) {
    err = std::string("I couldn't reach ") + s_cfg.sttName + ". " + s_sttHttp.err() + ".";
    return false;
  }
  std::string body;
  if (!s_sttHttp.readAll(body, 65536)) s_sttHttp.close();   // Deepgram lists every word, with timings
  if (status != 200) {
    err = describe(status, body, s_cfg.sttName);
    return false;
  }
  JsonDocument d(&s_psram);
  if (deserializeJson(d, body)) {
    err = s_cfg.sttName + " sent back something I couldn't read.";
    return false;
  }
  const char* t = jsonAt(d.as<JsonVariantConst>(), s_cfg.sttText) | "";
  text = t;
  size_t b = text.find_first_not_of(" \t\r\n"), e = text.find_last_not_of(" \t\r\n");
  text = b == std::string::npos ? std::string() : text.substr(b, e - b + 1);
  return true;
}

// ---------------------------------------------------------------- Claude

std::string contextNote() {
  std::string note;
  time_t t = time(nullptr);
  if (t > 1700000000) {
    t += (time_t)s_tzMin.load() * 60;
    struct tm tm;
    gmtime_r(&t, &tm);
    static const char* const kDays[] = {"Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday"};
    static const char* const kMonths[] = {"January", "February", "March", "April", "May", "June", "July",
                                          "August", "September", "October", "November", "December"};
    char buf[96];
    int h12 = tm.tm_hour % 12 == 0 ? 12 : tm.tm_hour % 12;
    snprintf(buf, sizeof buf, "Right now it's %s %d %s %d, %d:%02d %s local time. ", kDays[tm.tm_wday % 7], tm.tm_mday,
             kMonths[tm.tm_mon % 12], 1900 + tm.tm_year, h12, tm.tm_min, tm.tm_hour < 12 ? "am" : "pm");
    note = buf;
  }
  note +=
      "The home hub isn't reachable, so you're answering straight from the puck: you can talk and search "
      "the web, but you can't control the lights, music, TV, heating, timers or computer until the hub is "
      "back. If asked to, say so briefly.";
  return note;
}

struct Stream {
  int turn = 0;
  std::string reply, queryJson, error, stop;
  int toolIndex = -1;
  bool searched = false;
  bool done = false;
  bool spokeText = false;  // told the screen it's answering (no voice set up)
  uint32_t lastReplyAt = 0;
  SentenceSplitter sentences;
};

void addText(Stream& st, const char* delta) {
  if (!delta || !*delta) return;
  st.reply += delta;
  std::vector<std::string> pieces;
  st.sentences.add(delta, pieces);
  for (auto& p : pieces) say(st.turn, p, false);
  if (!s_hasVoice && !st.spokeText) {
    st.spokeText = true;
    emitAi(st.turn, "speaking");
  }
  if (millis() - st.lastReplyAt > kReplyEveryMs) {
    st.lastReplyAt = millis();
    emitReply(st.turn, st.reply, false);
  }
}

void onEvent(Stream& st, const char* json, size_t len) {
  static JsonDocument filter;
  if (filter.isNull()) {
    filter["type"] = true;
    filter["index"] = true;
    filter["content_block"]["type"] = true;
    filter["content_block"]["content"]["error_code"] = true;
    filter["delta"]["type"] = true;
    filter["delta"]["text"] = true;
    filter["delta"]["partial_json"] = true;
    filter["delta"]["stop_reason"] = true;
    filter["error"]["type"] = true;
    filter["error"]["message"] = true;
  }
  JsonDocument ev(&s_psram);
  if (deserializeJson(ev, json, len, DeserializationOption::Filter(filter))) return;
  const char* type = ev["type"] | "";
  int index = ev["index"] | -1;

  if (!strcmp(type, "content_block_start")) {
    const char* bt = ev["content_block"]["type"] | "";
    if (!strcmp(bt, "server_tool_use")) {
      st.toolIndex = index;
      st.queryJson.clear();
      emitAct(st.turn, "search", "Searching the web", "run");
    } else if (!strcmp(bt, "web_search_tool_result")) {
      bool failed = ev["content_block"]["content"]["error_code"].is<const char*>();
      emitAct(st.turn, "search", failed ? "The search didn't work" : "Searched the web", failed ? "err" : "ok");
      st.searched = true;
    } else if (!strcmp(bt, "text") && !st.reply.empty() && !isspace((unsigned char)st.reply.back())) {
      addText(st, " ");  // text after a search carries on the answer
    }
  } else if (!strcmp(type, "content_block_delta")) {
    const char* dt = ev["delta"]["type"] | "";
    if (!strcmp(dt, "text_delta")) {
      addText(st, ev["delta"]["text"] | "");
    } else if (!strcmp(dt, "input_json_delta") && index == st.toolIndex) {
      st.queryJson += ev["delta"]["partial_json"] | "";
    }
  } else if (!strcmp(type, "content_block_stop")) {
    if (index == st.toolIndex) {
      JsonDocument q;
      if (!deserializeJson(q, st.queryJson) && q["query"].is<const char*>())
        emitAct(st.turn, "search", std::string("Searching: ") + q["query"].as<const char*>(), "run");
      st.toolIndex = -1;
    }
  } else if (!strcmp(type, "message_delta")) {
    if (ev["delta"]["stop_reason"].is<const char*>()) st.stop = ev["delta"]["stop_reason"].as<const char*>();
  } else if (!strcmp(type, "message_stop")) {
    st.done = true;
  } else if (!strcmp(type, "error")) {
    const char* et = ev["error"]["type"] | "";
    if (!strcmp(et, "overloaded_error")) st.error = "Claude is overloaded right now. Try again shortly.";
    else st.error = std::string("Claude stopped: ") + (ev["error"]["message"] | "something went wrong");
    st.done = true;
  }
}

std::string buildRequest(const std::string& question) {
  JsonDocument req(&s_psram);
  req["model"] = s_cfg.model;
  req["max_tokens"] = s_cfg.maxTokens;
  req["stream"] = true;
  if (!s_cfg.effort.empty()) req["output_config"]["effort"] = s_cfg.effort;
  JsonArray sys = req["system"].to<JsonArray>();
  JsonObject s1 = sys.add<JsonObject>();
  s1["type"] = "text";
  s1["text"] = s_cfg.sys.empty() ? kDefaultSystem : s_cfg.sys.c_str();
  JsonObject s2 = sys.add<JsonObject>();
  s2["type"] = "text";
  s2["text"] = contextNote();
  if (s_cfg.search) {
    JsonObject tool = req["tools"].to<JsonArray>().add<JsonObject>();
    tool["type"] = "web_search_20260209";
    tool["name"] = "web_search";
    tool["max_uses"] = 3;
    if (!s_cfg.loc.empty()) {
      JsonDocument loc;
      if (!deserializeJson(loc, s_cfg.loc)) tool["user_location"] = loc.as<JsonObjectConst>();
    }
  }
  JsonArray msgs = req["messages"].to<JsonArray>();
  for (const Msg& m : s_history) {
    JsonObject o = msgs.add<JsonObject>();
    o["role"] = m.user ? "user" : "assistant";
    o["content"] = m.text;
  }
  JsonObject u = msgs.add<JsonObject>();
  u["role"] = "user";
  u["content"] = question;
  std::string body;
  serializeJson(req, body);
  return body;
}

void remember(const std::string& question, const std::string& answer) {
  if (answer.empty()) return;
  s_history.push_back({true, question});
  s_history.push_back({false, answer});
  while (s_history.size() > kMaxHistory) s_history.erase(s_history.begin(), s_history.begin() + 2);
}

// Asks Claude and streams the answer to the screen and the voice (errors go to the screen).
void ask(int turn, const std::string& question) {
  if (s_cfg.keepMin == 0 || millis() - s_lastTurnAt > (uint32_t)s_cfg.keepMin * 60000u) s_history.clear();
  std::string body = buildRequest(question);
  std::vector<std::string> headers = {
      "x-api-key: " + s_cfg.key,
      std::string("anthropic-version: ") + kApiVersion,
      "content-type: application/json",
  };
  Piece p{body.data(), body.size()};
  int status = s_claudeHttp.post(s_cfg.url, headers, &p, 1);
  body.clear();
  body.shrink_to_fit();
  if (s_turn.load() != turn) {
    s_claudeHttp.close();
    return;
  }
  if (status != 200) {
    std::string err;
    if (status > 0 && !s_claudeHttp.readAll(err, 4096)) s_claudeHttp.close();
    if (status < 0) s_claudeHttp.close();
    emitAi(turn, "error", status < 0 ? std::string("I can't reach Claude. ") + s_claudeHttp.err() + "." : describe(status, err, "Claude"));
    return;
  }

  Stream st;
  st.turn = turn;
  LineSplitter lines(kMaxSseLine);
  char* buf = s_sseBuf;
  uint32_t lastData = millis();
  bool ok = true;
  while (!st.done) {
    if (s_turn.load() != turn) {
      s_claudeHttp.close();
      return;
    }
    int n = s_claudeHttp.read(buf, (int)kSseBuf);
    if (n == -2) {
      if (millis() - lastData > kStreamIdleMs) {
        st.error = "Claude went quiet. Try again.";
        ok = false;
        break;
      }
      continue;
    }
    if (n <= 0) {
      if (n < 0) st.error = std::string("Lost the connection to Claude. ") + s_claudeHttp.err() + ".";
      else st.error = "Claude's answer stopped short.";  // the stream ended before message_stop
      ok = false;
      break;
    }
    lastData = millis();
    lines.feed(buf, (size_t)n, [&](const std::string& line) {
      if (line.compare(0, 5, "data:") != 0) return;
      size_t at = line.size() > 5 && line[5] == ' ' ? 6 : 5;
      onEvent(st, line.data() + at, line.size() - at);
    });
  }
  if (!ok || !st.done) s_claudeHttp.close();
  if (st.done) {
    // read to the end so the connection can be used again
    std::string rest;
    if (!s_claudeHttp.readAll(rest, 1024)) s_claudeHttp.close();
  }
  if (s_turn.load() != turn) return;

  if (!st.error.empty()) {
    if (st.reply.empty()) {
      emitAi(turn, "error", st.error);  // nothing was said: no speech to close
      return;
    }
    // part of an answer, then trouble: keep what came, and say what happened
    emitAct(turn, "wifi", st.error, "err");
  }
  if (st.stop == "refusal" && st.reply.empty()) {
    addText(st, "Sorry, that's not something I can help with.");
  } else if (st.reply.empty()) {
    addText(st, "Sorry, I don't have an answer for that.");
  }
  std::string rest = st.sentences.flush();
  if (!rest.empty()) say(turn, rest, false);
  emitReply(turn, st.reply, true);
  remember(question, st.reply);
  s_lastTurnAt = millis();
  bool followUp = s_cfg.followUp && st.stop != "max_tokens" && endsWithQuestion(st.reply);
  if (s_hasVoice) {
    say(turn, "", true, followUp);
  } else if (followUp) {
    emitListen(turn);
  } else {
    emitAi(turn, "idle");
  }
}

// ---------------------------------------------------------------- a turn

void runTurn(int turn, bool lift) {
  if (!s_ready.load()) {
    emitAi(turn, "error", "Direct mode isn't set up: connect to the hub once.");
    return;
  }
  size_t from = 0, to = 0;
  if (!listen(turn, lift, from, to)) return;
  emitAi(turn, "thinking");
  if (to - from < AUDIO_RATE / 4) {
    emitHeard(turn, "");
    emitAi(turn, "error", "Sorry, I didn't catch that.");
    return;
  }
  std::string text, err;
  if (!transcribe(turn, from, to, text, err)) {
    if (s_turn.load() == turn) emitAi(turn, "error", err);
    return;
  }
  if (s_turn.load() != turn) return;
  emitHeard(turn, text);
  if (text.empty()) {
    emitAi(turn, "error", "Sorry, I didn't catch that.");
    return;
  }
  ask(turn, text);
}

void directTask(void*) {
  loadConfig();
  for (;;) {
    Cmd c;
    if (xQueueReceive(s_cmds, &c, pdMS_TO_TICKS(10000)) != pdTRUE) {
      // nothing going on: let idle connections go (the servers would drop them anyway)
      if (s_claudeHttp.idleFor(90000)) s_claudeHttp.end();
      if (s_sttHttp.idleFor(90000)) s_sttHttp.end();
      continue;
    }
    if (c.op == Op::Reload) {
      loadConfig();
    } else if (c.op == Op::Start && c.turn == s_turn.load()) {
      runTurn(c.turn, c.lift);
    }
  }
}

// ---------------------------------------------------------------- the main loop's side

void* psram(size_t bytes) {
  void* p = heap_caps_malloc(bytes, MALLOC_CAP_SPIRAM | MALLOC_CAP_8BIT);
  return p ? p : malloc(bytes);
}

// The tasks and buffers, made the first time there's a setup to use (a puck that never gets keys
// spends no RAM on direct mode). Their stacks are internal RAM: they read flash (the saved setup).
void startTasks() {
  if (s_started) return;
  s_recCap = (size_t)AUDIO_RATE * 30;
  s_rec = (int16_t*)heap_caps_malloc(s_recCap * sizeof(int16_t), MALLOC_CAP_SPIRAM | MALLOC_CAP_8BIT);
  if (!s_rec) {
    s_recCap = (size_t)AUDIO_RATE * 4;  // no PSRAM: a short one
    s_rec = (int16_t*)malloc(s_recCap * sizeof(int16_t));
    if (!s_rec) s_recCap = 0;
  }
  s_sseBuf = (char*)psram(kSseBuf);
  s_voiceBuf = (char*)psram(kVoiceBuf);
  s_voiceSamples = (int16_t*)psram((kVoiceBuf / 2 + 1) * sizeof(int16_t));
  if (!s_recCap || !s_sseBuf || !s_voiceBuf || !s_voiceSamples) {
    hal::logLine("[direct] not enough memory for direct mode");
    return;
  }
  bool a = xTaskCreatePinnedToCore(directTask, "direct", 8192, nullptr, 3, nullptr, 0) == pdPASS;
  bool b = xTaskCreatePinnedToCore(voiceTask, "voice", 8192, nullptr, 3, nullptr, 0) == pdPASS;
  if (!a || !b) hal::logLine("[direct] couldn't start (internal RAM): direct=%d voice=%d", (int)a, (int)b);
  s_started = a;
}

}  // namespace

void directBegin() {
  s_voiceLock = xSemaphoreCreateMutex();
  s_cmds = xQueueCreate(8, sizeof(Cmd));
  s_says = xQueueCreate(32, sizeof(Say*));
  // Ready straight away if keys were saved (the task loads them properly in a moment)
  std::string j;
  if (storeGetString("d_claude", j) && storeGetString("d_stt", j)) {
    s_ready = true;
    startTasks();
  }
}

bool directConfigured() { return s_ready.load() && s_started; }

int directTurn() { return s_turn.load(); }

void directTalk(const char* action, bool lift) {
  if (!strcmp(action, "start")) {
    int turn = ++s_turnMain;
    s_turn = turn;
    Cmd c{Op::Start, turn, lift};
    xQueueSend(s_cmds, &c, 0);
  } else if (!strcmp(action, "stop")) {
    s_stopTurn = s_turn.load();
  } else if (!strcmp(action, "cancel")) {
    s_turn = ++s_turnMain;  // whatever the current turn was doing is dropped
  }
}

void directFromHub(const char* what, const char* json) {
  if (!strcmp(what, "off")) {
    storeSetString("d_claude", "");
    storeSetString("d_stt", "");
    storeSetString("d_tts", "");
    storeSetString("d_listen", "");
  } else if (!strcmp(what, "claude")) {
    storeSetString("d_claude", json);
  } else if (!strcmp(what, "stt")) {
    storeSetString("d_stt", json);
  } else if (!strcmp(what, "tts")) {
    storeSetString("d_tts", json);
  } else if (!strcmp(what, "listen")) {
    storeSetString("d_listen", json);
  } else {
    return;
  }
  if (strcmp(what, "off") != 0) startTasks();
  Cmd c{Op::Reload, 0, false};
  xQueueSend(s_cmds, &c, pdMS_TO_TICKS(50));
  if (!strcmp(what, "off")) s_ready = false;
}

void directSetTzMin(int minutes) { s_tzMin = minutes; }

}  // namespace detail
}  // namespace hub
