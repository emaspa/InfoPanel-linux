using InfoPanel.Models;
using Serilog;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Timers;

namespace InfoPanel.Services;

public class HwmonMonitor
{
    private static readonly Lazy<HwmonMonitor> _instance = new(() => new HwmonMonitor());
    public static HwmonMonitor Instance => _instance.Value;

    public static readonly ConcurrentDictionary<string, SensorReading> SENSORHASH = new();

    private Timer? _pollTimer;
    private const string HwmonPath = "/sys/class/hwmon";
    private const string ThermalPath = "/sys/class/thermal";

    private HwmonMonitor() { }

    public void Start(int intervalMs = 1000)
    {
        if (!Directory.Exists(HwmonPath) && !Directory.Exists(ThermalPath))
        {
            Log.Warning("Neither hwmon nor thermal path found");
            return;
        }

        Poll();

        _pollTimer = new Timer(intervalMs);
        _pollTimer.Elapsed += (_, _) => Poll();
        _pollTimer.AutoReset = true;
        _pollTimer.Start();

        Log.Information("HwmonMonitor started with {Interval}ms interval", intervalMs);
    }

    public void Stop()
    {
        _pollTimer?.Stop();
        _pollTimer?.Dispose();
        _pollTimer = null;
        SENSORHASH.Clear();
    }

