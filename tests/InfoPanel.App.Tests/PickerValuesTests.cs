using InfoPanel.Models;
using InfoPanel.Sensors;
using InfoPanel.Sensors.Linux.Tests;
using InfoPanel.Services;
using InfoPanel.ViewModels;
using Xunit;

namespace InfoPanel.App.Tests;

[Collection("AppState")]
public sealed class PickerValuesTests
{
    [Fact]
    public void UnusedSensorAppearsOnNextPickerPoll_UpdatesUntilClosed_ThenStopsReading()
    {
        using var fs = new FakeSysfs();
        var path = fs.Hwmon(0, fs.Platform("coretemp.0"), "coretemp", true, "temp1", "fan1");
        // An unavailable startup reading must recover when the picker opens, even
        // though no profile has ever referenced this sensor.
        fs.Channel(path, "temp1", value: "unavailable");
        fs.Channel(path, "fan1", value: "700");
        const string unused = "hwmon/v1/coretemp/platform+coretemp.0/temp1";
        const string used = "hwmon/v1/coretemp/platform+coretemp.0/fan1";

        using var completed = new ManualResetEventSlim();
        using var resume = new AutoResetEvent(false);
        var cycles = 0;
        var stopping = false;
        var parked = false;
        var viewer = false;
        var previousProvider = SensorDemand.DemandProvider;
        var previousForced = SensorDemand.ForcePollAll;
        var monitor = new HwmonMonitor(fs, pollSystemSensors: () =>
        {
            // Park at the end of each real worker poll so assertions and input
            // changes cannot race the next reading. Startup remains synchronous.
            if (++cycles > 1 && !Volatile.Read(ref stopping))
            {
                completed.Set();
                resume.WaitOne();
            }
        });

        void NextPoll()
        {
            if (parked)
            {
                completed.Reset();
                resume.Set();
            }
            Assert.True(completed.Wait(TimeSpan.FromSeconds(5)), "The sensor worker did not complete its next poll.");
            parked = true;
        }

        try
        {
            SensorReader.ConfigureResolver(monitor.Resolver);
            SensorDemand.ForcePollAll = false;
            SensorDemand.DemandProvider = () => new([used], [], [], SensorReader.HwmonCatalogGeneration);
            SensorDemand.Invalidate();
            Assert.False(SensorDemand.PollAll);
            monitor.Start(50);
            var catalog = monitor.Catalog;

            var tree = new SensorTreeViewModel();
            tree.Rebuild(HwmonMonitor.GetOrderedList(monitor));
            var leaves = tree.Roots[0].Children.SelectMany(d => d.Children).SelectMany(c => c.Children).ToArray();
            var unusedLeaf = Assert.Single(leaves, l => l.SensorId == unused);
            var usedLeaf = Assert.Single(leaves, l => l.SensorId == used);
            Assert.Equal("", unusedLeaf.DisplayValue);

            fs.Channel(path, "temp1", value: "42000");
            NextPoll();
            fs.ResetCounters();
            NextPoll();
            tree.RefreshValues();
            Assert.Equal("", unusedLeaf.DisplayValue);
            Assert.False(HwmonMonitor.SENSORHASH.ContainsKey(unused));
            Assert.EndsWith("fan1_input", Assert.Single(fs.ValueReads).Key);

            SensorDemand.AddUiViewer();
            viewer = true;
            NextPoll();
            tree.RefreshValues();
            Assert.Equal("42 °C", unusedLeaf.DisplayValue);
            Assert.Equal(42, HwmonMonitor.SENSORHASH[unused].ValueNow);

            fs.Channel(path, "temp1", value: "55000");
            NextPoll();
            tree.RefreshValues();
            Assert.Equal("55 °C", unusedLeaf.DisplayValue);
            Assert.Same(catalog, monitor.Catalog);
            Assert.Equal(0, fs.MetadataReads + fs.Listings + fs.Resolutions);

            SensorDemand.RemoveUiViewer();
            viewer = false;
            Assert.False(SensorDemand.PollAll);
            fs.ResetCounters();
            fs.Channel(path, "temp1", value: "65000");
            fs.Channel(path, "fan1", value: "900");
            NextPoll();
            tree.RefreshValues();
            Assert.Equal("55 °C", unusedLeaf.DisplayValue);
            Assert.Equal("900 RPM", usedLeaf.DisplayValue);
            Assert.EndsWith("fan1_input", Assert.Single(fs.ValueReads).Key);
            Assert.Same(catalog, monitor.Catalog);
            Assert.Equal(0, fs.MetadataReads + fs.Listings + fs.Resolutions);
        }
        finally
        {
            Volatile.Write(ref stopping, true);
            resume.Set();
            monitor.Stop();
            if (viewer) SensorDemand.RemoveUiViewer();
            SensorDemand.DemandProvider = previousProvider;
            SensorDemand.ForcePollAll = previousForced;
            SensorReader.ConfigureResolver(new());
            SensorDemand.Invalidate();
        }
    }
}
