using InfoPanel.Enums;
using InfoPanel.Models;
using InfoPanel.Persistence;
using InfoPanel.Sensors;
using InfoPanel.Stores;
using Xunit;

namespace InfoPanel.Core.Tests;

[Collection("ConfigPersistence")]
public sealed class SensorBindingMigrationTests(SensorStateFixture fixture) : SensorStateTest(fixture)
{
    [Theory]
    [InlineData("system/disk/nvme0n1/read_speed", "system/disk/nvme-serial+233529800995/read_speed")]
    [InlineData("system/block/sda/read_iops", "system/block/block-wwid+naa.123/read_iops")]
    [InlineData("system/gpu/temperature", "system/gpu/gpu-uuid+GPU-123~pci+0000-01-00.0/temperature")]
    [InlineData("system/gpu/1/temperature", "system/gpu/pci+0000-02-00.0/temperature")]
    [InlineData("system/amdgpu/temperature", "system/amdgpu/pci+0000-03-00.0/temperature")]
    [InlineData("system/amdgpu/1/clock_graphics", "system/amdgpu/pci+0000-04-00.0/clock_graphics")]
    public void SystemLegacyMigrationPersistsCanonicalIdAndOriginalAlias(string alias, string stable)
    {
        var resolver = new SensorIdResolver();
        resolver.Publish(new(1, [new SensorDescriptor(SensorId.Parse(stable)) { LegacyAliases = [alias], Label = "Product label" }]));
        SensorReader.ConfigureResolver(resolver);
        var item = new SensorDisplayItem { LibreSensorId = alias, SensorName = "Old item label" };
        Assert.Single(SensorBindingMigration.Migrate([item]).Migrated);
        Assert.Equal(stable, item.LibreSensorId);
        Assert.Equal(alias, item.HardwareSensorIdentity!.OriginalId);
        var xml = SensorBindingTests.Serialize(item);
        Assert.Contains($"<LibreSensorId>{stable}</LibreSensorId>", xml);
        Assert.Contains($"<OriginalId>{alias}</OriginalId>", xml);
        Assert.False(SensorBindingMigration.Migrate([item]).HasChanges);
        Assert.Equal(xml, SensorBindingTests.Serialize(item));
    }

    [Fact]
    public void SystemCpuRemainsByteIdenticalWhileMissingDiskAliasRemainsUnresolved()
    {
        var resolver = SensorStateFixture.PublishWireView();
        var cpu = new SensorDisplayItem { LibreSensorId = "system/cpu/total" };
        var disk = new SensorDisplayItem { LibreSensorId = "system/disk/nvme0n1/read_speed" };
        var before = SensorBindingTests.Serialize(cpu);
        var diskBefore = SensorBindingTests.Serialize(disk);
        var report = SensorBindingMigration.Migrate([cpu, disk]);
        Assert.Single(report.Unresolved);
        Assert.False(report.HasChanges);
        Assert.Equal(before, SensorBindingTests.Serialize(cpu));
        Assert.Equal(diskBefore, SensorBindingTests.Serialize(disk));
        Assert.Null(cpu.HardwareSensorIdentity);
    }

    [Fact]
    public void WarningGuardStaysBoundedAcrossImportedItemsAndBindingEdits()
    {
        SensorStateFixture.PublishWireView();
        var item = new SensorDisplayItem();
        for (var i = 0; i < SensorBindingMigration.WarningLimit + 20; i++)
        {
            item.LibreSensorId = $"hwmon{i}/temp1";
            Assert.Single(SensorBindingMigration.Migrate([item]).Unresolved);
        }
        Assert.Equal(SensorBindingMigration.WarningLimit, SensorBindingMigration.WarningCount);
        SensorBindingMigration.Migrate([item]);
        Assert.Equal(SensorBindingMigration.WarningLimit, SensorBindingMigration.WarningCount);
    }

