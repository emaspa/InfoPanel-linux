using InfoPanel.Models;
using Serilog;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;

namespace InfoPanel.Services;

public class RocmSmiMonitor
{
    private static readonly Lazy<RocmSmiMonitor> _instance = new(() => new RocmSmiMonitor());
    public static RocmSmiMonitor Instance => _instance.Value;

    private bool _available;
    private bool _initialized;
    private int _deviceCount;
    private string[] _deviceNames = [];

    // sysfs paths for clock reading (avoids rsmi_frequencies_t struct layout issues)
    private string?[] _sysfsClockPaths = [];
    private string?[] _sysfsMemClockPaths = [];

    private RocmSmiMonitor() { }

    public void Initialize()
    {
        try
        {
            var ret = Rsmi.rsmi_init(0);
            if (ret != RsmiStatus.Success)
            {
                Log.Debug("ROCm SMI init failed: {Result}", ret);
                return;
            }

            _initialized = true;

            ret = Rsmi.rsmi_num_monitor_devices(out uint count);
            if (ret != RsmiStatus.Success || count == 0)
            {
                Log.Debug("ROCm SMI: no devices found ({Result})", ret);
                Rsmi.rsmi_shut_down();
                _initialized = false;
                return;
            }

            _deviceCount = (int)count;
            _deviceNames = new string[_deviceCount];
            _sysfsClockPaths = new string?[_deviceCount];
            _sysfsMemClockPaths = new string?[_deviceCount];

            for (int i = 0; i < _deviceCount; i++)
            {
                ret = Rsmi.rsmi_dev_name_get((uint)i, out var name);
                _deviceNames[i] = ret == RsmiStatus.Success && !string.IsNullOrEmpty(name) ? name : $"AMD GPU {i}";

                // Find sysfs paths for clock reading
                FindSysfsClockPaths(i);
            }

            _available = true;
            Log.Information("ROCm SMI initialized: {Count} AMD GPU(s) found", _deviceCount);
        }
        catch (DllNotFoundException)
        {
            Log.Debug("ROCm SMI library not found (no AMD GPU driver or ROCm not installed)");
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "ROCm SMI initialization error");
        }
    }

    private void FindSysfsClockPaths(int deviceIndex)
    {
        // AMD GPUs expose clocks in /sys/class/drm/card*/device/pp_dpm_sclk and pp_dpm_mclk
        try
        {
            var drmPath = "/sys/class/drm";
            if (!Directory.Exists(drmPath)) return;

            foreach (var cardDir in Directory.GetDirectories(drmPath, "card*"))
            {
                var vendorFile = Path.Combine(cardDir, "device", "vendor");
                var vendor = ReadFile(vendorFile);
                // AMD vendor ID is 0x1002
                if (vendor != "0x1002") continue;

                // Match card index to device index (simple mapping)
                var sclkPath = Path.Combine(cardDir, "device", "pp_dpm_sclk");
                var mclkPath = Path.Combine(cardDir, "device", "pp_dpm_mclk");

                if (File.Exists(sclkPath) && _sysfsClockPaths[deviceIndex] == null)
                {
                    _sysfsClockPaths[deviceIndex] = sclkPath;
                }
                if (File.Exists(mclkPath) && _sysfsMemClockPaths[deviceIndex] == null)
                {
                    _sysfsMemClockPaths[deviceIndex] = mclkPath;
                }

                if (_sysfsClockPaths[deviceIndex] != null)
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Failed to find sysfs clock paths for AMD GPU {Index}", deviceIndex);
        }
    }

    public void Shutdown()
    {
        if (_initialized)
        {
            try { Rsmi.rsmi_shut_down(); } catch { }
            _initialized = false;
            _available = false;
        }
    }

    public void Poll()
    {
        if (!_available) return;

        for (uint i = 0; i < _deviceCount; i++)
        {
            var prefix = _deviceCount == 1 ? "system/amdgpu" : $"system/amdgpu/{i}";

            try
            {
                // Temperature (edge)
                if (Rsmi.rsmi_dev_temp_metric_get(i, RsmiTemperatureType.Edge, RsmiTemperatureMetric.Current, out long tempMilliC) == RsmiStatus.Success)
                {
                    UpdateSensor($"{prefix}/temperature", Math.Round(tempMilliC / 1000.0, 1), "°C");
                }

                // Junction temperature
                if (Rsmi.rsmi_dev_temp_metric_get(i, RsmiTemperatureType.Junction, RsmiTemperatureMetric.Current, out long juncTemp) == RsmiStatus.Success)
                {
                    UpdateSensor($"{prefix}/temperature_junction", Math.Round(juncTemp / 1000.0, 1), "°C");
                }

                // Memory temperature
                if (Rsmi.rsmi_dev_temp_metric_get(i, RsmiTemperatureType.Memory, RsmiTemperatureMetric.Current, out long memTemp) == RsmiStatus.Success)
                {
                    UpdateSensor($"{prefix}/temperature_memory", Math.Round(memTemp / 1000.0, 1), "°C");
                }

                // Power
                if (Rsmi.rsmi_dev_power_get(i, out ulong powerUw, out _) == RsmiStatus.Success)
                {
                    UpdateSensor($"{prefix}/power", Math.Round(powerUw / 1000000.0, 1), "W");
                }

                // GPU utilization
                if (Rsmi.rsmi_dev_busy_percent_get(i, out uint gpuBusy) == RsmiStatus.Success)
                {
                    UpdateSensor($"{prefix}/utilization", gpuBusy, "%");
                }

                // Memory controller utilization
                if (Rsmi.rsmi_dev_memory_busy_percent_get(i, out uint memBusy) == RsmiStatus.Success)
                {
                    UpdateSensor($"{prefix}/memory_utilization", memBusy, "%");
                }

                // VRAM usage
                if (Rsmi.rsmi_dev_memory_total_get(i, RsmiMemoryType.Vram, out ulong vramTotal) == RsmiStatus.Success)
                {
                    UpdateSensor($"{prefix}/memory_total", Math.Round(vramTotal / (1024.0 * 1024), 0), "MB");

                    if (Rsmi.rsmi_dev_memory_usage_get(i, RsmiMemoryType.Vram, out ulong vramUsed) == RsmiStatus.Success)
                    {
                        UpdateSensor($"{prefix}/memory_used", Math.Round(vramUsed / (1024.0 * 1024), 0), "MB");
                        var memPercent = vramTotal > 0 ? Math.Round(vramUsed * 100.0 / vramTotal, 1) : 0;
                        UpdateSensor($"{prefix}/memory_percent", memPercent, "%");
                    }
                }

                // Fan speed (percentage)
                if (Rsmi.rsmi_dev_fan_speed_get(i, 0, out long fanSpeed) == RsmiStatus.Success &&
                    Rsmi.rsmi_dev_fan_speed_max_get(i, 0, out ulong fanMax) == RsmiStatus.Success && fanMax > 0)
                {
                    UpdateSensor($"{prefix}/fan_speed", Math.Round(fanSpeed * 100.0 / fanMax, 0), "%");
                }

                // Clocks via sysfs (more reliable than rsmi_frequencies_t across ROCm versions)
                PollSysfsClocks((int)i, prefix);
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "ROCm SMI poll error for GPU {Index}", i);
            }
        }
    }

    private void PollSysfsClocks(int deviceIndex, string prefix)
    {
        // pp_dpm_sclk format: "0: 500Mhz\n1: 800Mhz *\n2: 1200Mhz"
        // The active entry has a trailing " *"
        var sclkPath = _sysfsClockPaths[deviceIndex];
        if (sclkPath != null)
        {
            var content = ReadFile(sclkPath);
            if (content != null)
            {
                var freq = ParseActiveDpmFrequency(content);
                if (freq > 0)
                    UpdateSensor($"{prefix}/clock_graphics", freq, "MHz");
            }
        }

        var mclkPath = _sysfsMemClockPaths[deviceIndex];
        if (mclkPath != null)
        {
            var content = ReadFile(mclkPath);
            if (content != null)
            {
                var freq = ParseActiveDpmFrequency(content);
                if (freq > 0)
                    UpdateSensor($"{prefix}/clock_memory", freq, "MHz");
            }
        }
    }

    private static double ParseActiveDpmFrequency(string content)
    {
        // Find line with " *" suffix (active state)
        foreach (var line in content.Split('\n'))
        {
            if (!line.EndsWith("*")) continue;
            // e.g., "1: 1800Mhz *"
            var mhzIdx = line.IndexOf("Mhz", StringComparison.OrdinalIgnoreCase);
            if (mhzIdx < 0) mhzIdx = line.IndexOf("MHz", StringComparison.OrdinalIgnoreCase);
            if (mhzIdx < 0) continue;

            // Walk backwards from "Mhz" to find the number start
            var numEnd = mhzIdx;
            var numStart = numEnd - 1;
            while (numStart >= 0 && (char.IsDigit(line[numStart]) || line[numStart] == '.'))
                numStart--;
            numStart++;

            if (numStart < numEnd && double.TryParse(line.AsSpan(numStart, numEnd - numStart),
                NumberStyles.Any, CultureInfo.InvariantCulture, out var freq))
            {
                return freq;
            }
        }
        return 0;
    }

    public List<HwmonSensorInfo> GetSensorInfoList()
    {
        var result = new List<HwmonSensorInfo>();
        if (!_available) return result;

        for (int i = 0; i < _deviceCount; i++)
        {
            var prefix = _deviceCount == 1 ? "system/amdgpu" : $"system/amdgpu/{i}";
            var deviceName = _deviceNames[i] ?? $"AMD GPU {i}";

            foreach (var (suffix, label, category, unit) in new[]
            {
                ("temperature", "Edge Temperature", "Temperature", "°C"),
                ("temperature_junction", "Junction Temperature", "Temperature", "°C"),
                ("temperature_memory", "Memory Temperature", "Temperature", "°C"),
                ("power", "Power Draw", "Power", "W"),
                ("utilization", "GPU Utilization", "Utilization", "%"),
                ("memory_utilization", "Memory Controller", "Utilization", "%"),
                ("clock_graphics", "Graphics Clock", "Clock", "MHz"),
                ("clock_memory", "Memory Clock", "Clock", "MHz"),
                ("memory_used", "VRAM Used", "Memory", "MB"),
                ("memory_total", "VRAM Total", "Memory", "MB"),
                ("memory_percent", "VRAM Usage", "Memory", "%"),
                ("fan_speed", "Fan Speed", "Fan", "%"),
            })
            {
                var key = $"{prefix}/{suffix}";
                if (HwmonMonitor.SENSORHASH.ContainsKey(key))
                {
                    result.Add(new HwmonSensorInfo
                    {
                        SensorId = key,
                        DeviceName = $"AMD {deviceName}",
                        Category = category,
                        Label = label,
                        Unit = unit
                    });
                }
            }
        }

        return result;
    }

    private static void UpdateSensor(string sensorId, double value, string unit)
    {
        if (HwmonMonitor.SENSORHASH.TryGetValue(sensorId, out var existing))
        {
            var min = Math.Min(existing.ValueMin, value);
            var max = Math.Max(existing.ValueMax, value);
            HwmonMonitor.SENSORHASH[sensorId] = new SensorReading(min, max, (min + max) / 2.0, value, unit);
        }
        else
        {
            HwmonMonitor.SENSORHASH[sensorId] = new SensorReading(value, value, value, value, unit);
        }
    }

    private static string? ReadFile(string path)
    {
        try
        {
            if (File.Exists(path))
                return File.ReadAllText(path).Trim();
        }
        catch { }
        return null;
    }
}

