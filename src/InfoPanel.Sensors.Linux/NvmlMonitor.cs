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

    // NvAPI (libnvidia-api.so.1) side channel for hotspot/VRAM temperature and
    // core voltage; absent on the open drivers and proprietary drivers < R525.
    private NvApi? _nvApi;
    private IntPtr[] _nvApiHandles = [];
    private int[] _nvApiThermalsMasks = [];
    private bool[] _isBlackwell = [];

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

            InitializeNvApi();
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

    /// <summary>
    /// Matches each NVML device to its NvAPI handle by PCI bus number and caches
    /// the per-device thermal sensor mask and architecture generation.
    /// </summary>
    private void InitializeNvApi()
    {
        try
        {
            _nvApi = NvApi.TryCreate();
            if (_nvApi == null) return;

            _nvApiHandles = new IntPtr[_deviceCount];
            _nvApiThermalsMasks = new int[_deviceCount];
            _isBlackwell = new bool[_deviceCount];

            for (int i = 0; i < _deviceCount; i++)
            {
                if (_deviceHandles[i] == IntPtr.Zero) continue;

                var apiHandle = IntPtr.Zero;
                if (Nvml.nvmlDeviceGetPciInfo_v3(_deviceHandles[i], out NvmlPciInfo pci) == NvmlReturn.Success)
                {
                    apiHandle = _nvApi.FindGpuByBusId(pci.bus);
                }
                if (apiHandle == IntPtr.Zero && _deviceCount == 1)
                {
                    apiHandle = _nvApi.SingleGpuHandle;
                }
                if (apiHandle == IntPtr.Zero) continue;

                _nvApiHandles[i] = apiHandle;
                _nvApiThermalsMasks[i] = _nvApi.CalculateThermalsMask(apiHandle);

                // Blackwell (arch id 10+) moved the hotspot out of the thermals query
                // and uses GDDR7; nvmlDeviceGetArchitecture needs driver R450+.
                try
                {
                    if (Nvml.nvmlDeviceGetArchitecture(_deviceHandles[i], out uint arch) == NvmlReturn.Success)
                    {
                        _isBlackwell[i] = arch != uint.MaxValue && arch >= 10;
                    }
                }
                catch (EntryPointNotFoundException) { }

                Log.Information("NvApi: GPU {Index} matched (thermals mask 0x{Mask:X}, blackwell={Blackwell})",
                    i, _nvApiThermalsMasks[i], _isBlackwell[i]);

                // Blackwell reports the hotspot only through a GPU register read, which the
                // driver restricts to root (thermals slot 9 still answers, but with the edge
                // temperature - verified on real hardware - so it must not be used instead).
                if (_isBlackwell[i])
                {
                    var (hotspot, _) = _nvApi.ReadTemperatures(apiHandle, _nvApiThermalsMasks[i], isBlackwell: true);
                    if (!hotspot.HasValue)
                    {
                        Log.Information("NvApi: GPU {Index}: hotspot temperature unavailable (Blackwell exposes it via a register read that requires root)", i);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "NvApi setup failed");
            _nvApi?.Dispose();
            _nvApi = null;
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

        _nvApi?.Dispose();
        _nvApi = null;
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

                // Power limit and draw as a percentage of it
                if (Nvml.nvmlDeviceGetPowerManagementLimit(handle, out uint limitMw) == NvmlReturn.Success && limitMw > 0)
                {
                    UpdateSensor($"{prefix}/power_limit", Math.Round(limitMw / 1000.0, 1), "W");
                    if (powerMw > 0)
                    {
                        UpdateSensor($"{prefix}/power_percent", Math.Round(powerMw * 100.0 / limitMw, 1), "%");
                    }
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
                if (Nvml.nvmlDeviceGetClockInfo(handle, NvmlClockType.Sm, out uint smClock) == NvmlReturn.Success)
                {
                    UpdateSensor($"{prefix}/clock_sm", smClock, "MHz");
                }
                if (Nvml.nvmlDeviceGetClockInfo(handle, NvmlClockType.Video, out uint videoClock) == NvmlReturn.Success)
                {
                    UpdateSensor($"{prefix}/clock_video", videoClock, "MHz");
                }

                // Performance state: P0 (max performance) .. P15 (min), reported as the number
                if (Nvml.nvmlDeviceGetPerformanceState(handle, out uint pstate) == NvmlReturn.Success && pstate <= 15)
                {
                    UpdateSensor($"{prefix}/pstate", pstate, "");
                }

                // Throttling: thermal (SW/HW thermal slowdown) and power (cap / power brake)
                if (Nvml.nvmlDeviceGetCurrentClocksThrottleReasons(handle, out ulong throttle) == NvmlReturn.Success)
                {
                    UpdateSensor($"{prefix}/throttle_thermal", (throttle & 0x60UL) != 0 ? 1 : 0, "");
                    UpdateSensor($"{prefix}/throttle_power", (throttle & 0x84UL) != 0 ? 1 : 0, "");
                }

                // Memory usage
                if (Nvml.nvmlDeviceGetMemoryInfo(handle, out NvmlMemory memInfo) == NvmlReturn.Success)
                {
                    UpdateSensor($"{prefix}/memory_used", Math.Round(memInfo.used / (1024.0 * 1024), 0), "MB");
                    UpdateSensor($"{prefix}/memory_total", Math.Round(memInfo.total / (1024.0 * 1024), 0), "MB");
                    var memPercent = memInfo.total > 0 ? Math.Round(memInfo.used * 100.0 / memInfo.total, 1) : 0;
                    UpdateSensor($"{prefix}/memory_percent", memPercent, "%");
                }

                // Fan speed (duty cycle - the only fan metric classic NVML exposes)
                if (Nvml.nvmlDeviceGetFanSpeed(handle, out uint fanSpeed) == NvmlReturn.Success)
                {
                    UpdateSensor($"{prefix}/fan_speed", fanSpeed, "%");
                }

                // Per-fan tachometer RPM: nvmlDeviceGetFanSpeedRPM exists from driver
                // R550+; on older drivers the entry point is missing and we stop trying.
                if (_fanRpmSupported)
                {
                    try
                    {
                        uint numFans = 1;
                        Nvml.nvmlDeviceGetNumFans(handle, ref numFans);
                        numFans = Math.Min(numFans, 8);
                        for (uint fan = 0; fan < numFans; fan++)
                        {
                            var info = new NvmlFanSpeedInfo
                            {
                                version = NvmlFanSpeedInfo.Version1,
                                fan = fan,
                            };
                            if (Nvml.nvmlDeviceGetFanSpeedRPM(handle, ref info) == NvmlReturn.Success)
                            {
                                UpdateSensor($"{prefix}/fan{fan}_rpm", info.speed, "RPM");
                            }
                        }
                    }
                    catch (EntryPointNotFoundException)
                    {
                        _fanRpmSupported = false;
                        Log.Debug("NVML: fan RPM API not available in this driver");
                    }
                }

                // NvAPI extras: hotspot temperature, VRAM temperature, core voltage
                if (_nvApi != null && i < _nvApiHandles.Length && _nvApiHandles[i] != IntPtr.Zero)
                {
                    var (hotspot, vram) = _nvApi.ReadTemperatures(_nvApiHandles[i], _nvApiThermalsMasks[i], _isBlackwell[i]);
                    if (hotspot.HasValue)
                    {
                        UpdateSensor($"{prefix}/temperature_hotspot", hotspot.Value, "°C");
                    }
                    if (vram.HasValue)
                    {
                        UpdateSensor($"{prefix}/temperature_vram", vram.Value, "°C");
                    }

                    var voltageMv = _nvApi.ReadVoltageMv(_nvApiHandles[i]);
                    if (voltageMv.HasValue)
                    {
                        UpdateSensor($"{prefix}/voltage", Math.Round(voltageMv.Value / 1000.0, 3), "V");
                    }
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
                ("temperature_hotspot", "Hotspot Temperature", "Temperature", "°C"),
                ("temperature_vram", "VRAM Temperature", "Temperature", "°C"),
                ("power", "Power Draw", "Power", "W"),
                ("power_limit", "Power Limit", "Power", "W"),
                ("power_percent", "Power (% of limit)", "Power", "%"),
                ("voltage", "Core Voltage", "Voltage", "V"),
                ("utilization", "GPU Utilization", "Utilization", "%"),
                ("memory_utilization", "Memory Controller", "Utilization", "%"),
                ("clock_graphics", "Graphics Clock", "Clock", "MHz"),
                ("clock_memory", "Memory Clock", "Clock", "MHz"),
                ("clock_sm", "SM Clock", "Clock", "MHz"),
                ("clock_video", "Video Clock", "Clock", "MHz"),
                ("pstate", "Performance State", "Status", ""),
                ("throttle_thermal", "Thermal Throttling", "Status", ""),
                ("throttle_power", "Power Throttling", "Status", ""),
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

            for (int fan = 0; fan < 8; fan++)
            {
                var key = $"{prefix}/fan{fan}_rpm";
                if (HwmonMonitor.SENSORHASH.ContainsKey(key))
                {
                    result.Add(new HwmonSensorInfo
                    {
                        SensorId = key,
                        DeviceName = $"NVIDIA {deviceName}",
                        Category = "Fan",
                        Label = $"Fan {fan + 1} RPM",
                        Unit = "RPM"
                    });
                }
            }
        }

        return result;
    }

    private static bool _fanRpmSupported = true;

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

    [DllImport(LibName, EntryPoint = "nvmlDeviceGetNumFans")]
    public static extern NvmlReturn nvmlDeviceGetNumFans(IntPtr device, ref uint numFans);

    [DllImport(LibName, EntryPoint = "nvmlDeviceGetFanSpeedRPM")]
    public static extern NvmlReturn nvmlDeviceGetFanSpeedRPM(IntPtr device, ref NvmlFanSpeedInfo fanSpeed);

    [DllImport(LibName, EntryPoint = "nvmlDeviceGetPowerManagementLimit")]
    public static extern NvmlReturn nvmlDeviceGetPowerManagementLimit(IntPtr device, out uint limit);

    [DllImport(LibName, EntryPoint = "nvmlDeviceGetPerformanceState")]
    public static extern NvmlReturn nvmlDeviceGetPerformanceState(IntPtr device, out uint pstate);

    [DllImport(LibName, EntryPoint = "nvmlDeviceGetCurrentClocksThrottleReasons")]
    public static extern NvmlReturn nvmlDeviceGetCurrentClocksThrottleReasons(IntPtr device, out ulong reasons);

    [DllImport(LibName, EntryPoint = "nvmlDeviceGetPciInfo_v3")]
    public static extern NvmlReturn nvmlDeviceGetPciInfo_v3(IntPtr device, out NvmlPciInfo pci);

    [DllImport(LibName, EntryPoint = "nvmlDeviceGetArchitecture")]
    public static extern NvmlReturn nvmlDeviceGetArchitecture(IntPtr device, out uint arch);
}

[StructLayout(LayoutKind.Sequential)]
internal struct NvmlPciInfo
{
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
    public byte[] busIdLegacy;
    public uint domain;
    public uint bus;
    public uint device;
    public uint pciDeviceId;
    public uint pciSubSystemId;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
    public byte[] busId;
}

/// <summary>Versioned struct for nvmlDeviceGetFanSpeedRPM (driver R550+).</summary>
[StructLayout(LayoutKind.Sequential)]
public struct NvmlFanSpeedInfo
{
    /// <summary>NVML versioned-API convention: sizeof(struct) | (version &lt;&lt; 24).</summary>
    public const uint Version1 = 12 | (1u << 24);

    public uint version;
    public uint fan;
    public uint speed; // RPM
}
