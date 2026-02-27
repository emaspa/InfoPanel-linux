using InfoPanel.Models;
using Serilog;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
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

    private static readonly Regex DiskDeviceRegex = new(@"^(sd[a-z]+|nvme\d+n\d+|vd[a-z]+|mmcblk\d+)$", RegexOptions.Compiled);

    private LinuxSystemSensors()
    {
        _prevTimestampMs = Stopwatch.GetTimestamp() / (Stopwatch.Frequency / 1000);
        NvmlMonitor.Instance.Initialize();
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
        try { NvmlMonitor.Instance.Poll(); } catch (Exception ex) { Log.Debug(ex, "LinuxSystemSensors: NVML poll error"); }

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

        // NVIDIA GPU
        result.AddRange(NvmlMonitor.Instance.GetSensorInfoList());

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