// ROCm SMI P/Invoke bindings
internal enum RsmiStatus : uint
{
    Success = 0x0,
    InvalidArgs = 0x1,
    NotSupported = 0x2,
    FileError = 0x3,
    PermissionDenied = 0x4,
    OutOfResources = 0x5,
    InternalException = 0x6,
    InputOutOfBounds = 0x7,
    InitError = 0x8,
    NotYetImplemented = 0x9,
    NotFound = 0xA,
    InsufficientSize = 0xB,
    Interrupt = 0xC,
    UnexpectedSize = 0xD,
    NoData = 0xE,
    UnexpectedData = 0xF,
    Busy = 0x10,
    Refcount = 0x11,
    Unknown = 0xFFFFFFFF,
}

internal enum RsmiTemperatureType : uint
{
    Edge = 0,
    Junction = 1,
    Memory = 2,
}

internal enum RsmiTemperatureMetric : uint
{
    Current = 0,
    Max = 1,
    Min = 2,
    MaxHyst = 3,
    MinHyst = 4,
    Critical = 5,
    CriticalHyst = 6,
    Emergency = 7,
    EmergencyHyst = 8,
    CritMin = 9,
    CritMinHyst = 10,
    Offset = 11,
    Lowest = 12,
    Highest = 13,
}

