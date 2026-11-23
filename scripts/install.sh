#!/usr/bin/env bash
# Installs the Pucksley hub for this user, in ~/.local/share/pucksley, and puts it in the app
# launcher. Running it again updates: it stops the running hub, swaps the new copy in and starts it
# again. Settings (~/.config/pucksley) aren't touched.
#
#   curl -fsSL https://raw.githubusercontent.com/tank104/Pucksley/main/scripts/install.sh | bash
#                                     # the latest release, downloaded from GitHub
#   ./install.sh                      # the copy this script came with (an unpacked release)
#   ./install.sh <folder>             # a build (scripts/build.sh --install does this)
set -euo pipefail

repo='tank104/Pucksley'

latest=0
src=''
case "${1:-}" in
  -h | --help)
    sed -n '2,9p' "${BASH_SOURCE[0]:-/dev/null}" | sed 's/^# \{0,1\}//'
    exit 0
    ;;
  --latest) latest=1 ;;
  '')
    # Piped in from curl there's no folder of our own: fetch the latest release
    here="$(dirname "${BASH_SOURCE[0]:-}")"
    if [[ -n "${BASH_SOURCE[0]:-}" && -x "$here/pucksley" ]]; then src="$(cd "$here" && pwd)"; else latest=1; fi
    ;;
  *) src="$1" ;;
esac

if ((latest)); then
  case "$(uname -m)" in
    x86_64) rid=linux-x64 ;;
    aarch64 | arm64) rid=linux-arm64 ;;
    *) echo "There's no Pucksley release for $(uname -m) processors." >&2; exit 1 ;;
  esac
  download="$(mktemp -d)"
  trap 'rm -rf "$download"' EXIT
  echo "== downloading the latest release ($rid)"
  curl -fSL --retry 3 "https://github.com/$repo/releases/latest/download/pucksley-$rid.tar.gz" | tar -xz -C "$download"
  src="$download/pucksley"
fi

[[ -x "$src/pucksley" ]] || { echo "There's no Pucksley in $src." >&2; exit 1; }
src="$(cd "$src" && pwd)"

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
  for _ in $(seq 75); do pgrep -u "$(id -u)" -x pucksley >/dev/null || break; sleep 0.2; done
  if pgrep -u "$(id -u)" -x pucksley >/dev/null; then
    echo "Pucksley didn't stop: quit it from its tray icon and try again." >&2
    exit 1
  fi
fi

# Copy beside it, then swap, so a half-copied hub never runs (and installing from the installed
# folder itself works too)
rm -rf "$home.new" "$home.old"
mkdir -p "$home.new"
cp -a "$src/." "$home.new/"
if [[ -d "$home" ]]; then mv "$home" "$home.old"; fi
mv "$home.new" "$home"
rm -rf "$home.old"
"$home/pucksley" --install >/dev/null
echo "   $("$home/pucksley" --version 2>/dev/null || echo '?') is in the app launcher"

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
  setsid -f "$home/pucksley" --open >/dev/null 2>&1 </dev/null
  echo "   Pucksley is running: its page is http://127.0.0.1:8765"
  echo "   To start it with the desktop (and again after a crash), tick Start at login in its tray menu."
fi
