file(GLOB_RECURSE SRCS "*.cpp" "*.c")
idf_component_register(
  SRCS ${SRCS}
  INCLUDE_DIRS "." "include"
  REQUIRES esp_driver_gpio esp_driver_i2c esp_driver_i2s esp_driver_spi esp_driver_usb_serial_jtag
           esp_wifi esp_netif esp_event nvs_flash esp_http_server esp_http_client esp_timer esp_system esp_ringbuf mbedtls lwip
           sensorlib xpowerslib)
target_compile_options(${COMPONENT_LIB} PRIVATE -Wno-unused-parameter -Wno-missing-field-initializers -Wno-format-truncation)
