using InfoPanel.Models;
using Serilog;
using System.Text;
using System.Xml;
using System.Xml.Serialization;

namespace InfoPanel.Persistence
{
    /// <summary>
    /// XML persistence for settings, profiles and display items.
    /// The on-disk format (paths, element names, serializer construction) is identical to
    /// InfoPanel v1.x / the Windows fork so existing ~/.local/share/InfoPanel data and
    /// profile files shared between platforms keep working. Do not change type or
    /// property names of the serialized classes, or the extraTypes list, without a
    /// compatibility test proving old files still round-trip.
    /// </summary>
    public static class ConfigPersistence
    {
        private static readonly ILogger Logger = Log.ForContext(typeof(ConfigPersistence));
        private static readonly SemaphoreSlim _settingsSaveSemaphore = new(1, 1);

        /// <summary>
        /// Polymorphic display item types registered with the serializer. This is the
        /// authoritative list (matches v1 SharedModel.SaveDisplayItems); order and content
        /// affect only serializer construction, but removal breaks old files.
        /// </summary>
        public static readonly Type[] DisplayItemExtraTypes =
        [
            typeof(GroupDisplayItem), typeof(BarDisplayItem), typeof(GraphDisplayItem),
            typeof(DonutDisplayItem), typeof(TableSensorDisplayItem), typeof(SensorDisplayItem),
            typeof(TextDisplayItem), typeof(ClockDisplayItem), typeof(CalendarDisplayItem),
            typeof(SensorImageDisplayItem), typeof(ImageDisplayItem), typeof(HttpImageDisplayItem),
            typeof(GaugeDisplayItem), typeof(ShapeDisplayItem), typeof(GuideDisplayItem)
        ];

        /// <summary>Overrides the data directory (tests, portable mode). Null = default XDG location.</summary>
        public static string? BaseFolderOverride { get; set; }

        public static string BaseFolder =>
            BaseFolderOverride ??
            Environment.GetEnvironmentVariable("INFOPANEL_DATA_DIR") ??
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "InfoPanel");

        public static string ProfilesFolder => Path.Combine(BaseFolder, "profiles");
        public static string AssetsFolder => Path.Combine(BaseFolder, "assets");
        private static string SettingsFile => Path.Combine(BaseFolder, "settings.xml");

        // ---- settings.xml ----

