// The few Arduino-style helpers the code uses (millis, micros, delay), on ESP-IDF, or on the
// desktop face simulator (which owns the clock there). Included from C (LVGL's tick) and C++.
#pragma once
#include <stddef.h>
#include <stdint.h>

#ifdef ESP_PLATFORM
#include "esp_timer.h"
#include "freertos/FreeRTOS.h"
#include "freertos/task.h"
static inline uint32_t millis(void) { return (uint32_t)(esp_timer_get_time() / 1000); }
static inline uint32_t micros(void) { return (uint32_t)esp_timer_get_time(); }
static inline void delay(uint32_t ms) { vTaskDelay(pdMS_TO_TICKS(ms)); }
#else
#ifdef __cplusplus
extern "C" {
#endif
uint32_t millis(void);
#ifdef __cplusplus
}
#endif
#endif

#ifdef __cplusplus
template <class T>
constexpr T clampv(T v, T lo, T hi) { return v < lo ? lo : v > hi ? hi : v; }
#endif
