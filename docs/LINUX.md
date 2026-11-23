# Pucksley on a Linux desktop

The Linux desktop app (`hub/src/Hosts/Pucksley.Linux`) is the twin of the Windows tray app: it runs
the hub in the background with an icon in the system tray, and adds the plugins that control the PC
it runs on. Claude sees the same tools on both systems (`pc_volume`, `pc_open_app`, `pc_screenshot`
and the rest), so everything said about "this PC" in the README holds on Linux too.

For a NAS or a server, use the console hub or the Docker image instead: they have no desktop to
control.

## Running it

```bash
curl -fsSL https://raw.githubusercontent.com/tank104/Pucksley/main/scripts/install.sh | bash
```

installs (or updates to) the latest release in `~/.local/share/pucksley` and starts it; the page is
<http://127.0.0.1:8765>. It brings its own .NET. From source instead:

```bash
./scripts/build.sh --hub-only            # builds dist/pucksley
dist/pucksley/pucksley                   # runs it from there
./scripts/build.sh --hub-only --install  # …or installs it, as the line above does with a release
```

| Command | What it does |
|---|---|
| `pucksley` | Runs the hub with its tray icon. A second launch only opens the page. |
| `pucksley --open` | …and opens the page in the browser. |
| `pucksley --no-tray` | Runs without the tray icon. |
| `pucksley --install` | Adds Pucksley to the app launcher: a `.desktop` file and icons under `~/.local/share`. Run it again after moving the folder. |
| `pucksley --autostart on` / `off` | Starts it with the desktop session (`~/.config/autostart/pucksley.desktop`). The tray menu's *Start at login* does the same. Where the session starts such things through systemd (Omarchy, GNOME, KDE Plasma), systemd also starts it again if it crashes (`~/.config/systemd/user/app-pucksley@autostart.service.d/keep-running.conf`); *Quit* in the tray menu ends it for good. |
| `pucksley --uninstall` | Removes the launcher entry, the icons and the start at login. Settings stay. |

Installing again updates: `scripts/install.sh` (which both the curl line and `build.sh --install`
run, and which comes inside each release as `pucksley/install.sh`) stops the running hub, swaps the
new copy in and starts it again (through systemd when *Start at login* is on). So staying on the
latest is the curl line again, or from a checkout of `main`:

```bash
git pull --ff-only && ./scripts/build.sh --hub-only --install
```

