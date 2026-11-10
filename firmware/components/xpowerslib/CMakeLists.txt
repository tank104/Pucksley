# XPowersLib 0.2.6 by Lewis He (MIT), vendored: the AXP2101 PMU, on ESP-IDF's i2c_master bus.
idf_component_register(SRCS "src/XPowersLibInterface.cpp" INCLUDE_DIRS "src" REQUIRES esp_driver_i2c)
target_compile_definitions(${COMPONENT_LIB} PUBLIC CONFIG_XPOWERS_ESP_IDF_NEW_API=1)
target_compile_options(${COMPONENT_LIB} PRIVATE -w)