    private void Poll()
    {
        try
        {
            if (Directory.Exists(HwmonPath))
            {
                foreach (var hwmonDir in Directory.GetDirectories(HwmonPath))
                {
                    var deviceName = ReadFileContent(Path.Combine(hwmonDir, "name")) ?? Path.GetFileName(hwmonDir);
                    var hwmonId = Path.GetFileName(hwmonDir);

                    ReadSensorFiles(hwmonDir, hwmonId, deviceName, "temp", "_input", 1000.0, "°C");
                    ReadSensorFiles(hwmonDir, hwmonId, deviceName, "fan", "_input", 1.0, "RPM");
                    ReadSensorFiles(hwmonDir, hwmonId, deviceName, "in", "_input", 1000.0, "V");
                    ReadSensorFiles(hwmonDir, hwmonId, deviceName, "curr", "_input", 1000.0, "A");
                    ReadSensorFiles(hwmonDir, hwmonId, deviceName, "power", "_input", 1000000.0, "W");
                    ReadSensorFiles(hwmonDir, hwmonId, deviceName, "freq", "_input", 1000000.0, "MHz");
                    ReadSensorFiles(hwmonDir, hwmonId, deviceName, "humidity", "_input", 1000.0, "%");
                }
            }

            // Also read thermal zones as fallback
            if (Directory.Exists(ThermalPath))
            {
                foreach (var zoneDir in Directory.GetDirectories(ThermalPath, "thermal_zone*"))
                {
                    var tempFile = Path.Combine(zoneDir, "temp");
                    var typeFile = Path.Combine(zoneDir, "type");
                    if (!File.Exists(tempFile)) continue;

                    var rawValue = ReadFileContent(tempFile);
                    if (rawValue == null || !double.TryParse(rawValue.Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out var numValue))
                        continue;

                    var value = numValue / 1000.0;
                    var zoneName = Path.GetFileName(zoneDir);
                    var zoneType = ReadFileContent(typeFile) ?? zoneName;
                    var sensorKey = $"thermal/{zoneName}";

                    if (SENSORHASH.TryGetValue(sensorKey, out var existing))
                    {
                        var min = Math.Min(existing.ValueMin, value);
                        var max = Math.Max(existing.ValueMax, value);
                        SENSORHASH[sensorKey] = new SensorReading(min, max, (min + max) / 2.0, value, "°C");
                    }
                    else
                    {
                        SENSORHASH[sensorKey] = new SensorReading(value, value, value, value, "°C");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "HwmonMonitor poll error");
        }

        try
        {
            LinuxSystemSensors.Instance.Poll();
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "LinuxSystemSensors poll error");
        }
    }

    private static void ReadSensorFiles(string hwmonDir, string hwmonId, string deviceName,
        string prefix, string suffix, double divisor, string unit)
    {
        try
        {
            foreach (var file in Directory.GetFiles(hwmonDir, $"{prefix}*{suffix}"))
            {
                var fileName = Path.GetFileNameWithoutExtension(file);
                // e.g., "temp1_input" -> index = "1"
                var index = fileName.Replace(prefix, "").Replace(suffix.TrimStart('_'), "").Trim('_');

                var rawValue = ReadFileContent(file);
                if (rawValue == null || !double.TryParse(rawValue.Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out var numValue))
                    continue;

                var value = numValue / divisor;

                // Try to read label
                var labelFile = Path.Combine(hwmonDir, $"{prefix}{index}_label");
                var label = ReadFileContent(labelFile) ?? $"{prefix}{index}";

                var sensorKey = $"{hwmonId}/{prefix}{index}";

                if (SENSORHASH.TryGetValue(sensorKey, out var existing))
                {
                    // Update min/max
                    var min = Math.Min(existing.ValueMin, value);
                    var max = Math.Max(existing.ValueMax, value);
                    SENSORHASH[sensorKey] = new SensorReading(min, max, (min + max) / 2.0, value, unit);
                }
                else
                {
                    SENSORHASH[sensorKey] = new SensorReading(value, value, value, value, unit);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Error reading hwmon files for {Prefix} in {Dir}", prefix, hwmonDir);
        }
    }

    private static string? ReadFileContent(string path)
    {
        try
        {
            if (File.Exists(path))
                return File.ReadAllText(path).Trim();
        }
        catch { }
        return null;
    }

    public static List<HwmonSensorInfo> GetOrderedList()
    {
        var result = new List<HwmonSensorInfo>();

        if (Directory.Exists(HwmonPath))
        {
            foreach (var hwmonDir in Directory.GetDirectories(HwmonPath))
            {
                var deviceName = ReadFileContent(Path.Combine(hwmonDir, "name")) ?? Path.GetFileName(hwmonDir);
                var hwmonId = Path.GetFileName(hwmonDir);

                AddSensorInfos(result, hwmonDir, hwmonId, deviceName, "temp", "Temperature", "°C");
                AddSensorInfos(result, hwmonDir, hwmonId, deviceName, "fan", "Fan", "RPM");
                AddSensorInfos(result, hwmonDir, hwmonId, deviceName, "in", "Voltage", "V");
                AddSensorInfos(result, hwmonDir, hwmonId, deviceName, "curr", "Current", "A");
                AddSensorInfos(result, hwmonDir, hwmonId, deviceName, "power", "Power", "W");
                AddSensorInfos(result, hwmonDir, hwmonId, deviceName, "freq", "Frequency", "MHz");
                AddSensorInfos(result, hwmonDir, hwmonId, deviceName, "humidity", "Humidity", "%");
            }
        }

        // Also include thermal zones
        if (Directory.Exists(ThermalPath))
        {
            foreach (var zoneDir in Directory.GetDirectories(ThermalPath, "thermal_zone*"))
            {
                var tempFile = Path.Combine(zoneDir, "temp");
                if (!File.Exists(tempFile)) continue;

                var zoneName = Path.GetFileName(zoneDir);
                var zoneType = ReadFileContent(Path.Combine(zoneDir, "type")) ?? zoneName;
                var sensorKey = $"thermal/{zoneName}";

                result.Add(new HwmonSensorInfo
                {
                    SensorId = sensorKey,
                    DeviceName = "Thermal Zones",
                    Category = "Temperature",
                    Label = zoneType,
                    Unit = "°C"
                });
            }
        }

        // Add Linux system sensors (CPU, Memory, Disk, Network, Load, Power, RAPL)
        result.AddRange(LinuxSystemSensors.Instance.GetSensorInfoList());

        return result;
    }

    private static void AddSensorInfos(List<HwmonSensorInfo> list, string hwmonDir, string hwmonId,
        string deviceName, string prefix, string category, string unit)
    {
        try
        {
            foreach (var file in Directory.GetFiles(hwmonDir, $"{prefix}*_input"))
            {
                var fileName = Path.GetFileNameWithoutExtension(file);
                var index = fileName.Replace(prefix, "").Replace("_input", "").Trim('_');

                var labelFile = Path.Combine(hwmonDir, $"{prefix}{index}_label");
                var label = ReadFileContent(labelFile) ?? $"{prefix}{index}";

                var sensorKey = $"{hwmonId}/{prefix}{index}";

                list.Add(new HwmonSensorInfo
                {
                    SensorId = sensorKey,
                    DeviceName = deviceName,
                    Category = category,
                    Label = label,
                    Unit = unit
                });
            }
        }
        catch { }
    }
}

public class HwmonSensorInfo
{
    public string SensorId { get; set; } = "";
    public string DeviceName { get; set; } = "";
    public string Category { get; set; } = "";
    public string Label { get; set; } = "";
    public string Unit { get; set; } = "";
}
