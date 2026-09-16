using System.Collections.Immutable;
using System.Text.RegularExpressions;
using InfoPanel.Sensors;

namespace InfoPanel.Services;

internal sealed record DiskDevice(string Name, string Identity, string RealPath, string StatPath,
    ImmutableArray<SensorDescriptor> Descriptors);

/// <summary>Block identity reads are confined to discovery through the injected sysfs root.</summary>
internal sealed class DiskCatalogScanner(SysfsAccess sysfs)
{
    private static readonly Regex DevicePattern = new(@"\A(sd[a-z]+|nvme\d+n\d+|vd[a-z]+|mmcblk\d+)\z");
    private static readonly (string Family, string Metric, string Label, string Category, string Unit)[] Metrics =
    [
        ("disk", "read_speed", "Read", "Throughput", "MB/s"),
        ("disk", "write_speed", "Write", "Throughput", "MB/s"),
        ("block", "queue_depth", "Queue Depth", "Performance", ""),
        ("block", "read_iops", "Read IOPS", "Performance", "IOPS"),
        ("block", "write_iops", "Write IOPS", "Performance", "IOPS"),
        ("block", "read_latency", "Read Latency", "Performance", "ms"),
        ("block", "write_latency", "Write Latency", "Performance", "ms")
    ];

    public ImmutableArray<DiskDevice> Scan()
    {
        var controllers = sysfs.GetDirectories(sysfs.PathUnderRoot("class", "nvme"), "nvme*")
            .Where(p => Regex.IsMatch(Path.GetFileName(p), @"\Anvme[0-9]+\z"))
            .Select(p => (Path: p, Real: sysfs.ResolvePath(p))).Where(c => c.Real != null).ToArray();
        var disks = ImmutableArray.CreateBuilder<DiskDevice>();
        foreach (var path in sysfs.GetDirectories(sysfs.PathUnderRoot("class", "block")))
        {
            var name = Path.GetFileName(path);
            if (!DevicePattern.IsMatch(name)) continue;
            var real = sysfs.ResolvePath(path);
            if (real == null || sysfs.FileExists(Path.Combine(path, "partition"))) continue;
            // Match actual controller ancestry (or the block device link), never a guessed nvmeN prefix.
            var device = sysfs.ResolvePath(Path.Combine(path, "device"));
            var controller = controllers.Where(c => real.StartsWith(c.Real + "/", StringComparison.Ordinal)
                || device == c.Real || device?.StartsWith(c.Real + "/", StringComparison.Ordinal) == true)
                .OrderByDescending(c => c.Real!.Length).FirstOrDefault();
            var nvmeSerial = controller.Path == null ? null : Read(controller.Path, "serial");
            var serial = nvmeSerial ?? Read(path, "device/serial") ?? Read(path, "serial");
            var wwid = Read(path, "wwid") ?? Read(path, "device/wwid");
            var anchor = nvmeSerial != null ? SensorId.Component("nvme-serial", nvmeSerial)
                : wwid != null ? SensorId.Component("block-wwid", wwid)
                : serial != null ? SensorId.Component("block-serial", serial) : SensorId.Component("name", name);
            var model = (controller.Path == null ? null : Read(controller.Path, "model"))
                ?? Read(path, "device/model") ?? Read(path, "model") ?? "Disk";
            var label = serial == null ? $"{model} ({name})" : $"{model} (…{serial[^Math.Min(4, serial.Length)..]}, {name})";
            var stat = Path.Combine(path, "stat");
            var descriptors = Metrics.Select(m => SystemSensorIdentity.Descriptor(
                SensorId.System(m.Family, anchor, m.Metric), $"system/{m.Family}/{name}/{m.Metric}",
                m.Family == "disk" ? "System Disk I/O" : "System Block I/O", $"{label} {m.Label}", m.Category, m.Unit,
                real, m.Family == "block" ? stat : "")).ToImmutableArray();
            disks.Add(new(name, anchor, real, stat, descriptors));
        }
        return disks.ToImmutable();
    }

    private string? Read(string path, string attribute)
    {
        var value = sysfs.ReadMetadata(Path.Combine(path, attribute));
        return string.IsNullOrWhiteSpace(value) || value.Equals("unknown", StringComparison.OrdinalIgnoreCase) ? null : value;
    }
}
