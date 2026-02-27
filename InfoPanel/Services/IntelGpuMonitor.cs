using InfoPanel.Models;
using Serilog;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;

namespace InfoPanel.Services;

/// <summary>
/// Intel GPU monitor using i915/xe PMU (Performance Monitoring Unit) via perf_event_open.
/// Reads GPU utilization, frequency, and RC6 residency from kernel perf counters.
/// Also reads sysfs for frequency limits and xe driver discrete GPU support.
/// </summary>
public class IntelGpuMonitor
{
    private static readonly Lazy<IntelGpuMonitor> _instance = new(() => new IntelGpuMonitor());
    public static IntelGpuMonitor Instance => _instance.Value;

    private bool _available;
    private string _gpuName = "Intel GPU";
    private string _cardPath = "";

    // PMU file descriptors
    private int _fdActualFreq = -1;
    private int _fdRequestedFreq = -1;
    private int _fdRc6Residency = -1;

    // Engine busy FDs: (engineName, fd)
    private List<(string name, int fd)> _engineFds = new();

    // Previous values for delta computation
    private long _prevRc6Ns;
    private long _prevTimestampNs;
    private Dictionary<string, long> _prevEngineBusyNs = new();

    // PMU type from sysfs
    private uint _pmuType;

    private IntelGpuMonitor() { }

    public void Initialize()
    {
        try
        {
            // Find Intel GPU card in DRM
            if (!FindIntelGpu()) return;

            // Try PMU-based monitoring (requires permissions)
            if (ReadPmuType())
            {
                OpenPerfEvents();

                if (_fdActualFreq >= 0 || _engineFds.Count > 0)
                {
                    PrimeDeltaState();
                    Log.Information("Intel GPU monitor initialized with PMU: {Name}, {EngineCount} engines", _gpuName, _engineFds.Count);
                }
                else
                {
                    Log.Debug("Intel GPU: PMU type found but no perf events opened (permissions?)");
                }
            }

            // Available if we have PMU events OR sysfs frequency files
            _available = _fdActualFreq >= 0 || _engineFds.Count > 0
                || File.Exists(Path.Combine(_cardPath, "gt_act_freq_mhz"))
                || File.Exists(Path.Combine(_cardPath, "gt_cur_freq_mhz"));

            if (_available)
                Log.Information("Intel GPU monitor initialized: {Name}", _gpuName);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Intel GPU monitor initialization failed");
        }
    }

    private bool FindIntelGpu()
    {
        const string drmPath = "/sys/class/drm";
        if (!Directory.Exists(drmPath)) return false;

        foreach (var cardDir in Directory.GetDirectories(drmPath, "card*"))
        {
            if (Path.GetFileName(cardDir).Contains('-')) continue; // skip connectors

            var vendorFile = Path.Combine(cardDir, "device", "vendor");
            var vendor = ReadFile(vendorFile);
            if (vendor != "0x8086") continue; // Intel

            _cardPath = cardDir;

            // Try to get device name
            var deviceId = ReadFile(Path.Combine(cardDir, "device", "device")) ?? "";
            _gpuName = $"Intel GPU [{deviceId.TrimStart('0').TrimStart('x')}]";

            // Check driver
            var uevent = ReadFile(Path.Combine(cardDir, "device", "uevent"));
            if (uevent != null && uevent.Contains("DRIVER=xe"))
            {
                _gpuName = $"Intel GPU [{deviceId}] (xe)";
            }

            return true;
        }

        return false;
    }

    private bool ReadPmuType()
    {
        // Try i915 PMU first, then xe
        foreach (var pmuName in new[] { "i915", "xe" })
        {
            var typePath = $"/sys/bus/event_source/devices/{pmuName}/type";
            var typeStr = ReadFile(typePath);
            if (typeStr != null && uint.TryParse(typeStr, out var type))
            {
                _pmuType = type;
                Log.Debug("Intel GPU PMU type: {Type} ({Name})", type, pmuName);

                // For xe driver, check for xe_0000:xx:xx.x format
                return true;
            }
        }

        // xe driver may use PCI-specific names like xe_0000:03:00.0
        try
        {
            var eventSourcePath = "/sys/bus/event_source/devices";
            if (Directory.Exists(eventSourcePath))
            {
                foreach (var dir in Directory.GetDirectories(eventSourcePath, "xe_*"))
                {
                    var typePath = Path.Combine(dir, "type");
                    var typeStr = ReadFile(typePath);
                    if (typeStr != null && uint.TryParse(typeStr, out var type))
                    {
                        _pmuType = type;
                        Log.Debug("Intel GPU PMU type: {Type} ({Name})", type, Path.GetFileName(dir));
                        return true;
                    }
                }
            }
        }
        catch { }

        Log.Debug("Intel GPU: no i915/xe PMU found");
        return false;
    }

