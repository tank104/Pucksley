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
    # The same installer a release comes with: replaces and restarts a running hub
    "$root/scripts/install.sh" "$out"
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
