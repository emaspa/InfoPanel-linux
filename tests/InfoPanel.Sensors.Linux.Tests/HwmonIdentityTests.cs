using InfoPanel.Services;
using Xunit;

namespace InfoPanel.Sensors.Linux.Tests;

public class HwmonIdentityTests
{
    [Fact]
    public void AllSevenPrefixesRetainChannelsUnitsAndDivisorsWithoutValueReads()
    {
        using var fs = new FakeSysfs();
        var channels = new[] { "temp1", "fan2", "in0", "curr5", "power1", "freq3", "humidity2" };
        fs.Hwmon(9, fs.Platform("wireview_hwmon"), "wireview", true, channels);
        var catalog = new HwmonCatalogScanner(fs).Scan().Snapshot;
        Assert.Equal(7, catalog.Descriptors.Length);
        var units = new[] { "°C", "RPM", "V", "A", "W", "MHz", "%" };
        var divisors = new[] { 1000d, 1, 1000, 1000, 1000000, 1000000, 1000 };
        for (var i = 0; i < channels.Length; i++)
        {
            var d = Assert.Single(catalog.Descriptors, d => d.Channel == channels[i]);
            Assert.Equal($"hwmon/v1/wireview/platform+wireview_hwmon/{channels[i]}", d.StableId);
            Assert.Equal(units[i], d.Unit);
            Assert.Equal(divisors[i], d.Divisor);
        }
        Assert.Empty(fs.ValueReads);
    }

    [Fact]
    public void NvmeAndHwmonRenumberingAndListingOrderDoNotChangeIds()
    {
        using var first = new FakeSysfs();
        using var second = new FakeSysfs { ReverseListings = true };
        first.Nvme(2, 0, "0000:81:00.0", "TEST-A");
        first.Nvme(3, 1, "0000:01:00.0", "TEST-B");
        second.Nvme(3, 1, "0000:81:00.0", "TEST-A");
        second.Nvme(2, 0, "0000:01:00.0", "TEST-B");
        var a = new HwmonCatalogScanner(first).Scan().Snapshot;
        var b = new HwmonCatalogScanner(second).Scan().Snapshot;
        Assert.Equal(a.Descriptors.Select(d => d.StableId), b.Descriptors.Select(d => d.StableId));
        Assert.Equal(new[] { "hwmon/v1/nvme/nvme-serial+TEST-A~pci+0000-81-00.0/temp1",
            "hwmon/v1/nvme/nvme-serial+TEST-B~pci+0000-01-00.0/temp1" }, a.Descriptors.Select(d => d.StableId));
        Assert.All(a.Descriptors, d => Assert.Equal("Composite", d.Label));
    }

    [Fact]
    public void DuplicateSerialAlwaysHasTopologyAndSurvivorDoesNotRename()
    {
        using var fs = new FakeSysfs();
        fs.Nvme(0, 0, "0000:01:00.0", "DUPLICATE");
        fs.Nvme(1, 1, "0000:02:00.0", "DUPLICATE");
        var scanner = new HwmonCatalogScanner(fs);
        var first = scanner.Scan().Snapshot;
        Assert.Equal(2, first.StableIdIndex.Count);
        File.Delete(fs.PathUnderRoot("class/hwmon/hwmon0"));
        var second = scanner.Scan(first).Snapshot;
        Assert.Equal(first.Descriptors[1].StableId, Assert.Single(second.Descriptors).StableId);
    }

    [Theory]
    [InlineData("amdgpu", "pci", "0000:03:00.0", "pci+0000-03-00.0")]
    [InlineData("nouveau", "pci", "0000:04:00.0", "pci+0000-04-00.0")]
    [InlineData("i915", "pci", "0000:00:02.0", "pci+0000-00-02.0")]
    [InlineData("k10temp", "pci", "0000:00:18.3", "pci+0000-00-18.3")]
    [InlineData("coretemp", "platform", "coretemp.0", "platform+coretemp.0")]
    [InlineData("nct6775", "platform", "nct6775.656", "platform+nct6775.656")]
    [InlineData("it87", "platform", "it87.656", "platform+it87.656")]
    public void ActualPciAndPlatformOwnersSurviveMissingDeviceLink(string chip, string kind, string name, string anchor)
    {
        using var fs = new FakeSysfs();
        var owner = kind == "pci" ? fs.Pci(name) : fs.Platform(name);
        fs.Hwmon(8, owner + "/arbitrary/deep/driver", chip, false);
        Assert.Equal($"hwmon/v1/{chip}/{anchor}/temp1", Assert.Single(new HwmonCatalogScanner(fs).Scan().Snapshot.Descriptors).StableId);
    }

