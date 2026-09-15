using System.Collections.Immutable;
using InfoPanel.Enums;
using InfoPanel.Models;
using Xunit;

namespace InfoPanel.Core.Tests;

[Collection("ConfigPersistence")]
public sealed class SensorDemandTests(SensorStateFixture fixture) : SensorStateTest(fixture)
{
    private static SensorDemand.Snapshot Collect(params DisplayItem[] items) =>
        SensorDemand.Collect([new Profile()], _ => [.. items]);

    [Fact]
    public void LegacyItemsInNestedGroupsDemandCanonicalKeysAndSystemPassesThrough()
    {
        SensorStateFixture.PublishWireView();
        var nested = new GroupDisplayItem();
        nested.DisplayItems.Add(new SensorDisplayItem { LibreSensorId = "hwmon4/curr5", SensorName = "Pin 5" });
        var root = new GroupDisplayItem();
        root.DisplayItems.Add(nested);
        var snapshot = Collect(root, new SensorDisplayItem { LibreSensorId = "system/cpu/total" });
        Assert.Equal(1, snapshot.Generation);
        Assert.Equal(new[] { SensorStateFixture.WireViewId, "system/cpu/total" }, snapshot.HwmonIds.Order());
        Assert.DoesNotContain("hwmon4/curr5", snapshot.HwmonIds);
    }

    [Fact]
    public void UnresolvedNotReadyAndForeignBindingsDemandNothing()
    {
        var item = new SensorDisplayItem { LibreSensorId = "hwmon4/curr5", SensorName = "Missing" };
        Assert.Empty(Collect(item).HwmonIds);
        SensorStateFixture.PublishWireView();
        Assert.Empty(Collect(item).HwmonIds);
        item.SensorName = "Pin 5";
        item.SensorType = SensorType.Libre;
        Assert.Empty(Collect(item).HwmonIds);
        item.SensorType = SensorType.HwInfo;
        Assert.Empty(Collect(item).HwmonIds);
    }

    [Fact]
    public void GenerationMismatchPollsAllUntilImmediateRebuild()
    {
        var resolver = SensorStateFixture.PublishWireView();
        var calls = 0;
        var item = new SensorDisplayItem { LibreSensorId = "hwmon4/curr5", SensorName = "Pin 5" };
        SensorDemand.DemandProvider = () => { calls++; return Collect(item); };
        SensorDemand.RebuildIfDue();
        Assert.True(SensorDemand.IsHwmonUsed(SensorStateFixture.WireViewId));
        Assert.False(SensorDemand.IsHwmonUsed("other"));
        resolver.Publish(new(2, []));
        Assert.True(SensorDemand.IsHwmonUsed("other"));
        SensorDemand.RebuildIfDue();
        Assert.Equal(2, calls);
        Assert.False(SensorDemand.IsHwmonUsed(SensorStateFixture.WireViewId));
        Assert.False(SensorDemand.IsHwmonUsed("other"));
    }

    [Fact]
    public void InvalidateBypassesThrottleAndDoesNotLoseInvalidationDuringCollection()
    {
        SensorStateFixture.PublishWireView();
        var calls = 0;
        SensorDemand.DemandProvider = () =>
        {
            calls++;
            if (calls == 2) SensorDemand.Invalidate();
            return Collect();
        };
        SensorDemand.RebuildIfDue();
        SensorDemand.RebuildIfDue();
        Assert.Equal(1, calls);
        SensorDemand.Invalidate();
        SensorDemand.RebuildIfDue();
        Assert.Equal(2, calls);
        Assert.True(SensorDemand.IsHwmonUsed("new"));
        SensorDemand.RebuildIfDue();
        Assert.Equal(3, calls);
        Assert.False(SensorDemand.IsHwmonUsed("new"));
    }

    [Fact]
    public void CatalogPublicationDuringCollectionCannotPublishCurrentDemand()
    {
        var resolver = SensorStateFixture.PublishWireView();
        SensorDemand.DemandProvider = () => SensorDemand.Collect([new Profile()], _ =>
        {
            resolver.Publish(new(2, []));
            return ImmutableList<DisplayItem>.Empty;
        });
        SensorDemand.RebuildIfDue();
        Assert.True(SensorDemand.IsHwmonUsed("new"));
        SensorDemand.DemandProvider = () => Collect();
        SensorDemand.RebuildIfDue();
        Assert.False(SensorDemand.IsHwmonUsed("new"));
    }

    [Fact]
    public void CollectionFailurePollsAllHardwareAndKeepsPluginDemandUntilRecovery()
    {
        SensorStateFixture.PublishWireView();
        SensorDemand.DemandProvider = () => Collect(new SensorDisplayItem { SensorType = SensorType.Plugin, PluginSensorId = "plugin/sensor" });
        SensorDemand.RebuildIfDue();
        SensorDemand.DemandProvider = () => throw new InvalidOperationException("collection failed");
        SensorDemand.Invalidate();
        SensorDemand.RebuildIfDue();
        Assert.True(SensorDemand.IsHwmonUsed("new"));
        Assert.True(SensorDemand.IsPluginSensorUsed("plugin/sensor"));
        Assert.False(SensorDemand.IsPluginSensorUsed("other"));
        SensorDemand.DemandProvider = () => Collect();
        SensorDemand.RebuildIfDue();
        Assert.False(SensorDemand.IsHwmonUsed("new"));
    }

    [Fact]
    public void PluginAndHotkeyIdsAreUnchangedAndPublishedSetsAreDetached()
    {
        SensorStateFixture.PublishWireView();
        var snapshot = Collect(
            new SensorDisplayItem { SensorType = SensorType.Plugin, PluginSensorId = "plugin/value", LibreSensorId = "ignored" },
            new TableSensorDisplayItem { PluginSensorId = "plugin/table" },
            new ImageDisplayItem { FilePath = "plugin-image://art/image" });
        snapshot.PluginIds.Add("hotkey");
        SensorDemand.DemandProvider = () => snapshot;
        SensorDemand.RebuildIfDue();
        snapshot.PluginSensorIds.Clear();
        snapshot.PluginIds.Clear();
        snapshot.HwmonIds.Add("late mutation");
        Assert.Equal(new[] { "plugin/table", "plugin/value" }, SensorDemand.UsedPluginSensorIds.Order());
        Assert.Equal(new[] { "art", "hotkey" }, SensorDemand.UsedPluginIds.Order());
        Assert.True(SensorDemand.IsPluginIdUsed("hotkey"));
        Assert.False(SensorDemand.IsHwmonUsed("late mutation"));
    }
}
