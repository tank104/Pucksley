#!/usr/bin/env bash
# Builds and runs the desktop tests for the firmware's plain logic (no board needed).
#   cd firmware/tools/test && ./run.sh
# Needs g++ (MSYS2 ucrt64 on Windows: pacman -S mingw-w64-ucrt-x86_64-gcc).
set -euo pipefail
cd "$(dirname "$0")"
[ -d /c/msys64/ucrt64/bin ] && export PATH=/c/msys64/ucrt64/bin:$PATH
mkdir -p build
g++ -std=gnu++17 -O1 -Wall -Wextra -I../../main -I../../managed_components/bblanchon__arduinojson/src ../../main/hub/direct_text.cpp direct_text_test.cpp -o build/direct_text_test.exe
./build/direct_text_test.exe
