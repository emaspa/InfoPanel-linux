using System.Text.RegularExpressions;
using InfoPanel.Sensors;

namespace InfoPanel.Services;

internal sealed record HwmonDeviceIdentity(string Chip, string Anchor, string? Secondary, SensorIdentityStrength Strength,
    bool IsAmbiguous = false, string? AmbiguityReason = null);
internal sealed record ThermalZoneIdentity(string ClassPath, string RealPath, string RawType, HwmonDeviceIdentity Identity);

/// <summary>Owner-aware scan-time identity derivation. Kernel enumeration names classify nodes but never enter ids.</summary>
internal sealed class HwmonIdentity(SysfsAccess sysfs)
{
    private sealed record Node(string Path, string Subsystem)
    {
        public string Name => System.IO.Path.GetFileName(Path);
    }
    private string? Read(string path, string attribute) => Nonempty(sysfs.ReadMetadata(Path.Combine(path, attribute)));
    private static string? Nonempty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static string? Serial(string? value) => value == null || value.Equals("unknown", StringComparison.OrdinalIgnoreCase)
        || value.Equals("none", StringComparison.OrdinalIgnoreCase) || value.Equals("n/a", StringComparison.OrdinalIgnoreCase) ? null : Nonempty(value);
    private static string C(string kind, params string[] tokens) => SensorId.Component(kind, tokens);
    private static bool Matches(string value, string pattern) => Regex.IsMatch(value, "\\A" + pattern + "\\z", RegexOptions.CultureInvariant);
    private static string? PciAddress(Node? node) => node != null && Matches(node.Name, "[0-9a-fA-F]{4}:[0-9a-fA-F]{2}:[0-9a-fA-F]{2}\\.[0-7]")
        ? node.Name.ToLowerInvariant().Replace(':', '-') : null;

    private List<Node> Ancestry(string realPath)
    {
        var result = new List<Node>();
        var device = sysfs.ResolvePath(Path.Combine(realPath, "device"));
        // Follow the owner link as well as the real ancestry; some class devices are virtual.
        foreach (var start in new[] { device, realPath })
        {
            for (var path = start; path != null && path.StartsWith(sysfs.Root + "/", StringComparison.Ordinal); path = Path.GetDirectoryName(path))
            {
                if (result.Any(n => n.Path == path)) continue;
                var subsystem = sysfs.ResolvePath(Path.Combine(path, "subsystem"));
                result.Add(new Node(path, subsystem == null ? "" : Path.GetFileName(subsystem)));
            }
        }
        return result;
    }

    private string? Firmware(IEnumerable<Node> nodes)
    {
        foreach (var node in nodes)
        {
            var device = sysfs.ResolvePath(Path.Combine(node.Path, "device"));
            var firmware = sysfs.ResolvePath(Path.Combine(node.Path, "firmware_node"));
            foreach (var path in new[] { node.Path, device, firmware }.Where(p => p != null).Distinct())
            {
                var acpi = Read(path!, "path");
                if (acpi?.StartsWith('\\') == true) return C("acpi", acpi);
                var of = sysfs.ResolvePath(Path.Combine(path!, "of_node"));
                var prefix = sysfs.PathUnderRoot("firmware", "devicetree", "base");
                if (of != null && of.StartsWith(prefix + "/", StringComparison.Ordinal)) return C("of", of[prefix.Length..]);
            }
        }
        return null;
    }

    public ThermalZoneIdentity Thermal(string classPath, string realPath)
    {
        var raw = Read(classPath, "type") ?? "unknown";
        var chip = SensorId.NormalizeChipName(raw);
        return new(classPath, realPath, raw, new(chip, C("type", chip), Firmware(Ancestry(realPath)), SensorIdentityStrength.Weak));
    }