    [Theory, MemberData(nameof(SensorBindingTests.HardwareFamilies), MemberType = typeof(SensorBindingTests))]
    public void LegacyBindingMigratesOnceAndKeepsOriginalId(string kind)
    {
        SensorStateFixture.PublishWireView();
        var item = SensorBindingTests.Item(kind);
        var sensor = (ISensorItem)item;
        sensor.LibreSensorId = "hwmon4/curr5";
        sensor.SensorName = "Pin 5";
        item.Name = "User's name";
        var report = SensorBindingMigration.Migrate([item]);
        Assert.Same(item, Assert.Single(report.Migrated));
        Assert.True(report.HasChanges);
        Assert.Empty(report.Unresolved);
        Assert.Empty(report.Deferred);
        Assert.Equal(SensorStateFixture.WireViewId, sensor.LibreSensorId);
        Assert.Equal("hwmon4/curr5", sensor.HardwareSensorIdentity!.OriginalId);
        Assert.Equal("wireview", sensor.HardwareSensorIdentity.ChipName);
        Assert.Equal("Pin 5", sensor.HardwareSensorIdentity.ChannelLabel);
        Assert.Equal("User's name", item.Name);
        Assert.Equal("Pin 5", sensor.SensorName);

        var xml = SensorBindingTests.Serialize(item);
        Assert.False(SensorBindingMigration.Migrate([item]).HasChanges);
        Assert.Equal(xml, SensorBindingTests.Serialize(item));
    }

    [Fact]
    public void ContradictoryLabelAndAmbiguousIdentityRemainUntouched()
    {
        var resolver = SensorStateFixture.PublishWireView();
        var item = new SensorDisplayItem { LibreSensorId = "hwmon4/curr5", SensorName = "Wrong label" };
        var before = SensorBindingTests.Serialize(item);
        Assert.Same(item, Assert.Single(SensorBindingMigration.Migrate([item]).Unresolved));
        Assert.Equal(before, SensorBindingTests.Serialize(item));
        Assert.Null(item.HardwareSensorIdentity);

        item.LibreSensorId = SensorStateFixture.WireViewId;
        item.HardwareSensorIdentity = new() { ChipName = "saved", ChannelLabel = "saved", OriginalId = "original" };
        var metadata = item.HardwareSensorIdentity;
        resolver.Publish(new(2, [SensorStateFixture.WireView, SensorStateFixture.WireView with { RealPath = "/other" }]));
        before = SensorBindingTests.Serialize(item);
        Assert.Same(item, Assert.Single(SensorBindingMigration.Migrate([item]).Unresolved));
        Assert.Equal(before, SensorBindingTests.Serialize(item));
        Assert.Same(metadata, item.HardwareSensorIdentity);
    }

    [Fact]
    public void NestedGroupsMigrateAndCloneMetadataRecursively()
    {
        SensorStateFixture.PublishWireView();
        var item = new SensorDisplayItem { LibreSensorId = "hwmon4/curr5", SensorName = "Pin 5" };
        var nested = new GroupDisplayItem();
        nested.DisplayItems.Add(item);
        var root = new GroupDisplayItem();
        root.DisplayItems.Add(nested);
        Assert.Same(item, Assert.Single(SensorBindingMigration.Migrate([root]).Migrated));
        var clone = (GroupDisplayItem)root.Clone();
        var copied = (SensorDisplayItem)((GroupDisplayItem)clone.DisplayItems[0]).DisplayItems[0];
        copied.HardwareSensorIdentity!.OriginalId = "changed";
        Assert.Equal("hwmon4/curr5", item.HardwareSensorIdentity!.OriginalId);
    }

    [Fact]
    public void FuzzyMigrationPreservesEarlierProvenanceAndExactOnlyFillsMissingHints()
    {
        var resolver = SensorStateFixture.PublishWireView();
        resolver.Publish(new(2, [SensorStateFixture.WireView with { LegacyAliases = ["hwmon9/curr5"] }]));
        var item = new SensorDisplayItem
        {
            LibreSensorId = "hwmon4/curr5", SensorName = "Pin 5",
            HardwareSensorIdentity = new() { OriginalId = "hwmon1/curr5" }
        };
        Assert.Single(SensorBindingMigration.Migrate([item]).Migrated);
        Assert.Equal("hwmon1/curr5", item.HardwareSensorIdentity!.OriginalId);
        item.HardwareSensorIdentity = new() { ChannelLabel = "preserved label", OriginalId = "hwmon1/curr5" };
        var report = SensorBindingMigration.Migrate([item]);
        Assert.Empty(report.Migrated);
        Assert.Single(report.Enriched);
        Assert.True(report.HasChanges);
        Assert.Equal("preserved label", item.HardwareSensorIdentity.ChannelLabel);
        Assert.Equal("wireview", item.HardwareSensorIdentity.ChipName);
        Assert.Equal("hwmon1/curr5", item.HardwareSensorIdentity.OriginalId);
    }

