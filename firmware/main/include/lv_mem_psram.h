/* LVGL's objects, styles and texts in PSRAM. Every face is built at boot and hundreds of small
 * widgets add up; left to plain malloc they would all land in internal RAM (anything under 4 kB
 * does), which the Wi-Fi, the task stacks, the TLS of direct mode and the display's DMA buffers
 * need. The draw buffers themselves are allocated separately, in internal DMA memory. On the
 * desktop simulator this is just malloc. */
#ifndef LV_MEM_PSRAM_H
#define LV_MEM_PSRAM_H

#include <stdlib.h>
#ifdef ESP_PLATFORM
#include <esp_heap_caps.h>

static inline void* lv_psram_malloc(size_t n) {
  void* p = heap_caps_malloc(n, MALLOC_CAP_SPIRAM | MALLOC_CAP_8BIT);
  return p ? p : malloc(n);
}
static inline void* lv_psram_realloc(void* p, size_t n) {
  if (!p) return lv_psram_malloc(n);
  void* q = heap_caps_realloc(p, n, MALLOC_CAP_SPIRAM | MALLOC_CAP_8BIT);
  return q ? q : realloc(p, n);
}
static inline void lv_psram_free(void* p) { heap_caps_free(p); }
#else
#define lv_psram_malloc malloc
#define lv_psram_realloc realloc
#define lv_psram_free free
#endif

#endif
