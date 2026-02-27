using InfoPanel.Models;
using Serilog;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace InfoPanel.Services;

public class LinuxSystemSensors
{
    private static readonly Lazy<LinuxSystemSensors> _instance = new(() => new LinuxSystemSensors());
    public static LinuxSystemSensors Instance => _instance.Value;

    // Delta state for rate calculations
    private Dictionary<string, long[]> _prevCpuJiffies = new();
    private Dictionary<string, (long readSectors, long writeSectors)> _prevDiskStats = new();
    private Dictionary<string, (long rxBytes, long txBytes)> _prevNetStats = new();
    private Dictionary<string, long> _prevRaplEnergy = new();
    private long _prevTimestampMs;

    // Track which sensors exist for GetSensorInfoList()
    private readonly List<HwmonSensorInfo> _sensorInfos = new();
    private readonly object _sensorInfoLock = new();

    // Block device I/O latency delta state
    private Dictionary<string, (long readTicks, long writeTicks, long readOps, long writeOps)> _prevBlockStats = new();

    // Filesystem types to exclude (virtual/pseudo filesystems)
    private static readonly HashSet<string> ExcludedFsTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "sysfs", "proc", "devtmpfs", "devpts", "tmpfs", "securityfs", "cgroup", "cgroup2",
        "pstore", "debugfs", "hugetlbfs", "mqueue", "configfs", "fusectl", "tracefs",
        "bpf", "autofs", "efivarfs", "squashfs", "overlay", "nsfs", "ramfs", "rpc_pipefs",
        "nfsd", "fuse.portal", "fuse.gvfsd-fuse"
    };

    private static readonly Regex DiskDeviceRegex = new(@"^(sd[a-z]+|nvme\d+n\d+|vd[a-z]+|mmcblk\d+)$", RegexOptions.Compiled);

    // P/Invoke for statvfs (filesystem statistics)
    [StructLayout(LayoutKind.Sequential)]
    private struct Statvfs
    {
        public ulong f_bsize;
        public ulong f_frsize;
        public ulong f_blocks;
        public ulong f_bfree;
        public ulong f_bavail;
        public ulong f_files;
        public ulong f_ffree;
        public ulong f_favail;
        public ulong f_fsid;
        public ulong f_flag;
        public ulong f_namemax;
        private int __f_spare0;
        private int __f_spare1;
        private int __f_spare2;
        private int __f_spare3;
        private int __f_spare4;
        private int __f_spare5;
    }

    [DllImport("libc", EntryPoint = "statvfs", SetLastError = true)]
    private static extern int statvfs_native([MarshalAs(UnmanagedType.LPStr)] string path, out Statvfs buf);

    private LinuxSystemSensors()
    {
        _prevTimestampMs = Stopwatch.GetTimestamp() / (Stopwatch.Frequency / 1000);
        NvmlMonitor.Instance.Initialize();
        RocmSmiMonitor.Instance.Initialize();
        IntelGpuMonitor.Instance.Initialize();
    }

    public void Poll()
    {
        var nowMs = Stopwatch.GetTimestamp() / (Stopwatch.Frequency / 1000);
        var deltaMs = nowMs - _prevTimestampMs;
        if (deltaMs <= 0) deltaMs = 1;

        try { PollCpu(deltaMs); } catch (Exception ex) { Log.Debug(ex, "LinuxSystemSensors: CPU poll error"); }
        try { PollMemory(); } catch (Exception ex) { Log.Debug(ex, "LinuxSystemSensors: Memory poll error"); }
        try { PollDiskIO(deltaMs); } catch (Exception ex) { Log.Debug(ex, "LinuxSystemSensors: Disk I/O poll error"); }
        try { PollNetwork(deltaMs); } catch (Exception ex) { Log.Debug(ex, "LinuxSystemSensors: Network poll error"); }
        try { PollLoadAvg(); } catch (Exception ex) { Log.Debug(ex, "LinuxSystemSensors: Load poll error"); }
        try { PollPowerSupply(); } catch (Exception ex) { Log.Debug(ex, "LinuxSystemSensors: Power supply poll error"); }
        try { PollRapl(deltaMs); } catch (Exception ex) { Log.Debug(ex, "LinuxSystemSensors: RAPL poll error"); }
        try { PollFilesystems(); } catch (Exception ex) { Log.Debug(ex, "LinuxSystemSensors: Filesystem poll error"); }
        try { PollCpuFreq(); } catch (Exception ex) { Log.Debug(ex, "LinuxSystemSensors: cpufreq poll error"); }
        try { IntelGpuMonitor.Instance.Poll(); } catch (Exception ex) { Log.Debug(ex, "LinuxSystemSensors: Intel GPU poll error"); }
        try { PollUptime(); } catch (Exception ex) { Log.Debug(ex, "LinuxSystemSensors: Uptime poll error"); }
        try { PollProcessCount(); } catch (Exception ex) { Log.Debug(ex, "LinuxSystemSensors: Process count poll error"); }
        try { PollNetworkInfo(); } catch (Exception ex) { Log.Debug(ex, "LinuxSystemSensors: Network info poll error"); }
        try { PollBlockDeviceStats(deltaMs); } catch (Exception ex) { Log.Debug(ex, "LinuxSystemSensors: Block device stats poll error"); }
        try { NvmlMonitor.Instance.Poll(); } catch (Exception ex) { Log.Debug(ex, "LinuxSystemSensors: NVML poll error"); }
        try { RocmSmiMonitor.Instance.Poll(); } catch (Exception ex) { Log.Debug(ex, "LinuxSystemSensors: ROCm SMI poll error"); }

        _prevTimestampMs = nowMs;
    }

    private void PollCpu(long deltaMs)
    {
        if (!File.Exists("/proc/stat")) return;

        var lines = File.ReadAllLines("/proc/stat");
        var newJiffies = new Dictionary<string, long[]>();

        foreach (var line in lines)
        {
            if (!line.StartsWith("cpu")) break;

            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 5) continue;

            var cpuName = parts[0]; // "cpu" for total, "cpu0", "cpu1", etc.
            var jiffies = new long[parts.Length - 1];
            for (int i = 1; i < parts.Length; i++)
            {
                long.TryParse(parts[i], out jiffies[i - 1]);
            }

            newJiffies[cpuName] = jiffies;

            if (_prevCpuJiffies.TryGetValue(cpuName, out var prev))
            {
                // user, nice, system, idle, iowait, irq, softirq, steal
                long totalDelta = 0;
                long idleDelta = 0;
                for (int i = 0; i < Math.Min(jiffies.Length, prev.Length); i++)
                {
                    var d = jiffies[i] - prev[i];
                    totalDelta += d;
                    if (i == 3 || i == 4) // idle + iowait
                        idleDelta += d;
                }

                double usage = totalDelta > 0 ? (totalDelta - idleDelta) * 100.0 / totalDelta : 0;
                usage = Math.Round(usage, 1);

                if (cpuName == "cpu")
                {
                    UpdateSensor("system/cpu/total", usage, "%");
                }
                else
                {
                    // cpu0 -> core0
                    var coreNum = cpuName.Substring(3);
                    UpdateSensor($"system/cpu/core{coreNum}", usage, "%");
                }
            }
        }

        _prevCpuJiffies = newJiffies;

        // Read CPU frequencies from /proc/cpuinfo
        if (File.Exists("/proc/cpuinfo"))
        {
            var cpuInfoLines = File.ReadAllLines("/proc/cpuinfo");
            int coreIndex = 0;
            foreach (var line in cpuInfoLines)
            {
                if (line.StartsWith("cpu MHz"))
                {
                    var colonIdx = line.IndexOf(':');
                    if (colonIdx >= 0 && double.TryParse(line.Substring(colonIdx + 1).Trim(),
                        NumberStyles.Any, CultureInfo.InvariantCulture, out var mhz))
                    {
                        UpdateSensor($"system/cpu/freq/core{coreIndex}", Math.Round(mhz, 0), "MHz");
                    }
                    coreIndex++;
                }
            }
        }
    }

    private void PollMemory()
    {
        if (!File.Exists("/proc/meminfo")) return;

        var lines = File.ReadAllLines("/proc/meminfo");
        long memTotal = 0, memAvailable = 0, memFree = 0, buffers = 0, cached = 0;
        long swapTotal = 0, swapFree = 0;

        foreach (var line in lines)
        {
            var parts = line.Split(':', 2);
            if (parts.Length < 2) continue;

            var key = parts[0].Trim();
            var valStr = parts[1].Trim().Split(' ')[0];
            if (!long.TryParse(valStr, out var valKb)) continue;

            switch (key)
            {
                case "MemTotal": memTotal = valKb; break;
                case "MemAvailable": memAvailable = valKb; break;
                case "MemFree": memFree = valKb; break;
                case "Buffers": buffers = valKb; break;
                case "Cached": cached = valKb; break;
                case "SwapTotal": swapTotal = valKb; break;
                case "SwapFree": swapFree = valKb; break;
            }
        }

        // If MemAvailable isn't provided (old kernels), estimate it
        if (memAvailable == 0 && memFree > 0)
            memAvailable = memFree + buffers + cached;

        var totalGb = Math.Round(memTotal / 1048576.0, 2);
        var usedKb = memTotal - memAvailable;
        var usedGb = Math.Round(usedKb / 1048576.0, 2);
        var availableGb = Math.Round(memAvailable / 1048576.0, 2);
        var usedPercent = memTotal > 0 ? Math.Round(usedKb * 100.0 / memTotal, 1) : 0;

        UpdateSensor("system/memory/total", totalGb, "GB");
        UpdateSensor("system/memory/used", usedGb, "GB");
        UpdateSensor("system/memory/available", availableGb, "GB");
        UpdateSensor("system/memory/used_percent", usedPercent, "%");

        if (swapTotal > 0)
        {
            var swapUsedKb = swapTotal - swapFree;
            UpdateSensor("system/memory/swap_used", Math.Round(swapUsedKb / 1048576.0, 2), "GB");
            UpdateSensor("system/memory/swap_percent", Math.Round(swapUsedKb * 100.0 / swapTotal, 1), "%");
        }
    }

    private void PollDiskIO(long deltaMs)
    {
        if (!File.Exists("/proc/diskstats")) return;

        var lines = File.ReadAllLines("/proc/diskstats");
        var newDiskStats = new Dictionary<string, (long readSectors, long writeSectors)>();

        foreach (var line in lines)
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 14) continue;

            var devName = parts[2];
            if (!DiskDeviceRegex.IsMatch(devName)) continue;

            if (!long.TryParse(parts[5], out var readSectors)) continue;  // field 3: sectors read
            if (!long.TryParse(parts[9], out var writeSectors)) continue; // field 7: sectors written

            newDiskStats[devName] = (readSectors, writeSectors);

            if (_prevDiskStats.TryGetValue(devName, out var prev))
            {
                var deltaRead = readSectors - prev.readSectors;
                var deltaWrite = writeSectors - prev.writeSectors;
                var deltaSec = deltaMs / 1000.0;
                if (deltaSec <= 0) deltaSec = 1;

                // sectors are 512 bytes each, convert to MB/s
                var readSpeed = Math.Round(deltaRead * 512.0 / (1024 * 1024) / deltaSec, 2);
                var writeSpeed = Math.Round(deltaWrite * 512.0 / (1024 * 1024) / deltaSec, 2);

                UpdateSensor($"system/disk/{devName}/read_speed", Math.Max(readSpeed, 0), "MB/s");
                UpdateSensor($"system/disk/{devName}/write_speed", Math.Max(writeSpeed, 0), "MB/s");
            }
        }

        _prevDiskStats = newDiskStats;
    }

    private void PollNetwork(long deltaMs)
    {
        if (!File.Exists("/proc/net/dev")) return;

        var lines = File.ReadAllLines("/proc/net/dev");
        var newNetStats = new Dictionary<string, (long rxBytes, long txBytes)>();

        foreach (var line in lines)
        {
            if (!line.Contains(':')) continue;

            var colonIdx = line.IndexOf(':');
            var iface = line.Substring(0, colonIdx).Trim();

            if (iface == "lo") continue;

            var valuesStr = line.Substring(colonIdx + 1);
            var parts = valuesStr.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 10) continue;

            if (!long.TryParse(parts[0], out var rxBytes)) continue;
            if (!long.TryParse(parts[8], out var txBytes)) continue;

            newNetStats[iface] = (rxBytes, txBytes);

            if (_prevNetStats.TryGetValue(iface, out var prev))
            {
                var deltaRx = rxBytes - prev.rxBytes;
                var deltaTx = txBytes - prev.txBytes;
                var deltaSec = deltaMs / 1000.0;
                if (deltaSec <= 0) deltaSec = 1;

                var rxSpeed = Math.Round(deltaRx / (1024 * 1024.0) / deltaSec, 2);
                var txSpeed = Math.Round(deltaTx / (1024 * 1024.0) / deltaSec, 2);

                UpdateSensor($"system/network/{iface}/rx_speed", Math.Max(rxSpeed, 0), "MB/s");
                UpdateSensor($"system/network/{iface}/tx_speed", Math.Max(txSpeed, 0), "MB/s");
            }
        }

        _prevNetStats = newNetStats;
    }

    private void PollLoadAvg()
    {
        if (!File.Exists("/proc/loadavg")) return;

        var content = File.ReadAllText("/proc/loadavg").Trim();
        var parts = content.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3) return;

        if (double.TryParse(parts[0], NumberStyles.Any, CultureInfo.InvariantCulture, out var load1))
            UpdateSensor("system/load/1min", load1, "");
        if (double.TryParse(parts[1], NumberStyles.Any, CultureInfo.InvariantCulture, out var load5))
            UpdateSensor("system/load/5min", load5, "");
        if (double.TryParse(parts[2], NumberStyles.Any, CultureInfo.InvariantCulture, out var load15))
            UpdateSensor("system/load/15min", load15, "");
    }

    private void PollPowerSupply()
    {
        const string path = "/sys/class/power_supply";
        if (!Directory.Exists(path)) return;

        foreach (var supplyDir in Directory.GetDirectories(path))
        {
            var name = Path.GetFileName(supplyDir);
            var typePath = Path.Combine(supplyDir, "type");
            var type = ReadFile(typePath)?.ToLowerInvariant() ?? "";

            if (type == "battery")
            {
                // Capacity
                var capacityStr = ReadFile(Path.Combine(supplyDir, "capacity"));
                if (capacityStr != null && double.TryParse(capacityStr, NumberStyles.Any, CultureInfo.InvariantCulture, out var capacity))
                {
                    UpdateSensor($"system/power/{name}/capacity", capacity, "%");
                }

                // Power (try power_now first, then current_now * voltage_now)
                var powerStr = ReadFile(Path.Combine(supplyDir, "power_now"));
                if (powerStr != null && long.TryParse(powerStr, out var powerUw))
                {
                    UpdateSensor($"system/power/{name}/power", Math.Round(powerUw / 1000000.0, 2), "W");
                }
                else
                {
                    var currentStr = ReadFile(Path.Combine(supplyDir, "current_now"));
                    var voltageStr = ReadFile(Path.Combine(supplyDir, "voltage_now"));
                    if (currentStr != null && voltageStr != null &&
                        long.TryParse(currentStr, out var currentUa) &&
                        long.TryParse(voltageStr, out var voltageUv))
                    {
                        var watts = Math.Round(currentUa * voltageUv / 1e12, 2);
                        UpdateSensor($"system/power/{name}/power", watts, "W");
                    }
                }

                // Status
                var status = ReadFile(Path.Combine(supplyDir, "status"));
                if (status != null)
                {
                    UpdateSensorText($"system/power/{name}/status", status);
                }
            }
            else if (type == "mains")
            {
                var onlineStr = ReadFile(Path.Combine(supplyDir, "online"));
                if (onlineStr != null && int.TryParse(onlineStr, out var online))
                {
                    UpdateSensor($"system/power/{name}/online", online, "");
                }
            }
        }
    }

    private void PollRapl(long deltaMs)
    {
        const string path = "/sys/class/powercap";
        if (!Directory.Exists(path)) return;

        var newRaplEnergy = new Dictionary<string, long>();

        foreach (var raplDir in Directory.GetDirectories(path, "intel-rapl:*"))
        {
            var energyFile = Path.Combine(raplDir, "energy_uj");
            var nameFile = Path.Combine(raplDir, "name");

            if (!File.Exists(energyFile)) continue;

            var energyStr = ReadFile(energyFile);
            var domainName = ReadFile(nameFile) ?? Path.GetFileName(raplDir);

            if (energyStr == null || !long.TryParse(energyStr, out var energyUj)) continue;

            var raplKey = Path.GetFileName(raplDir);
            newRaplEnergy[raplKey] = energyUj;

            if (_prevRaplEnergy.TryGetValue(raplKey, out var prevEnergy))
            {
                var deltaEnergy = energyUj - prevEnergy;
                // Handle counter wraparound
                if (deltaEnergy < 0)
                {
                    var maxRangeFile = Path.Combine(raplDir, "max_energy_range_uj");
                    var maxRangeStr = ReadFile(maxRangeFile);
                    if (maxRangeStr != null && long.TryParse(maxRangeStr, out var maxRange))
                    {
                        deltaEnergy += maxRange;
                    }
                    else
                    {
                        deltaEnergy = 0;
                    }
                }

                var deltaSec = deltaMs / 1000.0;
                if (deltaSec <= 0) deltaSec = 1;
                var watts = Math.Round(deltaEnergy / 1000000.0 / deltaSec, 2);

                UpdateSensor($"system/rapl/{domainName}/power", Math.Max(watts, 0), "W");
            }

            // Also check sub-domains (e.g., intel-rapl:0:0 for core, intel-rapl:0:1 for uncore)
            foreach (var subDir in Directory.GetDirectories(raplDir, "intel-rapl:*"))
            {
                var subEnergyFile = Path.Combine(subDir, "energy_uj");
                var subNameFile = Path.Combine(subDir, "name");

                if (!File.Exists(subEnergyFile)) continue;

                var subEnergyStr = ReadFile(subEnergyFile);
                var subDomainName = ReadFile(subNameFile) ?? Path.GetFileName(subDir);

                if (subEnergyStr == null || !long.TryParse(subEnergyStr, out var subEnergyUj)) continue;

                var subRaplKey = Path.GetFileName(subDir);
                newRaplEnergy[subRaplKey] = subEnergyUj;

                if (_prevRaplEnergy.TryGetValue(subRaplKey, out var prevSubEnergy))
                {
                    var deltaEnergy = subEnergyUj - prevSubEnergy;
                    if (deltaEnergy < 0)
                    {
                        var maxRangeFile = Path.Combine(subDir, "max_energy_range_uj");
                        var maxRangeStr = ReadFile(maxRangeFile);
                        if (maxRangeStr != null && long.TryParse(maxRangeStr, out var maxRange))
                        {
                            deltaEnergy += maxRange;
                        }
                        else
                        {
                            deltaEnergy = 0;
                        }
                    }

                    var deltaSec = deltaMs / 1000.0;
                    if (deltaSec <= 0) deltaSec = 1;
                    var watts = Math.Round(deltaEnergy / 1000000.0 / deltaSec, 2);

                    UpdateSensor($"system/rapl/{domainName}/{subDomainName}/power", Math.Max(watts, 0), "W");
                }
            }
        }

        _prevRaplEnergy = newRaplEnergy;
    }

    private void PollFilesystems()
    {
        if (!File.Exists("/proc/mounts")) return;

        string[] lines;
        try { lines = File.ReadAllLines("/proc/mounts"); }
        catch { return; }

        var seen = new HashSet<string>(); // avoid duplicate mount points

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3) continue;

            var device = parts[0];
            var mountPoint = parts[1];
            var fsType = parts[2];

            // Skip virtual filesystems and non-device mounts
            if (!device.StartsWith("/dev/")) continue;
            if (ExcludedFsTypes.Contains(fsType)) continue;
            if (!seen.Add(mountPoint)) continue;

            if (statvfs_native(mountPoint, out var stat) != 0) continue;
            if (stat.f_blocks == 0) continue;

            var totalBytes = stat.f_blocks * stat.f_frsize;
            var freeBytes = stat.f_bavail * stat.f_frsize; // available to non-root
            var usedBytes = totalBytes - (stat.f_bfree * stat.f_frsize);

            var totalGb = Math.Round(totalBytes / (1024.0 * 1024 * 1024), 1);
            var usedGb = Math.Round(usedBytes / (1024.0 * 1024 * 1024), 1);
            var freeGb = Math.Round(freeBytes / (1024.0 * 1024 * 1024), 1);
            var usedPercent = totalBytes > 0 ? Math.Round(usedBytes * 100.0 / totalBytes, 1) : 0;

            // Sanitize mount point for sensor ID: / -> root, /home -> home
            var fsName = mountPoint == "/" ? "root" : mountPoint.TrimStart('/').Replace('/', '-');

            UpdateSensor($"system/filesystem/{fsName}/total", totalGb, "GB");
            UpdateSensor($"system/filesystem/{fsName}/used", usedGb, "GB");
            UpdateSensor($"system/filesystem/{fsName}/free", freeGb, "GB");
            UpdateSensor($"system/filesystem/{fsName}/used_percent", usedPercent, "%");
        }
    }

    private void PollCpuFreq()
    {
        const string cpufreqBase = "/sys/devices/system/cpu";
        if (!Directory.Exists(cpufreqBase)) return;

        for (int i = 0; i < 256; i++)
        {
            var freqPath = Path.Combine(cpufreqBase, $"cpu{i}", "cpufreq", "scaling_cur_freq");
            var content = ReadFile(freqPath);
            if (content == null) break;

            if (long.TryParse(content, out var freqKhz))
            {
                UpdateSensor($"system/cpufreq/core{i}", Math.Round(freqKhz / 1000.0, 0), "MHz");
            }
        }

        // Also read governor (just from cpu0, usually same for all)
        var governorPath = Path.Combine(cpufreqBase, "cpu0", "cpufreq", "scaling_governor");
        var governor = ReadFile(governorPath);
        if (governor != null)
        {
            UpdateSensorText("system/cpufreq/governor", governor);
        }
    }

    private void PollUptime()
    {
        if (!File.Exists("/proc/uptime")) return;

        var content = File.ReadAllText("/proc/uptime").Trim();
        var parts = content.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 1) return;

        if (double.TryParse(parts[0], NumberStyles.Any, CultureInfo.InvariantCulture, out var uptimeSec))
        {
            UpdateSensor("system/uptime/seconds", Math.Round(uptimeSec, 0), "s");
            UpdateSensor("system/uptime/hours", Math.Round(uptimeSec / 3600.0, 1), "h");
            UpdateSensor("system/uptime/days", Math.Round(uptimeSec / 86400.0, 2), "d");
        }
    }

    private void PollProcessCount()
    {
        try
        {
            int processCount = 0;
            int threadCount = 0;

            foreach (var dir in Directory.GetDirectories("/proc"))
            {
                var name = Path.GetFileName(dir);
                if (name.Length == 0 || !char.IsDigit(name[0])) continue;

                // Check if it's actually a PID directory
                bool isPid = true;
                for (int c = 0; c < name.Length; c++)
                {
                    if (!char.IsDigit(name[c])) { isPid = false; break; }
                }
                if (!isPid) continue;

                processCount++;

                // Count threads from /proc/PID/task/ subdirectories
                var taskDir = Path.Combine(dir, "task");
                if (Directory.Exists(taskDir))
                {
                    try { threadCount += Directory.GetDirectories(taskDir).Length; }
                    catch { threadCount++; } // fallback: at least 1 thread per process
                }
                else
                {
                    threadCount++;
                }
            }

            UpdateSensor("system/processes/count", processCount, "");
            UpdateSensor("system/processes/threads", threadCount, "");
        }
        catch { }
    }

    private void PollNetworkInfo()
    {
        const string netBase = "/sys/class/net";
        if (!Directory.Exists(netBase)) return;

        foreach (var ifaceDir in Directory.GetDirectories(netBase))
        {
            var iface = Path.GetFileName(ifaceDir);
            if (iface == "lo") continue;

            // Link speed (Mbps)
            var speedStr = ReadFile(Path.Combine(ifaceDir, "speed"));
            if (speedStr != null && int.TryParse(speedStr, out var speedMbps) && speedMbps > 0)
            {
                UpdateSensor($"system/network/{iface}/link_speed", speedMbps, "Mbps");
            }

            // Operstate (up/down)
            var operstate = ReadFile(Path.Combine(ifaceDir, "operstate"));
            if (operstate != null)
            {
                UpdateSensorText($"system/network/{iface}/state", operstate);
            }
        }
    }

    private void PollBlockDeviceStats(long deltaMs)
    {
        // /sys/block/*/stat has: reads_completed reads_merged read_sectors read_ticks
        //                        writes_completed writes_merged write_sectors write_ticks
        //                        ios_in_progress io_ticks weighted_io_ticks ...
        const string blockBase = "/sys/block";
        if (!Directory.Exists(blockBase)) return;

        var newBlockStats = new Dictionary<string, (long readTicks, long writeTicks, long readOps, long writeOps)>();

        foreach (var blockDir in Directory.GetDirectories(blockBase))
        {
            var devName = Path.GetFileName(blockDir);
            if (!DiskDeviceRegex.IsMatch(devName)) continue;

            var statFile = Path.Combine(blockDir, "stat");
            var content = ReadFile(statFile);
            if (content == null) continue;

            var parts = content.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 11) continue;

            if (!long.TryParse(parts[0], out var readOps)) continue;
            if (!long.TryParse(parts[3], out var readTicks)) continue;   // ms spent reading
            if (!long.TryParse(parts[4], out var writeOps)) continue;
            if (!long.TryParse(parts[7], out var writeTicks)) continue;  // ms spent writing
            if (!long.TryParse(parts[8], out var iosInProgress)) continue;

            newBlockStats[devName] = (readTicks, writeTicks, readOps, writeOps);

            // Queue depth (instantaneous)
            UpdateSensor($"system/block/{devName}/queue_depth", iosInProgress, "");

            if (_prevBlockStats.TryGetValue(devName, out var prev))
            {
                var deltaSec = deltaMs / 1000.0;
                if (deltaSec <= 0) deltaSec = 1;

                var deltaReadOps = readOps - prev.readOps;
                var deltaWriteOps = writeOps - prev.writeOps;
                var deltaReadTicks = readTicks - prev.readTicks;
                var deltaWriteTicks = writeTicks - prev.writeTicks;

                // IOPS
                UpdateSensor($"system/block/{devName}/read_iops", Math.Round(Math.Max(deltaReadOps / deltaSec, 0), 0), "IOPS");
                UpdateSensor($"system/block/{devName}/write_iops", Math.Round(Math.Max(deltaWriteOps / deltaSec, 0), 0), "IOPS");

                // Average latency (ms per operation)
                if (deltaReadOps > 0)
                    UpdateSensor($"system/block/{devName}/read_latency", Math.Round((double)deltaReadTicks / deltaReadOps, 2), "ms");
                if (deltaWriteOps > 0)
                    UpdateSensor($"system/block/{devName}/write_latency", Math.Round((double)deltaWriteTicks / deltaWriteOps, 2), "ms");
            }
        }

        _prevBlockStats = newBlockStats;
    }

    private void UpdateSensor(string sensorId, double value, string unit)
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

    private void UpdateSensorText(string sensorId, string text)
    {
        HwmonMonitor.SENSORHASH[sensorId] = new SensorReading(text);
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

    public List<HwmonSensorInfo> GetSensorInfoList()
    {
        var result = new List<HwmonSensorInfo>();

        // CPU Usage
        if (HwmonMonitor.SENSORHASH.ContainsKey("system/cpu/total"))
        {
            result.Add(new HwmonSensorInfo
            {
                SensorId = "system/cpu/total",
                DeviceName = "System CPU",
                Category = "Usage",
                Label = "Total CPU Usage",
                Unit = "%"
            });

            // Add per-core usage sensors
            for (int i = 0; i < 256; i++)
            {
                var key = $"system/cpu/core{i}";
                if (!HwmonMonitor.SENSORHASH.ContainsKey(key)) break;
                result.Add(new HwmonSensorInfo
                {
                    SensorId = key,
                    DeviceName = "System CPU",
                    Category = "Usage",
                    Label = $"Core {i} Usage",
                    Unit = "%"
                });
            }
        }

        // CPU Frequency
        for (int i = 0; i < 256; i++)
        {
            var key = $"system/cpu/freq/core{i}";
            if (!HwmonMonitor.SENSORHASH.ContainsKey(key)) break;
            result.Add(new HwmonSensorInfo
            {
                SensorId = key,
                DeviceName = "System CPU",
                Category = "Frequency",
                Label = $"Core {i} Frequency",
                Unit = "MHz"
            });
        }

        // Memory
        foreach (var (suffix, label, unit) in new[]
        {
            ("total", "Total Memory", "GB"),
            ("used", "Used Memory", "GB"),
            ("available", "Available Memory", "GB"),
            ("used_percent", "Memory Usage", "%"),
            ("swap_used", "Swap Used", "GB"),
            ("swap_percent", "Swap Usage", "%"),
        })
        {
            var key = $"system/memory/{suffix}";
            if (HwmonMonitor.SENSORHASH.ContainsKey(key))
            {
                result.Add(new HwmonSensorInfo
                {
                    SensorId = key,
                    DeviceName = "System Memory",
                    Category = "Memory",
                    Label = label,
                    Unit = unit
                });
            }
        }

        // Disk I/O
        foreach (var (sensorId, reading) in HwmonMonitor.SENSORHASH)
        {
            if (sensorId.StartsWith("system/disk/"))
            {
                // system/disk/sda/read_speed -> devName=sda, type=read_speed
                var remainder = sensorId.Substring("system/disk/".Length);
                var slashIdx = remainder.IndexOf('/');
                if (slashIdx < 0) continue;
                var devName = remainder.Substring(0, slashIdx);
                var sensorName = remainder.Substring(slashIdx + 1);

                var label = sensorName == "read_speed" ? $"{devName} Read" : $"{devName} Write";
                result.Add(new HwmonSensorInfo
                {
                    SensorId = sensorId,
                    DeviceName = "System Disk I/O",
                    Category = "Throughput",
                    Label = label,
                    Unit = "MB/s"
                });
            }
        }

        // Network
        foreach (var (sensorId, reading) in HwmonMonitor.SENSORHASH)
        {
            if (sensorId.StartsWith("system/network/"))
            {
                var remainder = sensorId.Substring("system/network/".Length);
                var slashIdx = remainder.IndexOf('/');
                if (slashIdx < 0) continue;
                var iface = remainder.Substring(0, slashIdx);
                var sensorName = remainder.Substring(slashIdx + 1);

                var label = sensorName == "rx_speed" ? $"{iface} Download" : $"{iface} Upload";
                result.Add(new HwmonSensorInfo
                {
                    SensorId = sensorId,
                    DeviceName = "System Network",
                    Category = "Throughput",
                    Label = label,
                    Unit = "MB/s"
                });
            }
        }

        // Load Average
        foreach (var (suffix, label) in new[]
        {
            ("1min", "Load 1 min"),
            ("5min", "Load 5 min"),
            ("15min", "Load 15 min"),
        })
        {
            var key = $"system/load/{suffix}";
            if (HwmonMonitor.SENSORHASH.ContainsKey(key))
            {
                result.Add(new HwmonSensorInfo
                {
                    SensorId = key,
                    DeviceName = "System Load",
                    Category = "Load Average",
                    Label = label,
                    Unit = ""
                });
            }
        }

        // Power Supply
        foreach (var (sensorId, reading) in HwmonMonitor.SENSORHASH)
        {
            if (sensorId.StartsWith("system/power/"))
            {
                var remainder = sensorId.Substring("system/power/".Length);
                var slashIdx = remainder.IndexOf('/');
                if (slashIdx < 0) continue;
                var supplyName = remainder.Substring(0, slashIdx);
                var sensorName = remainder.Substring(slashIdx + 1);

                var (label, unit) = sensorName switch
                {
                    "capacity" => ($"{supplyName} Capacity", "%"),
                    "power" => ($"{supplyName} Power", "W"),
                    "online" => ($"{supplyName} Online", ""),
                    "status" => ($"{supplyName} Status", ""),
                    _ => ($"{supplyName} {sensorName}", "")
                };

                result.Add(new HwmonSensorInfo
                {
                    SensorId = sensorId,
                    DeviceName = "System Power",
                    Category = "Power Supply",
                    Label = label,
                    Unit = unit
                });
            }
        }

        // Filesystem
        foreach (var (sensorId, _) in HwmonMonitor.SENSORHASH)
        {
            if (!sensorId.StartsWith("system/filesystem/")) continue;

            var remainder = sensorId.Substring("system/filesystem/".Length);
            var slashIdx = remainder.IndexOf('/');
            if (slashIdx < 0) continue;
            var fsName = remainder.Substring(0, slashIdx);
            var sensorName = remainder.Substring(slashIdx + 1);

            var (label, unit) = sensorName switch
            {
                "total" => ($"/{fsName.Replace('-', '/')} Total", "GB"),
                "used" => ($"/{fsName.Replace('-', '/')} Used", "GB"),
                "free" => ($"/{fsName.Replace('-', '/')} Free", "GB"),
                "used_percent" => ($"/{fsName.Replace('-', '/')} Usage", "%"),
                _ => ($"/{fsName.Replace('-', '/')} {sensorName}", "")
            };
            if (fsName == "root") label = label.Replace("/root", "/");

            result.Add(new HwmonSensorInfo
            {
                SensorId = sensorId,
                DeviceName = "System Filesystem",
                Category = "Disk Space",
                Label = label,
                Unit = unit
            });
        }

        // CPU Frequency (cpufreq)
        for (int i = 0; i < 256; i++)
        {
            var key = $"system/cpufreq/core{i}";
            if (!HwmonMonitor.SENSORHASH.ContainsKey(key)) break;
            result.Add(new HwmonSensorInfo
            {
                SensorId = key,
                DeviceName = "System CPU",
                Category = "cpufreq",
                Label = $"Core {i} Frequency",
                Unit = "MHz"
            });
        }
        if (HwmonMonitor.SENSORHASH.ContainsKey("system/cpufreq/governor"))
        {
            result.Add(new HwmonSensorInfo
            {
                SensorId = "system/cpufreq/governor",
                DeviceName = "System CPU",
                Category = "cpufreq",
                Label = "Governor",
                Unit = ""
            });
        }

        // Intel GPU (PMU-based)
        result.AddRange(IntelGpuMonitor.Instance.GetSensorInfoList());

        // Uptime
        foreach (var (suffix, label, unit) in new[]
        {
            ("seconds", "Uptime (seconds)", "s"),
            ("hours", "Uptime (hours)", "h"),
            ("days", "Uptime (days)", "d"),
        })
        {
            var key = $"system/uptime/{suffix}";
            if (HwmonMonitor.SENSORHASH.ContainsKey(key))
            {
                result.Add(new HwmonSensorInfo
                {
                    SensorId = key,
                    DeviceName = "System Info",
                    Category = "Uptime",
                    Label = label,
                    Unit = unit
                });
            }
        }

        // Process/Thread count
        foreach (var (suffix, label) in new[]
        {
            ("count", "Process Count"),
            ("threads", "Thread Count"),
        })
        {
            var key = $"system/processes/{suffix}";
            if (HwmonMonitor.SENSORHASH.ContainsKey(key))
            {
                result.Add(new HwmonSensorInfo
                {
                    SensorId = key,
                    DeviceName = "System Info",
                    Category = "Processes",
                    Label = label,
                    Unit = ""
                });
            }
        }

        // Network info (link speed, state)
        foreach (var (sensorId, _) in HwmonMonitor.SENSORHASH)
        {
            if (!sensorId.StartsWith("system/network/")) continue;

            var remainder = sensorId.Substring("system/network/".Length);
            var slashIdx = remainder.IndexOf('/');
            if (slashIdx < 0) continue;
            var iface = remainder.Substring(0, slashIdx);
            var sensorName = remainder.Substring(slashIdx + 1);

            if (sensorName == "link_speed")
            {
                result.Add(new HwmonSensorInfo
                {
                    SensorId = sensorId,
                    DeviceName = "System Network",
                    Category = "Link",
                    Label = $"{iface} Link Speed",
                    Unit = "Mbps"
                });
            }
            else if (sensorName == "state")
            {
                result.Add(new HwmonSensorInfo
                {
                    SensorId = sensorId,
                    DeviceName = "System Network",
                    Category = "Link",
                    Label = $"{iface} State",
                    Unit = ""
                });
            }
        }

        // Block device stats (IOPS, latency, queue depth)
        foreach (var (sensorId, _) in HwmonMonitor.SENSORHASH)
        {
            if (!sensorId.StartsWith("system/block/")) continue;

            var remainder = sensorId.Substring("system/block/".Length);
            var slashIdx = remainder.IndexOf('/');
            if (slashIdx < 0) continue;
            var devName = remainder.Substring(0, slashIdx);
            var sensorName = remainder.Substring(slashIdx + 1);

            var (label, unit) = sensorName switch
            {
                "read_iops" => ($"{devName} Read IOPS", "IOPS"),
                "write_iops" => ($"{devName} Write IOPS", "IOPS"),
                "read_latency" => ($"{devName} Read Latency", "ms"),
                "write_latency" => ($"{devName} Write Latency", "ms"),
                "queue_depth" => ($"{devName} Queue Depth", ""),
                _ => ($"{devName} {sensorName}", "")
            };

            result.Add(new HwmonSensorInfo
            {
                SensorId = sensorId,
                DeviceName = "System Block I/O",
                Category = "Performance",
                Label = label,
                Unit = unit
            });
        }

        // NVIDIA GPU
        result.AddRange(NvmlMonitor.Instance.GetSensorInfoList());

        // AMD GPU
        result.AddRange(RocmSmiMonitor.Instance.GetSensorInfoList());

        // RAPL
        foreach (var (sensorId, reading) in HwmonMonitor.SENSORHASH)
        {
            if (sensorId.StartsWith("system/rapl/"))
            {
                var remainder = sensorId.Substring("system/rapl/".Length);
                // Could be "package-0/power" or "package-0/core/power"
                var label = remainder.Replace("/power", "").Replace("/", " - ");

                result.Add(new HwmonSensorInfo
                {
                    SensorId = sensorId,
                    DeviceName = "System RAPL",
                    Category = "Power",
                    Label = $"RAPL {label}",
                    Unit = "W"
                });
            }
        }

        return result;
    }
}
