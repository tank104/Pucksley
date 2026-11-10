# Face simulator

Runs the real faces (`main/ui`) and LVGL on the desktop with a pretend board and hub connection. A
script drives them through every state (Wi-Fi setup, approval, idle, listening, thinking, speaking,
long answers, errors, music, controls, timers, status, a yes/no question, the picker, a banner) and
saves what the glass would show: `out/NN_name.png` for each, and `out/sheet.png` with all of them.

```sh
cd firmware && idf.py build             # once, so ESP-IDF downloads LVGL into managed_components/
cd tools/sim && ./build.sh && ./build/sim.exe
```

On Windows, `build.sh` uses MSYS2's gcc (`C:\msys64\ucrt64\bin`, `pacman -S mingw-w64-ucrt-x86_64-gcc`)
from Git Bash. The script is `sim_main.cpp`; the pretend board and connection are `sim_board.cpp` (it
prints what the faces would send to the hub). Pass `-q` to keep it quiet.
