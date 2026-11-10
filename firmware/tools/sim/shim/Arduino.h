// Just enough of Arduino for the faces to run on a desktop: the simulator owns the clock.
#pragma once
#include <stdint.h>
#include <stdlib.h>
#ifdef __cplusplus
extern "C" {
#endif
uint32_t millis(void);
#ifdef __cplusplus
}
#endif
