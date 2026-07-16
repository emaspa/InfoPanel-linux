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
  Devices self-heal their USB binding after replug.
- **Sensors**: Linux hwmon, Intel iGPU (sysfs + PMU), AMD (ROCm SMI),
  NVIDIA (NVML), plus the .NET plugin system — existing InfoPanel plugin
  binaries load unchanged.
- **Outputs**: transparent desktop overlays (X11/XWayland), USB panels, and a
  built-in web server serving live profile images.
- **Headless mode**: `infopanel --headless` runs sensors + panels without a UI;
  `--render-once <dir>` renders profiles to PNG; `--dump-sensors` lists all
  live sensor readings.

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
