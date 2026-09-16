using InfoPanel.Models;
using InfoPanel.Sensors;
using InfoPanel.Services;
using Xunit;

namespace InfoPanel.Sensors.Linux.Tests;

public sealed class SystemSensorTests : IDisposable
{
    public SystemSensorTests()
    {
        HwmonMonitor.SENSORHASH.Clear();
        SensorDemand.Invalidate();
    }
    public void Dispose()
    {
        SensorDemand.DemandProvider = null;
        SensorDemand.Invalidate();
        SensorDemand.ForcePollAll = false;
        HwmonMonitor.SENSORHASH.Clear();
    }

    private static string Disk(FakeSysfs fs, string name, string? wwid = null, string? serial = null)
    {
        var device = fs.Device(fs.Pci("0000:03:00.0") + "/host7/target7:0:0/7:0:0:0", "scsi");
        var disk = fs.Device(device + "/block/" + name, "block");
        fs.Link("class/block/" + name, disk);
        fs.Link(disk + "/device", device);
        if (wwid != null) fs.Write(disk + "/wwid", wwid);
        if (serial != null) fs.Write(device + "/serial", serial);
        fs.Write(device + "/model", "Test Disk");
        fs.Write(disk + "/stat", "100 0 1024 200 50 0 2048 100 2 0 0");
        return disk;
    }

    private static void NvmeDisk(FakeSysfs fs, int number, string pci, string serial, string model)
    {
        var controller = fs.Device(fs.Pci(pci) + $"/nvme/nvme{number}", "nvme");
        fs.Write(controller + "/serial", serial);
        fs.Write(controller + "/model", model);
        fs.Link($"class/nvme/nvme{number}", controller);
        var disk = fs.Device(controller + $"/nvme{number}n1", "block");
        fs.Write(disk + "/stat", "100 0 1024 200 50 0 2048 100 2 0 0");
        fs.Link($"class/block/nvme{number}n1", disk);
        fs.Link(disk + "/device", controller);
    }

    [Fact]
    public void NvmeControllerAndNamespaceNumberSwapPreservesAllStableIdsAndChangesAliases()
    {
        using var a = new FakeSysfs();
        using var b = new FakeSysfs { ReverseListings = true };
        NvmeDisk(a, 0, "0000:01:00.0", "233529800995", "WD_BLACK SN850X");
        NvmeDisk(a, 1, "0000:81:00.0", "253652BEC1AD", "CT2000P310SSD8");
        NvmeDisk(b, 1, "0000:01:00.0", "233529800995", "WD_BLACK SN850X");
        NvmeDisk(b, 0, "0000:81:00.0", "253652BEC1AD", "CT2000P310SSD8");
        var first = new DiskCatalogScanner(a).Scan().SelectMany(d => d.Descriptors).OrderBy(d => d.StableId).ToArray();
        var second = new DiskCatalogScanner(b).Scan().SelectMany(d => d.Descriptors).OrderBy(d => d.StableId).ToArray();
        Assert.Equal(14, first.Length);
        Assert.Equal(first.Select(d => d.StableId), second.Select(d => d.StableId));
        Assert.All(first.Zip(second), pair => Assert.NotEqual(pair.First.LegacyAlias, pair.Second.LegacyAlias));
        var wd = Assert.Single(first, d => d.StableId == "system/disk/nvme-serial+233529800995/read_speed");
        Assert.Equal("WD_BLACK SN850X (…0995, nvme0n1) Read", wd.Label);
        Assert.Equal("System Disk I/O", wd.ChipDisplayName);
        Assert.Equal("", wd.ChipKey);
        Assert.Equal(SensorIdentityStrength.Strong, wd.IdentityStrength);
    }

    [Theory]
    [InlineData("wwid", "block-wwid+naa.12345")]
    [InlineData("device/wwid", "block-wwid+naa.12345")]
    [InlineData("device/serial", "block-serial+naa.12345")]
    public void ScsiProbeOrderDoesNotChangeIdentity(string attribute, string anchor)
    {
        using var a = new FakeSysfs();
        using var b = new FakeSysfs();
        var diskA = Disk(a, "sda");
        var diskB = Disk(b, "sdb");
        a.Write(diskA + "/" + attribute, "naa.12345");
        b.Write(diskB + "/" + attribute, "naa.12345");
        Assert.Equal(anchor, Assert.Single(new DiskCatalogScanner(a).Scan()).Identity);
        Assert.Equal(anchor, Assert.Single(new DiskCatalogScanner(b).Scan()).Identity);
    }