        public static async Task SaveSettingsAsync(Settings settings)
        {
            await _settingsSaveSemaphore.WaitAsync();
            try
            {
                Directory.CreateDirectory(BaseFolder);

                var fileName = SettingsFile;
                var tempFileName = fileName + ".tmp";
                var backupFileName = fileName + ".bak";

                // Serialize to memory first so a failed serialization never corrupts the file
                using var ms = new MemoryStream();
                var xs = new XmlSerializer(typeof(Settings));
                xs.Serialize(ms, settings);

                ms.Position = 0;
                await using (var stream = new FileStream(tempFileName, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true))
                {
                    await ms.CopyToAsync(stream);
                    await stream.FlushAsync();
                }

                // Atomic replace with backup
                if (File.Exists(fileName))
                {
                    File.Replace(tempFileName, fileName, backupFileName, ignoreMetadataErrors: true);
                }
                else
                {
                    File.Move(tempFileName, fileName, overwrite: true);
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Error saving settings");
                throw;
            }
            finally
            {
                _settingsSaveSemaphore.Release();
            }
        }

        /// <summary>Loads settings.xml, falling back to settings.xml.bak (and restoring it) on corruption.</summary>
        public static Settings? LoadSettings()
        {
            var fileName = SettingsFile;
            var backupFileName = fileName + ".bak";

            var settings = TryLoadSettingsFromFile(fileName);
            if (settings == null && File.Exists(backupFileName))
            {
                settings = TryLoadSettingsFromFile(backupFileName);
                if (settings != null)
                {
                    try
                    {
                        File.Copy(backupFileName, fileName, overwrite: true);
                        Logger.Information("Settings restored from backup file");
                    }
                    catch (Exception ex)
                    {
                        Logger.Error(ex, "Failed to restore settings backup");
                    }
                }
            }

            return settings;
        }

        private static Settings? TryLoadSettingsFromFile(string filePath)
        {
            if (!File.Exists(filePath))
            {
                return null;
            }

            try
            {
                var xs = new XmlSerializer(typeof(Settings));
                using var rd = XmlReader.Create(filePath);
                return xs.Deserialize(rd) as Settings;
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Error loading settings from {File}", filePath);
                return null;
            }
        }

        // ---- profiles.xml ----

        /// <summary>
        /// Saves the profile list. When <paramref name="cleanupOrphans"/> is set (v1 behavior),
        /// display-item xml files and asset folders belonging to no listed profile are deleted.
        /// </summary>
        public static void SaveProfiles(IReadOnlyList<Profile> profiles, bool cleanupOrphans = true)
        {
            Directory.CreateDirectory(BaseFolder);

            var fileName = Path.Combine(BaseFolder, "profiles.xml");
            var xs = new XmlSerializer(typeof(List<Profile>));
            var settings = new XmlWriterSettings() { Encoding = Encoding.UTF8, Indent = true };
            using (var wr = XmlWriter.Create(fileName, settings))
            {
                xs.Serialize(wr, profiles.ToList());
            }

            if (!cleanupOrphans)
            {
                return;
            }

            if (Directory.Exists(ProfilesFolder))
            {
                var files = Directory.GetFiles(ProfilesFolder).ToList();
                foreach (var profile in profiles)
                {
                    files.Remove(Path.Combine(ProfilesFolder, profile.Guid + ".xml"));
                }

                foreach (var file in files)
                {
                    try { File.Delete(file); } catch { }
                }
            }

            if (Directory.Exists(AssetsFolder))
            {
                var directories = Directory.GetDirectories(AssetsFolder).ToList();
                foreach (var profile in profiles)
                {
                    directories.Remove(Path.Combine(AssetsFolder, profile.Guid.ToString()));
                }

                foreach (var directory in directories)
                {
                    try { Directory.Delete(directory, true); } catch { }
                }
            }
        }

        public static List<Profile>? LoadProfiles()
        {
            var fileName = Path.Combine(BaseFolder, "profiles.xml");
            if (File.Exists(fileName))
            {
                var xs = new XmlSerializer(typeof(List<Profile>));
                using var rd = XmlReader.Create(fileName);
                try
                {
                    return xs.Deserialize(rd) as List<Profile>;
                }
                catch (Exception ex)
                {
                    Logger.Error(ex, "Error loading profiles");
                }
            }

            return null;
        }

        // ---- profiles/{guid}.xml (display items) ----

        public static void SaveDisplayItems(Profile profile, ICollection<DisplayItem> displayItems)
        {
            Directory.CreateDirectory(ProfilesFolder);
            var fileName = Path.Combine(ProfilesFolder, profile.Guid + ".xml");

            var xs = new XmlSerializer(typeof(List<DisplayItem>), DisplayItemExtraTypes);
            var settings = new XmlWriterSettings() { Encoding = Encoding.UTF8, Indent = true };
            using var wr = XmlWriter.Create(fileName, settings);

            // ToList() is necessary to avoid issues with serialization of ICollection directly
            xs.Serialize(wr, displayItems.ToList());
        }

        public static List<DisplayItem> LoadDisplayItems(Profile profile)
        {
            return LoadDisplayItemsFromFile(profile, Path.Combine(ProfilesFolder, profile.Guid + ".xml"));
        }

        // ---- autosave/profiles/{guid}.xml (session-start backups) ----

        public static string AutosaveFolder => Path.Combine(BaseFolder, "autosave", "profiles");

        /// <summary>
        /// Copies the profile's current on-disk display items to the autosave backup.
        /// Called once per profile per app run, before the first autosave overwrites
        /// the file, so the backup preserves the layout as this session found it.
        /// </summary>
        public static void BackupDisplayItems(Profile profile)
        {
            var source = Path.Combine(ProfilesFolder, profile.Guid + ".xml");
            if (!File.Exists(source))
            {
                return;
            }

            Directory.CreateDirectory(AutosaveFolder);
            File.Copy(source, Path.Combine(AutosaveFolder, profile.Guid + ".xml"), overwrite: true);
        }

        /// <summary>Local timestamp of the profile's backup, or null when none exists.</summary>
        public static DateTime? GetDisplayItemsBackupTime(Profile profile)
        {
            var fileName = Path.Combine(AutosaveFolder, profile.Guid + ".xml");
            return File.Exists(fileName) ? File.GetLastWriteTime(fileName) : null;
        }

        public static List<DisplayItem> LoadDisplayItemsBackup(Profile profile)
        {
            return LoadDisplayItemsFromFile(profile, Path.Combine(AutosaveFolder, profile.Guid + ".xml"));
        }

        private static List<DisplayItem> LoadDisplayItemsFromFile(Profile profile, string fileName)
        {
            if (File.Exists(fileName))
            {
                var xs = new XmlSerializer(typeof(List<DisplayItem>), DisplayItemExtraTypes);
                using var rd = XmlReader.Create(fileName);
                try
                {
                    if (xs.Deserialize(rd) is List<DisplayItem> displayItems)
                    {
                        foreach (var displayItem in displayItems)
                        {
                            displayItem.SetProfile(profile);
                        }

                        return displayItems;
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error(ex, "Error loading display items for profile {Guid}", profile.Guid);
                }
            }

            return [];
        }
    }
}
