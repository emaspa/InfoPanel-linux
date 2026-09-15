using InfoPanel.Models;
using InfoPanel.Persistence;
using System.IO.Compression;
using System.Xml.Linq;
using Xunit;

namespace InfoPanel.Core.Tests
{
    [Collection("ConfigPersistence")]
    public class ProfileTransferTests : SensorStateTest
    {
        private readonly string _tempDir;

        public ProfileTransferTests(SensorStateFixture fixture) : base(fixture)
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "infopanel-transfer-tests-" + Guid.NewGuid());
            Directory.CreateDirectory(_tempDir);
            ConfigPersistence.BaseFolderOverride = _tempDir;
        }

        public override void Dispose()
        {
            base.Dispose();
            try { Directory.Delete(_tempDir, true); } catch { }
        }

        [Fact]
        public void ExportImport_RoundTripsProfileItemsAndAssets()
        {
            var profile = new Profile { Guid = Guid.NewGuid(), Name = "My Panel", Width = 800, Height = 480, FontScale = 1.25f };

            ConfigPersistence.SaveDisplayItems(profile,
            [
                new TextDisplayItem { Name = "hello", X = 5, Y = 10 },
                new ShapeDisplayItem { Name = "box", X = 20, Y = 30, Width = 50, Height = 40 },
            ]);

            var assetDir = Path.Combine(ConfigPersistence.AssetsFolder, profile.Guid.ToString());
            Directory.CreateDirectory(assetDir);
            File.WriteAllBytes(Path.Combine(assetDir, "pic.png"), [1, 2, 3]);

            var archive = ProfileTransfer.Export(profile, _tempDir);
            Assert.NotNull(archive);
            Assert.True(File.Exists(archive));

            var imported = ProfileTransfer.Import(archive!);
            Assert.NotNull(imported);
            Assert.NotEqual(profile.Guid, imported!.Guid);
            Assert.Equal("[Import] My Panel", imported.Name);
            Assert.Equal(800, imported.Width);
            Assert.Equal(1.25f, imported.FontScale, 3);

            var items = ConfigPersistence.LoadDisplayItems(imported);
            Assert.Equal(2, items.Count);
            Assert.Equal("hello", items[0].Name);
            Assert.IsType<ShapeDisplayItem>(items[1]);

            var importedAsset = Path.Combine(ConfigPersistence.AssetsFolder, imported.Guid.ToString(), "pic.png");
            Assert.Equal([1, 2, 3], File.ReadAllBytes(importedAsset));
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void ExportMigratesDetachedCurrentOrDiskItemsWithoutSavingSource(bool useCurrentItems)
        {
            SensorStateFixture.PublishWireView();
            SensorStateFixture.EnableMigration();
            var profile = new Profile { Name = "Bindings" };
            var sensor = new SensorDisplayItem { Name = "On disk", LibreSensorId = "hwmon4/curr5", SensorName = "Pin 5" };
            var group = new GroupDisplayItem { X = 31, Y = 17, Hidden = true, IsExpanded = false };
            group.DisplayItems.Add(sensor);
            ConfigPersistence.SaveDisplayItems(profile, [group]);
            var path = Path.Combine(ConfigPersistence.ProfilesFolder, profile.Guid + ".xml");
            var before = File.ReadAllBytes(path);
            sensor.Name = "Unsaved edit";
            var notifications = 0;
            sensor.PropertyChanged += (_, _) => notifications++;
            var archivePath = ProfileTransfer.Export(profile, _tempDir, useCurrentItems ? [group] : null);
            Assert.NotNull(archivePath);
            Assert.Equal(0, notifications);
            Assert.Equal("hwmon4/curr5", sensor.LibreSensorId);
            Assert.Null(sensor.HardwareSensorIdentity);
            Assert.Equal(before, File.ReadAllBytes(path));
            Assert.False(Directory.Exists(ConfigPersistence.AutosaveFolder));
            using var archive = ZipFile.OpenRead(archivePath!);
            Assert.Equal(new[] { "DisplayItems.xml", "Profile.xml" }, archive.Entries.Select(e => e.FullName).Order());
            using var entry = archive.GetEntry("DisplayItems.xml")!.Open();
            var xml = XDocument.Load(entry);
            Assert.Equal(SensorStateFixture.WireViewId, Assert.Single(xml.Descendants("LibreSensorId")).Value);
            Assert.Equal("hwmon4/curr5", Assert.Single(xml.Descendants("OriginalId")).Value);
            Assert.Contains(xml.Descendants("Name"), e => e.Value == (useCurrentItems ? "Unsaved edit" : "On disk"));
            var exportedGroup = xml.Root!.Element("DisplayItem")!;
            Assert.Equal("31", exportedGroup.Element("X")!.Value);
            Assert.Equal("17", exportedGroup.Element("Y")!.Value);
            Assert.Equal("true", exportedGroup.Element("Hidden")!.Value);
            Assert.Equal("false", exportedGroup.Element("IsExpanded")!.Value);
        }

        [Fact]
        public void ExportIncludesUnsavedMigrationAndImportPreservesBindingsUntilNormalLoad()
        {
            var profile = new Profile { Name = "Unsaved migration" };
            var item = new SensorDisplayItem { LibreSensorId = "hwmon4/curr5", SensorName = "Pin 5" };
            ConfigPersistence.SaveDisplayItems(profile, [item]);
            var sourcePath = Path.Combine(ConfigPersistence.ProfilesFolder, profile.Guid + ".xml");
            var before = File.ReadAllBytes(sourcePath);
            SensorStateFixture.PublishWireView();
            SensorStateFixture.EnableMigration();
            Assert.Single(SensorBindingMigration.Migrate([item]).Migrated);
            var archivePath = ProfileTransfer.Export(profile, _tempDir, [item]);
            Assert.NotNull(archivePath);
            Assert.Equal(before, File.ReadAllBytes(sourcePath));
            var imported = ProfileTransfer.Import(archivePath!);
            Assert.NotNull(imported);
            Assert.False(imported.Active);
            var loaded = Assert.IsType<SensorDisplayItem>(Assert.Single(ConfigPersistence.LoadDisplayItems(imported)));
            Assert.Equal(SensorStateFixture.WireViewId, loaded.LibreSensorId);
            Assert.Equal("hwmon4/curr5", loaded.HardwareSensorIdentity!.OriginalId);
        }

        [Fact]
        public void MissingHardwareExportImportPreservesStringsAndRecoversOnStoreLoad()
        {
            var profile = new Profile { Name = "Absent hardware" };
            var sensor = new SensorDisplayItem
            {
                LibreSensorId = "hwmon4/curr5", SensorName = "Pin 5",
                HardwareSensorIdentity = new() { ChipName = "wireview", ChannelLabel = "Pin 5", OriginalId = "older id" }
            };
            var resolver = new InfoPanel.Sensors.SensorIdResolver();
            resolver.Publish(new(1, []));
            SensorReader.ConfigureResolver(resolver);
            SensorStateFixture.EnableMigration();
            var archivePath = ProfileTransfer.Export(profile, _tempDir, [sensor]);
            Assert.NotNull(archivePath);
            byte[] archivedBytes;
            using (var archive = ZipFile.OpenRead(archivePath!))
            using (var entry = archive.GetEntry("DisplayItems.xml")!.Open())
            using (var copy = new MemoryStream())
            {
                entry.CopyTo(copy);
                archivedBytes = copy.ToArray();
            }
            // Hardware is available at import time, but import must still copy strings verbatim.
            SensorStateFixture.PublishWireView();
            var imported = ProfileTransfer.Import(archivePath!);
            Assert.NotNull(imported);
            var path = Path.Combine(ConfigPersistence.ProfilesFolder, imported.Guid + ".xml");
            Assert.Equal(archivedBytes, File.ReadAllBytes(path));
            var store = new InfoPanel.Stores.DisplayItemStore();
            var loaded = Assert.IsType<SensorDisplayItem>(Assert.Single(store.GetOrLoad(imported)));
            Assert.Equal(SensorStateFixture.WireViewId, loaded.LibreSensorId);
            Assert.Equal("older id", loaded.HardwareSensorIdentity!.OriginalId);
            Assert.Equal("wireview", loaded.HardwareSensorIdentity.ChipName);
            Assert.Equal("Pin 5", loaded.HardwareSensorIdentity.ChannelLabel);
            Assert.Equal(archivedBytes, File.ReadAllBytes(path));
            Assert.Equal(0, store.ScheduledSaveCount);
        }
    }
}