internal enum RsmiMemoryType : uint
{
    Vram = 0,
    VisVram = 1,
    Gtt = 2,
}

internal enum RsmiPowerType : uint
{
    Average = 0,
    Current = 1,
    Invalid = 0xFFFFFFFF,
}

internal static class Rsmi
{
    private const string LibName = "rocm_smi64";

    [DllImport(LibName)]
    public static extern RsmiStatus rsmi_init(ulong flags);

    [DllImport(LibName)]
    public static extern RsmiStatus rsmi_shut_down();

    [DllImport(LibName)]
    public static extern RsmiStatus rsmi_num_monitor_devices(out uint count);

    [DllImport(LibName, EntryPoint = "rsmi_dev_name_get")]
    private static extern RsmiStatus rsmi_dev_name_get_native(uint dvInd, byte[] name, ulong len);

    public static RsmiStatus rsmi_dev_name_get(uint dvInd, out string name)
    {
        var buffer = new byte[256];
        var ret = rsmi_dev_name_get_native(dvInd, buffer, (ulong)buffer.Length);
        name = ret == RsmiStatus.Success
            ? System.Text.Encoding.UTF8.GetString(buffer).TrimEnd('\0')
            : "";
        return ret;
    }

    [DllImport(LibName)]
    public static extern RsmiStatus rsmi_dev_temp_metric_get(uint dvInd, uint sensorType,
        RsmiTemperatureMetric metric, out long temperature);

