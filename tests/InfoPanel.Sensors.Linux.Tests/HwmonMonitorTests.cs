using InfoPanel.Models;
using InfoPanel.Services;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace InfoPanel.Sensors.Linux.Tests;

public class HwmonMonitorTests : IDisposable
{
    public HwmonMonitorTests() => HwmonMonitor.SENSORHASH.Clear();
    public void Dispose()
    {
        SensorDemand.DemandProvider = null;
        SensorDemand.ForcePollAll = false;
        HwmonMonitor.SENSORHASH.Clear();
    }

    [Fact]
    public void FirstPollReadsAllThenOnlyDemandedValuesWithNoMetadataReads()
    {
        using var fs = new FakeSysfs();
        var path = fs.Hwmon(0, fs.Platform("wireview_hwmon"), "wireview", true, "temp1", "curr5");
        fs.Channel(path, "temp1", "Temperature", "42000");
        fs.Channel(path, "curr5", "Pin 5", "2300");
        const string demanded = "hwmon/v1/wireview/platform+wireview_hwmon/curr5";
        SensorDemand.DemandProvider = () => new([demanded], [], []);
        var monitor = new HwmonMonitor(fs, clock: () => 0);
        monitor.Start(60_000);
        try
        {
            Assert.Equal(2, fs.ValueReads.Values.Sum());
            Assert.Equal(42, HwmonMonitor.SENSORHASH["hwmon/v1/wireview/platform+wireview_hwmon/temp1"].ValueNow);
            Assert.Equal(2.3, HwmonMonitor.SENSORHASH[demanded].ValueNow);
            fs.ResetCounters();
            monitor.RunCycle();
            monitor.RunCycle();
            Assert.Equal(2, Assert.Single(fs.ValueReads).Value);
            Assert.EndsWith("curr5_input", Assert.Single(fs.ValueReads).Key);
            Assert.Equal(0, fs.MetadataReads);
            Assert.Equal(0, fs.Listings);
            Assert.Equal(0, fs.Resolutions);
            Assert.All(HwmonMonitor.SENSORHASH.Keys, id => Assert.StartsWith("hwmon/v1/", id));
        }
        finally { monitor.Stop(); }
    }

    [Fact]
    public void PeriodicRescanFindsLateDevicesEvenWhenBothClassDirectoriesWereAbsent()
    {
        using var fs = new FakeSysfs();
        long now = 0;
        var monitor = new HwmonMonitor(fs, clock: () => now);
        var events = 0;
        monitor.CatalogChanged += (_, _) => events++;
        monitor.RunCycle();
        Assert.Empty(monitor.Catalog!.Descriptors);
        fs.Hwmon(8, fs.Platform("coretemp.0"), "coretemp");
        now = 9999;
        monitor.RunCycle();
        Assert.Empty(monitor.Catalog.Descriptors);
        now = 10000;
        monitor.RunCycle();
        var descriptor = Assert.Single(monitor.Catalog.Descriptors);
        Assert.True(HwmonMonitor.SENSORHASH.ContainsKey(descriptor.StableId));
        Assert.Equal(2, events);
        now = 20000;
        monitor.RunCycle();
        Assert.Equal(2, events);
        Assert.Equal(2, monitor.Catalog.Generation);
    }

    [Fact]
    public void RequestRescanRemovesDepartedReadingsAndPreservesSystemKeys()
    {
        using var fs = new FakeSysfs();
        fs.Hwmon(0, fs.Platform("coretemp.0"), "coretemp");
        var monitor = new HwmonMonitor(fs, clock: () => 0);
        monitor.RunCycle();
        var key = Assert.Single(monitor.Catalog!.Descriptors).StableId;
        HwmonMonitor.SENSORHASH["system/test"] = new SensorReading(1, 1, 1, 1, "");
        File.Delete(fs.PathUnderRoot("class/hwmon/hwmon0"));
        monitor.RequestRescan();
        monitor.RunCycle(poll: false);
        Assert.False(HwmonMonitor.SENSORHASH.ContainsKey(key));
        Assert.True(HwmonMonitor.SENSORHASH.ContainsKey("system/test"));
        Assert.Empty(monitor.Catalog.Descriptors);
    }