    public HwmonDeviceIdentity Derive(string realPath, string rawChip, IReadOnlyList<(string Channel, string? Label)> channels,
        IReadOnlyList<ThermalZoneIdentity> zones, string? chipLabel)
    {
        var chip = SensorId.NormalizeChipName(rawChip);
        var nodes = Ancestry(realPath);
        var pci = nodes.FirstOrDefault(n => n.Subsystem == "pci" && PciAddress(n) != null);
        var pciAddress = PciAddress(pci);
        var platform = nodes.FirstOrDefault(n => n.Subsystem == "platform");
        var topology = pciAddress != null ? C("pci", pciAddress) : platform != null ? C("platform", platform.Name) : null;
        var nvme = nodes.FirstOrDefault(n => n.Subsystem == "nvme" || Matches(n.Name, "nvme[0-9]+"));
        if (nvme != null)
        {
            var serial = Serial(Read(nvme.Path, "serial"));
            if (serial == null)
            {
                foreach (var entry in sysfs.GetDirectories(sysfs.PathUnderRoot("class", "nvme"), "nvme*"))
                    if (sysfs.ResolvePath(entry) == nvme.Path) serial = Serial(Read(entry, "serial"));
            }
            if (serial != null) return new(chip, C("nvme-serial", serial), pciAddress == null ? null : C("pci", pciAddress), SensorIdentityStrength.Strong);
        }

        // Associate a block device with its actual owner, never with a shared controller ancestor.
        var blockOwner = nodes.FirstOrDefault(n => n.Subsystem is "scsi" or "block");
        if (chip == "drivetemp" || blockOwner != null)
        {
            var blocks = sysfs.GetDirectories(sysfs.PathUnderRoot("class", "block"));
            var identities = new List<HwmonDeviceIdentity>();
            foreach (var block in blocks)
            {
                var realBlock = sysfs.ResolvePath(block);
                var device = sysfs.ResolvePath(Path.Combine(block, "device"));
                if (realBlock == null || !nodes.Any(n => n.Path == realBlock || (device != null && n.Path == device))) continue;
                var wwid = Serial(Read(block, "wwid")) ?? (device == null ? null : Serial(Read(device, "wwid")));
                var serial = device == null ? null : Serial(Read(device, "serial"));
                var transport = device == null ? null : Read(device, "sas_address");
                if (wwid != null) identities.Add(new(chip, C("block-wwid", wwid), transport == null ? null : C("port", transport), SensorIdentityStrength.Strong));
                else if (serial != null) identities.Add(new(chip, C("block-serial", serial), transport == null ? null : C("port", transport), SensorIdentityStrength.Strong));
            }
            var distinct = identities.Distinct().ToArray();
            if (distinct.Length == 1) return distinct[0];
            if (distinct.Length > 1) return Fallback(chip, chipLabel, channels) with
            { IsAmbiguous = true, AmbiguityReason = "Owner is associated with multiple block identities" };
            // A disk's enclosing SATA/USB controller does not identify the disk.
            return Fallback(chip, chipLabel, channels);
        }

        var usb = nodes.FirstOrDefault(n => n.Subsystem == "usb" && Read(n.Path, "idVendor") != null && Read(n.Path, "idProduct") != null);
        var client = nodes.FirstOrDefault(n => n.Subsystem == "i2c" && Matches(n.Name, "[0-9]+-[0-9a-fA-F]{4}"));
        // Child buses own their clients, even beneath a PCI/platform controller.
        if (client != null && (usb == null || nodes.IndexOf(client) < nodes.IndexOf(usb))) return I2c(chip, client, nodes, topology);
        if (usb != null)
        {
            var vid = Read(usb.Path, "idVendor")!.ToLowerInvariant().PadLeft(4, '0');
            var pid = Read(usb.Path, "idProduct")!.ToLowerInvariant().PadLeft(4, '0');
            if (!Matches(vid, "[0-9a-f]{4}") || !Matches(pid, "[0-9a-f]{4}")) return Fallback(chip, chipLabel, channels);
            var port = Regex.Match(usb.Name, @"\A[0-9]+-([0-9]+(?:\.[0-9]+)*)\z");
            var role = nodes.Select(n => Read(n.Path, "bInterfaceNumber")).FirstOrDefault(v => v != null);
            var serial = Serial(Read(usb.Path, "serial"));
            var secondary = topology != null && port.Success ? C("port", [topology, port.Groups[1].Value, .. role == null ? Array.Empty<string>() : new[] { role }])
                : role != null ? C("role", role) : null;
            if (serial != null) return new(chip, C("usb-serial", vid, pid, serial), secondary, SensorIdentityStrength.Strong);
            if (topology != null && port.Success) return new(chip, C("usb-port", vid, pid, topology, port.Groups[1].Value),
                secondary, SensorIdentityStrength.Location);
            return Fallback(chip, chipLabel, channels);
        }

        var zone = zones.FirstOrDefault(z => nodes.Any(n => n.Path == z.RealPath));
        if (zone != null)
        {
            var aggregate = channels.Count(c => c.Channel.StartsWith("temp", StringComparison.Ordinal)) != 1;
            return zone.Identity with { Chip = chip, IsAmbiguous = aggregate,
                AmbiguityReason = aggregate ? "Thermal aggregate channel-to-zone association is unknown" : null };
        }
        // Also support thermal ancestry when a class/thermal entry was not exposed.
        var thermal = nodes.FirstOrDefault(n => n.Subsystem == "thermal" || Matches(n.Name, "thermal_zone[0-9]+"));
        if (thermal != null && Read(thermal.Path, "type") is { } type)
            return new(chip, C("type", SensorId.NormalizeChipName(type)), Firmware([thermal]), SensorIdentityStrength.Weak,
                channels.Count(c => c.Channel.StartsWith("temp", StringComparison.Ordinal)) != 1, "Thermal aggregate channel-to-zone association is unknown");
        if (pciAddress != null)
        {
            var mdio = Regex.Match(rawChip, @"\Ar8169_[0-9]+_[0-9a-fA-F]+:([0-9a-fA-F]+)\z");
            var phy = nodes.FirstOrDefault(n => n.Subsystem == "mdio_bus");
            var phyAddress = phy == null ? null : Regex.Match(phy.Name, @":([0-9a-fA-F]{1,2})\z");
            var address = mdio.Success ? mdio.Groups[1].Value.ToLowerInvariant().PadLeft(2, '0')
                : phyAddress?.Success == true ? phyAddress.Groups[1].Value.ToLowerInvariant().PadLeft(2, '0') : null;
            return new(chip, C("pci", pciAddress), address == null ? Firmware(nodes.TakeWhile(n => n != pci)) : C("role", "mdio", address), SensorIdentityStrength.Location);
        }
        if (platform != null) return new(chip, C("platform", platform.Name), Firmware(nodes)
            ?? (chipLabel == null ? null : C("meta", chipLabel)), SensorIdentityStrength.Location);

        // Generic thermal hwmon registrations may live outside the zone ancestry.
        var sameType = zones.Where(z => z.Identity.Chip == chip).ToArray();
        if (sameType.Length > 0)
        {
            var unique = sameType.Length == 1 && channels.Count(c => c.Channel.StartsWith("temp", StringComparison.Ordinal)) == 1;
            return new(chip, C("type", chip), unique ? sameType[0].Identity.Secondary : null, SensorIdentityStrength.Weak,
                !unique, unique ? null : "Thermal aggregate channel-to-zone association is unknown");
        }
        return Fallback(chip, chipLabel, channels);
    }