    private void OpenPerfEvents()
    {
        // Actual frequency (reports MHz, sampled)
        _fdActualFreq = OpenPerfEvent(I915PmuConfig.ActualFrequency);
        if (_fdActualFreq < 0)
            Log.Debug("Intel GPU: failed to open actual-frequency event");

        // Requested frequency
        _fdRequestedFreq = OpenPerfEvent(I915PmuConfig.RequestedFrequency);

        // RC6 residency (nanoseconds, cumulative)
        _fdRc6Residency = OpenPerfEvent(I915PmuConfig.Rc6Residency);

        // Engine busy events - try common engines
        foreach (var (name, cls, inst) in new[]
        {
            ("render", 0, 0),   // rcs0
            ("copy", 1, 0),     // bcs0
            ("video", 2, 0),    // vcs0
            ("video", 2, 1),    // vcs1
            ("video-enhance", 3, 0), // vecs0
            ("compute", 4, 0),  // ccs0
            ("compute", 4, 1),  // ccs1
            ("compute", 4, 2),  // ccs2
            ("compute", 4, 3),  // ccs3
        })
        {
            var config = I915PmuConfig.EngineBusy(cls, inst);
            var fd = OpenPerfEvent(config);
            if (fd >= 0)
            {
                var label = inst > 0 ? $"{name}{inst}" : name;
                _engineFds.Add((label, fd));
            }
        }
    }

    private int OpenPerfEvent(ulong config)
    {
        // struct perf_event_attr (we need at least 112 bytes = VER5)
        var attr = new byte[112];

        // type (offset 0, uint32)
        BitConverter.TryWriteBytes(attr.AsSpan(0), _pmuType);
        // size (offset 4, uint32)
        BitConverter.TryWriteBytes(attr.AsSpan(4), (uint)112);
        // config (offset 8, uint64)
        BitConverter.TryWriteBytes(attr.AsSpan(8), config);

        var fd = PerfEventOpen(attr, -1, 0, -1, 0);
        return fd;
    }

    private void PrimeDeltaState()
    {
        _prevTimestampNs = GetTimestampNs();

        if (_fdRc6Residency >= 0)
        {
            ReadPerfCounter(_fdRc6Residency, out _prevRc6Ns);
        }

        foreach (var (name, fd) in _engineFds)
        {
            if (ReadPerfCounter(fd, out var val))
            {
                _prevEngineBusyNs[name] = val;
            }
        }
    }

    public void Shutdown()
    {
        if (_fdActualFreq >= 0) { close(_fdActualFreq); _fdActualFreq = -1; }
        if (_fdRequestedFreq >= 0) { close(_fdRequestedFreq); _fdRequestedFreq = -1; }
        if (_fdRc6Residency >= 0) { close(_fdRc6Residency); _fdRc6Residency = -1; }
        foreach (var (_, fd) in _engineFds)
        {
            close(fd);
        }
        _engineFds.Clear();
        _available = false;
    }

    public void Poll()
    {
        if (!_available) return;

        var nowNs = GetTimestampNs();
        var deltaNs = nowNs - _prevTimestampNs;
        if (deltaNs <= 0) deltaNs = 1;

        try
        {
            // PMU-based sensors (require perf_event_open permissions)
            if (_fdActualFreq >= 0 && ReadPerfCounter(_fdActualFreq, out var actualFreqRaw))
            {
                UpdateSensor("system/igpu/frequency", actualFreqRaw, "MHz");
            }

            if (_fdRequestedFreq >= 0 && ReadPerfCounter(_fdRequestedFreq, out var reqFreqRaw))
            {
                UpdateSensor("system/igpu/req_frequency", reqFreqRaw, "MHz");
            }

            if (_fdRc6Residency >= 0 && ReadPerfCounter(_fdRc6Residency, out var rc6Ns))
            {
                var deltaRc6 = rc6Ns - _prevRc6Ns;
                if (deltaRc6 >= 0 && deltaNs > 0)
                {
                    var rc6Percent = Math.Round(deltaRc6 * 100.0 / deltaNs, 1);
                    rc6Percent = Math.Clamp(rc6Percent, 0, 100);
                    UpdateSensor("system/igpu/rc6_percent", rc6Percent, "%");
                    UpdateSensor("system/igpu/utilization", Math.Round(100 - rc6Percent, 1), "%");
                }
                _prevRc6Ns = rc6Ns;
            }

            foreach (var (name, fd) in _engineFds)
            {
                if (ReadPerfCounter(fd, out var busyNs))
                {
                    if (_prevEngineBusyNs.TryGetValue(name, out var prevBusyNs))
                    {
                        var deltaBusy = busyNs - prevBusyNs;
                        if (deltaBusy >= 0 && deltaNs > 0)
                        {
                            var busyPercent = Math.Round(deltaBusy * 100.0 / deltaNs, 1);
                            busyPercent = Math.Clamp(busyPercent, 0, 100);
                            UpdateSensor($"system/igpu/engine/{name}", busyPercent, "%");
                        }
                    }
                    _prevEngineBusyNs[name] = busyNs;
                }
            }

            // Sysfs frequency (always works without special permissions)
            PollSysfsFrequency();
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Intel GPU poll error");
        }

        _prevTimestampNs = nowNs;
    }