    [Fact]
    public void SameCountSameIdReplacementRemovesOldReadingBeforePublicationAndResetsMinMax()
    {
        using var fs = new FakeSysfs();
        var owner = fs.Platform("coretemp.0");
        fs.Hwmon(0, owner, "coretemp");
        var monitor = new HwmonMonitor(fs, clock: () => 0);
        monitor.RunCycle();
        var key = Assert.Single(monitor.Catalog!.Descriptors).StableId;
        var replacement = fs.Hwmon(1, owner, "coretemp");
        fs.Channel(replacement, "temp1", value: "60000");
        File.Delete(fs.PathUnderRoot("class/hwmon/hwmon1"));
        fs.Link("class/hwmon/hwmon0", replacement);
        monitor.CatalogChanged += (_, snapshot) =>
        {
            Assert.Same(snapshot, monitor.Resolver.Snapshot);
            Assert.False(HwmonMonitor.SENSORHASH.ContainsKey(key));
        };
        monitor.RequestRescan();
        monitor.RunCycle(poll: false);
        Assert.False(HwmonMonitor.SENSORHASH.ContainsKey(key));
        monitor.RunCycle();
        Assert.Equal(60, HwmonMonitor.SENSORHASH[key].ValueMin);
        Assert.Equal(60, HwmonMonitor.SENSORHASH[key].ValueMax);
    }

    [Fact]
    public void FailedValueRemovesReadingButKeepsDescriptorForRecovery()
    {
        using var fs = new FakeSysfs();
        var path = fs.Hwmon(0, fs.Platform("coretemp.0"), "coretemp");
        var monitor = new HwmonMonitor(fs, clock: () => 0);
        monitor.RunCycle();
        var d = Assert.Single(monitor.Catalog!.Descriptors);
        File.Delete(fs.PathUnderRoot(path + "/temp1_input"));
        monitor.RunCycle();
        Assert.False(HwmonMonitor.SENSORHASH.ContainsKey(d.StableId));
        Assert.Single(monitor.Catalog.Descriptors);
        fs.Channel(path, "temp1", value: "NaN");
        monitor.RunCycle();
        Assert.False(HwmonMonitor.SENSORHASH.ContainsKey(d.StableId));
        fs.Channel(path, "temp1", value: "55000");
        monitor.RunCycle();
        Assert.Equal(55, HwmonMonitor.SENSORHASH[d.StableId].ValueNow);
    }

    [Fact]
    public void IdenticalFullIdentitiesAreAmbiguousAndNeverPublishAReading()
    {
        using var fs = new FakeSysfs();
        var owner = fs.Platform("coretemp.0");
        fs.Hwmon(0, owner, "coretemp");
        var monitor = new HwmonMonitor(fs, clock: () => 0);
        monitor.RunCycle();
        var key = Assert.Single(monitor.Catalog!.Descriptors).StableId;
        Assert.True(HwmonMonitor.SENSORHASH.ContainsKey(key));
        fs.Hwmon(1, owner, "coretemp");
        monitor.RequestRescan();
        fs.ResetCounters();
        monitor.RunCycle();
        Assert.Single(monitor.Catalog.Ambiguities);
        Assert.All(monitor.Catalog.Descriptors, d => Assert.True(d.IsAmbiguous));
        Assert.Empty(monitor.Catalog.StableIdIndex);
        Assert.Empty(fs.ValueReads);
        Assert.False(HwmonMonitor.SENSORHASH.ContainsKey(key));
        Assert.All(HwmonMonitor.GetOrderedList(monitor), d => Assert.True(d.IsAmbiguous));
        File.Delete(fs.PathUnderRoot("class/hwmon/hwmon1"));
        monitor.RequestRescan();
        monitor.RunCycle();
        Assert.True(HwmonMonitor.SENSORHASH.ContainsKey(key));
    }

    [Fact]
    public void FailedListingRetainsCatalogAndRetriesLater()
    {
        using var fs = new FakeSysfs();
        fs.Hwmon(0, fs.Platform("coretemp.0"), "coretemp");
        var monitor = new HwmonMonitor(fs, clock: () => 0);
        monitor.RunCycle();
        var catalog = monitor.Catalog;
        fs.FailedListing = fs.PathUnderRoot("class/hwmon");
        monitor.RequestRescan();
        monitor.RunCycle();
        Assert.Same(catalog, monitor.Catalog);
        fs.FailedListing = null;
        File.Delete(fs.PathUnderRoot("class/hwmon/hwmon0"));
        monitor.RequestRescan();
        monitor.RunCycle();
        Assert.Empty(monitor.Catalog!.Descriptors);
    }

