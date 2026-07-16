using InfoPanel.Models;
using Serilog;
using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Serialization;

namespace InfoPanel.Persistence
{
    /// <summary>
    /// .infopanel profile archives (Profile.xml + DisplayItems.xml + assets/*),
    /// format-compatible with v1 and the Windows fork so profiles can be shared.
    /// </summary>
    public static class ProfileTransfer
    {
        private static readonly ILogger Logger = Log.ForContext(typeof(ProfileTransfer));

        public static string? Export(Profile profile, string outputFolder)
        {
            try
            {
                var baseName = string.Concat(profile.Name.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_'));
                var exportFilePath = Path.Combine(outputFolder, $"{baseName}-{DateTimeOffset.Now.ToUnixTimeSeconds()}.infopanel");

                if (File.Exists(exportFilePath))
                {
                    File.Delete(exportFilePath);
                }

                using (ZipArchive archive = ZipFile.Open(exportFilePath, ZipArchiveMode.Create))
                {
                    // profile settings (fresh guid on import; strip window placement)
                    var exportProfile = new Profile(profile.Name, profile.Width, profile.Height)
                    {
                        ShowFps = profile.ShowFps,
                        BackgroundColor = profile.BackgroundColor,
                        Font = profile.Font,
                        FontSize = profile.FontSize,
                        Color = profile.Color,
                        OpenGL = profile.OpenGL,
                        FontScale = profile.FontScale,
                    };

                    var entry = archive.CreateEntry("Profile.xml");
                    using (Stream entryStream = entry.Open())
                    {
                        XmlSerializer xs = new(typeof(Profile));
                        var settings = new XmlWriterSettings() { Encoding = Encoding.UTF8, Indent = true };
                        using var wr = XmlWriter.Create(entryStream, settings);
                        xs.Serialize(wr, exportProfile);
                    }

                    var profilePath = Path.Combine(ConfigPersistence.ProfilesFolder, profile.Guid + ".xml");
                    if (File.Exists(profilePath))
                    {
                        archive.CreateEntryFromFile(profilePath, "DisplayItems.xml");
                    }

                    var assetFolder = Path.Combine(ConfigPersistence.AssetsFolder, profile.Guid.ToString());
                    if (Directory.Exists(assetFolder))
                    {
                        foreach (var file in Directory.GetFiles(assetFolder))
                        {
                            archive.CreateEntryFromFile(file, "assets/" + Path.GetFileName(file));
                        }
                    }
                }

                Logger.Information("Exported profile {Name} to {Path}", profile.Name, exportFilePath);
                return exportFilePath;
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Failed to export profile {Name}", profile.Name);
                return null;
            }
        }

        /// <summary>Imports a .infopanel archive as a new profile (fresh guid). Returns the new profile or null.</summary>
        public static Profile? Import(string archivePath)
        {
            try
            {
                using var archive = ZipFile.OpenRead(archivePath);

                ZipArchiveEntry? profileEntry = null;
                ZipArchiveEntry? displayItemsEntry = null;
                List<ZipArchiveEntry> assets = [];

                foreach (var entry in archive.Entries)
                {
                    // v1 wrote Windows separators on Windows; accept both
                    var name = entry.FullName.Replace('\\', '/');
                    if (name.Equals("Profile.xml"))
                    {
                        profileEntry = entry;
                    }
                    else if (name.Equals("DisplayItems.xml"))
                    {
                        displayItemsEntry = entry;
                    }
                    else if (name.StartsWith("assets/") && entry.Name.Length > 0)
                    {
                        assets.Add(entry);
                    }
                }

                if (profileEntry == null || displayItemsEntry == null)
                {
                    Logger.Warning("Archive {Path} is not a valid .infopanel profile", archivePath);
                    return null;
                }

                Profile? profile = null;
                using (Stream entryStream = profileEntry.Open())
                {
                    XmlSerializer xs = new(typeof(Profile));
                    using var rd = XmlReader.Create(entryStream);
                    profile = xs.Deserialize(rd) as Profile;
                }

                if (profile == null)
                {
                    return null;
                }

                profile.Guid = Guid.NewGuid();
                profile.Name = "[Import] " + profile.Name;
                profile.Active = false;

                Directory.CreateDirectory(ConfigPersistence.ProfilesFolder);
                displayItemsEntry.ExtractToFile(Path.Combine(ConfigPersistence.ProfilesFolder, profile.Guid + ".xml"));

                var assetFolder = Path.Combine(ConfigPersistence.AssetsFolder, profile.Guid.ToString());
                Directory.CreateDirectory(assetFolder);
                foreach (var asset in assets)
                {
                    asset.ExtractToFile(Path.Combine(assetFolder, asset.Name), overwrite: true);
                }

                Logger.Information("Imported profile {Name} from {Path}", profile.Name, archivePath);
                return profile;
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Failed to import profile from {Path}", archivePath);
                return null;
            }
        }
    }
}
