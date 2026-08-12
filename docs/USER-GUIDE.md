# InfoPanel Linux User Guide

InfoPanel turns hardware monitoring data into designable dashboards shown on desktop overlays, USB LCD panels and web browsers. This guide covers the Linux build.

## Installation

1. Download the latest `infopanel-<version>-linux-x64.tar.gz` from [GitHub Releases](https://github.com/emaspa/InfoPanel-linux/releases). It is self-contained: no .NET runtime or other packages are needed.
2. Extract it and run the installer:
   ```bash
   tar xf infopanel-<version>-linux-x64.tar.gz
   cd infopanel-<version>-linux-x64
   ./install.sh
   ```
   The installer copies the app to `~/.local/opt/infopanel`, adds an `infopanel` launcher to `~/.local/bin`, creates a desktop entry, installs the udev rules for USB panel access (asks for sudo), and, if `smartmontools` is installed, sets up the systemd timer that feeds the SMART drive health sensors.
3. Make sure your user is in the `plugdev` group and replug your panel once so the udev rules apply, then run `infopanel`. Configuration is stored in `~/.local/share/InfoPanel/`.

Optional: enable "Start at login" in Settings to install an XDG autostart entry.

## Updates

InfoPanel checks GitHub Releases once at startup (a single anonymous request, no accounts or telemetry) and sends a desktop notification when a newer version is available. The About page then shows the release notes and a download link; it also has a "Check for updates" button for manual checks. Disable the startup check in Settings with "Check for updates at startup". Updating is the same as installing: extract the new tarball and run `./install.sh` again; your profiles and settings are untouched.

## Dashboard

The app opens on the Dashboard: profile cards with live thumbnails, quick navigation, and community links. Each profile card has:

- **Active** toggle: shows or hides the profile's desktop overlay.
- **Duplicate / Delete** buttons and an **Import** option for `.infopanel` profile exports.

## Designer

The Designer is where profiles are built. Pick a profile from the top-left picker, then use **+ Add** to insert items: text, sensor values, clocks, calendars, images, bars, graphs, donuts, gauges, tables and shapes.

- Drag to move, use handles to resize, arrow keys nudge (Shift for 10 px).
- **Layers** panel: reorder (z-order), duplicate, delete.
- **Sensors** panel: pick a hardware or plugin sensor, then add it as a value, graph, bar, gauge or image. Plugin sensors that carry an image (like Audio Spectrum) become live image items.
- The inspector on the right edits every property of the selected item. With nothing selected it shows profile options, including **Trigger programs** (see below).
- **Undo/Redo** cover every edit. Changes autosave about 2 seconds after you stop editing.
- **Restore** rolls the profile back to how it looked before this editing session began. Restoring again swaps back, so nothing is ever lost to a bad session.

## Sensors

The Sensors page lists everything InfoPanel can read on your system: CPU, GPU, memory, drives, network and any plugin-provided values. Values come from Linux hwmon/sysfs, Intel and AMD GPU interfaces, and NVMe SMART data.

## Plugins

The Plugins page manages bundled and third-party plugins. Plugins using the configuration framework get their own tile with a collapsible Configuration section; changes apply live and persist automatically. Each plugin can be enabled or disabled individually.

Bundled plugins include system info, drives, network, weather (OpenWeatherMap key required), MangoHud FPS, a stopwatch and Audio Spectrum, a real-time audio visualizer for the system output. Add its image from the Designer's sensor panel.

Third-party .NET plugins built for InfoPanel for Windows load as-is: drop the plugin folder into the `plugins` directory next to the executable, or use the import option on the Plugins page.

To keep idle cost low, a plugin whose sensors are not shown on any streaming panel, overlay or web view stops updating, and after 5 minutes stops completely; it restarts automatically within a second when one of its sensors is used again. While stopped, its sensors remain listed with their last values. The Sensors page and the designer always show everything live while open.

## Devices

The Devices page detects supported USB LCD panels: BeadaPanel, Turing Smart Screen (including Lian Li and Rev 4.6"), Thermalright (40+ models), Thermaltake / ASRock, JL / Hongtai, VMAX and Jonsbo (DS916, DS339). For each panel you can assign a profile, set rotation and brightness, and watch the live frame rate and latency. To stream, a panel needs its row's Enabled switch on and a profile assigned; each family also has a streaming master switch in its section header, which turns on automatically when a scan finds the family's first panel.

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
- Logs live in `~/.local/share/InfoPanel/logs/`.

## Getting help

- [Discord](https://discord.gg/cQnjdMC7Qc)
- [Reddit r/InfoPanel](https://www.reddit.com/r/InfoPanel/)
- [GitHub issues](https://github.com/emaspa/InfoPanel-linux/issues)
