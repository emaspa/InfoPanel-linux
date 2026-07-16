using InfoPanel.Models;
using InfoPanel.Persistence;
using Xunit;

namespace InfoPanel.Core.Tests
{
    [Collection("ConfigPersistence")]
    public class ProfileTransferTests : IDisposable
    {
        private readonly string _tempDir;

        public ProfileTransferTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "infopanel-transfer-tests-" + Guid.NewGuid());
            Directory.CreateDirectory(_tempDir);
            ConfigPersistence.BaseFolderOverride = _tempDir;
        }

        public void Dispose()
        {
            ConfigPersistence.BaseFolderOverride = null;
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
    }
}
