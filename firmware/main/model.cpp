#include "model.h"
#include "platform.h"
#include <string.h>
#include <atomic>

Model g_model;

static std::atomic<uint32_t> s_dirty{0};

void model_mark(uint32_t bits) { s_dirty.fetch_or(bits); }

uint32_t model_take_dirty() { return s_dirty.exchange(0); }

bool model_local_time(int* hour, int* minute, int* second) {
  if (!g_model.timeValid) return false;
  int64_t t = g_model.epochAtSync + (int64_t)(millis() - g_model.msAtSync) / 1000 + (int64_t)g_model.tzMin * 60;
  int64_t day = ((t % 86400) + 86400) % 86400;
  if (hour) *hour = (int)(day / 3600);
  if (minute) *minute = (int)(day / 60 % 60);
  if (second) *second = (int)(day % 60);
  return true;
}

bool model_local_date(int* year, int* month, int* day, int* weekday) {
  if (!g_model.timeValid) return false;
  int64_t t = g_model.epochAtSync + (int64_t)(millis() - g_model.msAtSync) / 1000 + (int64_t)g_model.tzMin * 60;
  int64_t days = t >= 0 ? t / 86400 : (t - 86399) / 86400;
  if (weekday) *weekday = (int)(((days % 7) + 7 + 4) % 7);  // 1970-01-01 was a Thursday
  // civil date from days since the epoch (Howard Hinnant's algorithm)
  int64_t z = days + 719468;
  int64_t era = (z >= 0 ? z : z - 146096) / 146097;
  int64_t doe = z - era * 146097;
  int64_t yoe = (doe - doe / 1460 + doe / 36524 - doe / 146096) / 365;
  int64_t y = yoe + era * 400;
  int64_t doy = doe - (365 * yoe + yoe / 4 - yoe / 100);
  int64_t mp = (5 * doy + 2) / 153;
  int64_t d = doy - (153 * mp + 2) / 5 + 1;
  int64_t m = mp < 10 ? mp + 3 : mp - 9;
  if (year) *year = (int)(y + (m <= 2));
  if (month) *month = (int)m;
  if (day) *day = (int)d;
  return true;
}

void model_copy(char* dst, size_t size, const char* src) {
  if (!size) return;
  if (!src) src = "";
  size_t n = strlen(src);
  if (n >= size) {
    n = size - 1;
    // Back off to the start of a UTF-8 character, so the faces never draw half of one.
    while (n > 0 && ((uint8_t)src[n] & 0xC0) == 0x80) n--;
  }
  memcpy(dst, src, n);
  dst[n] = 0;
}
