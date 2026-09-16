using System.Globalization;
using System.Text.RegularExpressions;
using InfoPanel.Sensors;

namespace InfoPanel.Services;

/// <summary>Pure vendor key builders and shared descriptor presentation. No vendor library calls.</summary>
internal static class SystemSensorIdentity
{
    public static string? NormalizePci(string? address)
    {
        var match = Regex.Match(address?.Trim() ?? "", @"\A([0-9a-fA-F]{4,8})[:-]([0-9a-fA-F]{2})[:-]([0-9a-fA-F]{2})\.([0-7])\z");
        if (!match.Success) return null;
        var domain = uint.Parse(match.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return $"{domain:x4}-{match.Groups[2].Value.ToLowerInvariant()}-{match.Groups[3].Value.ToLowerInvariant()}.{match.Groups[4].Value}";
    }

    public static string RocmPciAddress(ulong bdf) => $"{bdf >> 32:x4}-{(bdf >> 8) & 0xff:x2}-{(bdf >> 3) & 0x1f:x2}.{bdf & 7}";

    public static SensorId? Nvidia(string? uuid, string? pci, string metric)
    {
        var address = NormalizePci(pci);
        var location = address == null ? null : SensorId.Component("pci", address);
        return !string.IsNullOrWhiteSpace(uuid)
            ? SensorId.System("gpu", SensorId.Component("gpu-uuid", uuid), metric, location)
            : location == null ? null : SensorId.System("gpu", location, metric);
    }

    public static SensorId? Amd(string? pci, string metric) => NormalizePci(pci) is { } address
        ? SensorId.System("amdgpu", SensorId.Component("pci", address), metric) : null;

    public static SensorDescriptor Descriptor(SensorId id, string alias, string deviceName, string label,
        string category, string unit, string realPath = "", string valuePath = "") => new(id)
        {
            LegacyAliases = [alias], ChipDisplayName = deviceName, Label = label, RawLabel = label,
            IsLabelSynthetic = false, Category = category, Unit = unit, RealPath = realPath, ValuePath = valuePath,
            IdentityStrength = id.HasStrongIdentity ? SensorIdentityStrength.Strong
                : id.AnchorKind == "name" ? SensorIdentityStrength.Weak : SensorIdentityStrength.Location
        };

    public static HwmonSensorInfo Info(SensorDescriptor d) => new()
    {
        SensorId = d.StableId, DeviceName = d.ChipDisplayName, ChipKey = d.ChipKey, Label = d.Label,
        Category = d.Category, Unit = d.Unit, LegacyAlias = d.LegacyAlias,
        IdentityStrength = d.IdentityStrength, IsAmbiguous = d.IsAmbiguous
    };
}

/// <summary>Join DRM cards to vendor APIs by the full PCI domain/bus/device/function, never cardN.</summary>
internal static class DrmDevices
{
    public static IEnumerable<(string CardPath, string Pci)> Scan(SysfsAccess sysfs, string vendor) =>
        sysfs.GetDirectories(sysfs.PathUnderRoot("class", "drm"), "card*")
            .Where(p => Regex.IsMatch(Path.GetFileName(p), @"\Acard[0-9]+\z"))
            .Where(p => sysfs.ReadMetadata(Path.Combine(p, "device", "vendor")) == vendor)
            .Select(p => (CardPath: p, Pci: SystemSensorIdentity.NormalizePci(Path.GetFileName(sysfs.ResolvePath(Path.Combine(p, "device"))))))
            .Where(p => p.Pci != null).Select(p => (p.CardPath, p.Pci!))
            .OrderBy(p => ulong.Parse(p.Item2.Split('-')[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture))
            .ThenBy(p => p.Item2, StringComparer.Ordinal);

    public static string? Find(SysfsAccess sysfs, string vendor, string pci)
    {
        var address = SystemSensorIdentity.NormalizePci(pci);
        var matches = Scan(sysfs, vendor).Where(c => c.Pci == address).Take(2).ToArray();
        return matches.Length == 1 ? matches[0].CardPath : null;
    }
}