    [Fact]
    public void R8169GeneratedNameUsesPciEndpointAndMdioAddress()
    {
        using var fs = new FakeSysfs();
        var endpoint = fs.Pci("0000:ab:00.0");
        var mdio = fs.Device(endpoint + "/mdio_bus/r8169-0-ab00/r8169-0-ab00:00", "mdio_bus");
        fs.Hwmon(1, mdio, "r8169_0_ab00:00");
        var d = Assert.Single(new HwmonCatalogScanner(fs).Scan().Snapshot.Descriptors);
        Assert.Equal("hwmon/v1/r8169/pci+0000-ab-00.0~role+mdio+00/temp1", d.StableId);
        Assert.Equal("r8169_0_ab00:00", d.RawChipName);
    }

    [Theory]
    [InlineData("SERIAL", true)][InlineData("", false)][InlineData("unknown", false)]
    public void UsbSerialAndPortAnchorsSurviveBusRenumberingAtArbitraryDepth(string serial, bool strong)
    {
        string Scan(int bus)
        {
            using var fs = new FakeSysfs();
            var host = fs.Pci("0000:00:14.0");
            var usb = fs.Device(host + $"/usb{bus}/{bus}-2.3", "usb");
            fs.Write(usb + "/idVendor", "1A2B");
            fs.Write(usb + "/idProduct", "0042");
            fs.Write(usb + "/serial", serial);
            var iface = fs.Device(usb + $"/{bus}-2.3:1.0", "usb");
            fs.Write(iface + "/bInterfaceNumber", "00");
            fs.Hwmon(7, iface + "/hid/nested/extra", "usbchip", false);
            return Assert.Single(new HwmonCatalogScanner(fs).Scan().Snapshot.Descriptors).StableId;
        }
        var id = Scan(1);
        Assert.Equal(id, Scan(9));
        Assert.Contains(strong ? "usb-serial+1a2b+0042+SERIAL" : "usb-port+1a2b+0042+pci%2B0000-00-14.0+2.3", id);
        Assert.Contains("~port+pci%2B0000-00-14.0+2.3+00", id);
    }

    [Fact]
    public void I2cUsesClientAddressAndAdapterTopologyInsteadOfPciControllerAlone()
    {
        string Scan(int bus)
        {
            using var fs = new FakeSysfs();
            var adapter = fs.Device(fs.Pci("0000:00:1f.4") + "/i2c-" + bus, "i2c");
            fs.Write(adapter + "/name", "SMBus I801 adapter");
            var client = fs.Device(adapter + $"/{bus}-002d", "i2c");
            fs.Hwmon(bus, client, "nct7802");
            return Assert.Single(new HwmonCatalogScanner(fs).Scan().Snapshot.Descriptors).StableId;
        }
        Assert.Equal("hwmon/v1/nct7802/i2c+SMBus%20I801%20adapter+002d~adapter+pci%2B0000-00-1f.4/temp1", Scan(0));
        Assert.Equal(Scan(0), Scan(91));
    }

    [Fact]
    public void I2cMuxChannelsAreDistinctAndDoNotPersistLogicalBusNumbers()
    {
        string[] Scan(int rootBus, int firstBus)
        {
            using var fs = new FakeSysfs();
            var root = fs.Device(fs.Pci("0000:00:1f.4") + $"/i2c-{rootBus}", "i2c");
            fs.Write(root + "/name", "SMBus I801 adapter");
            var mux = fs.Device(root + $"/{rootBus}-0070", "i2c");
            for (var i = 0; i < 2; i++)
            {
                var bus = firstBus + i;
                var adapter = fs.Device(root + $"/i2c-{bus}", "i2c");
                fs.Write(adapter + "/name", $"i2c-{rootBus}-mux (chan_id {i})");
                fs.Link(adapter + "/mux_device", mux);
                fs.Link(mux + $"/channel-{i}", adapter);
                fs.Hwmon(i, fs.Device(adapter + $"/{bus}-0048", "i2c"), "tmp102");
            }
            var scan = new HwmonCatalogScanner(fs).Scan();
            Assert.Empty(scan.Snapshot.Ambiguities);
            return scan.Snapshot.Descriptors.Select(d => d.StableId).ToArray();
        }
        var ids = Scan(7, 70);
        Assert.Equal(ids, Scan(2, 90));
        Assert.Contains("+0070+0/temp1", ids[0]);
        Assert.Contains("+0070+1/temp1", ids[1]);
    }

