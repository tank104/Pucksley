// Sound, both ways, on one full-duplex I2S bus at 16 kHz:
//  - out: the hub's speech (streamed into a ring buffer, played once a little has arrived) mixed
//    with the puck's own little tones (ticks, taps, chimes), through the ES8311 and the speaker amp
//  - in: the two mics through the ES7210, downmixed to mono and queued for the link while listening
// One task does both in lockstep: read 10 ms from the mics, write 10 ms to the speaker. The amp is
// only on while something is audible, so the speaker doesn't hiss.
#include "../platform.h"
#include <algorithm>
#include <atomic>
#include <math.h>
#include <string.h>
#include <driver/i2s_std.h>
#include <esp_heap_caps.h>
#include <freertos/FreeRTOS.h>
#include <freertos/queue.h>
#include <freertos/task.h>
#include "../config.h"
#include "hal_internal.h"
#include "esp_aec.h"

namespace hal {
namespace detail {

namespace {

constexpr uint32_t kRate = AUDIO_RATE;
constexpr uint32_t kMclk = kRate * 256;
constexpr int kBlock = 160;                 // frames per round (10 ms)
constexpr uint32_t kAmpOffAfterMs = 400;    // keep the amp on briefly between sounds
constexpr int kAmpWarmBlocks = 2;           // silence after switching the amp on (it pops otherwise)
constexpr float kToneGain = 0.35f;          // of full scale: a desk toy, not an alarm
constexpr size_t kPrebuffer = (size_t)(kRate * SPK_PREBUFFER_MS / 1000);

// ---------------------------------------------------------------- tones

struct Note {
  float hz;
  uint16_t startMs, lenMs;
  float amp;
  float decay;     // envelope time constant in ms (exponential); 0 = flat with a short release
  bool buzzy;      // add odd harmonics (for the error buzz)
};

struct Sound {
  const Note* notes;
  uint8_t count;
};

constexpr Note kTick[] = {{2400, 0, 10, 0.35f, 2.5f, false}};
constexpr Note kTap[] = {{1250, 0, 35, 0.55f, 9, false}};
constexpr Note kSelect[] = {{1046, 0, 70, 0.6f, 25, false}, {1568, 20, 60, 0.25f, 20, false}};
constexpr Note kSuccess[] = {{660, 0, 110, 0.6f, 45, false}, {990, 95, 180, 0.6f, 70, false}};
constexpr Note kError[] = {{165, 0, 200, 0.55f, 0, true}};
constexpr Note kChime[] = {
    {1047, 0, 600, 0.55f, 180, false},
    {1319, 160, 600, 0.5f, 180, false},
    {1568, 320, 650, 0.5f, 200, false},
    {2093, 480, 900, 0.45f, 260, false},
};
// Listening starts: a soft rising pair. Listening stops: the same, falling.
constexpr Note kListen[] = {{880, 0, 70, 0.45f, 30, false}, {1320, 60, 90, 0.4f, 40, false}};
constexpr Note kListenEnd[] = {{1320, 0, 70, 0.35f, 30, false}, {880, 60, 90, 0.3f, 40, false}};

Sound soundFor(Tone t) {
  switch (t) {
    case Tone::Tick: return {kTick, 1};
    case Tone::Tap: return {kTap, 1};
    case Tone::Select: return {kSelect, 2};
    case Tone::Success: return {kSuccess, 2};
    case Tone::Error: return {kError, 1};
    case Tone::Chime: return {kChime, 4};
    case Tone::Listen: return {kListen, 2};
    case Tone::ListenEnd: return {kListenEnd, 2};
  }
  return {kTap, 1};
}

float envelope(const Note& n, float tMs) {
  if (tMs < 0 || tMs >= n.lenMs) return 0;
  const float attack = tMs < 2.0f ? tMs / 2.0f : 1.0f;
  const float body = n.decay > 0 ? expf(-tMs / n.decay) : 1.0f;
  const float release = (n.lenMs - tMs) < 6.0f ? (n.lenMs - tMs) / 6.0f : 1.0f;
  return attack * body * release;
}

// The tone being played, rendered a block at a time
struct ToneVoice {
  Sound sound = {nullptr, 0};
  uint32_t frame = 0, frames = 0;
  float phase[4] = {0, 0, 0, 0};
  bool active = false;

