using InfoPanel.Models;
using Serilog;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;

namespace InfoPanel.Services;

/// <summary>
/// Intel GPU monitor.
/// i915: PMU (Performance Monitoring Unit) counters via perf_event_open for frequency,
/// RC6 residency and per-engine busy time, plus sysfs frequency files.
/// xe: sysfs only, per GT under device/tile*/gt*: freq0/ for frequencies and
/// gtidle/idle_residency_ms for utilization. The xe PMU is not used: every open xe
/// event holds a runtime-PM reference, and engine and frequency events also hold GT
/// forcewake, so keeping them open would stop the GPU from ever idling (and pin the
/// utilization read from idle residency at 100%).
/// </summary>
public class IntelGpuMonitor
{
    private static readonly Lazy<IntelGpuMonitor> _instance = new(() => new IntelGpuMonitor(new SysfsAccess()));
    public static IntelGpuMonitor Instance => _instance.Value;

    private readonly SysfsAccess _sysfs;
    private bool _available;
    private string _gpuName = "Intel GPU";
    private string _cardPath = "";
    private string _pciSlot = "";   // e.g. 0000:03:00.0
    private bool _isXe;

    // xe: GT sysfs directories (device/tileN/gtN) and idle residency baselines
    private string? _xeMainGt;
    private string? _xeMediaGt;
    private long _prevXeMainIdleMs = -1;
    private long _prevXeMediaIdleMs = -1;

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

    internal IntelGpuMonitor(SysfsAccess sysfs) => _sysfs = sysfs;

    public void Initialize()
    {
        try
        {
            // Find Intel GPU card in DRM
            if (!FindIntelGpu()) return;

            if (_isXe)
            {
                InitializeXe();
                return;
            }

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
        foreach (var (cardDir, _) in DrmDevices.Scan(_sysfs, "0x8086"))
        {
            _cardPath = cardDir;
            _pciSlot = Path.GetFileName(_sysfs.ResolvePath(Path.Combine(cardDir, "device")) ?? "");

            // Try to get device name
            var deviceId = ReadFile(Path.Combine(cardDir, "device", "device")) ?? "";
            _gpuName = $"Intel GPU [{deviceId.TrimStart('0').TrimStart('x')}]";

            // Check driver
            var uevent = ReadFile(Path.Combine(cardDir, "device", "uevent"));
            if (uevent != null && uevent.Contains("DRIVER=xe"))
            {
                _isXe = true;
                _gpuName = $"Intel GPU [{deviceId}] (xe)";
            }

            return true;
        }

        return false;
    }

    private void InitializeXe()
    {
        // GTs by number across tiles. gtidle/name ends in -rc for the render/compute GT
        // and -mc for the standalone media GT (Meteor Lake and later, Battlemage).
        var device = Path.Combine(_cardPath, "device");
        var gts = _sysfs.GetDirectories(device, "tile*")
            .SelectMany(tile => _sysfs.GetDirectories(tile, "gt*"))
            .Select(dir => (dir, id: int.TryParse(Path.GetFileName(dir).AsSpan(2), out var id) ? id : int.MaxValue))
            .OrderBy(gt => gt.id)
            .Select(gt => gt.dir)
            .ToList();

        foreach (var gt in gts)
        {
            var name = ReadFile(Path.Combine(gt, "gtidle", "name")) ?? "";
            if (name.EndsWith("-mc", StringComparison.Ordinal)) _xeMediaGt ??= gt;
            else _xeMainGt ??= gt;
        }

        _available = _xeMainGt != null
            && (_sysfs.FileExists(Path.Combine(_xeMainGt, "freq0", "act_freq"))
                || _sysfs.FileExists(Path.Combine(_xeMainGt, "gtidle", "idle_residency_ms")));

        if (_available)
            Log.Information("Intel GPU monitor initialized (xe sysfs): {Name}, {GtCount} GT(s)", _gpuName, gts.Count);
        else
            Log.Debug("Intel GPU: xe device without tile/gt sysfs at {Path}", device);
    }

    private bool ReadPmuType()
    {
        // i915 names the integrated GPU's PMU "i915" and a discrete card's after its
        // PCI address, i915_0000_03_00.0.
        var names = new List<string>();
        if (_pciSlot.Length > 0) names.Add("i915_" + _pciSlot.Replace(':', '_'));
        names.Add("i915");

        foreach (var pmuName in names)
        {
            var typeStr = ReadFile(_sysfs.PathUnderRoot("bus", "event_source", "devices", pmuName, "type"));
            if (typeStr != null && uint.TryParse(typeStr, out var type))
            {
                _pmuType = type;
                Log.Debug("Intel GPU PMU type: {Type} ({Name})", type, pmuName);
                return true;
            }
        }

        Log.Debug("Intel GPU: no i915 PMU found");
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

    public void Poll() => Poll(GetTimestampNs());

    internal void Poll(long nowNs)
    {
        if (!_available) return;

        var deltaNs = nowNs - _prevTimestampNs;
        if (deltaNs <= 0) deltaNs = 1;

        if (_isXe)
        {
            try { PollXe(nowNs); }
            catch (Exception ex) { Log.Debug(ex, "Intel GPU (xe) poll error"); }
            _prevTimestampNs = nowNs;
            return;
        }

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
    }

    private void PollXe(long nowNs)
    {
        var main = _xeMainGt!;
        var freq = Path.Combine(main, "freq0");
        if (ReadDouble(Path.Combine(freq, "act_freq")) is { } act) UpdateSensor("system/igpu/frequency", act, "MHz");
        if (ReadDouble(Path.Combine(freq, "cur_freq")) is { } cur) UpdateSensor("system/igpu/req_frequency", cur, "MHz");
        if (ReadDouble(Path.Combine(freq, "min_freq")) is { } min) UpdateSensor("system/igpu/min_frequency", min, "MHz");
        if (ReadDouble(Path.Combine(freq, "max_freq")) is { } max) UpdateSensor("system/igpu/max_frequency", max, "MHz");

        // Utilization = share of the interval the GT was out of its C6 idle state.
        var elapsedMs = (nowNs - _prevTimestampNs) / 1_000_000.0;
        if (XeIdlePercent(main, ref _prevXeMainIdleMs, elapsedMs) is { } idle)
        {
            UpdateSensor("system/igpu/rc6_percent", idle, "%");
            UpdateSensor("system/igpu/utilization", Math.Round(100 - idle, 1), "%");
        }
        if (_xeMediaGt != null && XeIdlePercent(_xeMediaGt, ref _prevXeMediaIdleMs, elapsedMs) is { } mediaIdle)
        {
            UpdateSensor("system/igpu/media_utilization", Math.Round(100 - mediaIdle, 1), "%");
        }
    }

    private double? XeIdlePercent(string gt, ref long prevIdleMs, double elapsedMs)
    {
        if (ReadDouble(Path.Combine(gt, "gtidle", "idle_residency_ms")) is not { } value) return null;
        var idleMs = (long)value;
        var prev = prevIdleMs;
        prevIdleMs = idleMs;
        if (prev < 0 || idleMs < prev || elapsedMs <= 0) return null;   // first sample or counter reset
        return Math.Clamp(Math.Round((idleMs - prev) * 100.0 / elapsedMs, 1), 0, 100);
    }

    private double? ReadDouble(string path) =>
        ReadFile(path) is { } text && double.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out var value) ? value : null;

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
            ("media_utilization", "Media Utilization", "Utilization", "%"),
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

    private string? ReadFile(string path) => _sysfs.ReadValue(path);

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