    [Fact]
    public void OrderedListUsesCatalogOnlyAndAppendsInjectedSystemMetadataUnchanged()
    {
        using var fs = new FakeSysfs { ReverseListings = true };
        fs.Nvme(3, 0, "0000:01:00.0", "TEST-B");
        fs.Nvme(1, 1, "0000:02:00.0", "TEST-A");
        fs.Hwmon(9, fs.Platform("coretemp.0"), "coretemp");
        fs.Thermal(5, "acpitz");
        var system = new HwmonSensorInfo { SensorId = "system/test", DeviceName = "System" };
        var monitor = new HwmonMonitor(fs, () => [system], clock: () => 0);
        monitor.RunCycle();
        fs.ResetCounters();
        var list = HwmonMonitor.GetOrderedList(monitor);
        Assert.Equal(monitor.Catalog!.Descriptors.Select(d => d.StableId), list.Take(4).Select(d => d.SensorId));
        Assert.Same(system, list[^1]);
        Assert.Equal("coretemp", list[0].DeviceName);
        Assert.NotEqual(list[1].ChipKey, list[2].ChipKey);
        Assert.All(list.Take(4), d => Assert.NotNull(d.LegacyAlias));
        Assert.Equal(0, fs.MetadataReads + fs.Listings + fs.Resolutions);
        Assert.Empty(fs.ValueReads);
    }

    [Fact]
    public async Task CatalogSubscriberCanStopDuringStartupWithoutDeadlocking()
    {
        using var fs = new FakeSysfs();
        var monitor = new HwmonMonitor(fs);
        monitor.CatalogChanged += (_, _) => monitor.Stop();
        var start = Task.Run(() => monitor.Start(1));
        await start.WaitAsync(TimeSpan.FromSeconds(5));
        monitor.Stop();
    }

    [Fact]
    public void WorkerSerializesSystemPollsAndStopWaitsForCompletion()
    {
        using var fs = new FakeSysfs();
        var active = 0;
        var maxActive = 0;
        using var polled = new ManualResetEventSlim();
        var calls = 0;
        var monitor = new HwmonMonitor(fs, pollSystemSensors: () =>
        {
            var count = Interlocked.Increment(ref active);
            maxActive = Math.Max(count, maxActive);
            Thread.Sleep(10);
            Interlocked.Decrement(ref active);
            if (Interlocked.Increment(ref calls) >= 3) polled.Set();
        });
        monitor.Start(1);
        try { Assert.True(polled.Wait(TimeSpan.FromSeconds(5))); }
        finally { monitor.Stop(); }
        Assert.Equal(1, maxActive);
        Assert.Equal(0, active);
    }
    // xe on an Arc B580: energy1 = card, energy2 = pkg, both cumulative microjoules (#16).
    private static string XeHwmon(FakeSysfs fs)
    {
        var path = fs.Hwmon(3, fs.Pci("0000:03:00.0"), "xe", true, "temp2");
        fs.Channel(path, "temp2", "pkg", "45000");
        fs.Channel(path, "energy1", "card", "28186856811");
        fs.Channel(path, "energy2", "pkg", "19994687011");
        return path;
    }

    private static string IdOf(HwmonMonitor monitor, string channel) =>
        monitor.Catalog!.Descriptors.Single(d => d.Channel == channel).StableId;

    [Fact]
    public void EnergyCountersAreReportedAsPowerInWatts()
    {
        using var fs = new FakeSysfs();
        var path = XeHwmon(fs);
        long now = 0;
        var monitor = new HwmonMonitor(fs, clock: () => now);
        monitor.RunCycle(forcePollAll: true);
        var card = IdOf(monitor, "energy1");
        Assert.Equal("hwmon/v1/xe/pci+0000-03-00.0/energy1", card);
        var descriptor = monitor.Catalog!.StableIdIndex[card];
        Assert.Equal(("card", "Power", "W"), (descriptor.Label, descriptor.Category, descriptor.Unit));
        Assert.False(HwmonMonitor.SENSORHASH.ContainsKey(card)); // a rate needs two samples
        Assert.Equal(45, HwmonMonitor.SENSORHASH[IdOf(monitor, "temp2")].ValueNow);

        now = 1000;
        fs.Channel(path, "energy1", "card", (28186856811 + 37_000_000).ToString());
        monitor.RunCycle(forcePollAll: true);
        Assert.Equal(37, HwmonMonitor.SENSORHASH[card].ValueNow, 6);
        Assert.Equal("W", HwmonMonitor.SENSORHASH[card].Unit);
    }

