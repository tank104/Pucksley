#!/usr/bin/env bash
# Builds everything into dist/ (the Linux twin of build.ps1):
#   dist/pucksley/           the Linux hub (tray app, self-contained: no .NET install needed)
#   dist/firmware/           the puck firmware images (flash with idf.py or esptool)
#
#   ./scripts/build.sh                  # both
#   ./scripts/build.sh --hub-only
#   ./scripts/build.sh --firmware-only
#   ./scripts/build.sh --hub-only --install    # …and install it (replaces and restarts a running one)
#
# Needs: the .NET 10 SDK, and ESP-IDF 5.5 (default location ~/esp/esp-idf; --idf-path to say otherwise).
set -euo pipefail

hub=1
firmware=1
install=0
idf_path="${IDF_PATH:-$HOME/esp/esp-idf}"

while (($# > 0)); do
  case "$1" in
    --hub-only) firmware=0 ;;
    --firmware-only) hub=0 ;;
    --install) install=1 ;;
    --idf-path)
      (($# >= 2)) || { echo "--idf-path needs a folder" >&2; exit 2; }
      idf_path="$2"
      shift
      ;;
    -h | --help)
      sed -n '2,11p' "$0" | sed 's/^# \{0,1\}//'
      exit 0
      ;;
    *) echo "Unknown option: $1 (try --help)" >&2; exit 2 ;;
  esac
  shift
done

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
dist="$root/dist"
mkdir -p "$dist"

if ((hub)); then
  echo "== hub (Linux desktop app)"
  command -v dotnet >/dev/null || { echo "The .NET 10 SDK isn't installed (dotnet not found)." >&2; exit 1; }
  case "$(uname -m)" in
    x86_64) rid=linux-x64 ;;
    aarch64 | arm64) rid=linux-arm64 ;;
    *) echo "No build for $(uname -m) processors." >&2; exit 1 ;;
  esac
  out="$dist/pucksley"
  rm -rf "$out"
  # Self-contained: the ASP.NET Core runtime comes along from NuGet. AllowMissingPrunePackageData lets
  # that work with a distribution's SDK that leaves the ASP.NET targeting pack out (Arch's does,
  # unless aspnet-targeting-pack is installed too).
  dotnet publish "$root/hub/src/Hosts/Pucksley.Linux/Pucksley.Linux.csproj" -c Release -r "$rid" --self-contained \
    -p:AllowMissingPrunePackageData=true -o "$out"
  echo "   -> $out/pucksley"

  if ((install)); then
    # The installed copy lives in one place, so the launcher and the start at login always run the
    # latest build. Installing again stops the running hub, swaps the new build in and starts it again.
    home="${XDG_DATA_HOME:-$HOME/.local/share}/pucksley"
    unit='app-pucksley@autostart.service' # what systemd makes of the start at login
    autostart="${XDG_CONFIG_HOME:-$HOME/.config}/autostart/pucksley.desktop"
    echo "== installing to $home"
    have_systemd=0
    systemctl --user show-environment >/dev/null 2>&1 && have_systemd=1
    was_running=0
    if ((have_systemd)) && systemctl --user is-active --quiet "$unit"; then
      was_running=1
      systemctl --user stop "$unit"
    fi
    if pgrep -u "$(id -u)" -x pucksley >/dev/null; then
      was_running=1
      pkill -TERM -u "$(id -u)" -x pucksley || true
      for _ in $(seq 50); do pgrep -u "$(id -u)" -x pucksley >/dev/null || break; sleep 0.2; done
      if pgrep -u "$(id -u)" -x pucksley >/dev/null; then
        echo "Pucksley didn't stop: quit it from its tray icon and try again." >&2
        exit 1
      fi
    fi
    rm -rf "$home.new" "$home.old"
    mkdir -p "$home.new"
    cp -a "$out/." "$home.new/"
    [[ -d "$home" ]] && mv "$home" "$home.old"
    mv "$home.new" "$home"
    rm -rf "$home.old"
    "$home/pucksley" --install

    # Start it again (and on a first install with the start at login already on): through systemd
    # where the desktop session has it, so a crash brings it back, otherwise on its own.
    if ((was_running)) || [[ -f "$autostart" ]]; then
      if ((have_systemd)) && [[ -f "$autostart" ]] && systemctl --user start "$unit" 2>/dev/null; then
        echo "   Pucksley is running (systemd restarts it if it crashes)."
      else
        setsid -f "$home/pucksley" >/dev/null 2>&1 </dev/null
        echo "   Pucksley is running."
      fi
    else
      echo "   Start it from the app launcher, or: $home/pucksley"
      echo "   To start it with the desktop (and again after a crash): $home/pucksley --autostart on"
    fi
  fi
fi

if ((firmware)); then
  echo "== firmware (ESP-IDF)"
  [[ -f "$idf_path/export.sh" ]] || { echo "ESP-IDF isn't at $idf_path (pass --idf-path)." >&2; exit 1; }
  export IDF_PATH="$idf_path"
  # ESP-IDF's export script reads variables that may be unset
  set +u
  # shellcheck disable=SC1091
  . "$idf_path/export.sh" >/dev/null
  set -u
  command -v idf.py >/dev/null || { echo "ESP-IDF didn't load from $idf_path (pass --idf-path)." >&2; exit 1; }
  (
    cd "$root/firmware"
    idf.py build
    fw="$dist/firmware"
    mkdir -p "$fw"
    cp -f build/bootloader/bootloader.bin build/partition_table/partition-table.bin build/ota_data_initial.bin build/pucksley.bin build/flash_args "$fw/"
    echo "   -> $fw (flash: idf.py -p /dev/ttyACM0 flash, or esptool with flash_args)"
  )
fi