    [Fact]
    public void NotReadyDefersWithoutChangingItems()
    {
        var item = new SensorDisplayItem { LibreSensorId = "hwmon4/curr5", SensorName = "Pin 5" };
        var before = SensorBindingTests.Serialize(item);
        var report = SensorBindingMigration.Migrate([item]);
        Assert.Single(report.Deferred);
        Assert.Empty(report.Unresolved);
        Assert.False(report.HasChanges);
        Assert.Equal(before, SensorBindingTests.Serialize(item));
    }

    [Theory]
    [InlineData(SensorType.Plugin, "hwmon4/curr5")]
    [InlineData(SensorType.Libre, "hwmon4/curr5")]
    [InlineData(SensorType.HwInfo, "hwmon4/curr5")]
    [InlineData(SensorType.Hwmon, "system/cpu/total")]
    [InlineData(SensorType.Hwmon, "")]
    public void OtherBindingsAreUntouched(SensorType type, string id)
    {
        SensorStateFixture.PublishWireView();
        var item = new SensorDisplayItem { SensorType = type, LibreSensorId = id, SensorName = "Pin 5" };
        var before = SensorBindingTests.Serialize(item);
        var report = SensorBindingMigration.Migrate([item]);
        Assert.False(report.HasChanges);
        Assert.Empty(report.Unresolved);
        Assert.Empty(report.Deferred);
        Assert.Equal(before, SensorBindingTests.Serialize(item));
    }

    [Fact]
    public void UnknownFutureVersionSurvivesUnresolved()
    {
        SensorStateFixture.PublishWireView();
        var item = new SensorDisplayItem
        {
            LibreSensorId = "hwmon/v9/wireview/platform+wireview_hwmon/curr5", SensorName = "Pin 5",
            HardwareSensorIdentity = new() { OriginalId = "hwmon4/curr5" }
        };
        var before = SensorBindingTests.Serialize(item);
        Assert.Single(SensorBindingMigration.Migrate([item]).Unresolved);
        Assert.Equal(before, SensorBindingTests.Serialize(item));
    }

    [Fact]
    public void LoadBackupAndStoreReconciliationNeverWriteOrScheduleMigrationAutosave()
    {
        var folder = Path.Combine(Path.GetTempPath(), "infopanel-migration-" + Guid.NewGuid());
        ConfigPersistence.BaseFolderOverride = folder;
        var store = new DisplayItemStore();
        try
        {
            var profile = new Profile();
            var group = new GroupDisplayItem();
            group.DisplayItems.Add(new SensorDisplayItem { LibreSensorId = "hwmon4/curr5", SensorName = "Pin 5" });
            ConfigPersistence.SaveDisplayItems(profile, [group]);
            ConfigPersistence.BackupDisplayItems(profile);
            var files = Directory.GetFiles(folder, "*", SearchOption.AllDirectories).ToDictionary(p => p, File.ReadAllBytes);
            SensorStateFixture.EnableMigration(); // Load before discovery: deferred.
            var loaded = store.GetOrLoad(profile);
            Assert.Equal("hwmon4/curr5", ((SensorDisplayItem)((GroupDisplayItem)loaded[0]).DisplayItems[0]).LibreSensorId);
            Assert.Equal(0, store.ScheduledSaveCount);

            SensorStateFixture.PublishWireView();
            Assert.Single(store.ReconcileHardwareBindings().Migrated);
            Assert.Equal(0, store.ScheduledSaveCount);
            var backup = ConfigPersistence.LoadDisplayItemsBackup(profile);
            Assert.Equal(SensorStateFixture.WireViewId, ((SensorDisplayItem)((GroupDisplayItem)backup[0]).DisplayItems[0]).LibreSensorId);
            Assert.Equal(files.Keys.Order(), Directory.GetFiles(folder, "*", SearchOption.AllDirectories).Order());
            foreach (var file in files) Assert.Equal(file.Value, File.ReadAllBytes(file.Key));

            store.Save(profile);
            var path = Path.Combine(ConfigPersistence.ProfilesFolder, profile.Guid + ".xml");
            Assert.Contains(SensorStateFixture.WireViewId, File.ReadAllText(path));
            Assert.Equal(files[Path.Combine(ConfigPersistence.AutosaveFolder, profile.Guid + ".xml")],
                File.ReadAllBytes(Path.Combine(ConfigPersistence.AutosaveFolder, profile.Guid + ".xml")));
            // Loaded group children must still schedule autosave after a normal edit.
            ((GroupDisplayItem)loaded[0]).DisplayItems[0].Name = "Edited";
            Assert.Equal(1, store.ScheduledSaveCount);
        }
        finally
        {
            store.CancelPendingSavesForTests();
            Directory.Delete(folder, true);
        }
    }
}
