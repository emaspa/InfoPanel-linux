# InfoPanel Linux User Guide

InfoPanel turns hardware monitoring data into designable dashboards shown on desktop overlays, USB LCD panels and web browsers. This guide covers the Linux build.

## Installation

All packages are for x86_64 (amd64) and include the .NET runtime. Native system
libraries are still required; deb, rpm and AUR install these as dependencies.

### Ubuntu 26.04

Download `infopanel_<version>_amd64.deb` from
[GitHub Releases](https://github.com/emaspa/InfoPanel-linux/releases), then run:

```bash
sudo apt install ./infopanel_<version>_amd64.deb
```

### Fedora 44

Download `infopanel-<version>-1.x86_64.rpm` from the same release, then run:

```bash
sudo dnf install ./infopanel-<version>-1.x86_64.rpm
```

### Arch Linux (AUR)

Install the existing [infopanel-bin](https://aur.archlinux.org/packages/infopanel-bin)
package with an AUR helper:

```bash
yay -S infopanel-bin
```

Alternatively, clone `https://aur.archlinux.org/infopanel-bin.git`, review its
`PKGBUILD`, then run `makepkg -si` inside that directory as your normal user.

All three packages install in `/opt/infopanel`, with a launcher at
`/usr/bin/infopanel`, a desktop entry, icon and USB rules. Replug your panels
after installing. No group membership is required. For optional SMART sensors,
install `smartmontools` and run:

```bash
sudo systemctl enable --now infopanel-smart.timer
```

The timer is not enabled automatically by these packages. Video and RTSP items
need `ffmpeg` (`ffmpeg-free` on Fedora; supported codecs depend on that build).
Audio Spectrum needs PipeWire's PulseAudio server or PulseAudio and its
`parec`/`pactl` tools: `pulseaudio-utils` on Ubuntu/Fedora. Fedora calls the
PipeWire server package `pipewire-pulseaudio`; Ubuntu and Arch use `pipewire-pulse`.

### Tarball

1. Download the latest `infopanel-<version>-linux-x64.tar.gz` from [GitHub Releases](https://github.com/emaspa/InfoPanel-linux/releases). It includes .NET; the host still needs glibc, the GCC/C++ runtime, zlib, ICU, fontconfig, libX11, libICE and libSM.
2. Extract it and run the installer:
   ```bash
   tar xf infopanel-<version>-linux-x64.tar.gz
   cd infopanel-<version>-linux-x64
   ./install.sh
   ```
   The installer copies the app to `~/.local/opt/infopanel`, adds an `infopanel` launcher to `~/.local/bin`, creates a desktop entry, installs the udev rules for USB panel access (asks for sudo), and, if `smartmontools` is installed, sets up the systemd timer that feeds the SMART drive health sensors.
3. Replug your panel once (or reboot) so the udev rules apply, then run `infopanel`. No group membership is needed. Configuration is stored in `~/.local/share/InfoPanel/`.

Optional: enable "Start at login" in Settings to install an XDG autostart entry.

### Switching from the tarball to a system package

Quit InfoPanel and disable "Start at login" first, if enabled. Remove the old
tarball launcher (`~/.local/bin/infopanel`), desktop entry
(`~/.local/share/applications/infopanel.desktop`) and app directory
(`~/.local/opt/infopanel`) after confirming they belong to this installation.
If the tarball installed SMART units, stop its timer and remove its copies of
`/etc/systemd/system/infopanel-smart.service` and `infopanel-smart.timer`, then
run `sudo systemctl daemon-reload` and enable the packaged timer again. Also
remove the tarball's `/etc/udev/rules.d/99-infopanel.rules` after confirming the
package installed `/usr/lib/udev/rules.d/99-infopanel.rules`. Files in `/etc`
override package defaults in `/usr/lib`.

Keep `~/.local/share/InfoPanel/`; it contains your profiles and settings. Start
the packaged app, then enable "Start at login" again if wanted.

## Updates

InfoPanel checks GitHub Releases once at startup (a single anonymous request, no accounts or telemetry) and sends a desktop notification when a newer version is available. The About page then shows the release notes and a download link; it also has a "Check for updates" button for manual checks. Disable the startup check in Settings with "Check for updates at startup".

Quit InfoPanel before updating. On Ubuntu/Fedora, download the new deb/rpm and
repeat the installation command above; no apt/dnf repository is configured.
On Arch, update through your AUR helper (for example `yay -Syu`). For tarball
installations, extract the new tarball and run `./install.sh` again. Profiles and
settings are retained with each method. Use your existing installation method
when following an update notification.

## Dashboard

The app opens on the Dashboard: profile cards with live thumbnails, quick navigation, and community links. Each profile card has:

- **Active** toggle: shows or hides the profile's desktop overlay.
- **Profile settings** expander with a **Display** dropdown assigning the
  overlay to a monitor (or "Not assigned"), plus font and color options.
- **Duplicate / Delete** buttons and an **Import** option for `.infopanel` profile exports.

## Designer

The Designer is where profiles are built. Pick a profile from the top-left picker, then use **+ Add** to insert items: text, sensor values, clocks, calendars, images, bars, graphs, donuts, gauges, tables and shapes.

- Drag to move, use handles to resize, arrow keys nudge (Shift for 10 px).
- **Layers** panel: reorder (z-order), duplicate, delete.
- **Sensors** panel: pick a hardware or plugin sensor, then add it as a value, graph, bar, gauge or image. Plugin sensors that carry an image (like Audio Spectrum) become live image items.
- The inspector on the right edits every property of the selected item. With nothing selected it shows profile options: **Trigger programs** (see below) and **Display**, which assigns the profile's overlay to a monitor (dragging the overlay onto another screen updates it too).
- **Undo/Redo** cover every edit. Changes autosave about 2 seconds after you stop editing.
- **Restore** rolls the profile back to how it looked before this editing session began. Restoring again swaps back, so nothing is ever lost to a bad session.

When an older profile is opened, InfoPanel attempts to match its saved hardware bindings to the current sensors. Successful matches are saved automatically. If a binding is missing or ambiguous, its original ID is retained and the inspector shows **Unresolved sensor** with a **Replace Sensor** hint. Select the intended sensor in the Sensors panel and use Replace Sensor to repair the binding; this change supports Undo and Redo. Exports include the current layout and resolved bindings without saving the source profile as part of export.

## Sensors

The Sensors page lists everything InfoPanel can read on your system: CPU, GPU, memory, drives, network and any plugin-provided values. Values come from Linux hwmon/sysfs, Intel and AMD GPU interfaces, and NVMe SMART data.

Hardware bindings normally survive reboots even when Linux changes `hwmonN` or `thermal_zoneN` numbering. InfoPanel checks for hardware changes every 10 seconds, so a newly connected sensor may take that long to appear. Disconnected sensors show no current value and recover automatically when the same identifiable device returns. Devices with the same chip name appear separately using their hardware identity. Physical relocation or firmware changes can require rebinding for devices identified by location.

Disk and block I/O metrics under `system/...` use drive serials or WWIDs, and NVIDIA/AMD GPU metrics use UUID/PCI identity, so ordinary kernel device renumbering no longer changes their IDs. Disks without a serial or WWID retain a weak kernel-name fallback. Older disk/GPU bindings migrate when their current-boot alias identifies one sensor; these matches are logged as low confidence. Network interface names remain as-is (predictable names are usually stable). Intel GPU keys stay unchanged and select the card with the lowest PCI address. Plugin sensor IDs are unchanged. Exported profiles and sensor dumps can contain device serial numbers used in stable IDs.

## Plugins

The Plugins page manages bundled and third-party plugins. Plugins using the configuration framework get their own tile with a collapsible Configuration section; changes apply live and persist automatically. Each plugin can be enabled or disabled individually.

Bundled plugins include system info, drives, network, weather (OpenWeatherMap key required), MangoHud FPS, a stopwatch and Audio Spectrum, a real-time audio visualizer for the system output. Add its image from the Designer's sensor panel.

Third-party .NET plugins built for InfoPanel for Windows load as-is: drop the plugin folder into the `plugins` directory next to the executable, or use the import option on the Plugins page.

To keep idle cost low, a plugin whose sensors are not shown on any streaming panel, overlay or web view stops updating, and after 5 minutes stops completely; it restarts automatically within a second when one of its sensors is used again. While stopped, its sensors remain listed with their last values. The Sensors page and the designer always show everything live while open.

## Devices

The Devices page detects supported USB LCD panels: BeadaPanel, Turing Smart Screen (including Rev 4.6"), Thermalright (40+ models), Thermaltake / ASRock, JL / Hongtai, VMAX, Jonsbo (DS916, DS339) and Lian Li (Universal Screen 8.8"/9.2", HydroShift II). For each panel you can assign a profile, set rotation and brightness, and watch the live frame rate and latency. To stream, a panel needs its row's Enabled switch on and a profile assigned; each family also has a streaming master switch in its section header, which turns on automatically when a scan finds the family's first panel.

Some panels share one USB ID across many models (for example most Thermalright HID panels). The scan identifies the exact model by briefly talking to the panel; if that is not possible the row shows a placeholder model until the first connection, when the panel reports what it really is.

If a panel is not detected, confirm the udev rules are installed and the panel is listed by `lsusb`. See the README for the full supported model tables.

## Hotkeys

Configure global hotkeys on the Devices page to switch the profile shown on a panel, and to control the stopwatch plugin (start, stop, reset). On Wayland sessions hotkeys are grabbed through XWayland and fire while an X11 window (games, most apps) has focus.

## Program-specific profiles

Profiles can appear automatically while a specific application runs in the foreground:

1. In the Designer, select nothing and set **Trigger programs** to a comma-separated list of process names, e.g. `Cyberpunk2077.exe, retroarch`. The `.exe` suffix is optional; Proton and Wine games report their Windows executable names.
2. Enable **Program-specific profiles** in Settings. Optionally hide other overlays while a trigger profile is showing.

Profiles without trigger programs stay always-on while Active. On Wayland only X11/XWayland windows are observable, which covers games and most desktop apps.

## Web server

Enable the web server in Settings to view live profile renders from any browser on your network. The listen address, port and refresh rate are configurable; the settings page shows the URL.

## Troubleshooting

- **Panel not detected**: install udev rules, replug, check `lsusb` for the device id, and see the logs (About page opens the log folder).
- **Panel stopped responding** after a crash: unplug and replug it, or reset it with `usbreset <vid:pid>`.
- **Overlay not visible on Wayland**: overlays render through XWayland; make sure XWayland is available (it is on standard GNOME and KDE sessions).
- **Weather shows no data**: set the API key and city in the weather plugin's configuration on the Plugins page.
- **Sensor missing after a reboot or hardware change**: allow up to 10 seconds for discovery, then check the Sensors page and the selected item's binding status in the designer. InfoPanel will not choose arbitrarily between indistinguishable devices. Use **Replace Sensor** if needed. For diagnostics, run `infopanel --dump-sensors --verbose`: it prints canonical IDs, labels, availability, current legacy aliases and identity strength. Check the logs for the original ID and resolution result. Network names and weak disk fallbacks retain the naming limitations described above; physical GPU relocation can change PCI-based IDs.
- Logs live in `~/.local/share/InfoPanel/logs/`.

## Getting help

- [Discord](https://discord.gg/cQnjdMC7Qc)
- [Reddit r/InfoPanel](https://www.reddit.com/r/InfoPanel/)
- [GitHub issues](https://github.com/emaspa/InfoPanel-linux/issues)
