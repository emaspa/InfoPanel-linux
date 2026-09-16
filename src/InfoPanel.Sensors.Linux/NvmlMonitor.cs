using InfoPanel.Models;
using InfoPanel.Sensors;
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
    private SensorId?[] _deviceIds = [];
    private uint[] _fanCounts = [];
    private SensorCatalogSnapshot _catalog = new(0, []);
    private readonly HashSet<string> _updated = new(StringComparer.Ordinal);

    // NvAPI (libnvidia-api.so.1) side channel for hotspot/VRAM temperature and
    // core voltage; absent on the open drivers and proprietary drivers < R525.
    private NvApi? _nvApi;
    private IntPtr[] _nvApiHandles = [];
    private int[] _nvApiThermalsMasks = [];
    private bool[] _isBlackwell = [];

    private NvmlMonitor() { }

    public void Initialize()
    {
        if (_initialized) return;
        try
        {
            var ret = Nvml.nvmlInit_v2();
            if (ret != NvmlReturn.Success)
            {
                Log.Debug("NVML init failed: {Result}", ret);
                return;
            }

            _initialized = true;
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

    private void RefreshDevices()
    {
        _available = false;
        var ret = Nvml.nvmlDeviceGetCount_v2(out uint count);
        if (ret != NvmlReturn.Success || count == 0)
        {
            Log.Debug("NVML: no devices found ({Result})", ret);
            return;
        }

        _deviceCount = (int)count;
        _deviceHandles = new IntPtr[_deviceCount];
        _deviceNames = new string[_deviceCount];
        _deviceIds = new SensorId?[_deviceCount];
        _fanCounts = new uint[_deviceCount];

        for (int i = 0; i < _deviceCount; i++)
        {
            ret = Nvml.nvmlDeviceGetHandleByIndex_v2((uint)i, out _deviceHandles[i]);
            if (ret != NvmlReturn.Success)
            {
                Log.Debug("NVML: failed to get handle for GPU {Index}: {Result}", i, ret);
                continue;
            }

            ret = Nvml.nvmlDeviceGetName(_deviceHandles[i], out var name);
            _deviceNames[i] = ret == NvmlReturn.Success ? name : "NVIDIA GPU";
            string? uuid = null, pciAddress = null;
            try { if (Nvml.nvmlDeviceGetUUID(_deviceHandles[i], out var value) == NvmlReturn.Success) uuid = value; }
            catch (EntryPointNotFoundException) { }
            try
            {
                if (Nvml.nvmlDeviceGetPciInfo_v3(_deviceHandles[i], out var pci) == NvmlReturn.Success)
                    pciAddress = System.Text.Encoding.ASCII.GetString(pci.busId).TrimEnd('\0');
            }
            catch (EntryPointNotFoundException) { }
            _deviceIds[i] = SystemSensorIdentity.Nvidia(uuid, pciAddress, "temperature");
            try { Nvml.nvmlDeviceGetNumFans(_deviceHandles[i], ref _fanCounts[i]); }
            catch (EntryPointNotFoundException) { }
            _fanCounts[i] = Math.Min(_fanCounts[i], 8);
            if (_deviceIds[i] == null) Log.Warning("NVML GPU {Index} has no UUID or PCI identity; excluded", i);
        }

        _available = true;

        _nvApi?.Dispose();
        _nvApi = null;
        _nvApiHandles = [];
        InitializeNvApi();
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
        foreach (var descriptor in _catalog.Descriptors) HwmonMonitor.SENSORHASH.TryRemove(descriptor.StableId, out _);
        _catalog = new(_catalog.Generation + 1, []);
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
        _updated.Clear();

        for (int i = 0; i < _deviceCount; i++)
        {
            var handle = _deviceHandles[i];
            if (handle == IntPtr.Zero) continue;

            if (_deviceIds[i] is not { } id || !_catalog.StableIdIndex.ContainsKey(id.Value)) continue;
            var prefix = id.Value[..id.Value.LastIndexOf('/')];
            if (!_catalog.Descriptors.Any(d => d.Id.Identity == id.Identity && SensorDemand.IsHwmonUsed(d.StableId))) continue;

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
        foreach (var descriptor in _catalog.Descriptors)
            if (!_updated.Contains(descriptor.StableId)) HwmonMonitor.SENSORHASH.TryRemove(descriptor.StableId, out _);

    }

    private static readonly (string Metric, string Label, string Category, string Unit)[] Metrics =
    [
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
    ];

    public List<HwmonSensorInfo> GetSensorInfoList() => _catalog.Descriptors.Select(SystemSensorIdentity.Info).ToList();

    public IEnumerable<SensorDescriptor> ScanDescriptors()
    {
        var descriptors = new List<SensorDescriptor>();
        if (!_initialized) Initialize();
        try
        {
            if (_initialized) RefreshDevices();
            if (_available)
            {
                for (var i = 0; i < _deviceCount; i++)
                {
                    if (_deviceIds[i] is not { } id) continue;
                    var prefix = id.Value[..id.Value.LastIndexOf('/')];
                    var alias = _deviceCount == 1 ? "system/gpu" : $"system/gpu/{i}";
                    foreach (var metric in Metrics)
                        descriptors.Add(SystemSensorIdentity.Descriptor(SensorId.Parse($"{prefix}/{metric.Metric}"),
                            $"{alias}/{metric.Metric}", "NVIDIA GPU", $"{_deviceNames[i]} {metric.Label}", metric.Category, metric.Unit));
                    for (var fan = 0; fan < _fanCounts[i]; fan++)
                        descriptors.Add(SystemSensorIdentity.Descriptor(SensorId.Parse($"{prefix}/fan{fan}_rpm"),
                            $"{alias}/fan{fan}_rpm", "NVIDIA GPU", $"{_deviceNames[i]} Fan {fan + 1} RPM", "Fan", "RPM"));
                }
            }
        }
        catch (Exception ex) { _available = false; Log.Debug(ex, "GPU discovery failed"); }
        var catalog = new SensorCatalogSnapshot(_catalog.Generation + 1, descriptors);
        foreach (var old in _catalog.Descriptors)
            if (!catalog.StableIdIndex.ContainsKey(old.StableId)) HwmonMonitor.SENSORHASH.TryRemove(old.StableId, out _);
        _catalog = catalog;
        return catalog.Descriptors;
    }

    private static bool _fanRpmSupported = true;

    private void UpdateSensor(string sensorId, double value, string unit)
    {
        if (!_catalog.StableIdIndex.ContainsKey(sensorId) || !SensorDemand.IsHwmonUsed(sensorId)) return;
        _updated.Add(sensorId);
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

    [DllImport(LibName, EntryPoint = "nvmlDeviceGetUUID")]
    private static extern NvmlReturn nvmlDeviceGetUUID_native(IntPtr device, byte[] uuid, uint length);

    public static NvmlReturn nvmlDeviceGetUUID(IntPtr device, out string uuid)
    {
        var buffer = new byte[96];
        var ret = nvmlDeviceGetUUID_native(device, buffer, (uint)buffer.Length);
        uuid = ret == NvmlReturn.Success ? System.Text.Encoding.UTF8.GetString(buffer).TrimEnd('\0') : "";
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