    public static RsmiStatus rsmi_dev_temp_metric_get(uint dvInd, RsmiTemperatureType sensorType,
        RsmiTemperatureMetric metric, out long temperature)
        => rsmi_dev_temp_metric_get(dvInd, (uint)sensorType, metric, out temperature);

    [DllImport(LibName)]
    public static extern RsmiStatus rsmi_dev_power_get(uint dvInd, out ulong power, out RsmiPowerType type);

    [DllImport(LibName)]
    public static extern RsmiStatus rsmi_dev_busy_percent_get(uint dvInd, out uint percent);

    [DllImport(LibName)]
    public static extern RsmiStatus rsmi_dev_memory_busy_percent_get(uint dvInd, out uint percent);

    [DllImport(LibName)]
    public static extern RsmiStatus rsmi_dev_memory_total_get(uint dvInd, RsmiMemoryType type, out ulong total);

    [DllImport(LibName)]
    public static extern RsmiStatus rsmi_dev_memory_usage_get(uint dvInd, RsmiMemoryType type, out ulong used);

    [DllImport(LibName)]
    public static extern RsmiStatus rsmi_dev_fan_speed_get(uint dvInd, uint sensorInd, out long speed);

    [DllImport(LibName)]
    public static extern RsmiStatus rsmi_dev_fan_speed_max_get(uint dvInd, uint sensorInd, out ulong maxSpeed);
}
