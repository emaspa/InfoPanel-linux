using InfoPanel.Models;
using InfoPanel.Persistence;
using InfoPanel.Utils;
using System.Runtime.Loader;
using Xunit;

namespace InfoPanel.Core.Tests;

// These tests change process environment variables and the persistence override.
[Collection("ConfigPersistence")]
public sealed class DataDirectoryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "infopanel-data-tests-" + Guid.NewGuid());
    private readonly Dictionary<string, string?> _environment = new();
    private readonly string? _previousOverride = ConfigPersistence.BaseFolderOverride;
    private string Home => Path.Combine(_root, "home");

    public DataDirectoryTests()
    {
        foreach (var name in new[] { "HOME", "XDG_DATA_HOME", "INFOPANEL_DATA_DIR" })
        {
            _environment[name] = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, null);
        }
        Directory.CreateDirectory(Home);
        Environment.SetEnvironmentVariable("HOME", Home);
        ConfigPersistence.BaseFolderOverride = null;
    }

    [Fact]
    public void FreshHome_CreatesAbsoluteDataFolder_AndKeepsTheSameLocation()
    {
        var share = Path.Combine(Home, ".local", "share");
        Assert.False(Directory.Exists(share));

        var folder = ConfigPersistence.BaseFolder;

        Assert.Equal(Path.Combine(share, "InfoPanel"), folder);
        Assert.True(Path.IsPathFullyQualified(folder));
        Assert.True(Directory.Exists(folder));
        File.WriteAllText(Path.Combine(folder, "settings.xml"), "existing settings");
        Assert.Equal(folder, ConfigPersistence.BaseFolder);
        Assert.Equal("existing settings", File.ReadAllText(Path.Combine(ConfigPersistence.BaseFolder, "settings.xml")));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("relative/data")]
    public void UnsetOrRelativeXdgDataHome_FallsBackToHome(string? xdg)
    {
        Environment.SetEnvironmentVariable("XDG_DATA_HOME", xdg);

        var directory = DataDirectory.GetXdgDataDirectory();

        Assert.Equal(Path.Combine(Home, ".local", "share"), directory);
        Assert.True(Directory.Exists(directory));
    }

    [Fact]
    public void AbsoluteXdgDataHome_IsHonouredAndCreated()
    {
        var xdg = Path.Combine(_root, "xdg-data");
        Environment.SetEnvironmentVariable("XDG_DATA_HOME", xdg);

        Assert.Equal(Path.Combine(xdg, "InfoPanel"), ConfigPersistence.BaseFolder);
        Assert.True(Directory.Exists(xdg));
        Assert.False(Directory.Exists(Path.Combine(Home, ".local")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DataDirOverride_IsAbsoluteAndDoesNotTouchDefaultData(bool relative)
    {
        var folder = Path.Combine(_root, "portable");
        var value = relative ? Path.GetRelativePath(Environment.CurrentDirectory, folder) : folder;
        Environment.SetEnvironmentVariable("INFOPANEL_DATA_DIR", value);
        Environment.SetEnvironmentVariable("XDG_DATA_HOME", Path.Combine(_root, "unused-xdg"));

        Assert.Equal(folder, ConfigPersistence.BaseFolder);
        Assert.True(Directory.Exists(folder));
        Assert.False(Directory.Exists(Path.Combine(_root, "unused-xdg")));
        Assert.False(Directory.Exists(Path.Combine(Home, ".local")));

        // Extras compiles the same source into its net8 assembly, avoiding a
        // reference to Core. Exercise that copy too, without initializing plugins.
        var extras = typeof(InfoPanel.Extras.ClockPlugin).Assembly;
        var resolver = extras.GetType("InfoPanel.Utils.DataDirectory", throwOnError: true)!;
        var resolve = resolver.GetMethod("GetBaseFolder")!;
        Assert.Equal(folder, (string)resolve.Invoke(null, new object?[] { null })!);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PluginsAndAssets_UseTheSameBaseFolderAsProfiles(bool usePropertyOverride)
    {
        var folder = Path.Combine(_root, "isolated");
        Environment.SetEnvironmentVariable("INFOPANEL_DATA_DIR", folder);
        if (usePropertyOverride)
        {
            folder = Path.Combine(_root, "property-override");
            ConfigPersistence.BaseFolderOverride = folder;
        }
        var profileId = Guid.NewGuid();
        var expectedImage = Path.Combine(folder, "assets", profileId.ToString(), "image.png");
        var image = new ImageDisplayItem
        {
            RelativePath = true, FilePath = "image.png"
        };
        image.SetProfile(new Profile { Guid = profileId });

        Assert.Equal(Path.Combine(folder, "profiles"), ConfigPersistence.ProfilesFolder);
        Assert.Equal(Path.Combine(folder, "plugins"), FileUtil.GetExternalPluginFolder());
        Assert.Equal(Path.Combine(folder, "plugins.bin"), FileUtil.GetPluginStateFile());
        Assert.Equal(Path.Combine(folder, "plugin-modules.bin"), FileUtil.GetPluginModuleStateFile());
        Assert.Equal(Path.Combine(folder, "assets"), FileUtil.GetAssetDirectory());
        Assert.Equal(Path.Combine(folder, "assets", profileId.ToString()), FileUtil.GetAssetPath(profileId));
        Assert.Equal(expectedImage, FileUtil.GetRelativeAssetPath(profileId, "image.png"));
        Assert.Equal(expectedImage, image.CalculatedPath);
        Assert.Equal(Path.Combine(AppContext.BaseDirectory, "plugins"), FileUtil.GetBundledPluginFolder());
        Assert.False(Directory.Exists(Path.Combine(Home, ".local")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RepeatedReads_DoNotRecreateDirectory_InCoreAndExtras(bool extras)
    {
        Func<string> resolve = () => ConfigPersistence.BaseFolder;
        if (extras)
        {
            var resolver = typeof(InfoPanel.Extras.ClockPlugin).Assembly
                .GetType("InfoPanel.Utils.DataDirectory", throwOnError: true)!;
            var method = resolver.GetMethod("GetBaseFolder")!;
            resolve = () => (string)method.Invoke(null, new object?[] { null })!;
        }

        var folder = resolve();
        Assert.True(Directory.Exists(folder));
        Directory.Delete(folder, recursive: true);

        // Render/UI readers share the cached string and must not touch the disk.
        Parallel.For(0, 64, _ => Assert.Same(folder, resolve()));
        Assert.False(Directory.Exists(folder));

        var changed = Path.Combine(_root, "changed");
        Environment.SetEnvironmentVariable("INFOPANEL_DATA_DIR", changed);
        Assert.Equal(changed, resolve());
        Assert.True(Directory.Exists(changed));
        Assert.False(Directory.Exists(folder));
    }

    [Theory]
    [InlineData("BaseFolderOverride")]
    [InlineData("INFOPANEL_DATA_DIR")]
    [InlineData("XDG_DATA_HOME")]
    [InlineData("HOME")]
    public void Cache_TracksSettingChangingAndClearingEachInput(string input)
    {
        var defaultFolder = ConfigPersistence.BaseFolder;
        void SetInput(string? value)
        {
            if (input == "BaseFolderOverride")
                ConfigPersistence.BaseFolderOverride = value;
            else
                Environment.SetEnvironmentVariable(input, value);
        }

        string ExpectedFolder(string value) => input switch
        {
            "XDG_DATA_HOME" => Path.Combine(value, "InfoPanel"),
            "HOME" => Path.Combine(value, ".local", "share", "InfoPanel"),
            _ => value
        };

        foreach (var name in new[] { "first", "second" })
        {
            var value = Path.Combine(_root, name);
            SetInput(value);
            var folder = ConfigPersistence.BaseFolder;
            Assert.Equal(ExpectedFolder(value), folder);
            Assert.True(Directory.Exists(folder));
            Directory.Delete(folder, recursive: true);
            Assert.Same(folder, ConfigPersistence.BaseFolder);
            Assert.False(Directory.Exists(folder));
        }

        // Restore the safe test HOME instead of falling back to the real user's.
        SetInput(input == "HOME" ? Home : null);
        Directory.Delete(defaultFolder, recursive: true);
        Assert.Equal(defaultFolder, ConfigPersistence.BaseFolder);
        Assert.True(Directory.Exists(defaultFolder));
    }

    [Fact]
    public void EmptyPropertyOverride_UsesXdg_AndClearingItRestoresEnvironmentOverride()
    {
        var portable = Path.Combine(_root, "portable");
        Environment.SetEnvironmentVariable("INFOPANEL_DATA_DIR", portable);
        Assert.Equal(portable, ConfigPersistence.BaseFolder);

        ConfigPersistence.BaseFolderOverride = "";
        Assert.Equal(Path.Combine(Home, ".local", "share", "InfoPanel"), ConfigPersistence.BaseFolder);

        ConfigPersistence.BaseFolderOverride = null;
        Assert.Equal(portable, ConfigPersistence.BaseFolder);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RelativeOverride_TracksWorkingDirectoryChanges(bool usePropertyOverride)
    {
        var previousDirectory = Environment.CurrentDirectory;
        try
        {
            if (usePropertyOverride)
                ConfigPersistence.BaseFolderOverride = "portable";
            else
                Environment.SetEnvironmentVariable("INFOPANEL_DATA_DIR", "portable");

            foreach (var name in new[] { "cwd-one", "cwd-two" })
            {
                var cwd = Path.Combine(_root, name);
                Directory.CreateDirectory(cwd);
                Environment.CurrentDirectory = cwd;
                var folder = ConfigPersistence.BaseFolder;
                Assert.Equal(Path.Combine(cwd, "portable"), folder);
                Assert.True(Directory.Exists(folder));
                Directory.Delete(folder);
                Assert.Same(folder, ConfigPersistence.BaseFolder);
                Assert.False(Directory.Exists(folder));
            }
        }
        finally
        {
            Environment.CurrentDirectory = previousDirectory;
        }
    }

    [Fact]
    public void FailedDirectoryCreation_IsNotCached()
    {
        var folder = Path.Combine(_root, "blocked");
        File.WriteAllText(folder, "file in place of directory");
        ConfigPersistence.BaseFolderOverride = folder;
        Assert.Throws<IOException>(() => ConfigPersistence.BaseFolder);

        File.Delete(folder);
        Assert.Equal(folder, ConfigPersistence.BaseFolder);
        Assert.True(Directory.Exists(folder));
    }

    [Theory]
    [InlineData("settings")]
    [InlineData("profiles")]
    [InlineData("display-items")]
    [InlineData("asset")]
    public async Task Save_RecreatesDeletedDataDirectory(string writer)
    {
        var folder = ConfigPersistence.BaseFolder;
        var profile = new Profile { Name = "Saved after deletion" };
        Directory.Delete(folder, recursive: true);
        Assert.Equal(folder, ConfigPersistence.BaseFolder);
        Assert.False(Directory.Exists(folder));

        switch (writer)
        {
            case "settings":
                await ConfigPersistence.SaveSettingsAsync(new Settings { Version = 123 });
                Assert.Equal(123, ConfigPersistence.LoadSettings()!.Version);
                break;
            case "profiles":
                ConfigPersistence.SaveProfiles([profile]);
                Assert.Equal(profile.Guid, Assert.Single(ConfigPersistence.LoadProfiles()!).Guid);
                break;
            case "display-items":
                ConfigPersistence.SaveDisplayItems(profile, [new TextDisplayItem { Name = "Recovered" }]);
                Assert.Equal("Recovered", Assert.IsType<TextDisplayItem>(
                    Assert.Single(ConfigPersistence.LoadDisplayItems(profile))).Name);
                break;
            case "asset":
                byte[] data = [1, 2, 3];
                Assert.True(await FileUtil.SaveAsset(profile, "image.png", data));
                Assert.Equal(data, await File.ReadAllBytesAsync(FileUtil.GetRelativeAssetPath(profile, "image.png")));
                break;
        }

        Assert.True(Directory.Exists(folder));
    }

    [Fact]
    public void ExtrasConfig_KeepsResolvedPath_AndRecreatesParentWhenWritingDefaults()
    {
        var folder = Path.Combine(_root, "extras");
        Environment.SetEnvironmentVariable("INFOPANEL_DATA_DIR", folder);
        // Isolate static Config initialization from other plugin tests.
        var context = new AssemblyLoadContext("extras-config-" + Guid.NewGuid(), isCollectible: true);
        try
        {
            var assembly = context.LoadFromAssemblyPath(typeof(InfoPanel.Extras.ClockPlugin).Assembly.Location);
            var configType = assembly.GetType("InfoPanel.Extras.Config", throwOnError: true)!;
            var config = Activator.CreateInstance(configType, nonPublic: true)!;
            var path = (string)configType.GetProperty("FilePath")!.GetValue(null)!;
            Assert.Equal(Path.Combine(folder, "plugins", "InfoPanel.Extras.dll.ini"), path);
            Directory.Delete(folder, recursive: true);
            var unused = Path.Combine(_root, "unused");
            Environment.SetEnvironmentVariable("INFOPANEL_DATA_DIR", unused);
            Assert.Equal(path, configType.GetProperty("FilePath")!.GetValue(null));
            Assert.False(Directory.Exists(folder));

            configType.GetMethod("Load")!.Invoke(config, null);

            Assert.Contains("[Weather]", File.ReadAllText(path));
            Assert.False(Directory.Exists(unused));
        }
        finally
        {
            context.Unload();
        }
    }

    public void Dispose()
    {
        ConfigPersistence.BaseFolderOverride = _previousOverride;
        foreach (var (name, value) in _environment)
            Environment.SetEnvironmentVariable(name, value);
        Directory.Delete(_root, recursive: true);
    }
}
