# InfoPanel for Linux

<p align=center>
  <img src="Images/logo.png" width=60/>
</p>

<p align=center>
Linux port of <a href="https://github.com/habibrehmansg/infopanel">InfoPanel</a>, a desktop visualization tool that displays hardware monitoring data on desktop overlays and USB LCD panels (BeadaPanel, Turing Smart Screen, Thermalright).
</p>

## What is InfoPanel?

InfoPanel is a Windows application by [habibrehmansg](https://github.com/habibrehmansg/infopanel) that renders hardware sensor data (CPU temps, GPU load, fan speeds, etc.) onto desktop overlays and USB LCD panels. It supports a wide range of visualization types (gauges, charts, bars, images, text) and a plugin system for custom data sources.

This fork ports InfoPanel to Linux, replacing all Windows-specific dependencies with cross-platform alternatives.

## Features

- **Hardware monitoring** via Linux hwmon/sysfs — CPU temps, fan speeds, voltages, power, frequencies, etc.
- **GPU monitoring** — Intel iGPU (sysfs frequency + PMU perf events), NVIDIA (NVML), AMD (ROCm SMI)
- **System sensors** — filesystem usage, CPU frequency, uptime, process count, load average, block I/O, network throughput
- **Desktop overlay windows** with SkiaSharp rendering
- **Design editor** with 3-panel layout: sensor browser, display items list, and type-specific property editors
- **11 property editors** for all display item types (Text, Sensor, DateTime, Image, Bar, Graph, Donut, Shape, Gauge, Group, Common)
- **Profiles** with color pickers, font picker, import/export, window position controls
- **USB panel support** — BeadaPanel, Turing Smart Screen, Thermalright/ChiZhu (see table below)
- **Plugin system** for extensible sensor sources
- **Built-in web server** for remote access
- **Minimize-to-tray** with system tray icon
- **Start minimized** option

## Differences from the Windows version

| Area | Windows (upstream) | Linux (this fork) |
|---|---|---|
| **UI framework** | WPF | Avalonia 11 |
| **Graphics** | DirectX + SkiaSharp | SkiaSharp only |
| **USB discovery** | Windows SetupAPI | LibUsbDotNet `DevicePath` |
| **Serial port discovery** | WMI (`Win32_SerialPort`) | sysfs (`/sys/bus/usb-serial/devices/`, `/sys/class/tty/`) |
| **SCSI panel I/O** | `kernel32.dll` P/Invoke | `libc` P/Invoke (`open`, `ioctl` with `SG_IO`) |
| **HID panels** | HidSharp (cross-platform) | HidSharp (no changes needed) |
| **Hardware monitoring** | HWiNFO SHM, LibreHardwareMonitor | hwmon/sysfs, Intel PMU, NVML, ROCm SMI |
| **Desktop overlay** | WPF transparent window | Avalonia transparent window + SkiaSharp |
| **Installer** | Microsoft Store / MSI | Manual build, udev rules provided |

## Installing

### Prerequisites

- .NET 8.0 SDK
- Linux (tested on Ubuntu)
- `libusb-1.0` for USB panel communication

```bash
# Install .NET 8.0 SDK (if not already installed)
# See https://learn.microsoft.com/dotnet/core/install/linux

# Install libusb
sudo apt install libusb-1.0-0-dev

# Clone and build
git clone https://github.com/emaspa/InfoPanel-linux.git
cd InfoPanel-linux
dotnet build InfoPanel.sln -c Release

# Run
dotnet run --project InfoPanel/InfoPanel.csproj -c Release
```

### USB device permissions (udev rules)

USB panels require permission rules for non-root access. Install the provided udev rules:

```bash
sudo cp infopanel-udev.rules /etc/udev/rules.d/99-infopanel.rules
sudo udevadm control --reload
sudo udevadm trigger
```

This grants access to all supported panel families: BeadaPanel, Turing Smart Screen (serial and USB), and Thermalright (HID, bulk, and SCSI).

### Intel GPU utilization monitoring (optional)

To monitor Intel iGPU utilization via PMU perf events, set the kernel parameter:

```bash
sudo sysctl kernel.perf_event_paranoid=-1
```

To make it permanent, add `kernel.perf_event_paranoid=-1` to `/etc/sysctl.conf`. Intel GPU frequency monitoring works without this.

### Desktop integration

Install the `.desktop` file and icon for proper dock/taskbar integration:

```bash
mkdir -p ~/.local/share/icons/hicolor/256x256/apps
cp InfoPanel/Resources/Images/logo.png ~/.local/share/icons/hicolor/256x256/apps/infopanel.png
cp infopanel.desktop ~/.local/share/applications/
gtk-update-icon-cache -f -t ~/.local/share/icons/hicolor/
```

## Supported USB panels

| Family | Models | Transport | Status |
|---|---|---|---|
| **BeadaPanel** | All (VID `4E58`) | LibUsbDotNet bulk | Working |
| **Turing Smart Screen** | 3.5", 5", 8.8" Rev 1.0 | Serial (CH340) | Working |
| **Turing Smart Screen** | 2.1", 8.8" Rev 1.1, 8", 9.2", 5" USB, 1.6", 10.2" | LibUsbDotNet bulk | Working |
| **Thermalright** | Trofeo HID variants | HidSharp | Working |
| **Thermalright** | ChiZhu, Trofeo bulk variants | LibUsbDotNet bulk | Working |
| **Thermalright** | Frozen Warframe Elite Vision 360 | SCSI (SG_IO) | Working |

## Project structure

```
InfoPanel/                    # Main application
  Services/                   # Background tasks, GPU monitors, system sensors
  TuringPanel/                # Turing Smart Screen protocol + discovery
  ThermalrightPanel/          # Thermalright protocol + discovery
  BeadaPanel/                 # BeadaPanel protocol + discovery
  Monitors/                   # HwmonMonitor, PluginMonitor
  Views/                      # Avalonia UI (AXAML)
    Components/               # Property editors and sensor actions
    Converters/               # Value converters (color, font, bool, enum)
    Pages/                    # Main pages (Design, Profiles, Settings, etc.)
  ViewModels/                 # MVVM view models
    Components/               # Sensor tree view models
  Models/                     # Data models and configuration
  Drawing/                    # SkiaSharp rendering
InfoPanel.Plugins/            # Plugin interface definitions
InfoPanel.Plugins.Loader/     # Plugin discovery and loading
InfoPanel.Extras/             # Built-in plugins (weather, system info, etc.)
```

## Credits

- [InfoPanel](https://github.com/habibrehmansg/infopanel) by habibrehmansg — the original Windows application

## Support

- [Discord](https://discord.gg/cQnjdMC7Qc)
- [Reddit](https://www.reddit.com/r/InfoPanel/)
- [Buy Me a Coffee](https://buymeacoffee.com/emaspa)

## License

GPL 3.0 — see the [LICENSE](LICENSE) file. Same license as the upstream project.
