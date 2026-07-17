# InfoPanel for Linux

Hardware monitoring dashboards for desktop overlays and USB LCD panels, rebuilt
natively for Linux on .NET 10 and Avalonia 12. Based on
[InfoPanel](https://github.com/habibrehmansg/infopanel) and its
[Thermalright-enabled fork](https://github.com/emaspa/infopanel-1).

> The previous Linux port (v1.4.x) lives on the [`v1`](../../tree/v1) branch.
> Profiles and settings are fully compatible in both directions.

## Features

- **Designer**: direct-manipulation editor — zoom/pan canvas, drag with grid
  snapping, resize handles, marquee selection, layers panel, live sensor tree
  (double-click to add), contextual inspector, full undo/redo, autosave.
- **USB panels**: Thermalright/TRCC family (HID, TrofeoBulk, ChiZhu, ALi, SCSI —
  incl. Trofeo Vision 9.16" with flicker fix and display masks), Turing Smart
  Screen (USB + serial, CT13/CT21INCH companion detection), BeadaPanel.
  Devices self-heal their USB binding after replug. Full model list under
  [Supported USB panels](#supported-usb-panels).
- **Sensors**: Linux hwmon, Intel iGPU (sysfs + PMU), AMD (ROCm SMI),
  NVIDIA (NVML), plus the .NET plugin system — existing InfoPanel plugin
  binaries load unchanged.
- **Outputs**: transparent desktop overlays (X11/XWayland), USB panels, and a
  built-in web server serving live profile images.
- **Headless mode**: `infopanel --headless` runs sensors + panels without a UI;
  `--render-once <dir>` renders profiles to PNG; `--dump-sensors` lists all
  live sensor readings.

## Supported USB panels

Panels are auto-detected when plugged in (udev rules required, see
[Installing](#installing)). Models marked ✓ have been verified on real
hardware under Linux; the rest use the same protocol implementations as the
Windows build and are expected to work — reports welcome.

### Thermalright (TRCC family)

**Trofeo / HID protocol** — USB `0416:5302`, `0418:5303`, `0418:5304`:

| Model | Resolution |
|---|---|
| Trofeo Vision 6.86" | 1280×480 |
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

**Trofeo bulk protocol** — USB `0416:5408` (LY), `0416:5409` (LY1):

| Model | Resolution |
|---|---|
| Trofeo Vision 9.16" (v1 and v2 firmware) ✓ | 1920×480 |
| Trofeo Vision 11.3" | 1920×400 |

**ALi bulk protocol** — USB `0416:5406`:

| Model | Resolution |
|---|---|
| ALi Vision | 320×240 / 320×320 |

**ChiZhu bulk protocol** — USB `87AD:70DB`:

| Model | Resolution |
|---|---|
| Grand / Hydro / Hyper / Peerless Vision 240/360 | 480×480 |
| Wonder Vision 360 6.67" (v1 and v2 firmware) | 2400×1080 (renders 1600×720) |
| Rainbow Vision 360 6.67" | 2400×1080 (renders 1600×720) |
| Levita Vision 360 6.67" | 2400×1080 (renders 1600×720) |
| TL-M10 Vision | 1920×462 |
| Elite Vision 360 ARGB Black (SPISCRM-V2) | 320×320 |
| Core Vision, Hyper Vision, RP130 Vision, LM16 SE, LF10V, LM19 SE, Grand Vision, Phantom Spirit 120 Vision EVO, Frozen Warframe Ultra, Frozen Vision V2 | 480×480 |
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

**SCSI protocol** — USB `0402:3922`, `87CD:70DB`, `0416:5406` (SG_IO pass-through):

| Model | Resolution |
|---|---|
| Elite Vision 360 2.73" (and Frozen Warframe SCSI variants) | 240×240 – 320×320 (auto-detected) |
| Frozen Horizon Pro, Frozen Magic Pro, Core/Elite/Wonder Vision, AK120/AX120/PA120 Digital | auto-detected |
| LC1 / LC2 / LC3 / LC5 AIO pump heads | auto-detected |

### Thermaltake / ASRock

BY-OEM HID protocol (JPEG over 1024-byte reports):

| Model | Resolution | USB ID |
|---|---|---|
| Thermaltake 6" LCD (ToughLiquid Ultra) | 1480×720 | `264A:2347` |
| ASRock Phantom Gaming 360 LCD | 480×480 | `26CE:0A10` |

### Jungle Leopard / Hongtai

CDC serial JPEG protocol at 2 Mbaud (VID `33C3`):

| Model | Resolution |
|---|---|
| JL Chill Arc 360 | 480×960 |
| JL Strip Display (OEM SKUs, PID 7791–7810) | 1920×462 |

### Turing Smart Screen

| Model | Resolution | Connection |
|---|---|---|
| Turing Smart Screen 3.5" / XuanFang 3.5" | 320×480 | Serial `1a86:5722` |
| Turing Smart Screen 2.1" | 480×480 | Serial `1d6b:0121` or USB `1cbe:0021` |
| Turing Smart Screen 5" | 800×480 | Serial `1d6b:0106` |
| Turing Smart Screen 5" | 720×1280 | USB `1cbe:0050` |
| Turing Smart Screen 8" | 800×1280 | USB `1cbe:0080` |
| Turing Smart Screen 8.8" Rev 1.0 | 480×1920 | Serial `0525:a4a7` |
| Turing Smart Screen 8.8" Rev 1.1 | 480×1920 | USB `1cbe:0088` |
| Turing Smart Screen 9.2" | 464×1920 | USB `1cbe:0092` |
| Turing Smart Screen 1.6" | 400×400 | USB `1cbe:0016` |
| Turing Smart Screen 4.6" | 320×960 | USB `1cbe:0046` |
| Shiny Snake G600 11.3" (Turing 10.2") | 440×1920 | Serial `0525:a4a7` |

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

## Building

```bash
dotnet build InfoPanel.slnx -c Release      # requires .NET 10 SDK
dotnet run --project src/InfoPanel.App
```

## Installing

```bash
packaging/publish.sh 2.0.0     # builds artifacts/infopanel-2.0.0-linux-x64.tar.gz
# on the target machine:
tar xf infopanel-2.0.0-linux-x64.tar.gz && cd infopanel-2.0.0-linux-x64 && ./install.sh
```

USB panel access requires the bundled udev rules (installed by `install.sh`)
and membership in the `plugdev` group. Intel GPU engine-utilization sensors
need `sysctl kernel.perf_event_paranoid=-1` (see comments in
`packaging/infopanel-udev.rules`).

## Data

Configuration lives in `~/.local/share/InfoPanel/` (XML, format-compatible
with InfoPanel for Windows — profiles can be shared across platforms).
Override with `INFOPANEL_DATA_DIR` for portable/test setups.

## Project layout

| Project | Purpose |
|---|---|
| `InfoPanel.Core` | Models, XML persistence, stores — UI-free |
| `InfoPanel.Rendering` | SkiaSharp render pipeline (PanelDraw, graphs, image cache) |
| `InfoPanel.Platform(.Linux)` | OS abstractions: SCSI transport, autostart |
| `InfoPanel.Sensors(.Linux)` | hwmon/GPU monitors, plugin monitor |
| `InfoPanel.Devices.*` | USB panel families over LibUsbDotNet/HidSharp |
| `InfoPanel.Web` | ASP.NET Core preview server |
| `InfoPanel.App` | Avalonia 12 UI + headless CLI |
| `InfoPanel.Plugins(.Loader)` | Plugin SDK (net8.0, binary-compatible) |

## License

GPL-3.0, same as upstream InfoPanel.