    [Theory]
    [InlineData(true)][InlineData(false)]
    public void BlockIdentitySurvivesScsiAndBlockRenumbering(bool wwid)
    {
        string Scan(int host, string block)
        {
            using var fs = new FakeSysfs();
            var owner = fs.Device(fs.Pci("0000:00:17.0") + $"/ata1/host{host}/target{host}:0:0/{host}:0:0:0", "scsi");
            var device = fs.Device(owner + "/block/" + block, "block");
            fs.Link("class/block/" + block, device);
            fs.Link(device + "/device", owner);
            fs.Write(wwid ? device + "/wwid" : owner + "/serial", "DRIVE-A");
            fs.Hwmon(host, owner, "drivetemp");
            return Assert.Single(new HwmonCatalogScanner(fs).Scan().Snapshot.Descriptors).StableId;
        }
        Assert.Equal($"hwmon/v1/drivetemp/block-{(wwid ? "wwid" : "serial")}+DRIVE-A/temp1", Scan(0, "sda"));
        Assert.Equal(Scan(0, "sda"), Scan(12, "sdc"));
    }

    [Fact]
    public void ThermalHwmonAndDirectZonesUseTypeAndFirmwareAndNormalizeIwlwifi()
    {
        using var fs = new FakeSysfs();
        fs.Hwmon(0, fs.Thermal(7, "acpitz", "\\_TZ_.TZ00"), "acpitz");
        fs.Hwmon(1, fs.Thermal(9, "iwlwifi_1"), "iwlwifi_1");
        var ids = new HwmonCatalogScanner(fs).Scan().Snapshot.Descriptors.Select(d => d.StableId).ToArray();
        Assert.Contains("hwmon/v1/acpitz/type+acpitz~acpi+%5C_TZ_.TZ00/temp1", ids);
        Assert.Contains("thermal/v1/type+acpitz~acpi+%5C_TZ_.TZ00/temp", ids);
        Assert.Contains("hwmon/v1/iwlwifi/type+iwlwifi/temp1", ids);
        Assert.Contains("thermal/v1/type+iwlwifi/temp", ids);
    }

    [Fact]
    public void ThermalZoneRenumberingIsIndependentOfTypeCounter()
    {
        using var a = new FakeSysfs();
        using var b = new FakeSysfs();
        a.Thermal(0, "iwlwifi_1");
        b.Thermal(12, "iwlwifi_5");
        Assert.Equal(new HwmonCatalogScanner(a).Scan().Snapshot.Descriptors[0].StableId,
            new HwmonCatalogScanner(b).Scan().Snapshot.Descriptors[0].StableId);
    }

    [Fact]
    public void ThermalAggregateDoesNotGuessChannelAssociations()
    {
        using var fs = new FakeSysfs();
        var zone = fs.Thermal(0, "acpitz", "\\_TZ_.TZ00");
        fs.Thermal(1, "acpitz", "\\_TZ_.TZ01");
        fs.Hwmon(0, zone, "acpitz", true, "temp1", "temp2");
        var result = new HwmonCatalogScanner(fs).Scan();
        Assert.Equal(2, result.Snapshot.StableIdIndex.Count);
        Assert.All(result.Snapshot.Descriptors.Where(d => d.Id.Source == "hwmon"), d => Assert.True(d.IsAmbiguous));
        Assert.All(result.PollPlan.Entries, d => Assert.StartsWith("thermal/v1/", d.StableId));
    }

    [Theory]
    [InlineData("")][InlineData("unknown")]
    public void EmptyOrPlaceholderNvmeSerialFallsThroughToPci(string serial)
    {
        using var fs = new FakeSysfs();
        fs.Nvme(0, 0, "0000:01:00.0", serial);
        Assert.Equal("hwmon/v1/nvme/pci+0000-01-00.0/temp1", Assert.Single(new HwmonCatalogScanner(fs).Scan().Snapshot.Descriptors).StableId);
    }