    private HwmonDeviceIdentity I2c(string chip, Node client, List<Node> nodes, string? topology)
    {
        var adapters = nodes.Where(n => Matches(n.Name, "i2c-[0-9]+")).ToArray();
        var root = adapters.LastOrDefault();
        var adapterName = root == null ? "unknown" : Read(root.Path, "name") ?? "unknown";
        // Only recognized generated names are normalized. Ordinary adapter names retain meaningful numbers.
        adapterName = Regex.Replace(adapterName, @"\Ai2c-[0-9]+-mux \(chan_id ([0-9]+)\)\z", "i2c-mux");
        if (Matches(adapterName, "i2c-[0-9]+")) adapterName = "i2c";
        var route = new List<string>();
        if (topology != null) route.Add(topology);
        foreach (var adapter in adapters.Reverse())
        {
            var mux = sysfs.ResolvePath(Path.Combine(adapter.Path, "mux_device"));
            if (mux == null) continue;
            var address = Regex.Match(Path.GetFileName(mux), @"\A[0-9]+-([0-9a-fA-F]{4})\z");
            if (!address.Success) continue;
            var channel = sysfs.GetDirectories(mux, "channel-*").FirstOrDefault(p => sysfs.ResolvePath(p) == adapter.Path);
            if (channel == null) continue;
            route.Add(address.Groups[1].Value.ToLowerInvariant());
            route.Add(Path.GetFileName(channel)["channel-".Length..]);
        }
        return new(chip, C("i2c", adapterName, client.Name[(client.Name.IndexOf('-') + 1)..].ToLowerInvariant()),
            route.Count == 0 ? null : C("adapter", route.ToArray()), SensorIdentityStrength.Location);
    }

    private static HwmonDeviceIdentity Fallback(string chip, string? label, IReadOnlyList<(string Channel, string? Label)> channels)
    {
        // A signature is explicitly weak evidence, but is stable under enumeration permutations.
        var signature = string.Join(';', channels.OrderBy(c => c.Channel, StringComparer.Ordinal).Select(c => c.Channel + "=" + (c.Label ?? "")));
        return new(chip, C("name", chip), label != null ? C("meta", label) : signature.Length > 0 ? C("meta", signature) : null, SensorIdentityStrength.Weak);
    }
}