  void start(Tone t) {
    sound = soundFor(t);
    uint32_t total = 0;
    for (int i = 0; i < sound.count; i++) total = std::max<uint32_t>(total, sound.notes[i].startMs + sound.notes[i].lenMs);
    frames = total * kRate / 1000;
    frame = 0;
    memset(phase, 0, sizeof(phase));
    active = true;
  }

  float next() {
    if (!active) return 0;
    const float tMs = (float)frame * 1000.0f / kRate;
    float v = 0;
    for (int k = 0; k < sound.count && k < 4; k++) {
      const Note& n = sound.notes[k];
      const float e = envelope(n, tMs - n.startMs);
      if (e <= 0) continue;
      phase[k] += 2.0f * (float)M_PI * n.hz / kRate;
      if (phase[k] > 2.0f * (float)M_PI) phase[k] -= 2.0f * (float)M_PI;
      float w = sinf(phase[k]);
      if (n.buzzy) w = 0.7f * w + 0.2f * sinf(3 * phase[k]) + 0.1f * sinf(5 * phase[k]);
      v += n.amp * e * w;
    }
    if (++frame >= frames) active = false;
    return v * kToneGain;
  }
};

// ---------------------------------------------------------------- ring buffers

// A ring of samples in PSRAM, guarded by a spinlock (one task writes, the other reads; the
// critical sections are a memcpy long).
struct Ring {
  int16_t* buf = nullptr;
  size_t cap = 0, head = 0, tail = 0, count = 0;
  portMUX_TYPE mux = portMUX_INITIALIZER_UNLOCKED;

  bool init(size_t samples) {
    buf = (int16_t*)heap_caps_malloc(samples * sizeof(int16_t), MALLOC_CAP_SPIRAM | MALLOC_CAP_8BIT);
    if (!buf) buf = (int16_t*)malloc(samples * sizeof(int16_t) / 4), samples /= 4;  // no PSRAM: a smaller one
    cap = buf ? samples : 0;
    return buf != nullptr;
  }
  size_t write(const int16_t* src, size_t n) {
    portENTER_CRITICAL(&mux);
    size_t room = cap - count;
    if (n > room) n = room;
    for (size_t done = 0; done < n;) {
      size_t chunk = std::min(n - done, cap - head);
      memcpy(buf + head, src + done, chunk * sizeof(int16_t));
      head = (head + chunk) % cap;
      done += chunk;
    }
    count += n;
    portEXIT_CRITICAL(&mux);
    return n;
  }
  size_t read(int16_t* dst, size_t n) {
    portENTER_CRITICAL(&mux);
    if (n > count) n = count;
    for (size_t done = 0; done < n;) {
      size_t chunk = std::min(n - done, cap - tail);
      memcpy(dst + done, buf + tail, chunk * sizeof(int16_t));
      tail = (tail + chunk) % cap;
      done += chunk;
    }
    count -= n;
    portEXIT_CRITICAL(&mux);
    return n;
  }
  size_t available() {
    portENTER_CRITICAL(&mux);
    size_t c = count;
    portEXIT_CRITICAL(&mux);
    return c;
  }
  void clear() {
    portENTER_CRITICAL(&mux);
    head = tail = count = 0;
    portEXIT_CRITICAL(&mux);
  }
};

// ---------------------------------------------------------------- state

QueueHandle_t s_tones = nullptr;
i2s_chan_handle_t s_txCh = nullptr;   // speaker (ES8311)
i2s_chan_handle_t s_rxCh = nullptr;   // mics (ES7210)
std::atomic<bool> s_soundOn{true};
bool s_ok = false;
bool s_micOk = false;

Ring s_mic, s_spk;
std::atomic<bool> s_micOn{false};
std::atomic<float> s_micLevel{0};
std::atomic<int> s_micGainDb{30};
std::atomic<int> s_micGainApplied{-1};

std::atomic<int> s_spkId{-1};        // the utterance being queued/played
std::atomic<uint32_t> s_spkPos{0};   // samples of it handed to I2S so far (a talking face keeps time by it)
std::atomic<bool> s_spkEnded{false}; // no more audio for it
std::atomic<bool> s_spkFlush{false}; // drop everything now
std::atomic<int> s_spkDone{-1};      // finished playing (reported once)
std::atomic<bool> s_spkPlaying{false};
std::atomic<float> s_spkLevel{0};
std::atomic<float> s_spkGain{0.5f};

int16_t s_rx[kBlock * 2];
int16_t s_tx[kBlock * 2];
int16_t s_mono[kBlock];
int16_t s_speech[kBlock];

uint32_t s_rounds = 0, s_underruns = 0;

// Talking over the puck (see hal.h)
std::atomic<bool> s_bargeArmed{false};
std::atomic<bool> s_bargeHeard{false};
std::atomic<uint32_t> s_bargeCount{0};
constexpr size_t kRecent = kRate / 2;        // the half second kept, for the words that set it off
int16_t* s_recent = nullptr;
size_t s_recentPos = 0;
bool s_recentFull = false;

// ---- talking over the puck: echo cancellation
//
// The mics hear the puck's own speaker at up to ~20000 RMS, far louder than someone a metre away,
// so loudness alone can't tell them apart. ESP-SR's echo canceller (full-duplex mode) takes the
// mics and what was played, and leaves the mics without the echo: someone talking over it stands
// out in what's left. What was played reaches the mics late (the I2S buffers, the air), so the
// reference is delayed to line up; the delay is measured from each reply and logged.
struct Canceller {
  aec_handle_t* aec = nullptr;
  int chunk = 0;                       // samples per AEC frame
  int16_t *mic = nullptr, *ref = nullptr, *out = nullptr;
  int fill = 0;
  static constexpr int kDelayCap = 8192;
  int16_t* line = nullptr;             // what was played, newest at pos
  int pos = 0;
  // per 10 ms block, for measuring the echo's delay
  static constexpr int kEnv = 600;
  float *envMic = nullptr, *envRef = nullptr;
  int envN = 0;

