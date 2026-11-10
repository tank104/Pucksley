// The two queues between the main loop and the net task: JSON lines in each direction, each
// prefixed with the transport it came from / should go to. ESP-IDF no-split ring buffers.
#include "../platform.h"
#include <freertos/FreeRTOS.h>
#include <freertos/ringbuf.h>
#include <string.h>
#include "internal.h"

namespace hub {
namespace detail {

namespace {
RingbufHandle_t s_in = nullptr;    // net task → main loop
RingbufHandle_t s_out = nullptr;   // main loop → net task

bool push(RingbufHandle_t rb, uint8_t tp, const char* data, size_t len) {
  if (!rb || len == 0) return false;
  void* item = nullptr;
  if (xRingbufferSendAcquire(rb, &item, len + 2, 0) != pdTRUE || !item) return false;
  auto* p = (char*)item;
  p[0] = (char)tp;
  memcpy(p + 1, data, len);
  p[len + 1] = 0;
  return xRingbufferSendComplete(rb, item) == pdTRUE;
}

char* take(RingbufHandle_t rb, uint8_t* tp, size_t* len) {
  if (!rb) return nullptr;
  size_t size = 0;
  auto* p = (char*)xRingbufferReceive(rb, &size, 0);
  if (!p) return nullptr;
  if (tp) *tp = (uint8_t)p[0];
  if (len) *len = size >= 2 ? size - 2 : 0;
  return p + 1;
}
}  // namespace

void queuesBegin() {
  s_in = xRingbufferCreate(24 * 1024, RINGBUF_TYPE_NOSPLIT);
  s_out = xRingbufferCreate(12 * 1024, RINGBUF_TYPE_NOSPLIT);
}

bool sendLine(const char* json, uint8_t tp) { return push(s_out, tp, json, strlen(json)); }

char* takeLine(uint8_t* tp, size_t* len) { return take(s_in, tp, len); }

void doneLine(char* line) {
  if (line) vRingbufferReturnItem(s_in, line - 1);
}

bool pushIncoming(uint8_t tp, const char* data, size_t len) { return push(s_in, tp, data, len); }

char* takeOutgoing(uint8_t* tp, size_t* len) { return take(s_out, tp, len); }

void doneOutgoing(char* line) {
  if (line) vRingbufferReturnItem(s_out, line - 1);
}

}  // namespace detail
}  // namespace hub
