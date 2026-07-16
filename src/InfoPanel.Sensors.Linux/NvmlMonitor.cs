using InfoPanel.Models;
using Serilog;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace InfoPanel.Services;

public class NvmlMonitor
{
    private static readonly Lazy<NvmlMonitor> _instance = new(() => new NvmlMonitor());
    public static NvmlMonitor Instance => _instance.Value;

    private bool _available;
    private bool _initialized;
    private int _deviceCount;
    private IntPtr[] _deviceHandles = [];
    private string[] _deviceNames = [];

    private NvmlMonitor() { }

    public void Initialize()
    {
        try
        {
            var ret = Nvml.nvmlInit_v2();
            if (ret != NvmlReturn.Success)
            {
                Log.Debug("NVML init failed: {Result}", ret);
                return;
            }

            _initialized = true;

            ret = Nvml.nvmlDeviceGetCount_v2(out uint count);
            if (ret != NvmlReturn.Success || count == 0)
            {
                Log.Debug("NVML: no devices found ({Result})", ret);
                Nvml.nvmlShutdown();
                _initialized = false;
                return;
            }

            _deviceCount = (int)count;
            _deviceHandles = new IntPtr[_deviceCount];
            _deviceNames = new string[_deviceCount];

            for (int i = 0; i < _deviceCount; i++)
            {
                ret = Nvml.nvmlDeviceGetHandleByIndex_v2((uint)i, out _deviceHandles[i]);
                if (ret != NvmlReturn.Success)
                {
                    Log.Debug("NVML: failed to get handle for GPU {Index}: {Result}", i, ret);
                    continue;
                }

                ret = Nvml.nvmlDeviceGetName(_deviceHandles[i], out var name);
                _deviceNames[i] = ret == NvmlReturn.Success ? name : $"GPU {i}";
            }

            _available = true;
            Log.Information("NVML initialized: {Count} GPU(s) found", _deviceCount);
        }
        catch (DllNotFoundException)
        {
            Log.Debug("NVML library not found (no NVIDIA driver installed)");
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "NVML initialization error");
        }
    }

    public void Shutdown()
    {
        if (_initialized)
        {
            try { Nvml.nvmlShutdown(); } catch { }
            _initialized = false;
            _available = false;
        }
    }

    public void Poll()
    {
        if (!_available) return;

        for (int i = 0; i < _deviceCount; i++)
        {
            var handle = _deviceHandles[i];
            if (handle == IntPtr.Zero) continue;

            var prefix = _deviceCount == 1 ? "system/gpu" : $"system/gpu/{i}";

            try
            {
                // Temperature
                if (Nvml.nvmlDeviceGetTemperature(handle, NvmlTemperatureSensor.Gpu, out uint temp) == NvmlReturn.Success)
                {
                    UpdateSensor($"{prefix}/temperature", temp, "°C");
                }

                // Power
                if (Nvml.nvmlDeviceGetPowerUsage(handle, out uint powerMw) == NvmlReturn.Success)
                {
                    UpdateSensor($"{prefix}/power", Math.Round(powerMw / 1000.0, 1), "W");
                }

                // Utilization
                if (Nvml.nvmlDeviceGetUtilizationRates(handle, out NvmlUtilization util) == NvmlReturn.Success)
                {
                    UpdateSensor($"{prefix}/utilization", util.gpu, "%");
                    UpdateSensor($"{prefix}/memory_utilization", util.memory, "%");
                }

                // Clock speeds
                if (Nvml.nvmlDeviceGetClockInfo(handle, NvmlClockType.Graphics, out uint graphicsClock) == NvmlReturn.Success)
                {
                    UpdateSensor($"{prefix}/clock_graphics", graphicsClock, "MHz");
                }
                if (Nvml.nvmlDeviceGetClockInfo(handle, NvmlClockType.Mem, out uint memClock) == NvmlReturn.Success)
                {
                    UpdateSensor($"{prefix}/clock_memory", memClock, "MHz");
                }

                // Memory usage
                if (Nvml.nvmlDeviceGetMemoryInfo(handle, out NvmlMemory memInfo) == NvmlReturn.Success)
                {
                    UpdateSensor($"{prefix}/memory_used", Math.Round(memInfo.used / (1024.0 * 1024), 0), "MB");
                    UpdateSensor($"{prefix}/memory_total", Math.Round(memInfo.total / (1024.0 * 1024), 0), "MB");
                    var memPercent = memInfo.total > 0 ? Math.Round(memInfo.used * 100.0 / memInfo.total, 1) : 0;
                    UpdateSensor($"{prefix}/memory_percent", memPercent, "%");
                }

                // Fan speed
                if (Nvml.nvmlDeviceGetFanSpeed(handle, out uint fanSpeed) == NvmlReturn.Success)
                {
                    UpdateSensor($"{prefix}/fan_speed", fanSpeed, "%");
                }
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "NVML poll error for GPU {Index}", i);
            }
        }
    }

    public List<HwmonSensorInfo> GetSensorInfoList()
    {
        var result = new List<HwmonSensorInfo>();
        if (!_available) return result;

        for (int i = 0; i < _deviceCount; i++)
        {
            var prefix = _deviceCount == 1 ? "system/gpu" : $"system/gpu/{i}";
            var deviceName = _deviceNames[i] ?? $"GPU {i}";

            foreach (var (suffix, label, category, unit) in new[]
            {
                ("temperature", "Temperature", "Temperature", "°C"),
                ("power", "Power Draw", "Power", "W"),
                ("utilization", "GPU Utilization", "Utilization", "%"),
                ("memory_utilization", "Memory Controller", "Utilization", "%"),
                ("clock_graphics", "Graphics Clock", "Clock", "MHz"),
                ("clock_memory", "Memory Clock", "Clock", "MHz"),
                ("memory_used", "Memory Used", "Memory", "MB"),
                ("memory_total", "Memory Total", "Memory", "MB"),
                ("memory_percent", "Memory Usage", "Memory", "%"),
                ("fan_speed", "Fan Speed", "Fan", "%"),
            })
            {
                var key = $"{prefix}/{suffix}";
                if (HwmonMonitor.SENSORHASH.ContainsKey(key))
                {
                    result.Add(new HwmonSensorInfo
                    {
                        SensorId = key,
                        DeviceName = $"NVIDIA {deviceName}",
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
}

// NVML P/Invoke bindings
internal enum NvmlReturn : uint
{
    Success = 0,
    Uninitialized = 1,
    InvalidArgument = 2,
    NotSupported = 3,
    NoPermission = 4,
    NotFound = 6,
    InsufficientSize = 7,
    InsufficientPower = 8,
    GpuIsLost = 9,
    Unknown = 999,
}

internal enum NvmlTemperatureSensor : uint
{
    Gpu = 0,
}

internal enum NvmlClockType : uint
{
    Graphics = 0,
    Sm = 1,
    Mem = 2,
    Video = 3,
}

[StructLayout(LayoutKind.Sequential)]
internal struct NvmlUtilization
{
    public uint gpu;
    public uint memory;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NvmlMemory
{
    public ulong total;
    public ulong free;
    public ulong used;
}

internal static class Nvml
{
    private const string LibName = "nvidia-ml";

    [DllImport(LibName, EntryPoint = "nvmlInit_v2")]
    public static extern NvmlReturn nvmlInit_v2();

    [DllImport(LibName, EntryPoint = "nvmlShutdown")]
    public static extern NvmlReturn nvmlShutdown();

    [DllImport(LibName, EntryPoint = "nvmlDeviceGetCount_v2")]
    public static extern NvmlReturn nvmlDeviceGetCount_v2(out uint deviceCount);

    [DllImport(LibName, EntryPoint = "nvmlDeviceGetHandleByIndex_v2")]
    public static extern NvmlReturn nvmlDeviceGetHandleByIndex_v2(uint index, out IntPtr device);

    [DllImport(LibName, EntryPoint = "nvmlDeviceGetName", CharSet = CharSet.Ansi)]
    private static extern NvmlReturn nvmlDeviceGetName_native(IntPtr device, byte[] name, uint length);

    public static NvmlReturn nvmlDeviceGetName(IntPtr device, out string name)
    {
        var buffer = new byte[96];
        var ret = nvmlDeviceGetName_native(device, buffer, (uint)buffer.Length);
        name = ret == NvmlReturn.Success
            ? System.Text.Encoding.UTF8.GetString(buffer).TrimEnd('\0')
            : "";
        return ret;
    }

    [DllImport(LibName, EntryPoint = "nvmlDeviceGetTemperature")]
    public static extern NvmlReturn nvmlDeviceGetTemperature(IntPtr device, NvmlTemperatureSensor sensorType, out uint temp);

    [DllImport(LibName, EntryPoint = "nvmlDeviceGetPowerUsage")]
    public static extern NvmlReturn nvmlDeviceGetPowerUsage(IntPtr device, out uint power);

    [DllImport(LibName, EntryPoint = "nvmlDeviceGetUtilizationRates")]
    public static extern NvmlReturn nvmlDeviceGetUtilizationRates(IntPtr device, out NvmlUtilization utilization);

    [DllImport(LibName, EntryPoint = "nvmlDeviceGetClockInfo")]
    public static extern NvmlReturn nvmlDeviceGetClockInfo(IntPtr device, NvmlClockType type, out uint clock);

    [DllImport(LibName, EntryPoint = "nvmlDeviceGetMemoryInfo")]
    public static extern NvmlReturn nvmlDeviceGetMemoryInfo(IntPtr device, out NvmlMemory memory);

    [DllImport(LibName, EntryPoint = "nvmlDeviceGetFanSpeed")]
    public static extern NvmlReturn nvmlDeviceGetFanSpeed(IntPtr device, out uint speed);
}