  bool begin() {
    // Its working memory in PSRAM: internal RAM is short once Wi-Fi is up
    aec_config_t cfg = {};
    cfg.mic_num = 1;
    cfg.ref_num = 1;
    cfg.out_num = 1;
    cfg.filter_length = 2;   // 32 ms of tail (the delay line takes out the rest). 4 cancels more but slows the talking face to ~14 fps (they share the cache); 3 misbehaves
    cfg.sample_rate = kRate;
    cfg.caps = MALLOC_CAP_SPIRAM | MALLOC_CAP_8BIT;   // internal: Wi-Fi then fails to connect
    cfg.mode = AEC_MODE_FD_LOW_COST;
    cfg.nlp_level = AEC_NLP_LEVEL_AGGR;
    aec = aec_create_from_config(&cfg);
    if (!aec) return false;
    chunk = aec_get_chunksize(aec);
    auto alloc = [](size_t n) { return (int16_t*)heap_caps_aligned_alloc(16, n * sizeof(int16_t), MALLOC_CAP_INTERNAL | MALLOC_CAP_8BIT); };
    mic = alloc(chunk);
    ref = alloc(chunk);
    out = alloc(chunk);
    line = (int16_t*)heap_caps_calloc(kDelayCap, sizeof(int16_t), MALLOC_CAP_SPIRAM);
    envMic = (float*)heap_caps_malloc(kEnv * sizeof(float), MALLOC_CAP_SPIRAM);
    envRef = (float*)heap_caps_malloc(kEnv * sizeof(float), MALLOC_CAP_SPIRAM);
    return mic && ref && out && line && envMic && envRef;
  }
};
Canceller s_cancel;
bool s_cancelOk = false;
std::atomic<int> s_echoDelay{960};     // samples the reference is held back: 60 ms (measured 60-70 ms on the puck)

// The lag (in 10 ms blocks) at which the mics' loudness best follows what was played
int bestLag(const float* mic, const float* ref, int n, float* score) {
  int best = 0;
  float bestC = -2;
  for (int lag = 0; lag <= 30 && lag < n - 50; lag++) {
    double sx = 0, sy = 0, sxx = 0, syy = 0, sxy = 0;
    int m = n - lag;
    for (int i = 0; i < m; i++) {
      double x = log1p(ref[i]), y = log1p(mic[i + lag]);
      sx += x; sy += y; sxx += x * x; syy += y * y; sxy += x * y;
    }
    double cov = sxy / m - (sx / m) * (sy / m);
    double vx = sxx / m - (sx / m) * (sx / m), vy = syy / m - (sy / m) * (sy / m);
    float c = (vx > 0 && vy > 0) ? (float)(cov / sqrt(vx * vy)) : 0;
    if (c > bestC) {
      bestC = c;
      best = lag;
    }
  }
  if (score) *score = bestC;
  return best;
}

void audioTask(void*) {
  ToneVoice tone;
  bool ampOn = false;
  int warm = 0;
  uint32_t lastSoundMs = 0;
  bool speechStarted = false;     // the prebuffer was reached for the current utterance
  int playingId = -1;
  float hpX = 0, hpY = 0;         // DC blocker on the mic
  float micEnv = 0, spkEnv = 0;
  // Talking over the puck (see Canceller): what's left after the echo is taken out is compared
  // with the echo that's left over (learned from its peaks while it speaks) and with the room's
  // noise (learned in its pauses).
  bool armed = false;
  constexpr int kRefChunks = 6;          // ~100 ms: what was just played, for the echo left over
  float refHist[kRefChunks] = {};
  int refIdx = 0;
  float residual = -1;                   // what's left of the echo, per unit of what was played
  float noise = 100;
  int learned = 0, hot = 0, chunks = 0;
  float peak = 0;
  float secRaw = 0, secOut = 0, secRef = 0, secRatio = 0;
  double sumRaw = 0, sumOut = 0;         // while it speaks: how much echo the canceller takes out
  double winRaw = 0, winOut = 0;         // the same over about the last second
  int secChunks = 0;
  float micRms = 0;
  int16_t refBlock[kBlock];

  for (;;) {
    // ---- in: 10 ms from the mics (this read paces the whole loop)
    size_t got = 0;
    if (i2s_channel_read(s_rxCh, s_rx, sizeof(s_rx), &got, 100) != ESP_OK || got == 0) {
      vTaskDelay(1);
      continue;
    }
    int frames = (int)(got / (2 * sizeof(int16_t)));
    s_rounds++;

    const int gainWanted = std::max(0, s_micGainDb.load() - (armed ? 9 : 0));
    if (s_micGainApplied.load() != gainWanted) {
      es7210SetGain(gainWanted);
      s_micGainApplied = gainWanted;
    }

    bool armNow = s_bargeArmed.load() && s_micOk && s_cancelOk;
    if (armNow != armed) {
      if (armNow) {
        for (float& r : refHist) r = 0;
        residual = -1;
        learned = hot = chunks = 0;
        peak = 0;
        sumRaw = sumOut = 0;
        winRaw = winOut = 0;
        secRaw = secOut = secRef = secRatio = 0;
        secChunks = 0;
        s_cancel.fill = 0;
        s_cancel.envN = 0;
        s_recentPos = 0;
        s_recentFull = false;
      } else if (chunks > 60) {
        float score = 0;
        int lag = bestLag(s_cancel.envMic, s_cancel.envRef, s_cancel.envN, &score);
        hal::logLine("[barge] reply over: %d chunks, closest %.2f of the way, echo removed %.1f dB, left %.3f, noise %.0f; "
                     "echo arrives %d ms after it's played (fit %.2f)",
                     chunks, (double)peak, sumOut > 0 ? 10.0 * log10(sumRaw / sumOut) : 0.0, (double)residual, (double)noise,
                     lag * 10, (double)score);
        // Line the reference up with it (a block early: the canceller's filter covers the rest).
        // Only for a real change: it measures 60 or 70 ms by turns, and each move makes the
        // canceller learn the echo again.
        const int want = std::min((lag - 1) * kBlock, Canceller::kDelayCap - kBlock);
        if (score > 0.5f && lag > 0 && abs(want - s_echoDelay.load()) >= 2 * kBlock) s_echoDelay = want;
      }
      armed = armNow;
    }
    if ((s_micOn.load() || armed) && s_micOk) {
      float sumSq = 0;
      for (int i = 0; i < frames; i++) {
        int l = s_rx[2 * i], r = s_rx[2 * i + 1];
        float x = MIC_CHANNELS == 0 ? (float)l : MIC_CHANNELS == 1 ? (float)r : 0.5f * (l + r);
        // one-pole DC blocker: MEMS mics sit a little off zero
        float y = x - hpX + 0.995f * hpY;
        hpX = x;
        hpY = y;
        if (y > 32767) y = 32767;
        if (y < -32768) y = -32768;
        s_mono[i] = (int16_t)y;
        sumSq += y * y;
      }
      if (s_micOn.load()) s_mic.write(s_mono, frames);
      float rms = sqrtf(sumSq / frames);
      micRms = rms;
      float lvl = log10f(1.0f + rms) / 4.0f;  // ~0 (silence) .. ~1 (shouting)
      micEnv = lvl > micEnv ? lvl : micEnv * 0.85f + lvl * 0.15f;
      s_micLevel = micEnv > 1 ? 1 : micEnv;
    } else {
      micEnv *= 0.8f;
      s_micLevel = micEnv;
    }

    // ---- out: speech + tones
    if (s_spkFlush.exchange(false)) {
      // drop whatever is queued (a stop, or a new utterance replacing the old one)
      s_spk.clear();
      speechStarted = false;
      playingId = -1;
      s_spkPos = 0;
    }
    int id = s_spkId.load();
    if (id != playingId) {
      speechStarted = false;
      playingId = id;
      s_spkPos = 0;
    }
    size_t speechN = 0;
    if (id >= 0) {
      if (!speechStarted && (s_spk.available() >= kPrebuffer || s_spkEnded.load())) speechStarted = true;
      if (speechStarted) {
        if (!ampOn) warm = kAmpWarmBlocks;
        if (warm == 0) {
          speechN = s_spk.read(s_speech, frames);
          s_spkPos += (uint32_t)speechN;
          if (speechN < (size_t)frames) {
            if (s_spkEnded.load()) {
              // played out: report it, and go quiet
              s_spkDone = id;
              s_spkId = -1;
              playingId = -1;
              speechStarted = false;
            } else {
              s_underruns++;
            }
          }
        }
      }
    }
    s_spkPlaying = id >= 0;

    Tone t;
    if (!tone.active && xQueueReceive(s_tones, &t, 0) == pdTRUE && s_soundOn.load()) {
      tone.start(t);
      if (!ampOn) warm = kAmpWarmBlocks;
    }

    bool audible = speechN > 0 || tone.active || speechStarted;
    if (audible) {
      if (!ampOn) {
        pinWrite(PIN_PA_EN, 1);
        ampOn = true;
      }
      lastSoundMs = millis();
    } else if (ampOn && millis() - lastSoundMs > kAmpOffAfterMs) {
      pinWrite(PIN_PA_EN, 0);
      ampOn = false;
    }

    float gain = s_spkGain.load();
    float sumSq = 0;
    for (int i = 0; i < frames; i++) {
      float v = 0;
      if (warm == 0) {
        if ((size_t)i < speechN) v += s_speech[i] * gain;
        // tones duck under speech so a tick doesn't spike over a sentence
        if (tone.active) v += tone.next() * 32767.0f * (speechN ? 0.4f : 1.0f);
      }
      if (v > 32767) v = 32767;
      if (v < -32768) v = -32768;
      int16_t s = (int16_t)v;
      s_tx[2 * i] = s;
      s_tx[2 * i + 1] = s;
      sumSq += v * v;
    }
    if (warm > 0) warm--;
    float lvl = log10f(1.0f + sqrtf(sumSq / frames)) / 4.0f;
    spkEnv = lvl > spkEnv ? lvl : spkEnv * 0.85f + lvl * 0.15f;
    s_spkLevel = spkEnv;

    if (armed) {
      Canceller& c = s_cancel;
      // What was played this block, into the delay line
      for (int i = 0; i < frames; i++) {
        c.line[c.pos] = s_tx[2 * i];
        c.pos = (c.pos + 1) % Canceller::kDelayCap;
      }
      // For measuring the delay: loudness of the mics and of what was played, per block
      if (c.envN < Canceller::kEnv) {
        c.envMic[c.envN] = micRms;
        c.envRef[c.envN] = sqrtf(sumSq / frames);
        c.envN++;
      }
      // The same block of mics, with what was played `delay` samples ago
      const int delay = s_echoDelay.load();
      for (int i = 0; i < frames; i++) {
        int at = c.pos - frames - delay + i;
        while (at < 0) at += Canceller::kDelayCap;
        refBlock[i] = c.line[at % Canceller::kDelayCap];
      }
      for (int i = 0; i < frames; i++) {
        c.mic[c.fill] = s_mono[i];
        c.ref[c.fill] = refBlock[i];
        if (++c.fill < c.chunk) continue;
        c.fill = 0;
        aec_process(c.aec, c.mic, c.ref, c.out);
        chunks++;

        // The cancelled audio is what's kept from before it noticed someone (sent as their first words)
        if (s_recent) {
          for (int k = 0; k < c.chunk; k++) s_recent[(s_recentPos + k) % kRecent] = c.out[k];
          s_recentPos = (s_recentPos + c.chunk) % kRecent;
          if (s_recentPos < (size_t)c.chunk) s_recentFull = true;
        }

        double rr = 0, ro = 0, rf = 0;
        for (int k = 0; k < c.chunk; k++) {
          rr += (double)c.mic[k] * c.mic[k];
          ro += (double)c.out[k] * c.out[k];
          rf += (double)c.ref[k] * c.ref[k];
        }
        const float raw = sqrtf(rr / c.chunk), left = sqrtf(ro / c.chunk), played = sqrtf(rf / c.chunk);
        refHist[refIdx] = played;
        refIdx = (refIdx + 1) % kRefChunks;
        float refMax = 0;
        for (float r : refHist) refMax = r > refMax ? r : refMax;
        if (refMax > 150) {
          // It's speaking: learn the most of its echo that gets through the canceller (not from
          // moments far above it, which are someone talking)
          const float q = left / refMax;
          if (residual < 0) residual = q;
          // (its first moments, while the canceller settles, fade from it within a couple of seconds)
          else if (learned < 30 || q < residual * 2.0f) residual = q > residual ? residual + (q - residual) * 0.25f : residual * 0.995f;
          learned++;
          sumRaw += rr;
          sumOut += ro;
          winRaw = winRaw * 0.95 + rr;
          winOut = winOut * 0.95 + ro;
        } else {
          noise = left < noise ? noise * 0.9f + left * 0.1f : noise * 0.995f + left * 0.005f;
        }
        const float echoLeft = residual > 0 ? residual * refMax : 0;
        const float need = std::max(echoLeft * 2.5f, noise * 3.0f) + 80.0f;
        const float ratio = left / need;
        // A second or so of its speech first (to know what's left of its echo); then about a
        // quarter second of mostly-loud sound is someone talking
        // ...and only while the canceller is doing its job (10 dB or more of its voice taken out
        // lately): settling after a restart, or out of line, its leftovers would look like someone
        const bool working = winOut > 0 && winRaw > winOut * 10.0;
        const bool loud = learned >= 30 && working && ratio > 1.0f;
        hot = loud ? hot + 1 : (hot >= 2 ? hot - 2 : 0);
        if (learned >= 30 && working && ratio > peak) peak = ratio;
        if (raw > secRaw) secRaw = raw;
        if (left > secOut) secOut = left;
        if (played > secRef) secRef = played;
        if (learned >= 30 && working && ratio > secRatio) secRatio = ratio;
        if (++secChunks >= 62) {
          hal::logLine("[barge] 1s: mics %.0f, after the canceller %.0f, played %.0f, closest %.2f", (double)secRaw,
                       (double)secOut, (double)secRef, (double)secRatio);
          secRaw = secOut = secRef = secRatio = 0;
          secChunks = 0;
        }
        if (hot >= 15) {
          hal::logLine("[barge] someone's talking: %.0f left after the canceller, echo left %.0f, noise %.0f", (double)left,
                       (double)echoLeft, (double)noise);
          s_bargeCount++;
          s_bargeHeard = true;
          s_bargeArmed = false;
          armed = false;
          break;
        }
      }
    }

    size_t wrote;
    i2s_channel_write(s_txCh, s_tx, frames * 2 * sizeof(int16_t), &wrote, 100);
  }
}

}  // namespace

bool audioOk() { return s_ok; }

TaskHandle_t s_audioTask = nullptr;

int audioDiag(char* buf, size_t len) {
  return snprintf(buf, len, "astack:%u barge:%lu audio:%d mic:%d micOn:%d lvl:%.2f spk:%d q:%u under:%u rounds:%u",
                  s_audioTask ? (unsigned)uxTaskGetStackHighWaterMark(s_audioTask) : 0u, (unsigned long)s_bargeCount.load(),
                  (int)s_ok, (int)s_micOk, (int)s_micOn.load(), (double)s_micLevel.load(), s_spkId.load(),
                  (unsigned)s_spk.available(), (unsigned)s_underruns, (unsigned)s_rounds);
}

bool audioBegin() {
  pinOutput(PIN_PA_EN, 0);
  if (!s_mic.init(MIC_RING_SAMPLES) || !s_spk.init(SPK_RING_SAMPLES)) {
    hal::logLine("[hal] audio: no memory for the audio buffers");
    return false;
  }

  // I2S first: the codecs want MCLK running while they're configured. Master, full duplex: one
  // controller, a TX channel to the ES8311 and an RX channel from the ES7210 on the same clocks.
  i2s_chan_config_t chan = I2S_CHANNEL_DEFAULT_CONFIG(I2S_NUM_0, I2S_ROLE_MASTER);
  chan.dma_desc_num = 6;
  chan.dma_frame_num = kBlock;
  chan.auto_clear = true;  // an underrun plays silence, not the last buffer on repeat
  if (i2s_new_channel(&chan, &s_txCh, &s_rxCh) != ESP_OK) {
    logLine("[hal] audio: I2S channels failed; sound is off");
    return false;
  }
  i2s_std_config_t std = {};
  std.clk_cfg = I2S_STD_CLK_DEFAULT_CONFIG(kRate);  // MCLK = 256 fs = 4.096 MHz
  std.clk_cfg.mclk_multiple = I2S_MCLK_MULTIPLE_256;
  std.slot_cfg = I2S_STD_PHILIPS_SLOT_DEFAULT_CONFIG(I2S_DATA_BIT_WIDTH_16BIT, I2S_SLOT_MODE_STEREO);
  std.gpio_cfg.mclk = (gpio_num_t)PIN_I2S_MCLK;
  std.gpio_cfg.bclk = (gpio_num_t)PIN_I2S_BCLK;
  std.gpio_cfg.ws = (gpio_num_t)PIN_I2S_LRCK;
  std.gpio_cfg.dout = (gpio_num_t)PIN_I2S_DOUT;
  std.gpio_cfg.din = (gpio_num_t)PIN_I2S_DIN;
  if (i2s_channel_init_std_mode(s_txCh, &std) != ESP_OK || i2s_channel_init_std_mode(s_rxCh, &std) != ESP_OK ||
      i2s_channel_enable(s_txCh) != ESP_OK || i2s_channel_enable(s_rxCh) != ESP_OK) {
    logLine("[hal] audio: I2S setup failed; sound is off");
    return false;
  }
  delay(5);

  if (!es8311Begin(kRate, kMclk)) {
    i2s_channel_disable(s_txCh);
    i2s_channel_disable(s_rxCh);
    i2s_del_channel(s_txCh);
    i2s_del_channel(s_rxCh);
    return false;
  }
  s_micOk = es7210Begin(s_micGainDb.load());
  const size_t freeBefore = heap_caps_get_free_size(MALLOC_CAP_INTERNAL);
  s_cancelOk = s_micOk && s_cancel.begin();
  hal::logLine("[hal] echo canceller: %s (internal RAM %u -> %u)", s_cancelOk ? aec_get_config_string(s_cancel.aec) : "not available",
               (unsigned)freeBefore, (unsigned)heap_caps_get_free_size(MALLOC_CAP_INTERNAL));
  s_micGainApplied = s_micGainDb.load();
  if (!s_micOk) hal::logLine("[hal] mic: not available; the puck can't listen");

  s_tones = xQueueCreate(8, sizeof(Tone));
  // Core 0 with the input task; priority above it so speech never stutters behind a touch burst
  xTaskCreatePinnedToCore(audioTask, "audio", 8192, nullptr, 6, &s_audioTask, 0);  // the echo canceller uses ~3 KB of it
  s_ok = true;
  return true;
}

}  // namespace detail

void beep(Tone t) {
  if (!detail::s_ok || !detail::s_soundOn.load()) return;
  // Never block the caller; a full queue just drops this one (ticks come in bursts).
  xQueueSend(detail::s_tones, &t, 0);
}

void setSoundEnabled(bool on) { detail::s_soundOn = on; }

// ---- mics

void micStart() {
  detail::s_mic.clear();
  detail::s_micOn = true;
}

void micStop() { detail::s_micOn = false; }

void bargeArm(bool on) {
  if (on && !detail::s_recent)
    detail::s_recent = (int16_t*)heap_caps_malloc(detail::kRecent * sizeof(int16_t), MALLOC_CAP_SPIRAM | MALLOC_CAP_8BIT);
  if (!on) detail::s_bargeHeard = false;
  detail::s_bargeArmed = on;
}

bool bargeTaken() { return detail::s_bargeHeard.exchange(false); }

void micStartWithPreroll() {
  detail::s_mic.clear();
  if (detail::s_recent) {
    // oldest first
    size_t n = detail::s_recentFull ? detail::kRecent : detail::s_recentPos;
    size_t from = detail::s_recentFull ? detail::s_recentPos : 0;
    for (size_t done = 0; done < n;) {
      size_t at = (from + done) % detail::kRecent;
      size_t chunk = std::min(n - done, detail::kRecent - at);
      detail::s_mic.write(detail::s_recent + at, chunk);
      done += chunk;
    }
  }
  detail::s_micOn = true;
}

bool micActive() { return detail::s_micOn.load() && detail::s_micOk; }

size_t micRead(int16_t* dst, size_t maxSamples) { return detail::s_mic.read(dst, maxSamples); }

float micLevel() { return detail::s_micLevel.load(); }

void setMicGain(int db) { detail::s_micGainDb = db < 0 ? 0 : db > 37 ? 37 : db; }

// ---- speaker

void speakerBegin(int id) {
  detail::s_spkFlush = true;    // the audio task drops what's queued (and reports the old one done)
  // give it a round to do so before the new audio arrives
  for (int i = 0; i < 4 && detail::s_spkFlush.load(); i++) vTaskDelay(pdMS_TO_TICKS(3));
  detail::s_spk.clear();
  detail::s_spkEnded = false;
  detail::s_spkId = id;
}

void speakerWrite(const int16_t* pcm, size_t samples) {
  if (detail::s_spkId.load() < 0) return;
  detail::s_spk.write(pcm, samples);
}

void speakerEnd(int id) {
  if (detail::s_spkId.load() == id) detail::s_spkEnded = true;
  else detail::s_spkDone = id;  // nothing of it is playing: done already
}

void speakerStop() {
  // No current utterance any more: frames still on their way are dropped (speakerWrite checks),
  // until the next speakerBegin. The stopped one counts as done.
  int id = detail::s_spkId.exchange(-1);
  detail::s_spkEnded = true;
  detail::s_spkFlush = true;
  if (id >= 0) detail::s_spkDone = id;
}

bool speakerPlaying() { return detail::s_spkPlaying.load(); }

uint32_t speakerPosition() { return detail::s_spkPos.load(); }

size_t speakerFree() { return detail::s_spk.cap - detail::s_spk.available(); }

int speakerTakeDone() { return detail::s_spkDone.exchange(-1); }

float speakerLevel() { return detail::s_spkLevel.load(); }

void setSpeakerVolume(int percent) {
  if (percent < 0) percent = 0;
  if (percent > 100) percent = 100;
  // perceptual curve: 70 % ≈ 0.63 of full scale
  detail::s_spkGain = powf(percent / 100.0f, 1.3f);
}

}  // namespace hal