    [Fact]
    public void MissingIdentityUsesWeakNameAndDuplicateFullIdentitiesAreUnbindable()
    {
        using var fs = new FakeSysfs();
        Disk(fs, "sda");
        var sensors = new LinuxSystemSensors(fs, fs.PathUnderRoot("diskstats"));
        var weak = sensors.ScanDescriptors().ToArray();
        Assert.All(weak, d => Assert.Equal("name+sda", d.Id.Anchor));
        Assert.All(weak, d => Assert.Equal(SensorIdentityStrength.Weak, d.IdentityStrength));
        Disk(fs, "sda", "duplicate");
        Disk(fs, "sdb", "duplicate");
        var catalog = new HwmonCatalogScanner(fs, sensors.ScanDescriptors).Scan().Snapshot;
        Assert.Equal(7, catalog.Ambiguities.Length);
        Assert.Empty(catalog.StableIdIndex);
        SensorDemand.ForcePollAll = true;
        sensors.PollDisks(1000);
        Assert.Empty(HwmonMonitor.SENSORHASH);
    }

    [Fact]
    public void SharedCatalogPublishesStableDemandedReadingsRescansAliasesAndRemovesDepartedDisk()
    {
        using var fs = new FakeSysfs();
        var disk = Disk(fs, "sda", "naa.12345");
        fs.Write("diskstats", "8 0 sda 100 0 1024 200 50 0 2048 100 2 0 0");
        var provider = new LinuxSystemSensors(fs, fs.PathUnderRoot("diskstats"));
        long now = 0;
        var monitor = new HwmonMonitor(fs, provider.GetSensorInfoList, () => provider.PollDisks(1000),
            clock: () => now, systemDescriptors: provider.ScanDescriptors);
        monitor.RunCycle(forcePollAll: true);
        const string read = "system/disk/block-wwid+naa.12345/read_speed";
        const string queue = "system/block/block-wwid+naa.12345/queue_depth";
        Assert.Equal(7, monitor.Catalog!.Descriptors.Length);
        Assert.Equal(7, HwmonMonitor.GetOrderedList(monitor).Count);
        Assert.Equal(SensorMatchReason.LegacyAlias, monitor.Resolver.Resolve(new("system/disk/sda/read_speed")).MatchReason);
        Assert.Equal(2, HwmonMonitor.SENSORHASH[queue].ValueNow);
        SensorDemand.DemandProvider = () => new([read], [], []);
        SensorDemand.Invalidate();
        fs.Write("diskstats", "8 0 sda 110 0 3072 220 55 0 4096 110 1 0 0");
        fs.ResetCounters();
        monitor.RunCycle();
        Assert.Equal(1, HwmonMonitor.SENSORHASH[read].ValueNow);
        Assert.False(HwmonMonitor.SENSORHASH.ContainsKey("system/disk/block-wwid+naa.12345/write_speed"));
        Assert.Empty(fs.ValueReads); // Undemanded block stat file wasn't read.
        Assert.Equal(0, fs.MetadataReads);
        Assert.Equal(0, fs.Listings);
        Assert.Equal(0, fs.Resolutions);
        Assert.All(HwmonMonitor.SENSORHASH.Keys, key => Assert.Contains("block-wwid+", key));

        File.Delete(fs.PathUnderRoot("class/block/sda"));
        fs.Write("diskstats", "");
        now = 10000;
        monitor.RunCycle();
        Assert.Empty(monitor.Catalog.Descriptors);
        Assert.Empty(HwmonMonitor.SENSORHASH);
        Assert.Equal(SensorResolutionStatus.Unresolved, monitor.Resolver.Resolve(new(read)).Status);
        Assert.Equal(SensorResolutionStatus.Unresolved, monitor.Resolver.Resolve(new("system/disk/sda/read_speed")).Status);

        fs.Link("class/block/sdb", disk);
        fs.Write("diskstats", "8 16 sdb 100 0 1024 200 50 0 2048 100 2 0 0");
        now = 20000;
        monitor.RunCycle(forcePollAll: true);
        Assert.Equal(read, monitor.Resolver.Resolve(new("system/disk/sdb/read_speed")).CanonicalId);
        Assert.False(HwmonMonitor.SENSORHASH.ContainsKey(read)); // Fresh rate baseline after reappearance.
    }