    private void PollSysfsFrequency()
    {
        if (string.IsNullOrEmpty(_cardPath)) return;

        // i915 sysfs
        var minFreq = ReadFile(Path.Combine(_cardPath, "gt_min_freq_mhz"));
        var maxFreq = ReadFile(Path.Combine(_cardPath, "gt_max_freq_mhz"));
        var boostFreq = ReadFile(Path.Combine(_cardPath, "gt_boost_freq_mhz"));
        var actFreq = ReadFile(Path.Combine(_cardPath, "gt_act_freq_mhz"));
        var curFreq = ReadFile(Path.Combine(_cardPath, "gt_cur_freq_mhz"));

        if (actFreq != null && double.TryParse(actFreq, NumberStyles.Any, CultureInfo.InvariantCulture, out var act))
            UpdateSensor("system/igpu/frequency", act, "MHz");
        else if (curFreq != null && double.TryParse(curFreq, NumberStyles.Any, CultureInfo.InvariantCulture, out var cur))
            UpdateSensor("system/igpu/frequency", cur, "MHz");

        if (minFreq != null && double.TryParse(minFreq, NumberStyles.Any, CultureInfo.InvariantCulture, out var min))
            UpdateSensor("system/igpu/min_frequency", min, "MHz");
        if (maxFreq != null && double.TryParse(maxFreq, NumberStyles.Any, CultureInfo.InvariantCulture, out var max))
            UpdateSensor("system/igpu/max_frequency", max, "MHz");
        if (boostFreq != null && double.TryParse(boostFreq, NumberStyles.Any, CultureInfo.InvariantCulture, out var boost))
            UpdateSensor("system/igpu/boost_frequency", boost, "MHz");

        // xe driver: tile/gt layout
        try
        {
            var deviceDir = Path.Combine(_cardPath, "device");
            if (Directory.Exists(deviceDir))
            {
                foreach (var tileDir in Directory.GetDirectories(deviceDir, "tile*"))
                {
                    foreach (var gtDir in Directory.GetDirectories(tileDir, "gt*"))
                    {
                        foreach (var freqDir in Directory.GetDirectories(gtDir, "freq*"))
                        {
                            var xeActFreq = ReadFile(Path.Combine(freqDir, "act_freq"));
                            var xeCurFreq = ReadFile(Path.Combine(freqDir, "cur_freq"));
                            var xeMinFreq = ReadFile(Path.Combine(freqDir, "min_freq"));
                            var xeMaxFreq = ReadFile(Path.Combine(freqDir, "max_freq"));

                            if (xeActFreq != null && double.TryParse(xeActFreq, NumberStyles.Any, CultureInfo.InvariantCulture, out var xeAct))
                                UpdateSensor("system/igpu/frequency", xeAct, "MHz");
                            else if (xeCurFreq != null && double.TryParse(xeCurFreq, NumberStyles.Any, CultureInfo.InvariantCulture, out var xeCur))
                                UpdateSensor("system/igpu/frequency", xeCur, "MHz");

                            if (xeMinFreq != null && double.TryParse(xeMinFreq, NumberStyles.Any, CultureInfo.InvariantCulture, out var xeMin))
                                UpdateSensor("system/igpu/min_frequency", xeMin, "MHz");
                            if (xeMaxFreq != null && double.TryParse(xeMaxFreq, NumberStyles.Any, CultureInfo.InvariantCulture, out var xeMax))
                                UpdateSensor("system/igpu/max_frequency", xeMax, "MHz");

                            return; // only first freq node
                        }
                    }
                }
            }
        }
        catch { }
    }