    [Fact]
    public void IdentityTokensEncodeSeparatorsSpacesAndPercent()
    {
        using var fs = new FakeSysfs();
        fs.Nvme(0, 0, "0000:01:00.0", "Serial % + ~ / space");
        Assert.Equal("hwmon/v1/nvme/nvme-serial+Serial%20%25%20%2B%20%7E%20%2F%20space~pci+0000-01-00.0/temp1",
            Assert.Single(new HwmonCatalogScanner(fs).Scan().Snapshot.Descriptors).StableId);
    }

    [Fact]
    public void IdenticalScanPreservesSnapshotAndGenerationButPathReplacementChangesIt()
    {
        using var fs = new FakeSysfs();
        var owner = fs.Platform("coretemp.0");
        fs.Hwmon(0, owner, "coretemp");
        var scanner = new HwmonCatalogScanner(fs);
        var first = scanner.Scan();
        fs.ReverseListings = true;
        var second = scanner.Scan(first.Snapshot);
        Assert.True(second.IsIdentical);
        Assert.Same(first.Snapshot, second.Snapshot);
        var replacement = fs.Hwmon(1, owner, "coretemp");
        File.Delete(fs.PathUnderRoot("class/hwmon/hwmon1"));
        fs.Link("class/hwmon/hwmon0", replacement);
        var third = scanner.Scan(second.Snapshot);
        Assert.False(third.IsIdentical);
        Assert.Equal(first.Snapshot.Generation + 1, third.Snapshot.Generation);
        Assert.Equal(first.Snapshot.Descriptors[0].StableId, third.Snapshot.Descriptors[0].StableId);
        Assert.NotEqual(first.Snapshot.Descriptors[0].RealPath, third.Snapshot.Descriptors[0].RealPath);
    }

    [Fact]
    public void MultipleBlocksOnOneOwnerAreAmbiguousRatherThanSelectedByListingOrder()
    {
        using var fs = new FakeSysfs();
        var owner = fs.Device(fs.Pci("0000:00:17.0") + "/host0/target0:0:0/0:0:0:0", "scsi");
        foreach (var block in new[] { "sda", "sdb" })
        {
            var path = fs.Device(owner + "/block/" + block, "block");
            fs.Link("class/block/" + block, path);
            fs.Link(path + "/device", owner);
            fs.Write(path + "/wwid", "TEST-" + block);
        }
        fs.Hwmon(0, owner, "drivetemp");
        var scanner = new HwmonCatalogScanner(fs);
        var first = scanner.Scan();
        Assert.Single(first.Snapshot.Ambiguities);
        Assert.Empty(first.PollPlan.Entries);
        fs.ReverseListings = true;
        Assert.True(scanner.Scan(first.Snapshot).IsIdentical);
    }

    [Fact]
    public void MissingNameAndDeviceLinkUseWeakIdentityWithoutEnumerationNumbers()
    {
        using var fs = new FakeSysfs();
        var path = fs.Hwmon(12, "devices/virtual/unidentified", "missing", false);
        File.Delete(fs.PathUnderRoot(path + "/name"));
        var descriptor = Assert.Single(new HwmonCatalogScanner(fs).Scan().Snapshot.Descriptors);
        Assert.Equal("unknown", descriptor.ChipName);
        Assert.Equal(SensorIdentityStrength.Weak, descriptor.IdentityStrength);
        Assert.DoesNotContain("hwmon12", descriptor.StableId);
        Assert.True(descriptor.IsLabelSynthetic);
    }

    [Fact]
    public void SmallFileReadsAreBoundedAndNestedRelativeSymlinksResolve()
    {
        using var fs = new FakeSysfs();
        fs.Write("devices/a/value", new string('x', 4097));
        fs.Link("class/parent", "devices/a");
        fs.Link("class/alias", "class/parent");
        Assert.Equal(fs.PathUnderRoot("devices/a"), fs.ResolvePath(fs.PathUnderRoot("class/alias")));
        Assert.Equal(fs.PathUnderRoot("devices/a/value"), fs.ResolvePath(fs.PathUnderRoot("class/alias/value")));
        Assert.Null(fs.ReadMetadata(fs.PathUnderRoot("class/alias/value")));
    }
}
