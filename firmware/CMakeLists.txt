# Pucksley firmware (ESP-IDF 5.5): idf.py build / idf.py -p COMx flash monitor
cmake_minimum_required(VERSION 3.16)
include($ENV{IDF_PATH}/tools/cmake/project.cmake)
# LVGL takes its configuration from main/include/lv_conf.h (CONFIG_LV_CONF_SKIP=n), so every
# component (LVGL itself included) has to see that folder
idf_build_set_property(COMPILE_OPTIONS "-I${CMAKE_CURRENT_LIST_DIR}/main/include" APPEND)
project(pucksley)
