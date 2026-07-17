using InfoPanel.Plugins;
using InfoPanel.Plugins.Loader;
using Xunit;

namespace InfoPanel.Core.Tests
{
    /// <summary>
    /// Gate for the net8-plugin-on-net10-host requirement: the unchanged net8.0
    /// InfoPanel.Extras assembly must load through the isolated PluginLoadContext
    /// and yield IPlugin instances on this net10 host.
    /// </summary>
    public class PluginLoaderSmokeTests
    {
        [Fact]
        public void Net8ExtrasAssembly_LoadsOnNet10Host()
        {
            // Stage a production-like plugin folder: deployed plugins ship WITHOUT
            // InfoPanel.Plugins.dll so the contract assembly resolves from the host's
            // load context (shared type identity). Copying it alongside would split
            // IPlugin identity - exactly what this layout avoids.
            var sourcePath = Path.Combine(AppContext.BaseDirectory, "InfoPanel.Extras.dll");
            Assert.True(File.Exists(sourcePath), $"InfoPanel.Extras.dll not found at {sourcePath}");

            var pluginDir = Path.Combine(Path.GetTempPath(), "infopanel-plugin-smoke-" + Guid.NewGuid());
            Directory.CreateDirectory(pluginDir);
            var extrasPath = Path.Combine(pluginDir, "InfoPanel.Extras.dll");
            File.Copy(sourcePath, extrasPath);

            var plugins = PluginLoader.InitializePlugin(extrasPath).ToList();

            Assert.NotEmpty(plugins);
            Assert.All(plugins, plugin => Assert.IsAssignableFrom<IPlugin>(plugin));
            Assert.Contains(plugins, plugin => plugin.GetType().Name == "ClockPlugin");
        }
    }
}
