# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build Commands

```bash
# Restore packages
dotnet restore

# Build Debug
dotnet build InfoPanel.sln -c Debug

# Build Release
dotnet build InfoPanel.sln -c Release

# Publish for deployment (Linux x64)
dotnet publish InfoPanel/InfoPanel.csproj -c Release -r linux-x64 --self-contained

# Run the main application
dotnet run --project InfoPanel/InfoPanel.csproj

# Run plugin simulator for testing plugins
dotnet run --project InfoPanel.Plugins.Simulator/InfoPanel.Plugins.Simulator.csproj
```

## Architecture Overview

InfoPanel is a Linux desktop application built on .NET 8.0 with Avalonia UI that displays hardware monitoring data on desktop overlays and USB LCD panels. Ported from the original Windows WPF app. The codebase follows MVVM architecture with a modular plugin system.

### Core Projects

- **InfoPanel** - Main Avalonia UI application
  - Entry: `Program.cs` → `App.axaml.cs` → `MainWindow.axaml`
  - MVVM structure: ViewModels handle logic, Views handle UI
  - Background services run display updates and hardware communication
  - SkiaSharp-based rendering

- **InfoPanel.Plugins** - Plugin interface definitions
  - `IPlugin` - Base plugin interface
  - `IPluginSensor` - For sensor data providers
  - `IPluginText/Table` - For display elements
  - All plugins must inherit from `BasePlugin`

- **InfoPanel.Plugins.Loader** - Dynamic plugin loading
  - Discovers plugins in the `plugins` directory
  - Loads assemblies in isolated contexts
  - Manages plugin lifecycle and dependencies

- **InfoPanel.Extras** - Built-in plugins
  - Ships with the application in the plugins folder
  - Provides system info, network, drives, weather functionality

### Key Services and Background Tasks

Located in `InfoPanel/Services/`:
- `PanelDrawTask` - Renders visualizations at high frame rates
- `BeadaPanelTask/TuringPanelTask/ThermalrightPanelTask` - USB panel communication
- `WebServerTask` - HTTP API and web interface
- `LinuxSystemSensors` - Collects sensor data from sysfs/hwmon

Located in `InfoPanel/Monitors/`:
- `HwmonMonitor` - Linux hwmon/sysfs sensor polling
- `PluginMonitor` - Plugin lifecycle and sensor updates

Located in `InfoPanel/Services/` (GPU monitors):
- `IntelGpuMonitor` - Intel iGPU via sysfs frequency + PMU perf events
- `RocmSmiMonitor` - AMD GPU via ROCm SMI

### Display System

The drawing system (`InfoPanel/Drawing/`):
- `SkiaGraphics` - Primary renderer using SkiaSharp
- `PanelDraw` - Orchestrates rendering of display items

Display items (`InfoPanel/Models/`) represent visualizations:
- `SensorDisplayItem` - Text-based sensor values
- `GaugeDisplayItem` - Circular gauge visualizations
- `ChartDisplayItem` - Graphs, bars, and donut charts
- `ImageDisplayItem` - Static and animated images

### USB Panel Support

USB panel communication uses libusb via LibUsbDotNet:
- `InfoPanel/TuringPanel/` - Turing Smart Screen panels
- `InfoPanel/BeadaPanel/` - BeadaPanel (NXElec) devices
- `InfoPanel/ThermalrightPanel/` - Thermalright / ChiZhu panels
- Model-specific configurations in database classes
- Requires udev rules for non-root access (see `infopanel-udev.rules`)

### Plugin Development

Plugins are .NET libraries that:
1. Reference `InfoPanel.Plugins` package
2. Implement `IPlugin` interface
3. Use attributes like `[PluginSensor]` to expose data
4. Include a `PluginInfo.ini` manifest file

See `PLUGINS.md` for detailed plugin development guide.

## Key Technologies

- **Avalonia UI** for cross-platform desktop UI
- **CommunityToolkit.MVVM** for MVVM implementation
- **SkiaSharp** for graphics rendering
- **Serilog** for structured logging (see `LoggingGuidelines.md`)
- **LibUsbDotNet** / **HidSharp** for USB panel communication
- **ASP.NET Core** for built-in web server

## Development Notes

- The solution uses .NET 8.0
- Warning level 6 and nullable reference types are enabled
- No unit test projects currently exist
- Plugins are loaded from the `plugins` directory at runtime
- Configuration is stored in `~/.local/share/InfoPanel/`
- Single instance enforced via file lock at `~/.local/share/InfoPanel/.lock`
- WM_CLASS is set to `infopanel` (matching `infopanel.desktop` for dock icon)
- .NET `Directory.GetDirectories` does NOT support `[0-9]` character class patterns — use `*` instead

## Git Workflow

- Branch `all-changes` tracks `linux/main`
- Push with: `git push linux HEAD:main`
