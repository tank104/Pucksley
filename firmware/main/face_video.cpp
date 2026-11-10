// The talking face (see face_video.h).
//
// The WebSocket task copies each frame into one of a few PSRAM slots and queues it; the player task
// (core 1, beside the UI, which sleeps while the face has the screen) waits until the speaker
// reaches the frame's timestamp, skips frames it's too late for, and draws: esp_new_jpeg decodes a
// block (one MCU row, 8 or 16 lines) into one of LVGL's idle draw buffers; the centre 464 columns
// are copied into a strip (outside the circle cleared) in the other one, which
// goes out to the panel over DMA while the next block decodes.
#include "face_video.h"

#include <atomic>
#include <math.h>
#include <string.h>
#include <esp_heap_caps.h>
#include <esp_timer.h>
#include <freertos/FreeRTOS.h>
#include <freertos/queue.h>
#include <freertos/task.h>

#include "esp_jpeg_dec.h"
#include "config.h"
#include "hal.h"
#include "hal/hal_internal.h"
#include "platform.h"

namespace face_video {
namespace {

constexpr int W = 464, H = 464;            // what's drawn (multiples of 8); the panel's last 2 px stay black
constexpr int kMaxSrcW = 512;              // the decode block buffer fits a 512-wide image
constexpr int kSlots = 24;                // the hub runs about 0.7 s ahead of the speaker: ~18 frames in flight
constexpr size_t kSlotBytes = 48 * 1024;
constexpr uint32_t kEarly = AUDIO_RATE * 30 / 1000;   // start decoding 30 ms before the frame is due
constexpr uint32_t kLate = AUDIO_RATE * 80 / 1000;    // more than this behind, with a newer frame waiting: skip it
constexpr int64_t kIdleUs = 1500000;       // no frames and nothing playing for this long: give the screen back

enum State : int { Idle, WantScreen, HaveScreen, Done };
std::atomic<int> s_state{Idle};
std::atomic<bool> s_stop{false}, s_ending{false};

struct Slot {
  uint8_t* data;
  size_t len;
};
Slot s_slots[kSlots];
QueueHandle_t s_free = nullptr, s_ready = nullptr;  // slot indices
int s_filling = -1;
size_t s_fillTotal = 0, s_fillLen = 0;

int16_t s_x0[H], s_x1[H];  // the circle: each row's first and last pixel inside it
uint16_t* s_block = nullptr;
uint16_t* s_strip[2] = {};
int s_cur = 0;
jpeg_dec_handle_t s_dec = nullptr;

uint32_t u32(const uint8_t* p) { return p[0] | (p[1] << 8) | (p[2] << 16) | ((uint32_t)p[3] << 24); }
uint32_t ptsOf(int slot) { return u32(s_slots[slot].data + 4); }

uint16_t* aligned16(void* p) { return reinterpret_cast<uint16_t*>(((uintptr_t)p + 15) & ~(uintptr_t)15); }

// ---- the screen

bool takeScreen() {
  // LVGL's draw buffers are idle while we have the panel: one for decoded blocks, one split in two strips
  size_t a = 0, b = 0;
  void* bufA = hal::detail::lvglDrawBuffer(0, &a);
  void* bufB = hal::detail::lvglDrawBuffer(1, &b);
  if (!bufA || !bufB || a < (size_t)kMaxSrcW * 16 * 2 + 16 || b < (size_t)W * 16 * 2 * 2 + 16) {
    hal::logLine("[face] draw buffers too small");
    return false;
  }
  s_block = aligned16(bufA);
  s_strip[0] = aligned16(bufB);
  s_strip[1] = s_strip[0] + W * 16;
  jpeg_dec_config_t cfg = DEFAULT_JPEG_DEC_CONFIG();
  cfg.output_type = JPEG_PIXEL_FORMAT_RGB565_BE;
  cfg.block_enable = true;
  if (jpeg_dec_open(&cfg, &s_dec) != JPEG_ERR_OK) {
    s_dec = nullptr;
    return false;
  }
  s_ending = false;
  s_state = WantScreen;
  while (s_state == WantScreen) vTaskDelay(pdMS_TO_TICKS(2));
  // Black everywhere first: the panel's last two rows and columns aren't redrawn by the frames.
  // Full-width bands of 8 rows fit in one strip.
  memset(s_strip[0], 0, (size_t)LCD_W * 8 * 2);
  for (int y = 0; y < LCD_H; y += 8) {
    const int y2 = y + 7 < LCD_H ? y + 7 : LCD_H - 1;
    hal::detail::lcdSetWindow(0, y, LCD_W - 1, y2);
    hal::detail::lcdQueuePixels(s_strip[0], (size_t)LCD_W * (y2 - y + 1) * 2, nullptr, nullptr);
  }
  hal::detail::lcdDrain();
  return true;
}

void giveScreenBack() {
  hal::detail::lcdDrain();
  if (s_dec) jpeg_dec_close(s_dec);
  s_dec = nullptr;
  s_block = nullptr;
  s_strip[0] = s_strip[1] = nullptr;
  s_state = Done;  // the UI redraws the faces
}

bool draw(const uint8_t* jpeg, size_t len) {
  jpeg_dec_io_t io = {};
  io.inbuf = const_cast<uint8_t*>(jpeg);
  io.inbuf_len = (int)len;
  jpeg_dec_header_info_t info;
  if (jpeg_dec_parse_header(s_dec, &io, &info) != JPEG_ERR_OK) return false;
  const int w = info.width, h = info.height;
  if (w < W || h < H || w > kMaxSrcW || w % 8 || h % 8) return false;
  const int offX = (w - W) / 2, offY = ((h - H) / 2) & ~1;
  int blocks = 0;
  if (jpeg_dec_get_process_count(s_dec, &blocks) != JPEG_ERR_OK) return false;
  int srcY = 0;
  for (int b = 0; b < blocks && srcY < offY + H; b++) {
    io.outbuf = reinterpret_cast<uint8_t*>(s_block);
    if (jpeg_dec_process(s_dec, &io) != JPEG_ERR_OK) return false;
    const int rows = io.out_size / (w * 2);
    const int first = srcY > offY ? srcY : offY;
    const int last = (srcY + rows < offY + H ? srcY + rows : offY + H) - 1;
    if (first <= last) {
      uint16_t* strip = s_strip[s_cur];
      for (int r = first; r <= last; r++) {
        const int sy = r - offY;
        const uint16_t* src = s_block + (size_t)(r - srcY) * w + offX;
        uint16_t* dst = strip + (size_t)(r - first) * W;
        const int x0 = s_x0[sy], x1 = s_x1[sy];
        if (x0 > 0) memset(dst, 0, (size_t)x0 * 2);
        memcpy(dst + x0, src + x0, (size_t)(x1 - x0 + 1) * 2);
        if (x1 < W - 1) memset(dst + x1 + 1, 0, (size_t)(W - 1 - x1) * 2);
      }
      const int sy0 = first - offY, n = last - first + 1;
      hal::detail::lcdSetWindow(0, sy0, W - 1, sy0 + n - 1);  // waits for the strip before
      hal::detail::lcdQueuePixels(strip, (size_t)W * n * 2, nullptr, nullptr);
      s_cur ^= 1;
    }
    srcY += rows;
  }
  return true;
}

void release(int slot) { xQueueSend(s_free, &slot, 0); }

void flushReady() {
  int i;
  while (xQueueReceive(s_ready, &i, 0) == pdTRUE) release(i);
}

// ---- the player

void task(void*) {
  bool showing = false;
  int64_t lastFrameUs = 0;
  uint32_t shown = 0, skipped = 0, bad = 0;
  int64_t startUs = 0, busyUs = 0;
  for (;;) {
    if (s_stop.exchange(false)) {
      flushReady();
      if (showing) giveScreenBack();
      showing = false;
    }
    int i;
    if (xQueueReceive(s_ready, &i, pdMS_TO_TICKS(20)) != pdTRUE) {
      if (showing) {
        int64_t now = esp_timer_get_time();
        bool quiet = !hal::speakerPlaying();
        if ((s_ending && quiet) || (quiet && now - lastFrameUs > kIdleUs) || now - lastFrameUs > 4 * kIdleUs) {
          giveScreenBack();
          showing = false;
          float secs = (now - startUs) / 1e6f;
          hal::logLine("[face] %lu frames in %.1f s (%.1f fps), %lu skipped, %lu bad, %.0f ms a frame",
                       (unsigned long)shown, secs, secs > 0 ? shown / secs : 0.0f, (unsigned long)skipped,
                       (unsigned long)bad, shown ? busyUs / 1000.0f / shown : 0.0f);
        }
      }
      continue;
    }
    if (!showing) {
      if (!takeScreen()) {
        release(i);
        flushReady();
        continue;
      }
      showing = true;
      shown = skipped = bad = 0;
      busyUs = 0;
      startUs = esp_timer_get_time();
    }
    lastFrameUs = esp_timer_get_time();
    const uint32_t pts = ptsOf(i);
    // Wait for its audio (bounded: if the speaker stalls, show it anyway)
    const int64_t waitFrom = esp_timer_get_time();
    while (!s_stop.load() && hal::speakerPosition() + kEarly < pts && esp_timer_get_time() - waitFrom < 500000)
      vTaskDelay(1);
    if (s_stop.load()) {
      release(i);
      continue;
    }
    // Too late for it, and a newer one is waiting: skip to that
    int next;
    if (hal::speakerPosition() > pts + kLate && xQueuePeek(s_ready, &next, 0) == pdTRUE && ptsOf(next) <= hal::speakerPosition()) {
      skipped++;
      release(i);
      continue;
    }
    const int64_t a = esp_timer_get_time();
    if (draw(s_slots[i].data + 8, s_slots[i].len - 8)) shown++;
    else bad++;
    busyUs += esp_timer_get_time() - a;
    release(i);
  }
}

}  // namespace

void begin() {
  s_free = xQueueCreate(kSlots, sizeof(int));
  s_ready = xQueueCreate(kSlots, sizeof(int));
  for (int i = 0; i < kSlots; i++) {
    s_slots[i].data = (uint8_t*)heap_caps_malloc(kSlotBytes, MALLOC_CAP_SPIRAM | MALLOC_CAP_8BIT);
    if (s_slots[i].data) xQueueSend(s_free, &i, 0);
  }
  const float c = W / 2.0f;
  for (int y = 0; y < H; y++) {
    float dy = y + 0.5f - c, half = sqrtf(fmaxf(0.0f, c * c - dy * dy));
    int x0 = (int)ceilf(c - half - 0.5f), x1 = (int)floorf(c + half - 0.5f);
    s_x0[y] = (int16_t)(x0 < 0 ? 0 : x0);
    s_x1[y] = (int16_t)(x1 > W - 1 ? W - 1 : x1);
  }
  xTaskCreatePinnedToCore(task, "face", 8192, nullptr, 3, nullptr, 1);
}

bool frameStart(size_t total) {
  s_filling = -1;
  if (!s_free || total < 16 || total > kSlotBytes) return false;
  int i;
  // No free slot: the oldest waiting frame is the one to lose
  if (xQueueReceive(s_free, &i, 0) != pdTRUE && xQueueReceive(s_ready, &i, 0) != pdTRUE) return false;
  s_filling = i;
  s_fillTotal = total;
  s_fillLen = 0;
  return true;
}

void frameData(const uint8_t* data, size_t len) {
  if (s_filling < 0) return;
  Slot& s = s_slots[s_filling];
  if (s_fillLen + len > s_fillTotal) {
    release(s_filling);
    s_filling = -1;
    return;
  }
  memcpy(s.data + s_fillLen, data, len);
  s_fillLen += len;
  if (s_fillLen == s_fillTotal) {
    s.len = s_fillTotal;
    xQueueSend(s_ready, &s_filling, 0);
    s_filling = -1;
  }
}

void end() { s_ending = true; }

void stop() { s_stop = true; }

bool screenTaken() {
  switch (s_state.load()) {
    case WantScreen:
      hal::detail::lcdDrain();  // LVGL's last flush is out
      if (!hal::displayOn()) hal::setDisplayOn(true);
      s_state = HaveScreen;
      return true;
    case HaveScreen:
      return true;
    case Done:
      hal::detail::lvglRedrawNow();
      s_state = Idle;
      return false;
    default:
      return false;
  }
}

}  // namespace face_video
