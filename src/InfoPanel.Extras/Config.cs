using IniParser;
using IniParser.Model;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace InfoPanel.Extras
{
    internal class Config
    {
        private static readonly Lazy<Config> _instance = new(() => new Config());
        public static Config Instance => _instance.Value;

        private readonly static string _configFilePath = ResolveConfigPath();

        private static string ResolveConfigPath()
        {
            // Historical location: next to the plugin assembly. Works for the
            // tarball install in the user's home, but system packages put the
            // plugin in a read-only location (/opt, /usr/lib), where writing
            // the defaults throws and takes the whole plugin down with it.
            var legacy = $"{Assembly.GetExecutingAssembly().ManifestModule.FullyQualifiedName}.ini";
            try
            {
                if (File.Exists(legacy))
                {
                    return legacy;
                }

                var dir = Path.GetDirectoryName(legacy)!;
                var probe = Path.Combine(dir, ".infopanel-write-probe");
                File.WriteAllText(probe, string.Empty);
                File.Delete(probe);
                return legacy;
            }
            catch
            {
                var dataDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "InfoPanel", "plugins");
                Directory.CreateDirectory(dataDir);
                return Path.Combine(dataDir, Path.GetFileName(legacy));
            }
        }

        public readonly static string SECTION_WEATHER = "Weather"; 
        public readonly static string SECTION_SYSTEM_INFO = "System Info";
        public readonly static string SECTION_MANGOHUD = "MangoHud";
        public readonly static string SECTION_SMART = "SMART";

        private IniData? IniData { get; set; }
        private bool IsDirty { get; set; }

        public static string FilePath => _configFilePath;

        public void Load()
        {
            if(File.Exists(_configFilePath))
            {
                var parser = new FileIniDataParser();
                IniData = parser.ReadFile(_configFilePath);
            }

            EnsureDefaults();
        }

        private void EnsureDefaults()
        {
            IniData ??= new IniData();

            // Begin Weather Plugin Section
            if (!HasValue(SECTION_WEATHER, "APIKey"))
            {
                SetValue(SECTION_WEATHER, "APIKey", "<your-open-weather-api-key>");
            }

            if (!HasValue(SECTION_WEATHER, "City"))
            {
                SetValue(SECTION_WEATHER, "City", "Singapore");
            }
            // End Weather Plugin Section

            // Begin System Info Plugin Section
            if (!HasValue(SECTION_SYSTEM_INFO, "Blacklist"))
            {
                SetValue(SECTION_SYSTEM_INFO, "Blacklist", "_Total,Idle,dwm,csrss,svchost,lsass,system,spoolsv,Memory Compression");
            }
            // End System Info Plugin Section

            if (IsDirty)
            {
                var parser = new FileIniDataParser();
                parser.WriteFile(_configFilePath, IniData);
                IsDirty = false;
            }
        }

        public bool HasValue(string section, string key)
        {
            return IniData != null && IniData[section].ContainsKey(key);
        }

        public bool TryGetValue(string section, string key, out string value)
        {
            value = string.Empty;
            if(IniData != null && IniData[section].ContainsKey(key))
            {
                value = IniData[section][key];
                return true;
            }

            return false;
        }

        private void SetValue(string section, string key, string value)
        {
            if (IniData != null)
            {
                IniData[section][key] = value;
                IsDirty = true;
            }
        }
    }
}