Settings aren't in that folder, so an update keeps them. `systemctl --user status
app-pucksley@autostart.service` and `journalctl --user -u app-pucksley@autostart.service` show the
running hub and its log when it was started at login.

Settings live in `~/.config/pucksley` (`PUCKSLEY_DATA` to change that); the port is 8765
(`PUCKSLEY_PORT`). Pucks find the hub by mDNS, so the PC's firewall, if it has one, needs to let
port 8765 in. Omarchy turns ufw on with everything blocked, so there it's a must:
`sudo ufw allow from 192.168.0.0/16 to any port 8765 proto tcp` (your home network; mDNS itself
is already allowed). A puck on USB shows up as `/dev/ttyACM0`: your user needs
to be in the group that owns it (`uucp` on Arch and Omarchy, `dialout` on Debian, Ubuntu and Fedora).

The tray icon appears wherever the desktop shows StatusNotifierItems: KDE Plasma, Hyprland bars
(Omarchy's, Waybar), Sway bars, Xfce, Cinnamon, and GNOME once the *AppIndicator and
KStatusNotifierItem Support* extension is on. Without a tray the hub runs all the same, and the icon
appears if a tray turns up later (or comes back after the panel restarts).

### Moving from the Windows app

On the Windows PC, from a copy of this repository (or just the one file):

```powershell
powershell -ExecutionPolicy Bypass -File scripts\export-settings.ps1
```

It stops the hub there (and its start with Windows) and leaves `pucksley-settings.zip` on the
desktop: every setting, the plugins' data, and the keys and puck pairings that Windows keeps
encrypted for its own user, written so another computer can read them. On the Linux PC:

```bash
systemctl --user stop app-pucksley@autostart.service    # or Quit from the tray icon
mv ~/.config/pucksley ~/.config/pucksley.before && mkdir ~/.config/pucksley
cp ~/.config/pucksley.before/secret.key ~/.config/pucksley/ 2>/dev/null
unzip ~/Downloads/pucksley-settings.zip -d ~/.config/pucksley
systemctl --user start app-pucksley@autostart.service   # or start it from the launcher
```

The hub encrypts the keys again with its own key as it starts. The pucks keep their pairing: when
the old hub stops answering they look for it on the network and find this one. Delete the zip
afterwards.

Settings that name Windows programs come across as they were: Openspline's start command, for one,
is `wsl.exe …` on Windows. Install Openspline on the Linux PC with its own installer (it needs an
NVIDIA card) and set the plugin's start command to `sh ~/.local/share/openspline/run.sh` and its stop
command to `pkill -f openspline_server.native`.

## What it uses

Linux isn't one desktop, so the plugins lean on what nearly every desktop has, plus a few small
programs. Whatever is missing only switches off the tool that needs it, and Claude is told which
program to ask you to install.

| For | It uses | Package, where it isn't already there |
|---|---|---|
| Volume, outputs, each app's volume | `pactl` (PipeWire through pipewire-pulse, or PulseAudio) | `libpulse` (Arch), `pulseaudio-utils` (Debian, Ubuntu, Fedora) |
| What's playing; play, pause, skip | The desktop's media players on the session bus (MPRIS) | – |
| Opening apps | The `.desktop` files, started with `uwsm` (Omarchy), `gio` or `gtk-launch` | – |
| Opening files, folders, sites | `xdg-open` | `xdg-utils` |
| Lock | The desktop's own locker (see below), else `loginctl lock-session` | – |
| Sleep, shut down, restart | `systemctl`, or the desktop's own way to end the session | – |
| Screenshots | `grim` (Hyprland, Sway and the like), Spectacle (KDE), `gnome-screenshot`, the desktop portal, or `maim` / `scrot` (X11) | `grim`; `maim` |
| Clipboard | `wl-copy` / `wl-paste` (Wayland), `xclip` (X11) | `wl-clipboard`; `xclip` |
| Typing | `wtype` (Hyprland, Sway and the like), `xdotool` (X11), or `ydotool` | `wtype`; `xdotool` |
| Windows: listing, switching | `hyprctl`, `swaymsg` / `i3-msg`, or `wmctrl` (X11) | `wmctrl` |
| Dark mode | `gsettings` (the colour scheme GTK apps, browsers and the portal read), or Plasma's own | – |
| Brightness | A laptop's backlight (`brightnessctl`, or logind), or a monitor over DDC | `brightnessctl`; `ddcutil` |
| A voice with no account | `espeak-ng` | `espeak-ng` |

On Omarchy all of these are there already.

## What works where

| | Hyprland (Omarchy) | Sway, i3 | KDE Plasma | GNOME | Other X11 desktops |
|---|---|---|---|---|---|
| PC audio (volume, outputs, apps, media) | yes | yes | yes | yes | yes |
| Open apps, files, sites | yes | yes | yes | yes | yes |
| Lock, sleep, shut down | yes | yes | yes | yes | yes |
| Screenshot | yes | yes | yes (Spectacle) | whole screen (the portal asks once) | yes, with `maim` or `scrot` |
| Clipboard | yes | yes | yes | yes | yes |
| Typing | yes (`wtype`) | yes | with `ydotool` on Wayland; `xdotool` on X11 | with `ydotool` on Wayland; `xdotool` on X11 | yes (`xdotool`) |
| List and switch windows; "what's in front" | yes | yes | on X11 only | on X11 only | yes (`wmctrl`) |
| Show the desktop | an empty workspace | no | yes | on X11 only | yes |
| System info, dark mode, brightness | yes | yes | yes | yes | yes |

GNOME and KDE on Wayland don't let other programs see or switch their windows (by design, for
privacy), nor type into them without a helper running with extra rights (`ydotool`). The tools that
need those say so rather than failing oddly.

**Only the Hyprland column has been run** (on Omarchy). The others are written to each desktop's
documented interface and tested against recorded output; treat them as untried.

## Omarchy

Omarchy is where this was built, and a few of its own commands are used when they're there, because
they do the job more thoroughly than the generic way:

- **Lock** is `omarchy-system-lock` (it also locks the password manager and stops the screensaver).
- **Is it locked?** comes from Hyprland itself. Omarchy's lock screen doesn't set logind's flag, so
  the hub reads the same sign Omarchy's own scripts do.
- **Shut down and restart** go through `omarchy-system`, which closes the apps first.
- **Brightness** is `omarchy-brightness-display`, which knows laptop panels, desktop monitors (DDC)
  and Apple displays, and shows the usual on-screen bar.
- **Volume** follows the sound through Omarchy's speaker tuning (or EasyEffects) to the speakers
  behind it, as the volume keys do, and switching output leaves the tuning's own plumbing alone.
- **Apps** start through `uwsm-app`, so each gets its own place in the session like one started
  from the launcher. Windows are controlled with Hyprland's Lua commands (0.55 and later), falling
  back to the older ones.

## If something's off

- **No tray icon.** On GNOME, install and turn on the AppIndicator extension. On a bar you set up
  yourself, add its tray module. The hub runs regardless: open <http://127.0.0.1:8765>.
- **"Linux audio isn't responding".** There's no sound server for this user (`pactl info` says the
  same), or the hub was started outside the desktop session.
- **PC tools say there's no desktop session.** The hub was started from a console, over SSH or as a
  system service. Start it from the desktop (the launcher, or *Start at login*).
- **An app Pucksley opened closes when Pucksley quits.** It shouldn't: apps are started in their own
  systemd scope. On a system without systemd they share the hub's fate when it's stopped as a group.
- **The hub won't start: the port is in use.** Another copy is running under another user, or
  something else has 8765. `PUCKSLEY_PORT=8766 pucksley` picks another (re-pair the pucks, which
  remember the address).

## How it's put together

The plugins that control a PC come in three projects:

| Project | What's in it |
|---|---|
| `Pucksley.Plugins.Pc` | What Windows and Linux share: the two plugins' tools (`PcAudioPluginBase`, `PcControlPluginBase`: what Claude sees, the checks, the wording of every answer), the app index, name matching for apps, windows and outputs, the rules for what "open …" may open, and small helpers. |
| `Pucksley.Plugins.Windows` | Windows behind those tools: Core Audio, the media sessions, the shell, Win32. |
| `Pucksley.Plugins.Linux` | Linux behind those tools: `pactl`, MPRIS, the compositors, logind, the portal. |

Each system derives a plugin from the shared base and hands it its half through two interfaces:
`IPcAudio` and `IPcMedia` for sound, `IPcDesktop` for everything else. A new tool, or a change to
what one says, is written once in `Pucksley.Plugins.Pc` and each system implements the one method it
needs. Where a system needs other words for a tool ("the trash" for "the recycle bin"), it gives
them in `IPcDesktop.ToolDescriptions`.

Inside the Linux half, `DesktopSession` works out which desktop the hub is in, and `IWindowManager`
has one implementation per way of reaching windows (`HyprlandWindows`, `SwayWindows`, `X11Windows`,
and `ClosedWindows` for desktops that don't allow it). Supporting another compositor means adding
one of those.

The hosts share what they can too: finding the plugin assemblies, the status line and the "a new
puck wants to join" notice are in `Pucksley.Core` (`DesktopHost`), used by the Windows tray app, the
Linux app and the console alike. The tray icon itself is each system's own: on Linux it's the
StatusNotifierItem protocol and its menu, spoken directly over D-Bus.

Tests: `Pucksley.Plugins.Pc.Tests` (the shared half, against a mixer, media sessions and a desktop
in memory; runs on both systems), `Pucksley.Plugins.Linux.Tests` (parsers and plans against recorded
output, plus a *Live* category that only reads this PC's state) and `Pucksley.Linux.Tests` (the tray
menu and the files the app writes).
