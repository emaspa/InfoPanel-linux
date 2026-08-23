# InfoPanel for Linux

Hardware monitoring dashboards for desktop overlays, USB LCD panels and web
browsers, rebuilt natively for Linux on .NET 10 and Avalonia 12. Based on
[InfoPanel](https://github.com/habibrehmansg/infopanel) and its
[Thermalright-enabled fork](https://github.com/emaspa/infopanel-1), with full
two-way compatibility for profiles, settings and plugins.

New here? Start with the [User Guide](docs/USER-GUIDE.md).

## Contents

- [Features](#features)
- [Architecture](#architecture)
- [Supported USB panels](#supported-usb-panels)
- [Windows interoperability](#windows-interoperability)
- [Installing](#installing)
- [Building from source](#building-from-source)
- [Data and paths](#data-and-paths)
- [Command line](#command-line)
- [Credits](#credits)
- [License](#license)

## Features

### Designer

A direct-manipulation profile editor: zoom/pan canvas with vector-crisp
scaling, drag with grid snapping, resize handles, marquee multi-select,
layers panel with z-order control, and a contextual inspector for every
property of the selected item. A live sensor tree adds any reading as a
value, graph, bar, gauge, table or image with a double click.

Fifteen display item types are supported: text, sensor value, clock,
calendar, image (file, URL or RTSP/video stream), sensor-driven image,
URL image, bar, graph, donut, gauge, sensor table, shape, group and guide
line. Gauges support custom image frames with smooth crossfade, mirroring
and live preview; charts support auto-ranging, corner radius and glow
effects; sensor values support thousands separators, unit overrides and
current/min/max/average reading modes.

Editing is protected end to end: full undo/redo, autosave about 2 seconds
after the last change, and a per-profile session backup taken before the
first change of each app run. The Restore button swaps the current layout
with that backup, and restoring twice toggles between the two states, so a
bad editing session is never fatal.

### Outputs

- **Desktop overlays**: transparent, repositionable windows rendered
  through X11/XWayland, one per active profile. Each profile can be
  assigned to a specific monitor from the designer, or simply dragged
  where it should live.
- **USB LCD panels**: eight device families with per-device profile
  assignment, rotation, brightness, and live frame rate and latency
  readouts. Devices are supervised: they self-heal their USB binding after
  a replug and back off cleanly when unplugged.
- **Web server**: a built-in ASP.NET Core server lists profiles at `/`,
  serves a live viewer page per profile and streams the rendered image at
  `/{profile-id}/image` for browsers, wall tablets or OBS overlays.

### Sensors

Native Linux providers, no kernel modules or vendor daemons required:

- **hwmon/sysfs**: CPU temperatures, voltages, fans, power, NVMe and SATA
  drive temperatures, and everything else the kernel exposes.
- **Intel GPU**: frequency via sysfs plus engine utilization via PMU perf
  events.
- **AMD GPU**: ROCm SMI (usage, clocks, VRAM, power, temperature).
- **NVIDIA GPU**: NVML + NvAPI (usage, clocks, VRAM, power draw and
  limit, GPU/hotspot/VRAM temperatures, core voltage, performance state,
  throttling flags, fan RPM). Hotspot, VRAM temperature and voltage need
  the proprietary driver 525+; on RTX 50 series the hotspot additionally
  requires root and is omitted otherwise.
- **Drive health**: SMART data (health, wear, spare, power-on hours, data
  written) collected by a root systemd timer into `/run/infopanel/smart.json`
  and read by the bundled plugin without elevating the app.

### Plugins

The .NET plugin system is binary-compatible with InfoPanel for Windows:
existing plugin assemblies load unchanged. The Plugins page manages each
module individually with enable/disable toggles and per-plugin reload.

- **Configuration framework**: `IPluginConfigurable` plugins get an
  auto-generated settings UI (text, numeric, toggle and choice editors)
  with host-managed persistence in `plugins/<id>.config.json`. Changes
  apply live.
- **Plugin-rendered images**: the `InfoPanel.Plugins.Graphics` contract
  lets plugins draw into shared image buffers. Each image appears in the
  sensor tree as a `plugin-image://` entry and can be placed on any
  profile as a live image item.
- **Bundled**: the Extras superpack (system info, clock, network, drives,
  volume, weather via OpenWeatherMap, MangoHud FPS, SMART drive health),
  Audio Spectrum (real-time system-audio visualizer via
  PulseAudio/PipeWire) and a stopwatch with global hotkeys.

### Automation

- **Profile hotkeys**: system-wide shortcuts (X11 `XGrabKey`) switch any
  panel to any profile, plus stopwatch start/stop/reset bindings,
  configured on the Devices page.
- **Program-specific profiles**: overlays that appear automatically while
  a chosen application is in the foreground, with an optional rule to hide
  all other overlays meanwhile. Detection reads `_NET_ACTIVE_WINDOW` with
  an XRes client-pid fallback; Proton and Wine games report their Windows
  executable names (e.g. `Cyberpunk2077.exe`), so trigger lists carry over
  from Windows setups unchanged.
- **Refreshing URL images**: image items pointing at a URL can re-download
  on a per-item interval (webcams, rendered dashboards, weather radar),
  swapping frames in the background without blocking rendering.
- **Update notifications**: a startup check against GitHub Releases (no
  accounts, no telemetry, one anonymous request) sends a desktop
  notification when a new version is out; the About page shows what
  changed and links the download, and has a manual check button. Can be
  turned off in Settings.

Wayland note: hotkey grabs and foreground detection go through XWayland,
so they act on X11 windows (games and most apps). On plain X11 sessions
both are fully global.

### Headless mode

`infopanel --headless` runs sensors, panels and the web server with no UI,
for kiosk or server use. `--render-once <dir>` renders every profile to
PNG and exits; `--dump-sensors` prints all live sensor readings.

## Architecture

Single process, layered so that nothing below the UI references Avalonia:

```
InfoPanel.App (Avalonia 12 UI, tray, headless CLI)
   ├─ InfoPanel.Web             ASP.NET Core preview server
   ├─ InfoPanel.Devices.*       one project per USB panel family
   │    └─ InfoPanel.Devices    supervisor/worker framework, frame mailbox
   ├─ InfoPanel.Sensors(.Linux) hwmon/GPU monitors, plugin monitor
   ├─ InfoPanel.Rendering       SkiaSharp pipeline: PanelDraw, graphs,
   │                            image cache, font cache
   ├─ InfoPanel.Platform(.Linux) OS seams: SG_IO SCSI, autostart, X11
   │                            hotkeys, foreground app detection
   └─ InfoPanel.Core            models, XML persistence, stores (UI-free)

InfoPanel.Plugins (net8.0 SDK) + Plugins.Loader + Plugins.Graphics
```

Key design points:

- **Render pipeline**: each profile renders once per tick into a shared,
  reused buffer that every consumer (panels, web viewer) reads from, with
  a content-version check so unchanged frames skip the resize, encode and
  USB transfer entirely (panels still receive full-cadence frames from the
  cached payload). Text layouts and font lookups are cached while
  unchanged, which is what sustains full frame rate on 1920-wide panels
  at a few percent of CPU.
- **Demand-driven sensing**: only sensors referenced by a profile that is
  actually being consumed are polled each second, and a plugin whose
  sensors are unused for a few minutes stops completely (audio capture,
  network fetches and worker threads released), restarting within a
  second of demand returning. The Sensors page and designer always see
  the full live catalog while open.
- **Device supervision**: each panel runs a supervised worker with its own
  lifecycle (present, starting, streaming, faulted, cooldown), exponential
  backoff and a bounded frame mailbox, so one wedged device never stalls
  the others.
- **Platform seams**: OS specifics sit behind interfaces in
  `InfoPanel.Platform` (`IScsiTransport`, `IGlobalHotkeyService`,
  `IForegroundAppService`, `IAutostartService`), with Linux
  implementations in `InfoPanel.Platform.Linux`. Windows backends can slot
  in later without touching the app.
- **Plugin isolation**: each plugin package loads in its own collectible
  `AssemblyLoadContext`; the SDK stays net8.0 so assemblies built against
  the Windows app load on the net10 host unchanged. Shared contracts
  (`InfoPanel.Plugins.Graphics`, SkiaSharp) resolve to the host copies for
  type identity.

## Supported USB panels

Panels are auto-detected when plugged in (udev rules required, see
[Installing](#installing)). Models marked ✓ have been verified on real
hardware under Linux; the rest use the same protocol implementations as the
Windows build and are expected to work - reports welcome.

### Thermalright (TRCC family)

**Trofeo / HID protocol** - USB `0416:5302`, `0418:5303`, `0418:5304`:

| Model | Resolution |
|---|---|
| Trofeo Vision 6.86" ✓ | 1280×480 |
| Trofeo Vision 1600×720 | 1600×720 |
| Trofeo Vision 960×540 | 960×540 |
| Trofeo Vision 800×480 | 800×480 |
| Trofeo Vision 320×320 | 320×320 |
| Elite Vision 9.16" | 1920×462 |
| Elite Vision / LF14 | 320×320 |
| Frozen Warframe SE / LM26 | 240×320 |
| Frozen Warframe (SPI and JPEG variants) | 240×320 / 320×320 |
| Frozen Warframe Pro / LM22 | 320×320 |
| Assassin Spirit 120 Vision 1.54" | 240×240 |
| BA120 Vision 2.4" | 240×320 |
| LF20 / LF21 / LF22 | 320×320 |
| LC5 (fan LCD) | 360×360 |

**Trofeo bulk protocol** - USB `0416:5408` (LY), `0416:5409` (LY1):

| Model | Resolution |
|---|---|
| Trofeo Vision 9.16" (v1 and v2 firmware) ✓ | 1920×480 |
| Trofeo Vision 11.3" ✓ | 1920×400 |

**ALi bulk protocol** - USB `0416:5406`:

| Model | Resolution |
|---|---|
| ALi Vision | 320×240 / 320×320 |

**ChiZhu bulk protocol** - USB `87AD:70DB`:

| Model | Resolution |
|---|---|
| Grand / Hydro / Hyper / Peerless Vision 240/360 | 480×480 |
| Wonder Vision 360 6.67" (v1 and v2 firmware) | 2400×1080 (renders 1600×720) |
| Rainbow Vision 360 6.67" | 2400×1080 (renders 1600×720) |
| Levita Vision 360 6.67" | 2400×1080 (renders 1600×720) |
| TL-M10 Vision | 1920×462 |
| Elite Vision 360 ARGB Black (SPISCRM-V2) | 320×320 |
| Core Vision, Hyper Vision, RP130 Vision, LM16 SE, LF10V, LM19 SE, Grand Vision, Phantom Spirit 120 Vision EVO ✓, Frozen Warframe Ultra, Frozen Vision V2 | 480×480 |
| Mjolnir Vision | 320×240 |
| Mjolnir Vision Pro, Stream Vision | 640×480 |
| LC2JD, LF19, LD8 | 854×480 |
| LC3, LF16, LF18, LD6, CZ2 | 960×540 |
| LF17 | 800×480 |
| PC1, LC9 | 960×320 |
| LC7, LC8 | 640×172 |
| LM24 | 1280×480 |
| LM22, LM27, LM30 | 1600×720 |
| LF14, LD7, LD10 | 1920×462 |
| LD9 | 1920×440 |
| ChiZhu Vision 320×320 | 320×320 |

**SCSI protocol** - USB `0402:3922`, `87CD:70DB`, `0416:5406` (SG_IO pass-through):

| Model | Resolution |
|---|---|
| Elite Vision 360 2.73" (and Frozen Warframe SCSI variants) | 240×240 – 320×320 (auto-detected) |
| Frozen Horizon Pro, Frozen Magic Pro, Core/Elite/Wonder Vision, AK120/AX120/PA120 Digital | auto-detected |
| LC1 / LC2 / LC3 / LC5 AIO pump heads | auto-detected |

### Thermaltake / ASRock

BY-OEM HID protocol (JPEG over 1024-byte reports):

| Model | Resolution | USB ID |
|---|---|---|
| Thermaltake 6" LCD (ToughLiquid Ultra) ✓ | 1480×720 | `264A:2347` |
| ASRock Phantom Gaming 360 LCD | 480×480 | `26CE:0A10` |

### Jungle Leopard / Hongtai

CDC serial JPEG protocol at 2 Mbaud (VID `33C3`):

| Model | Resolution |
|---|---|
| JL Chill Arc 360 | 480×960 |
| JL Strip Display (OEM SKUs, PID 7791–7810) | 1920×462 |

### VMAX / AuyiHomu

| Model | Resolution | USB ID |
|---|---|---|
| VMAX 4.6" LCD (AuyiHomu HY-001, HY-002) | 320×960 | `345F:9132` |

### Jonsbo

Displays shipped with Jonsbo AIO coolers; protocols reverse-engineered from
the OEM JONSBO-AIO app:

| Model | Resolution | Connection |
|---|---|---|
| Jonsbo DS916 ✓ | 462×1920 | Serial `33C3:F101` (HLVMAX, raw JPEG frames) |
| Jonsbo DS339 ✓ | 376×960 | USB `345F:9132` (MacroSilicon MS9132, HID + bulk UYVY422) |

The DS339 shares its USB ID with the VMAX 4.6"; the device scan reads the
panel EDID to tell them apart.

### Turing Smart Screen

| Model | Resolution | Connection |
|---|---|---|
| Turing Smart Screen 3.5" / XuanFang 3.5" | 320×480 | Serial `1a86:5722` |
| Turing Smart Screen 2.1" | 480×480 | Serial `1d6b:0121` or USB `1cbe:0021` |
| Turing Smart Screen 5" (incl. Turzx with CT21INCH companion) ✓ | 800×480 | Serial `1d6b:0106` |
| Turing Smart Screen 5" | 720×1280 | USB `1cbe:0050` |
| Turing Smart Screen 8" | 800×1280 | USB `1cbe:0080` |
| Turing Smart Screen 8.8" Rev 1.0 | 480×1920 | Serial `0525:a4a7` |
| Turing Smart Screen 8.8" Rev 1.1 (TURZX) ✓ | 480×1920 | USB `1cbe:0088` |
| Turing Smart Screen 9.2" | 464×1920 | USB `1cbe:0092` |
| Turing Smart Screen 1.6" | 400×400 | USB `1cbe:0016` |
| Turing Smart Screen 4.6" | 320×960 | USB `1cbe:0046` |
| Shiny Snake G600 11.3" (Turing 10.2") | 440×1920 | Serial `0525:a4a7` |

### Lian Li

Encrypted bulk USB protocol (DES command packets + JPEG frames):

| Model | Resolution | USB ID |
|---|---|---|
| Lian Li Universal Screen 8.8" | 480×1920 | `1cbe:a088` |
| Lian Li Universal Screen 9.2" | 464×1920 | `1cbe:a092` |
| Lian Li HydroShift II OLED Curve | 2288×1080 | `1cbe:a068` |
| Lian Li HydroShift II LCD | 480×480 | `1cbe:a034` |

A Universal Screen 8.8" previously configured under the Turing family is
migrated to this family automatically, keeping its profile and settings.

### BeadaPanel (NXElec)

All PanelLink models are supported; the exact model is read from the device
descriptor at connect time.

| Model | Resolution | Size |
|---|---|---|
| 2 / 2W | 480×480 | 2.1" / 2.8" square |
| 3 / 3C | 320×480 / 480×320 | 3" |
| 4 / 4C | 480×800 / 800×480 | 4" |
| 5 / 5C / 5T | 800×480 | 5" |
| 5S | 480×854 | 5" portrait |
| 6 / 6C / 6S | 480×1280 / 1280×480 | 6.8" |
| 7C | 800×480 | 7" |
| 7S | 1280×400 | 7.9" ultrawide |
| 8 / Y | 480×1920 | 8.8" |
| 9 / Z | 462×1920 | 9.2" |
| 11 / X | 440×1920 | 11.3" |

## Windows interoperability

This build is designed to coexist with InfoPanel for Windows, in both
directions:

- **Configuration format**: `settings.xml`, `profiles.xml` and the
  per-profile `profiles/{guid}.xml` files use the exact same XML
  serialization (class names, property names and CLR namespace preserved).
  A data folder written by one app loads in the other. Unknown elements
  are ignored on both sides, so Linux-only additions (like URL image
  refresh) simply have no effect on Windows rather than breaking files.
- **Profile archives**: `.infopanel` exports (profile + display items +
  assets) import on either platform with a fresh guid.
- **Sensor bindings**: the `SensorType` enum is the union of both apps'
  values (`HwInfo`, `Libre`, `Plugin`, `Hwmon`). Plugin sensor ids are
  identical across platforms, so plugin-bound items (including
  `plugin-image://` entries) work unchanged. Hardware sensors use
  different backends (HWiNFO/LibreHardwareMonitor on Windows, hwmon here),
  so hardware-bound items keep their layout but need re-binding to the
  equivalent Linux sensor with the designer's Replace Sensor action.
- **Plugins**: third-party plugin binaries built for the Windows app load
  unchanged (the SDK targets net8.0 and keeps its public surface). The
  configuration framework and image-provider contract match the 1.4.x
  behavior, including config sidecar naming.
- **Hotkeys**: bindings are stored using the WPF key vocabulary
  ("Control, Alt" + "F5"), so hotkey settings survive a round trip
  through the Windows app.
- **Trigger programs**: program-specific profile rules use process names
  with the `.exe` suffix ignored; because Proton/Wine expose Windows
  executable names, the same trigger lists work for the same games on
  both platforms.

## Installing

Download the latest `infopanel-<version>-linux-x64.tar.gz` from
[Releases](https://github.com/emaspa/InfoPanel-linux/releases). The
tarball is self-contained, so no .NET runtime is needed.

```bash
tar xf infopanel-<version>-linux-x64.tar.gz
cd infopanel-<version>-linux-x64
./install.sh
```

`install.sh`
installs to `~/.local/opt/infopanel` with a launcher in `~/.local/bin`, a
desktop entry, the udev rules (sudo) and, when `smartmontools` is present,
a root systemd timer that feeds the SMART drive health sensors.

**Updating** works the same way: quit InfoPanel, extract the new tarball
and run `./install.sh` again - it overwrites the app in place and leaves
your profiles and settings in `~/.local/share/InfoPanel/` untouched. The
app checks for new releases at startup and notifies you when one is out;
the About page shows what changed and links the download.

Requirements and optional dependencies:

- **USB panels**: the bundled udev rules (world-accessible device nodes; no
  group membership needed. udev 261+ silently rejects rules that assign
  device nodes to non-system groups like the old `plugdev`, which is why the
  rules no longer use it).
- **Intel GPU engine utilization**: `sysctl kernel.perf_event_paranoid=-1`
  (see comments in `packaging/infopanel-udev.rules`).
- **Video/RTSP display items**: a system `ffmpeg` binary.
- **Audio Spectrum**: PulseAudio or PipeWire (uses `parec`/`pactl`).
- **SMART sensors**: `smartmontools`.

## Building from source

Requires the .NET 10 SDK.

```bash
dotnet build InfoPanel.slnx -c Release
dotnet run --project src/InfoPanel.App
```

To produce the same self-contained tarball as the published releases:

```bash
packaging/publish.sh 0.2.1     # builds artifacts/infopanel-0.2.1-linux-x64.tar.gz
```

## Data and paths

Configuration lives in `~/.local/share/InfoPanel/`:

| Path | Contents |
|---|---|
| `settings.xml`, `profiles.xml` | app settings and profile list |
| `profiles/{guid}.xml` | display items per profile |
| `assets/{guid}/` | per-profile images and media |
| `autosave/profiles/{guid}.xml` | session-start layout backups |
| `plugins/{id}.config.json` | plugin configuration sidecars |
| `logs/` | rolling Serilog output |

Set `INFOPANEL_DATA_DIR` to relocate everything (portable or test setups).
A single instance is enforced via a lock file in the data directory.

## Command line

| Flag | Effect |
|---|---|
| `--headless` | run sensors, panels and web server without a UI |
| `--render-once <dir>` | render every profile to PNG in `<dir>` and exit |
| `--dump-sensors` | print all live sensor readings and exit |
| `--verbose` | debug-level logging |

## Credits

- [habibrehmansg](https://github.com/habibrehmansg): creator of the
  original InfoPanel for Windows.
- [emaspa](https://github.com/emaspa): Linux port and Thermalright panel
  support.
- [F3NN3X](https://github.com/F3NN3X): for the countless support and
  awesome plugins.
- [mrZoSo](https://github.com/mrZoSo): for the beta testing.
- [CyberFreek](https://github.com/CyberFreek): Lian Li panel support and
  weather units.
- [ozgurce](https://github.com/ozgurce): the standalone Lian Li family
  (Universal Screen and HydroShift II models).
- [yattuLizard](https://github.com/yattuLizard): VMAX / AuyiHomu panel
  support.
- [fweepa](https://github.com/fweepa): stopwatch plugin and hotkeys.
- [Orkunowski](https://github.com/Orkunowski): designer UX improvements.
- [LACT](https://github.com/ilya-zlobintsev/LACT): the NvAPI-on-Linux
  approach behind the NVIDIA hotspot, VRAM temperature and voltage
  sensors.
- Everyone else: for those that messaged or posted questions, feedback and
  panel designs on Reddit, HWiNFO forums and Discord.

## License

GPL-3.0, like the original InfoPanel this project is based on. See
[LICENSE](LICENSE) for the full text and [LICENSES.md](LICENSES.md) for
the third-party notices of the bundled libraries; both files also ship
inside every release tarball.
