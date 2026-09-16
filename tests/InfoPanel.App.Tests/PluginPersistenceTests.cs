using InfoPanel.Monitors;
using InfoPanel.Persistence;
using InfoPanel.Utils;
using System.Reflection;
using Xunit;

namespace InfoPanel.App.Tests;

[Collection("AppState")]
public sealed class PluginPersistenceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PluginStateSave_RecreatesDeletedDataDirectory(bool moduleState)
    {
        var folder = Path.Combine(Path.GetTempPath(), "infopanel-plugin-state-" + Guid.NewGuid());
        var previousOverride = ConfigPersistence.BaseFolderOverride;
        try
        {
            ConfigPersistence.BaseFolderOverride = folder;
            // Construct without starting background monitoring or changing the singleton.
            var monitor = (PluginMonitor)Activator.CreateInstance(typeof(PluginMonitor), nonPublic: true)!;
            Assert.True(Directory.Exists(FileUtil.GetExternalPluginFolder()));
            Directory.Delete(folder, recursive: true);
            Assert.Equal(folder, ConfigPersistence.BaseFolder);
            Assert.False(Directory.Exists(folder));

            if (moduleState)
            {
                typeof(PluginMonitor).GetMethod("SaveModuleState", BindingFlags.NonPublic | BindingFlags.Static)!
                    .Invoke(null, null);
                Assert.True(File.Exists(FileUtil.GetPluginModuleStateFile()));
            }
            else
            {
                monitor.SavePluginState();
                Assert.True(File.Exists(FileUtil.GetPluginStateFile()));
                Assert.Empty(File.ReadAllLines(FileUtil.GetPluginStateFile()));
            }
        }
        finally
        {
            ConfigPersistence.BaseFolderOverride = previousOverride;
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }
}