    private static bool ReadPerfCounter(int fd, out long value)
    {
        value = 0;
        var buf = new byte[8];
        var n = read(fd, buf, 8);
        if (n == 8)
        {
            value = BitConverter.ToInt64(buf, 0);
            return true;
        }
        return false;
    }

    private static long GetTimestampNs()
    {
        // Use CLOCK_MONOTONIC for consistency with perf
        clock_gettime(1 /* CLOCK_MONOTONIC */, out var ts);
        return ts.tv_sec * 1_000_000_000L + ts.tv_nsec;
    }

    public List<HwmonSensorInfo> GetSensorInfoList()
    {
        var result = new List<HwmonSensorInfo>();
        if (!_available && string.IsNullOrEmpty(_cardPath)) return result;

        foreach (var (suffix, label, category, unit) in new[]
        {
            ("frequency", "GPU Frequency", "Frequency", "MHz"),
            ("req_frequency", "Requested Frequency", "Frequency", "MHz"),
            ("min_frequency", "Min Frequency", "Frequency", "MHz"),
            ("max_frequency", "Max Frequency", "Frequency", "MHz"),
            ("boost_frequency", "Boost Frequency", "Frequency", "MHz"),
            ("utilization", "GPU Utilization", "Utilization", "%"),
            ("rc6_percent", "RC6 Residency (Idle)", "Power", "%"),
        })
        {
            var key = $"system/igpu/{suffix}";
            if (HwmonMonitor.SENSORHASH.ContainsKey(key))
            {
                result.Add(new HwmonSensorInfo
                {
                    SensorId = key,
                    DeviceName = _gpuName,
                    Category = category,
                    Label = label,
                    Unit = unit
                });
            }
        }

        // Per-engine utilization
        foreach (var (sensorId, _) in HwmonMonitor.SENSORHASH)
        {
            if (!sensorId.StartsWith("system/igpu/engine/")) continue;
            var engineName = sensorId.Substring("system/igpu/engine/".Length);

            result.Add(new HwmonSensorInfo
            {
                SensorId = sensorId,
                DeviceName = _gpuName,
                Category = "Engine Utilization",
                Label = $"{engineName} Busy",
                Unit = "%"
            });
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

    // --- P/Invoke ---

    [DllImport("libc", SetLastError = true)]
    private static extern int close(int fd);

    [DllImport("libc", SetLastError = true)]
    private static extern long read(int fd, byte[] buf, long count);

    [StructLayout(LayoutKind.Sequential)]
    private struct Timespec
    {
        public long tv_sec;
        public long tv_nsec;
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int clock_gettime(int clockId, out Timespec ts);

    private static int PerfEventOpen(byte[] attr, int pid, int cpu, int groupFd, uint flags)
    {
        return (int)syscall(298, attr, pid, cpu, groupFd, flags);
    }

    [DllImport("libc", SetLastError = true)]
    private static extern long syscall(long number, byte[] attr, int pid, int cpu, int groupFd, uint flags);
}

// i915 PMU config value constants
internal static class I915PmuConfig
{
    private const int SampleBits = 4;
    private const int InstanceBits = 8;
    private const int ClassShift = SampleBits + InstanceBits; // 12

    private const int SampleBusy = 0;
    private const int SampleWait = 1;
    private const int SampleSema = 2;

    private static ulong Engine(int cls, int instance, int sample)
        => (ulong)((cls << ClassShift) | (instance << SampleBits) | sample);

    // Base for non-engine events = engine(0xff, 0xff, 0xf) + 1 = 0x100000
    private static ulong Other(int x)
        => Engine(0xff, 0xff, 0xf) + 1 + (ulong)x;

    public static ulong EngineBusy(int cls, int instance) => Engine(cls, instance, SampleBusy);
    public static ulong EngineWait(int cls, int instance) => Engine(cls, instance, SampleWait);
    public static ulong EngineSema(int cls, int instance) => Engine(cls, instance, SampleSema);

    public static readonly ulong ActualFrequency = Other(0);      // 0x100000
    public static readonly ulong RequestedFrequency = Other(1);   // 0x100001
    public static readonly ulong Interrupts = Other(2);           // 0x100002
    public static readonly ulong Rc6Residency = Other(3);         // 0x100003
    public static readonly ulong SwGtAwakeTime = Other(4);        // 0x100004
}
