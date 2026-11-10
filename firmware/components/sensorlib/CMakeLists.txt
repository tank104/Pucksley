# SensorLib 0.3.3 by Lewis He (MIT), vendored: the touch (CST9217) and motion (QMI8658) drivers,
# on ESP-IDF's i2c_master bus.
file(GLOB TOUCH_SRCS "src/touch/*.cpp")
file(GLOB PLATFORM_SRCS "src/platform/*.cpp")
idf_component_register(
  SRCS "src/TouchDrvCSTXXX.cpp" "src/TouchDrvInterface.cpp" ${TOUCH_SRCS} ${PLATFORM_SRCS}
  INCLUDE_DIRS "src"
  REQUIRES esp_driver_i2c esp_driver_gpio esp_driver_spi esp_timer)
target_compile_definitions(${COMPONENT_LIB} PUBLIC CONFIG_SENSORLIB_ESP_IDF_NEW_API=1)
target_compile_options(${COMPONENT_LIB} PRIVATE -w)
