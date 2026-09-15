using InfoPanel.Designer;
using InfoPanel.Drawing;
using InfoPanel.Enums;
using InfoPanel.Models;
using InfoPanel.Persistence;
using InfoPanel.Sensors;
using InfoPanel.Services;
using InfoPanel.Stores;
using InfoPanel.ViewModels;
using SkiaSharp;
using Xunit;

namespace InfoPanel.App.Tests;

[Collection("AppState")]
public sealed class SensorBindingTests : IDisposable
{
    private const string WireViewId = "hwmon/v1/wireview/platform+wireview_hwmon/curr5";
    private static long _generation;
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "infopanel-binding-tests-" + Guid.NewGuid());
    private readonly SensorIdResolver _resolver = new();

    public SensorBindingTests()
    {
        ConfigPersistence.BaseFolderOverride = _tempDir;
        ConfigPersistence.PostLoadHook = null;
        SensorReader.ConfigureResolver(_resolver);
        SensorReader.ConfigureHwmonSource(_ => null);
    }

    public void Dispose()
    {
        Publish();
        GraphDraw.PruneHardwareHistory();
        SensorReader.ConfigureResolver(new());
        SensorReader.ConfigureHwmonSource(_ => null);
        ConfigPersistence.PostLoadHook = null;
        ConfigPersistence.BaseFolderOverride = null;
        HwmonMonitor.SENSORHASH.TryRemove(WireViewId, out _);
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    private void Publish(params SensorDescriptor[] descriptors) =>
        _resolver.Publish(new(Interlocked.Increment(ref _generation), descriptors));

    private static SensorDescriptor WireView => new(SensorId.Parse(WireViewId))
    {
        Label = "Pin 5", RawLabel = "Pin 5", IsLabelSynthetic = false, LegacyAliases = ["hwmon4/curr5"]
    };

    private static SensorTreeItem HardwareLeaf => new()
    {
        Name = "Pin 5", SensorId = WireViewId, ChipName = "wireview", SensorType = SensorType.Hwmon
    };

    [Fact]
    public void CreateFromHardwareLeaf_StoresStableIdAndIdentityHints()
    {
        var item = DesignerSensorBinding.CreateSensorItem(HardwareLeaf, new Profile());
        Assert.Equal(WireViewId, item.LibreSensorId);
        Assert.True(SensorId.TryParse(item.LibreSensorId, out _));
        Assert.Equal(SensorType.Hwmon, item.SensorType);
        Assert.Equal("Pin 5", item.SensorName);
        Assert.Equal("wireview", item.HardwareSensorIdentity?.ChipName);
        Assert.Equal("Pin 5", item.HardwareSensorIdentity?.ChannelLabel);
        Assert.Null(item.HardwareSensorIdentity?.OriginalId);
    }

    [Theory]
    [InlineData("text")]
    [InlineData("graph")]
    [InlineData("bar")]
    [InlineData("donut")]
    [InlineData("gauge")]
    [InlineData("sensor-image")]
    [InlineData("http-image")]
    public void Replace_UndoRedo_RestoresCompleteDetachedBinding(string kind)
    {
        DisplayItem item = kind switch
        {
            "text" => new SensorDisplayItem(), "graph" => new GraphDisplayItem(),
            "bar" => new BarDisplayItem(), "donut" => new DonutDisplayItem(),
            "gauge" => new GaugeDisplayItem(), "sensor-image" => new SensorImageDisplayItem(),
            _ => new HttpImageDisplayItem()
        };
        var previous = SensorBinding.ForHardware("hwmon/v1/coretemp/platform+coretemp.0/temp1", "coretemp", "Package");
        previous.HardwareSensorIdentity!.OriginalId = "hwmon2/temp1";
        SensorBinding.Apply(item, previous);
        var session = new DesignerSession(new Profile());

        var oldIdentity = IdentityOf(item)!;
        DesignerSensorBinding.Replace(session, item, HardwareLeaf);
        AssertBinding(DesignerSensorBinding.FromLeaf(HardwareLeaf), SensorBinding.Capture(item)!);
        // Neither the old nor the newly applied live metadata belongs to the undo record.
        oldIdentity.OriginalId = "changed after capture";
        IdentityOf(item)!.ChipName = "changed after apply";
        session.Undo.Undo();
        AssertBinding(previous, SensorBinding.Capture(item)!);
        session.Undo.Redo();
        AssertBinding(DesignerSensorBinding.FromLeaf(HardwareLeaf), SensorBinding.Capture(item)!);
    }

    [Fact]
    public void ReplaceWithPlugin_ClearsHardwareHints_AndTableRejectsHardware()
    {
        var session = new DesignerSession(new Profile());
        var leaf = new SensorTreeItem { Name = "Plugin value", SensorId = "plugin/value", SensorType = SensorType.Plugin };
        var item = DesignerSensorBinding.CreateSensorItem(HardwareLeaf, session.Profile);
        DesignerSensorBinding.Replace(session, item, leaf);
        AssertBinding(SensorBinding.ForPlugin("plugin/value", "Plugin value"), SensorBinding.Capture(item)!);
        session.Undo.Undo();
        AssertBinding(DesignerSensorBinding.FromLeaf(HardwareLeaf), SensorBinding.Capture(item)!);

        var table = new TableSensorDisplayItem();
        SensorBinding.Apply(table, SensorBinding.ForPlugin("old/table", "Old"));
        session.Undo.Clear();
        DesignerSensorBinding.Replace(session, table, HardwareLeaf);
        Assert.False(session.Undo.CanUndo);
        Assert.Equal("old/table", table.PluginSensorId);
        DesignerSensorBinding.Replace(session, table, leaf);
        Assert.Equal("plugin/value", table.PluginSensorId);
        session.Undo.Undo();
        Assert.Equal("old/table", table.PluginSensorId);
    }

    [Fact]
    public void AmbiguousLeaf_IsMarkedAndCannotBeBound()
    {
        var descriptor = Info(WireViewId, "wireview", "Pin 5");
        descriptor.IsAmbiguous = true;
        var tree = new SensorTreeViewModel();
        tree.Rebuild([descriptor]);
        var leaf = tree.Roots[0].Children[0].Children[0].Children[0];
        Assert.False(leaf.IsSensor);
        Assert.Contains("(ambiguous)", leaf.DisplayName);
        Assert.True(leaf.DisplayOpacity < 1);
        Assert.Throws<ArgumentException>(() => DesignerSensorBinding.FromLeaf(leaf));
        var session = new DesignerSession(new Profile());
        DesignerSensorBinding.Replace(session, new SensorDisplayItem(), leaf);
        Assert.False(session.Undo.CanUndo);
    }

    [Fact]
    public void Tree_GroupsSameDisplayNameByChipKey_AndDecodesAnchors()
    {
        var tree = new SensorTreeViewModel();
        var a = Info("hwmon/v1/nvme/nvme-serial+TEST%20A/temp1", "nvme");
        var b = Info("hwmon/v1/nvme/nvme-serial+TEST%20B/temp1", "nvme");
        tree.Rebuild([a, b, Info("hwmon/v1/nvme/nvme-serial+TEST%20A/temp2", "nvme", "Sensor 2")]);
        var devices = tree.Roots[0].Children;
        Assert.Equal(2, devices.Count);
        Assert.Equal("nvme (TEST A)", devices[0].Name);
        Assert.Equal("nvme (TEST B)", devices[1].Name);
        Assert.Equal(2, devices[0].Children[0].Children.Count);
        Assert.All(devices.SelectMany(d => d.Children[0].Children), leaf => Assert.Equal("nvme", leaf.ChipName));
    }

    [Fact]
    public void Tree_IdenticalSerialsUseSecondaryLocation()
    {
        var tree = new SensorTreeViewModel();
        tree.Rebuild([
            Info("hwmon/v1/nvme/nvme-serial+SAME~pci+0000-01-00.0/temp1", "nvme"),
            Info("hwmon/v1/nvme/nvme-serial+SAME~pci+0000-81-00.0/temp1", "nvme")]);
        var devices = tree.Roots[0].Children;
        Assert.NotEqual(devices[0].Name, devices[1].Name);
        Assert.Contains("0000-01-00.0", devices[0].Name);
        Assert.Contains("0000-81-00.0", devices[1].Name);
    }

    [Fact]
    public void Tree_RebuildPreservesSelectionAndExpansionWhenTitleChanges()
    {
        var tree = new SensorTreeViewModel();
        var a = Info("hwmon/v1/nvme/nvme-serial+A/temp1", "nvme");
        var b = Info("hwmon/v1/nvme/nvme-serial+B/temp1", "nvme");
        tree.Rebuild([a, b]);
        tree.Roots[0].Children[0].IsExpanded = true;
        tree.Roots[0].Children[0].Children[0].IsExpanded = true;
        tree.SelectedItem = tree.Roots[0].Children[0].Children[0].Children[0];
        tree.Rebuild([a]);
        Assert.Equal("nvme", tree.Roots[0].Children[0].Name);
        Assert.True(tree.Roots[0].Children[0].IsExpanded);
        Assert.True(tree.Roots[0].Children[0].Children[0].IsExpanded);
        Assert.Equal(a.SensorId, tree.SelectedItem?.SensorId);
        tree.Rebuild([b]);
        Assert.Null(tree.SelectedItem);
    }

    [Fact]
    public void Tree_SystemEntriesKeepDeviceGrouping_AndRemovedReadingsClear()
    {
        var tree = new SensorTreeViewModel();
        HwmonMonitor.SENSORHASH[WireViewId] = new SensorReading(12, 12, 12, 12, "A");
        tree.Rebuild([Info(WireViewId, "wireview", "Pin 5"),
            new HwmonSensorInfo { SensorId = "system/cpu/load", DeviceName = "CPU" },
            new HwmonSensorInfo { SensorId = "system/cpu/frequency", DeviceName = "CPU" }]);
        Assert.Equal(2, tree.Roots[0].Children.Count);
        Assert.Equal(2, tree.Roots[0].Children[1].Children[0].Children.Count);
        var leaf = tree.Roots[0].Children[0].Children[0].Children[0];
        Assert.Equal("12", leaf.Value);
        HwmonMonitor.SENSORHASH.TryRemove(WireViewId, out _);
        tree.RefreshValues();
        Assert.Equal("", leaf.Value);
        Assert.Equal("", leaf.DisplayValue);
    }

    [Fact]
    public void Graph_LegacyAndStableReferencesShareCanonicalHistory()
    {
        Publish(WireView);
        var legacy = new GraphDisplayItem { SensorType = SensorType.Hwmon, LibreSensorId = "hwmon4/curr5", SensorName = "Pin 5" };
        var stable = new GraphDisplayItem();
        SensorBinding.Apply(stable, DesignerSensorBinding.FromLeaf(HardwareLeaf));
        Assert.Equal(WireViewId, GraphDraw.GetHardwareHistoryKey(legacy));
        var queue = GraphDraw.GetGraphDataQueue(GraphDraw.GetHardwareHistoryKey(legacy)!);
        Assert.Same(queue, GraphDraw.GetGraphDataQueue(GraphDraw.GetHardwareHistoryKey(stable)!));
        SensorBindingMigration.Migrate([legacy]);
        Assert.Same(queue, GraphDraw.GetGraphDataQueue(GraphDraw.GetHardwareHistoryKey(legacy)!));
    }

    [Fact]
    public void Graph_UnresolvedAndAmbiguousBindingsHaveNoKey()
    {
        var chart = new GraphDisplayItem { SensorType = SensorType.Hwmon, LibreSensorId = WireViewId };
        Assert.Null(GraphDraw.GetHardwareHistoryKey(chart)); // not scanned
        Publish();
        Assert.Null(GraphDraw.GetHardwareHistoryKey(chart));
        Publish(WireView with { IsAmbiguous = true });
        Assert.Null(GraphDraw.GetHardwareHistoryKey(chart));
        chart.SensorType = SensorType.Plugin;
        Assert.Null(GraphDraw.GetHardwareHistoryKey(chart));
    }

    [Fact]
    public void Graph_CatalogRemovalDropsHistory_ButMissingReadingAndSystemKeysKeepIt()
    {
        Publish(WireView);
        var queue = GraphDraw.GetGraphDataQueue(WireViewId);
        var system = GraphDraw.GetGraphDataQueue("system/test/history");
        queue.Enqueue(42);
        GraphDraw.PruneHardwareHistory();
        Assert.Same(queue, GraphDraw.GetGraphDataQueue(WireViewId)); // descriptor exists without a reading
        Publish();
        GraphDraw.PruneHardwareHistory();
        Assert.Same(system, GraphDraw.GetGraphDataQueue("system/test/history"));
        Publish(WireView);
        var replugged = GraphDraw.GetGraphDataQueue(WireViewId);
        Assert.NotSame(queue, replugged);
        Assert.Empty(replugged);
    }

    [Fact]
    public void Graph_UnresolvedRendersBackgroundAndFrameWithoutLiveTrace()
    {
        Publish();
        var chart = new GraphDisplayItem
        {
            SensorType = SensorType.Hwmon, LibreSensorId = WireViewId, Width = 80, Height = 40,
            Background = true, BackgroundColor = "#0000FF", Color = "#FF0000", Fill = true,
            FillColor = "#FF0000", Frame = true, FrameColor = "#00FF00"
        };
        using var bitmap = new SKBitmap(80, 40);
        using var graphics = SkiaGraphics.FromBitmap(bitmap, 1);
        GraphDraw.Run(chart, graphics, preview: true);
        Assert.Equal(SKColors.Blue, bitmap.GetPixel(40, 20));
        Assert.Contains(bitmap.Pixels, pixel => pixel.Green > 0);
        Assert.DoesNotContain(bitmap.Pixels, pixel => pixel.Red > 0);
    }

    [Fact]
    public void Bar_UnresolvedDoesNotDrawZeroAsALiveValue()
    {
        Publish();
        var chart = new BarDisplayItem
        {
            SensorType = SensorType.Hwmon, LibreSensorId = WireViewId, Width = 80, Height = 40,
            MinValue = -100, MaxValue = 100, Background = true, BackgroundColor = "#0000FF",
            Color = "#FF0000", Frame = false
        };
        using var bitmap = new SKBitmap(80, 40);
        using var graphics = SkiaGraphics.FromBitmap(bitmap, 1);
        GraphDraw.Run(chart, graphics, preview: true);
        Assert.Equal(SKColors.Blue, bitmap.GetPixel(20, 20));
        Assert.DoesNotContain(bitmap.Pixels, pixel => pixel.Red > 0);
    }

    [Fact]
    public void MigrationSaveRequests_OnlyRewrites_OncePerAffectedProfile()
    {
        var a = new Profile();
        var b = new Profile();
        var migrated1 = new SensorDisplayItem(); migrated1.SetProfile(a);
        var migrated2 = new GraphDisplayItem(); migrated2.SetProfile(a);
        var enriched = new SensorDisplayItem(); enriched.SetProfile(b);
        List<Profile> saves = [];
        AppHost.RequestMigrationSaves(new([migrated1, migrated2], [], [], [enriched]), saves.Add);
        Assert.Equal([a], saves);
        saves.Clear();
        AppHost.RequestMigrationSaves(new([], [], [], [enriched]), saves.Add);
        Assert.Empty(saves);
    }

    [Fact]
    public async Task MigrationSaveRequests_PersistRewritesWithoutUserEdits()
    {
        var profile = new Profile();
        var item = new SensorDisplayItem
        {
            SensorType = SensorType.Hwmon, LibreSensorId = "hwmon4/curr5", SensorName = "Pin 5"
        };
        ConfigPersistence.SaveDisplayItems(profile, [item]);
        DisplayItemStore.Instance.GetOrLoad(profile); // catalog is not ready yet
        Publish(WireView);
        var report = DisplayItemStore.Instance.ReconcileHardwareBindings();
        Assert.Contains(report.Migrated, migrated => migrated.Profile == profile);
        AppHost.RequestMigrationSaves(report, DisplayItemStore.Instance.RequestSave);

        var file = Path.Combine(ConfigPersistence.ProfilesFolder, profile.Guid + ".xml");
        var saved = false;
        for (var attempt = 0; attempt < 60 && !saved; attempt++)
        {
            await Task.Delay(100);
            saved = File.ReadAllText(file).Contains(WireViewId, StringComparison.Ordinal);
        }
        Assert.True(saved, "The normal debounce should persist the migration without an edit.");
        Assert.Contains("hwmon4/curr5", File.ReadAllText(Path.Combine(ConfigPersistence.AutosaveFolder, profile.Guid + ".xml")));
        var restored = Assert.IsType<SensorDisplayItem>(Assert.Single(ConfigPersistence.LoadDisplayItems(profile)));
        Assert.Equal("hwmon4/curr5", restored.HardwareSensorIdentity?.OriginalId);
    }

    [Fact]
    public void Inspector_ReportsNotReadyUnresolvedAmbiguousAndMigrationProvenance()
    {
        var item = DesignerSensorBinding.CreateSensorItem(HardwareLeaf, new Profile());
        Assert.Equal("Sensors not scanned yet", InspectorPanel.GetSensorStatus(item));
        Publish();
        Assert.Equal($"Unresolved sensor: {WireViewId} — use Replace Sensor", InspectorPanel.GetSensorStatus(item));
        Publish(WireView with { IsAmbiguous = true });
        Assert.Contains("Unresolved sensor:", InspectorPanel.GetSensorStatus(item));
        Publish(WireView);
        Assert.Null(InspectorPanel.GetSensorStatus(item));
        item.HardwareSensorIdentity!.OriginalId = "hwmon4/curr5";
        Assert.Equal("migrated from hwmon4/curr5", InspectorPanel.GetSensorStatus(item));
        SensorBinding.Apply(item, SensorBinding.ForPlugin("plugin/value", "Plugin"));
        Assert.Null(InspectorPanel.GetSensorStatus(item));
    }

    private static HwmonSensorInfo Info(string id, string chip, string label = "Composite") => new()
    {
        SensorId = id, ChipKey = SensorId.Parse(id).ChipKey, DeviceName = chip,
        Label = label, Category = "Temperature", Unit = "°C"
    };

    private static HardwareSensorIdentity? IdentityOf(DisplayItem item) => item switch
    {
        SensorDisplayItem sensor => sensor.HardwareSensorIdentity,
        ChartDisplayItem chart => chart.HardwareSensorIdentity,
        GaugeDisplayItem gauge => gauge.HardwareSensorIdentity,
        SensorImageDisplayItem image => image.HardwareSensorIdentity,
        HttpImageDisplayItem http => http.HardwareSensorIdentity,
        _ => null
    };

    private static void AssertBinding(SensorBinding expected, SensorBinding actual)
    {
        Assert.Equal(expected.SensorType, actual.SensorType);
        Assert.Equal(expected.LibreSensorId, actual.LibreSensorId);
        Assert.Equal(expected.PluginSensorId, actual.PluginSensorId);
        Assert.Equal(expected.SensorName, actual.SensorName);
        Assert.Equal(expected.HardwareSensorIdentity?.ChipName, actual.HardwareSensorIdentity?.ChipName);
        Assert.Equal(expected.HardwareSensorIdentity?.ChannelLabel, actual.HardwareSensorIdentity?.ChannelLabel);
        Assert.Equal(expected.HardwareSensorIdentity?.OriginalId, actual.HardwareSensorIdentity?.OriginalId);
    }
}
