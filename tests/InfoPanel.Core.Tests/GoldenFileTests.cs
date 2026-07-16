using InfoPanel.Models;
using InfoPanel.Persistence;
using System.Xml.Linq;
using Xunit;

namespace InfoPanel.Core.Tests
{
    /// <summary>
    /// Compatibility gate: real v1 data files (captured from ~/.local/share/InfoPanel)
    /// must load, and re-serialization must preserve every element the v1 files contain.
    /// New elements added by v2 (e.g. DisplayMask/FlickerFix) are allowed; losing or
    /// changing an existing one is a failure.
    /// </summary>
    [Collection("ConfigPersistence")]
    public class GoldenFileTests : IDisposable
    {
        private readonly string _tempDir;

        public GoldenFileTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "infopanel-tests-" + Guid.NewGuid());
            Directory.CreateDirectory(_tempDir);
            ConfigPersistence.BaseFolderOverride = _tempDir;
        }

        public void Dispose()
        {
            ConfigPersistence.BaseFolderOverride = null;
            try { Directory.Delete(_tempDir, true); } catch { }
        }

        private static string TestData(string name) =>
            Path.Combine(AppContext.BaseDirectory, "TestData", name);

        // ---- settings.xml ----

        [Fact]
        public async Task Settings_V1File_LoadsAndRoundTripsWithoutLoss()
        {
            File.Copy(TestData("settings.xml"), Path.Combine(_tempDir, "settings.xml"));

            var settings = ConfigPersistence.LoadSettings();

            Assert.NotNull(settings);
            Assert.Equal(131, settings!.Version);
            Assert.True(settings.ThermalrightPanelMultiDeviceMode);
            var device = Assert.Single(settings.ThermalrightPanelDevices);
            Assert.Equal(InfoPanel.ThermalrightPanel.ThermalrightPanelModel.TrofeoVision916, device.Model);
            Assert.Equal("usbdev3.9", device.DeviceId);
            Assert.Equal(Guid.Parse("b1480583-5510-4f08-9ecb-acc427ab1afe"), device.ProfileGuid);
            Assert.Equal(95, device.JpegQuality);

            await ConfigPersistence.SaveSettingsAsync(settings);

            AssertNoElementLost(TestData("settings.xml"), Path.Combine(_tempDir, "settings.xml"));
        }

        // ---- profiles.xml ----

        [Fact]
        public void Profiles_V1File_LoadsAndRoundTripsWithoutLoss()
        {
            File.Copy(TestData("profiles.xml"), Path.Combine(_tempDir, "profiles.xml"));

            var profiles = ConfigPersistence.LoadProfiles();

            Assert.NotNull(profiles);
            var profile = Assert.Single(profiles!);
            Assert.Equal(Guid.Parse("b1480583-5510-4f08-9ecb-acc427ab1afe"), profile.Guid);
            Assert.Equal("Profile 1", profile.Name);
            Assert.Equal(600, profile.Width);
            Assert.Equal(400, profile.Height);
            Assert.Equal(1.33f, profile.FontScale, 3);

            ConfigPersistence.SaveProfiles(profiles!, cleanupOrphans: false);

            AssertNoElementLost(TestData("profiles.xml"), Path.Combine(_tempDir, "profiles.xml"));
        }

        // ---- profiles/{guid}.xml (display items) ----

        [Fact]
        public void DisplayItems_V1File_LoadsAndRoundTripsWithoutLoss()
        {
            var profile = new Profile { Guid = Guid.Parse("b1480583-5510-4f08-9ecb-acc427ab1afe") };
            Directory.CreateDirectory(ConfigPersistence.ProfilesFolder);
            File.Copy(TestData("displayitems.xml"), Path.Combine(ConfigPersistence.ProfilesFolder, profile.Guid + ".xml"));

            var items = ConfigPersistence.LoadDisplayItems(profile);

            Assert.Equal(3, items.Count);
            Assert.IsType<TextDisplayItem>(items[0]);
            Assert.IsType<TextDisplayItem>(items[1]);
            var gauge = Assert.IsType<GaugeDisplayItem>(items[2]);
            Assert.Equal(InfoPanel.Enums.SensorType.Hwmon, gauge.SensorType);
            Assert.Equal("hwmon4/curr5", gauge.LibreSensorId);
            Assert.Equal(100, gauge.X);
            Assert.Equal(100, gauge.Y);
            Assert.All(items, item => Assert.Same(profile, item.Profile));

            ConfigPersistence.SaveDisplayItems(profile, items);

            AssertNoElementLost(TestData("displayitems.xml"), Path.Combine(ConfigPersistence.ProfilesFolder, profile.Guid + ".xml"));
        }

        [Fact]
        public void DisplayItems_SecondSaveIsByteStable()
        {
            var profile = new Profile { Guid = Guid.NewGuid() };
            Directory.CreateDirectory(ConfigPersistence.ProfilesFolder);
            File.Copy(TestData("displayitems.xml"), Path.Combine(ConfigPersistence.ProfilesFolder, profile.Guid + ".xml"));

            var items = ConfigPersistence.LoadDisplayItems(profile);
            ConfigPersistence.SaveDisplayItems(profile, items);
            var firstSave = File.ReadAllBytes(Path.Combine(ConfigPersistence.ProfilesFolder, profile.Guid + ".xml"));

            var reloaded = ConfigPersistence.LoadDisplayItems(profile);
            ConfigPersistence.SaveDisplayItems(profile, reloaded);
            var secondSave = File.ReadAllBytes(Path.Combine(ConfigPersistence.ProfilesFolder, profile.Guid + ".xml"));

            Assert.Equal(firstSave, secondSave);
        }

        // ---- Windows-fork-written files (SensorType names the Linux port lacked) ----

        [Fact]
        public void DisplayItems_ForkFileWithLibreAndHwInfoSensors_Loads()
        {
            var profile = new Profile { Guid = Guid.NewGuid() };
            Directory.CreateDirectory(ConfigPersistence.ProfilesFolder);

            const string forkXml = """
                <?xml version="1.0" encoding="utf-8"?>
                <ArrayOfDisplayItem xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:xsd="http://www.w3.org/2001/XMLSchema">
                  <DisplayItem xsi:type="SensorDisplayItem">
                    <Name>CPU Temp</Name>
                    <X>10</X>
                    <Y>10</Y>
                    <SensorType>Libre</SensorType>
                    <LibreSensorId>/amdcpu/0/temperature/2</LibreSensorId>
                  </DisplayItem>
                  <DisplayItem xsi:type="SensorDisplayItem">
                    <Name>GPU Clock</Name>
                    <X>10</X>
                    <Y>40</Y>
                    <SensorType>HwInfo</SensorType>
                    <Id>100</Id>
                    <Instance>0</Instance>
                    <EntryId>200</EntryId>
                  </DisplayItem>
                </ArrayOfDisplayItem>
                """;
            File.WriteAllText(Path.Combine(ConfigPersistence.ProfilesFolder, profile.Guid + ".xml"), forkXml);

            var items = ConfigPersistence.LoadDisplayItems(profile);

            Assert.Equal(2, items.Count);
            Assert.Equal(InfoPanel.Enums.SensorType.Libre, ((SensorDisplayItem)items[0]).SensorType);
            Assert.Equal(InfoPanel.Enums.SensorType.HwInfo, ((SensorDisplayItem)items[1]).SensorType);
        }

        [Fact]
        public async Task Settings_ForkDeviceWithDisplayMaskAndFlickerFix_RoundTrips()
        {
            var settings = new Settings();
            settings.ThermalrightPanelDevices.Add(new ThermalrightPanelDevice
            {
                DeviceId = "test",
                Model = InfoPanel.ThermalrightPanel.ThermalrightPanelModel.TrofeoVision916,
                FlickerFix = true,
                DisplayMask = InfoPanel.ThermalrightPanel.ThermalrightDisplayMask.RoundedAll,
            });

            await ConfigPersistence.SaveSettingsAsync(settings);
            var reloaded = ConfigPersistence.LoadSettings();

            var device = Assert.Single(reloaded!.ThermalrightPanelDevices);
            Assert.True(device.FlickerFix);
            Assert.Equal(InfoPanel.ThermalrightPanel.ThermalrightDisplayMask.RoundedAll, device.DisplayMask);
        }

        // ---- helpers ----

        /// <summary>
        /// Asserts every element/attribute/value present in the original document also
        /// exists identically in the re-serialized one (additions are permitted).
        /// </summary>
        private static void AssertNoElementLost(string originalFile, string newFile)
        {
            var original = XDocument.Load(originalFile);
            var updated = XDocument.Load(newFile);

            AssertSubtree(original.Root!, updated.Root!, original.Root!.Name.LocalName);
        }

        private static void AssertSubtree(XElement expected, XElement actual, string path)
        {
            Assert.True(expected.Name == actual.Name, $"Element name mismatch at {path}: {expected.Name} vs {actual.Name}");

            var expectedChildren = expected.Elements().ToList();
            if (expectedChildren.Count == 0)
            {
                Assert.True((expected.Value ?? "") == (actual.Value ?? ""),
                    $"Value mismatch at {path}: '{expected.Value}' vs '{actual.Value}'");
                return;
            }

            var actualChildren = actual.Elements().ToList();
            var actualIndex = 0;
            foreach (var expectedChild in expectedChildren)
            {
                // find the expected child among remaining actual children, in order
                var foundAt = -1;
                for (var i = actualIndex; i < actualChildren.Count; i++)
                {
                    if (actualChildren[i].Name == expectedChild.Name)
                    {
                        foundAt = i;
                        break;
                    }
                }

                Assert.True(foundAt >= 0, $"Element {path}/{expectedChild.Name.LocalName} was lost on re-serialization");
                AssertSubtree(expectedChild, actualChildren[foundAt], $"{path}/{expectedChild.Name.LocalName}");
                actualIndex = foundAt + 1;
            }
        }
    }
}
