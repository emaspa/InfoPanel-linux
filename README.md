# InfoPanel for Linux

<p align=center>
  <img src="Images/logo.png" width=60/>
</p>

<p align=center>
Unofficial Linux port of <a href="https://github.com/habibrehmansg/infopanel">InfoPanel</a>, a desktop visualization tool that displays hardware monitoring data on desktop overlays and USB LCD panels (BeadaPanel, Turing Smart Screen, Thermalright).
</p>

> **Status:** Work in progress. The app builds and runs on Linux. Desktop overlays, USB panel discovery, device communication, sensor monitoring, and the Design/Profiles UI are functional.

## What is InfoPanel?

InfoPanel is a Windows application by [habibrehmansg](https://github.com/habibrehmansg/infopanel) that renders hardware sensor data (CPU temps, GPU load, fan speeds, etc.) onto desktop overlays and USB LCD panels. It supports a wide range of visualization types (gauges, charts, bars, images, text) and a plugin system for custom data sources.

This fork ports InfoPanel to Linux, replacing all Windows-specific dependencies with cross-platform alternatives.

## Differences from the Windows version

| Area | Windows (upstream) | Linux (this fork) |
|---|---|---|
| **UI framework** | WPF | Avalonia 11 |
| **Graphics** | DirectX + SkiaSharp | SkiaSharp only |
| **USB discovery** | Windows SetupAPI (`DeviceProperties["DeviceID"]`) | LibUsbDotNet `DevicePath` fallback |
| **Serial port discovery** | WMI (`Win32_SerialPort`) | Linux sysfs (`/sys/bus/usb-serial/devices/`, `/sys/class/tty/`) |
| **SCSI panel I/O** | `kernel32.dll` P/Invoke (`CreateFile`, `DeviceIoControl`) | `libc` P/Invoke (`open`, `ioctl` with `SG_IO`) |
| **HID panels** | HidSharp (cross-platform) | HidSharp (no changes needed) |
| **Hardware monitoring** | HWiNFO SHM, LibreHardwareMonitor | Linux hwmon (sysfs `/sys/class/hwmon`, `/sys/class/thermal`), plugin system |
| **Desktop overlay** | WPF transparent window | Avalonia transparent window with SkiaSharp canvas |
| **Installer** | Microsoft Store / MSI | Manual build (see below), udev rules provided |

### What works

- App builds and launches on Linux (.NET 8.0 + Avalonia)
- Main window with sidebar navigation (Home, Profiles, Design, USB Panels, Plugins, Settings, Logs, Updates, About)
- Desktop overlay windows with SkiaSharp rendering
- **Hardware monitoring** via Linux hwmon (sysfs) — CPU temps, fan speeds, voltages, power, etc.
- **Design page** with 3-panel layout: sensor browser, display items list, and type-specific property editors
- **Sensor browser** with hwmon and plugin tabs, plus action buttons to add sensor-bound items
- **11 property editors** for all display item types (Text, Sensor, DateTime, Image, Bar, Graph, Donut, Shape, Gauge, Group, Common)
- **Profiles page** with color pickers, font picker, import/export, window position controls
- **Minimize-to-tray** support with system tray icon
- BeadaPanel USB device discovery and communication (via LibUsbDotNet)
- Turing Smart Screen serial device discovery (via sysfs) and communication
- Turing Smart Screen USB device discovery and communication
- Thermalright HID panel discovery (via HidSharp) and communication
- Thermalright WinUSB/bulk panel discovery (via LibUsbDotNet) and communication
- Thermalright SCSI panel discovery and communication (via Linux SG_IO ioctl)
- Plugin loading system
- Configuration persistence

### What doesn't work yet

- DirectX-accelerated rendering (Linux uses SkiaSharp software rendering)
- Auto-update system
- Drag-and-drop layout editing in the design view

## Building

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
dotnet build InfoPanel.sln -c Debug

# Run
dotnet run --project InfoPanel/InfoPanel.csproj
```

### USB device permissions (udev rules)

USB panels require permission rules for non-root access. Install the provided udev rules:

```bash
sudo cp InfoPanel/packaging/99-infopanel.rules /etc/udev/rules.d/
sudo udevadm control --reload
sudo udevadm trigger
```

This grants access to all supported panel families: BeadaPanel, Turing Smart Screen (serial and USB), and Thermalright (HID, bulk, and SCSI).

## Supported USB panels

| Family | Models | Transport | Linux status |
|---|---|---|---|
| **BeadaPanel** | All (VID `4E58`) | LibUsbDotNet bulk | Working |
| **Turing Smart Screen** | 3.5", 5", 8.8" Rev 1.0 | Serial (CH340) | Working |
| **Turing Smart Screen** | 2.1", 8.8" Rev 1.1, 8", 9.2", 5" USB, 1.6", 10.2" | LibUsbDotNet bulk | Working |
| **Thermalright** | Trofeo HID variants | HidSharp | Working (cross-platform, no changes needed) |
| **Thermalright** | ChiZhu, Trofeo bulk variants | LibUsbDotNet bulk | Working |
| **Thermalright** | Frozen Warframe Elite Vision 360 | SCSI (SG_IO) | Working |

## Project structure

```
InfoPanel/                    # Main application
  Services/                   # Background tasks (panel comms, HwmonMonitor)
  TuringPanel/                # Turing Smart Screen protocol + discovery
  ThermalrightPanel/          # Thermalright protocol + discovery
  BeadaPanel/                 # BeadaPanel protocol + discovery
  Views/                      # Avalonia UI (AXAML)
    Components/               # Property editors and sensor actions
    Converters/               # Value converters (color, font, bool, enum)
    Pages/                    # Main pages (Design, Profiles, Settings, etc.)
  ViewModels/                 # MVVM view models
    Components/               # Sensor tree view models
  Models/                     # Data models and configuration
  Drawing/                    # SkiaSharp rendering
  packaging/                  # udev rules
InfoPanel.Plugins/            # Plugin interface definitions
InfoPanel.Plugins.Loader/     # Plugin discovery and loading
InfoPanel.Extras/             # Built-in plugins
```

## Credits

- [InfoPanel](https://github.com/habibrehmansg/infopanel) by habibrehmansg - the original Windows application

## License

GPL 3.0 - see the [LICENSE](LICENSE) file. Same license as the upstream project.