    [Fact]
    public void BurstyEnergyCounterIsAveragedOverTheWindow()
    {
        using var fs = new FakeSysfs();
        var path = XeHwmon(fs);
        long now = 0;
        var monitor = new HwmonMonitor(fs, clock: () => now);
        monitor.RunCycle(forcePollAll: true);
        var pkg = IdOf(monitor, "energy2");
        // A steady 20 W draw whose counter stalls for a second and then catches up.
        long joules = 0;
        var perSecond = new[] { 20, 20, 0, 40, 20, 20, 20, 0, 40, 20, 20, 20 };
        var readings = new List<double>();
        foreach (var delta in perSecond)
        {
            now += 1000;
            joules += delta;
            fs.Channel(path, "energy2", "pkg", (19994687011 + joules * 1_000_000).ToString());
            monitor.RunCycle(forcePollAll: true);
            readings.Add(HwmonMonitor.SENSORHASH[pkg].ValueNow);
        }
        // Once the window is full no reading strays by more than one stalled second spread over it.
        Assert.All(readings.Skip((int)(HwmonMonitor.EnergyWindowMs / 1000)), w => Assert.InRange(w, 16, 24));
    }

    [Fact]
    public void EnergyCounterResetSkipsOneSampleAndStaleStartIsDropped()
    {
        using var fs = new FakeSysfs();
        var path = XeHwmon(fs);
        long now = 0;
        var monitor = new HwmonMonitor(fs, clock: () => now);
        monitor.RunCycle(forcePollAll: true);
        var card = IdOf(monitor, "energy1");
        now = 1000;
        fs.Channel(path, "energy1", "card", (28186856811 + 30_000_000).ToString());
        monitor.RunCycle(forcePollAll: true);
        Assert.Equal(30, HwmonMonitor.SENSORHASH[card].ValueNow, 6);

        // The driver was reloaded: the counter restarts near zero and must not read as negative watts.
        now = 2000;
        fs.Channel(path, "energy1", "card", "1000000");
        monitor.RunCycle(forcePollAll: true);
        Assert.Equal(30, HwmonMonitor.SENSORHASH[card].ValueNow, 6);
        now = 3000;
        fs.Channel(path, "energy1", "card", "51000000");
        monitor.RunCycle(forcePollAll: true);
        Assert.Equal(50, HwmonMonitor.SENSORHASH[card].ValueNow, 6);

        // A minute without polling: the next reading starts a fresh window instead of averaging the gap.
        now = 63_000;
        fs.Channel(path, "energy1", "card", "251000000");
        monitor.RunCycle(forcePollAll: true);
        Assert.Equal(50, HwmonMonitor.SENSORHASH[card].ValueNow, 6);
        now = 64_000;
        fs.Channel(path, "energy1", "card", "291000000");
        monitor.RunCycle(forcePollAll: true);
        Assert.Equal(40, HwmonMonitor.SENSORHASH[card].ValueNow, 6);
    }

    [Fact]
    public void EnergyChannelsDoNotChangeExistingSensorIds()
    {
        // Fallback identities hash every channel into the ID; energy must stay out of that signature.
        using var before = new FakeSysfs();
        before.Hwmon(0, before.Dir("devices/virtual/misc/foo"), "foo", false, "temp1");
        using var after = new FakeSysfs();
        var path = after.Hwmon(0, after.Dir("devices/virtual/misc/foo"), "foo", false, "temp1");
        after.Channel(path, "energy1", "total");
        var id = Assert.Single(new HwmonCatalogScanner(before).Scan().Snapshot.Descriptors).StableId;
        var ids = new HwmonCatalogScanner(after).Scan().Snapshot.Descriptors.Select(d => d.StableId).ToList();
        Assert.Contains(id, ids);
        Assert.Equal(2, ids.Count);
    }

    [Fact]
    public void ChipsThatReportPowerGetNoSecondPowerFromEnergy()
    {
        using var fs = new FakeSysfs();
        var path = fs.Hwmon(0, fs.Platform("wireview_hwmon"), "wireview", true, "power1");
        fs.Channel(path, "power1", "Total", "18790823");
        fs.Channel(path, "energy1", "Total", "968240567125");
        var descriptor = Assert.Single(new HwmonCatalogScanner(fs).Scan().Snapshot.Descriptors);
        Assert.Equal("power1", descriptor.Channel);
    }
}
