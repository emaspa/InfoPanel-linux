using System.Collections.Concurrent;
using InfoPanel.Services;

namespace InfoPanel.Sensors.Linux.Tests;

/// <summary>Real relative symlinks and bounded production file reads; all paths stay in a temporary /sys tree.</summary>
public sealed class FakeSysfs : SysfsAccess, IDisposable
{
    public bool ReverseListings { get; set; }
    public string? FailedListing { get; set; }
    public int MetadataReads;
    public int Listings;
    public int Resolutions;
    public ConcurrentDictionary<string, int> ValueReads { get; } = new(StringComparer.Ordinal);
    public FakeSysfs() : base(Path.Combine(Path.GetTempPath(), "infopanel-sysfs-" + Guid.NewGuid().ToString("N"))) => Directory.CreateDirectory(Root);
    public string Dir(string relative)
    {
        var path = PathUnderRoot(relative);
        Directory.CreateDirectory(path);
        return path;
    }
    public void Write(string relative, string value)
    {
        var path = PathUnderRoot(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, value + "\n");
    }
    public void Link(string relative, string targetRelative)
    {
        var path = PathUnderRoot(relative);
        var target = PathUnderRoot(targetRelative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.Delete(path);
        Directory.CreateSymbolicLink(path, Path.GetRelativePath(Path.GetDirectoryName(path)!, target));
    }
    public string Device(string relative, string subsystem)
    {
        Dir(relative);
        Dir("bus/" + subsystem);
        Link(relative + "/subsystem", "bus/" + subsystem);
        return relative;
    }
    public string Pci(string address) => Device("devices/pci0000:00/" + address, "pci");
    public string Platform(string name) => Device("devices/platform/" + name, "platform");
    public string Hwmon(int number, string owner, string chip, bool deviceLink = true, params string[] channels)
    {
        var path = owner + "/hwmon/hwmon" + number;
        Dir(path);
        Write(path + "/name", chip);
        Link("class/hwmon/hwmon" + number, path);
        if (deviceLink) Link(path + "/device", owner);
        foreach (var channel in channels.Length == 0 ? new[] { "temp1" } : channels) Channel(path, channel);
        return path;
    }
    public void Channel(string hwmon, string channel, string? label = null, string value = "42000")
    {
        Write(hwmon + "/" + channel + "_input", value);
        if (label != null) Write(hwmon + "/" + channel + "_label", label);
    }
    public string Nvme(int hwmon, int controller, string pci, string serial)
    {
        var owner = Device(Pci(pci) + "/nvme/nvme" + controller, "nvme");
        Write(owner + "/serial", serial);
        Link("class/nvme/nvme" + controller, owner);
        var path = Hwmon(hwmon, owner, "nvme");
        Channel(path, "temp1", "Composite");
        return path;
    }
    public string Thermal(int number, string type, string? acpiPath = null)
    {
        var path = Device("devices/virtual/thermal/thermal_zone" + number, "thermal");
        Write(path + "/type", type);
        Write(path + "/temp", "37000");
        Link("class/thermal/thermal_zone" + number, path);
        if (acpiPath != null)
        {
            var acpi = Device("devices/acpi/zone" + number, "acpi");
            Write(acpi + "/path", acpiPath);
            Link(path + "/device", acpi);
        }
        return path;
    }
    public override string? ReadMetadata(string path)
    {
        Interlocked.Increment(ref MetadataReads);
        return base.ReadMetadata(path);
    }
    public override string? ReadValue(string path)
    {
        ValueReads.AddOrUpdate(path, 1, (_, count) => count + 1);
        return base.ReadValue(path);
    }
    public override bool FileExists(string path)
    {
        Interlocked.Increment(ref MetadataReads);
        return base.FileExists(path);
    }
    public override string? ResolvePath(string path)
    {
        Interlocked.Increment(ref Resolutions);
        return base.ResolvePath(path);
    }
    private string[] Listing(string path, Func<string[]> list)
    {
        Interlocked.Increment(ref Listings);
        if (path == FailedListing) throw new IOException("Simulated unavailable class listing");
        var entries = list().Order(StringComparer.Ordinal).ToArray();
        return ReverseListings ? entries.Reverse().ToArray() : entries;
    }
    public override string[] GetDirectories(string path, string pattern = "*") => Listing(path, () => base.GetDirectories(path, pattern));
    public override string[] GetFiles(string path, string pattern = "*") => Listing(path, () => base.GetFiles(path, pattern));
    public void ResetCounters()
    {
        MetadataReads = Listings = Resolutions = 0;
        ValueReads.Clear();
    }
    public void Dispose() => Directory.Delete(Root, recursive: true);
}