    [Fact]
    public void ReadFailuresRemoveDiskValuesBeforeRescan()
    {
        using var fs = new FakeSysfs();
        var disk = Disk(fs, "sda", "naa.12345");
        fs.Write("diskstats", "8 0 sda 100 0 1024 200 50 0 2048 100 2 0 0");
        var provider = new LinuxSystemSensors(fs, fs.PathUnderRoot("diskstats"));
        provider.ScanDescriptors();
        SensorDemand.ForcePollAll = true;
        provider.PollDisks(1000);
        provider.PollDisks(1000);
        Assert.NotEmpty(HwmonMonitor.SENSORHASH);
        File.Delete(fs.PathUnderRoot(disk + "/stat"));
        File.Delete(fs.PathUnderRoot("diskstats"));
        provider.PollDisks(1000);
        Assert.Empty(HwmonMonitor.SENSORHASH);
    }

    [Theory]
    [InlineData("GPU-abc+def/ghi", "00000000:AB:1F.2", "gpu-uuid+GPU-abc%2Bdef%2Fghi~pci+0000-ab-1f.2")]
    [InlineData(null, "0001:02:03.4", "pci+0001-02-03.4")]
    [InlineData("GPU-abc", null, "gpu-uuid+GPU-abc")]
    public void NvidiaKeyIsIndependentOfIndexAndCardCount(string? uuid, string? pci, string identity)
    {
        var id = SystemSensorIdentity.Nvidia(uuid, pci, "temperature")!;
        Assert.Equal($"system/gpu/{identity}/temperature", id.Value);
        Assert.Equal(uuid != null, id.HasStrongIdentity);
        Assert.Equal("", id.ChipKey);
        Assert.Equal(id, SensorId.Parse(id.Value));
    }

    [Fact]
    public void GpuMissingIdentityIsExcludedAndAmdDecodesFullPciAddress()
    {
        Assert.Null(SystemSensorIdentity.Nvidia(null, null, "temperature"));
        Assert.Null(SystemSensorIdentity.Nvidia(" ", "invalid", "temperature"));
        Assert.Null(SystemSensorIdentity.Amd(null, "temperature"));
        const ulong bdf = (0x1234UL << 32) | (0xabUL << 8) | (0x1fUL << 3) | 5;
        Assert.Equal("1234-ab-1f.5", SystemSensorIdentity.RocmPciAddress(bdf));
        Assert.Equal("system/amdgpu/pci+1234-ab-1f.5/temperature",
            SystemSensorIdentity.Amd(SystemSensorIdentity.RocmPciAddress(bdf), "temperature")!.Value);
    }

    [Fact]
    public void DrmJoinUsesFullPciAddressAndIntelSelectionUsesLowestPci()
    {
        using var fs = new FakeSysfs { ReverseListings = true };
        void Card(int number, string pci, string vendor)
        {
            var device = fs.Pci(pci);
            fs.Write(device + "/vendor", vendor);
            fs.Write(device + "/pp_dpm_sclk", $"0: {number * 100}Mhz *");
            fs.Dir($"class/drm/card{number}");
            fs.Link($"class/drm/card{number}/device", device);
        }
        Card(0, "0001:03:00.0", "0x1002");
        Card(8, "0000:03:00.0", "0x1002");
        Assert.Equal(fs.PathUnderRoot("class/drm/card8"), DrmDevices.Find(fs, "0x1002", "0000-03-00.0"));
        Assert.Equal(fs.PathUnderRoot("class/drm/card0"), DrmDevices.Find(fs, "0x1002", "0001-03-00.0"));
        Assert.Null(DrmDevices.Find(fs, "0x1002", "0000-09-00.0"));
        Card(1, "0000:81:00.0", "0x8086");
        Card(9, "0000:01:00.0", "0x8086");
        fs.Dir("class/drm/card9-DP-1");
        fs.Link("class/drm/card9-DP-1/device", "devices/pci0000:00/0000:01:00.0");
        var intel = DrmDevices.Scan(fs, "0x8086").ToArray();
        Assert.Equal(2, intel.Length);
        Assert.Equal(fs.PathUnderRoot("class/drm/card9"), intel[0].CardPath);
    }
}
