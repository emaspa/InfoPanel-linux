using InfoPanel.Plugins;
using InfoPanel.Plugins.Loader;
using InfoPanel.Utils;
using Serilog;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace InfoPanel.Monitors
{
    public class PluginMonitor : BackgroundTask
    {
        private static readonly ILogger Logger = Log.ForContext<PluginMonitor>();
        private static readonly Lazy<PluginMonitor> _instance = new(() => new PluginMonitor());
        public static PluginMonitor Instance => _instance.Value;

        private static long _demandStampMs;
        private static HashSet<string> _demandedPluginIds = [];

        /// <summary>True when any used sensor id belongs to this plugin, the plugin's
        /// images are displayed, or full polling is active (sensor UI visible).</summary>
        internal static bool IsPluginDemanded(string pluginId)
        {
            if (InfoPanel.Models.SensorDemand.PollAll) return true;

            var now = Environment.TickCount64;
            if (now - _demandStampMs >= 1000)
            {
                _demandStampMs = now;
                var set = new HashSet<string>(InfoPanel.Models.SensorDemand.UsedPluginIds);
                foreach (var id in InfoPanel.Models.SensorDemand.UsedPluginSensorIds)
                {
                    if (SENSORHASH.TryGetValue(id, out var reading) && reading.PluginId != null)
                    {
                        set.Add(reading.PluginId);
                    }
                }
                _demandedPluginIds = set;
            }

            return _demandedPluginIds.Contains(pluginId);
        }

        public static readonly ConcurrentDictionary<string, PluginReading> SENSORHASH = new();

        public List<PluginDescriptor> Plugins { get; private set; } = [];

        private PluginMonitor() {
            if(!Directory.Exists(FileUtil.GetExternalPluginFolder()))
            {
                Directory.CreateDirectory(FileUtil.GetExternalPluginFolder());
            }
        }

        public void SavePluginState()
        {
            try
            {
                var deactivatedPlugins = Plugins.Where(p => p.PluginWrappers.All(w => !w.Value.IsRunning)).Select(p => p.FilePath).ToList();
                File.WriteAllLines(FileUtil.GetPluginStateFile(), deactivatedPlugins);
            }
            catch { }
        }

        public string[] GetPluginState()
        {
            if (File.Exists(FileUtil.GetPluginStateFile()))
            {
                try
                {
                    return File.ReadAllLines(FileUtil.GetPluginStateFile());
                }
                catch { }
            }

            return [];
        }

        private void UnzipPluginArchives()
        {
            try
            {
                foreach (var file in Directory.GetFiles(FileUtil.GetExternalPluginFolder(), "InfoPanel.*.zip"))
                {
                    UnzipPluginArchive(file);
                    File.Delete(file);
                }
            }
            catch { }
        }

        private static bool UnzipPluginArchive(string filePath)
        {
            using var fs = new FileStream(filePath, FileMode.Open);
            using var za = new ZipArchive(fs, ZipArchiveMode.Read);
            var entry = za.Entries[0];

            if (!Regex.IsMatch(entry.FullName, "InfoPanel.[a-zA-Z0-9]+\\/"))
            {
                return false;
            }

            //if (Directory.Exists(Path.Combine(FileUtil.GetExternalPluginFolder(), entry.FullName)))
            //{
            //    return false;
            //}

            za.ExtractToDirectory(FileUtil.GetExternalPluginFolder(), true);
            return true;
        }

        protected override async Task DoWorkAsync(CancellationToken token)
        {
            await Task.Delay(300, token);

            try
            {
                var stopwatch = Stopwatch.StartNew();
                //await LoadAllPluginsAsync();
                FindPlugins();

                var deactivatedPlugins = GetPluginState();
                foreach (var descriptor in Plugins)
                {
                    if(deactivatedPlugins.Contains(descriptor.FilePath))
                    {
                        continue;
                    }

                    await StartPluginModulesAsync(descriptor);
                }

                stopwatch.Stop();
                Logger.Information("Plugins loaded in {ElapsedMs}ms", stopwatch.ElapsedMilliseconds);

                while (!token.IsCancellationRequested)
                {
                    stopwatch.Restart();

                    foreach(var pluginDescriptor in Plugins)
                    {
                        foreach(var wrapper in pluginDescriptor.PluginWrappers.Values)
                        {
                            if (wrapper.IsLoaded)
                            {
                                try
                                {
                                    wrapper.Update();
                                }
                                catch { }
                            }
                        }
                    }
                   
                    stopwatch.Stop();
                    //Trace.WriteLine($"Plugins updated: {stopwatch.ElapsedMilliseconds}ms");
                    await Task.Delay(100, token);
                }
            }
            catch (TaskCanceledException)
            {
                Logger.Debug("PluginMonitor task cancelled");
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Exception during PluginMonitor work");
            }
        }

        // Whitelisted bundled plugin folder names (v1 compared full paths with a
        // Windows separator, so bundled plugins never matched on Linux)
        private static readonly string[] _bundledPlugins = ["InfoPanel.Extras", "InfoPanel.AudioSpectrum", "InfoPanel.StopWatch"];
        internal void FindPlugins()
        {
            UnzipPluginArchives();
            //bundled plugins
            var bundledFolder = FileUtil.GetBundledPluginFolder();
            if (Directory.Exists(bundledFolder))
            {
                foreach (var directory in Directory.GetDirectories(bundledFolder))
                {
                    //whitelist plugins
                    if (_bundledPlugins.Contains(Path.GetFileName(directory)))
                    {
                        Plugins.Add(CreatePluginDescriptor(directory));
                    }
                }
            }
            else
            {
                Logger.Warning("Bundled plugin folder {Folder} not found; skipping bundled plugins", bundledFolder);
            }

            //external plugins
            foreach (var directory in Directory.GetDirectories(FileUtil.GetExternalPluginFolder()))
            {
                Plugins.Add(CreatePluginDescriptor(directory));
            }
        }

        internal PluginDescriptor CreatePluginDescriptor(string directory)
        {
            var pluginInfo = PluginLoader.GetPluginInfo(directory);
            //lock plugin convention to <FolderName>.dll
            var pluginFile = Path.Combine(directory, Path.GetFileName(directory) + ".dll");
            var pluginDescriptor = new PluginDescriptor(pluginFile, pluginInfo);

            var plugins = PluginLoader.InitializePlugin(pluginFile);

            foreach (var plugin in plugins)
            {
                PluginWrapper wrapper = new(pluginDescriptor, plugin);
                pluginDescriptor.PluginWrappers.TryAdd(wrapper.Id, wrapper);
            }

            return pluginDescriptor;
        }

        public async Task StopPluginModulesAsync(PluginDescriptor pluginDescriptor)
        {
            foreach (var wrapper in pluginDescriptor.PluginWrappers.Values)
            {
                await ShutdownWrapperAsync(wrapper);
            }
        }

        // ================= per-module enable/disable =================

        private static HashSet<string>? _deactivatedModules;

        private static HashSet<string> DeactivatedModules
        {
            get
            {
                if (_deactivatedModules == null)
                {
                    _deactivatedModules = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    try
                    {
                        if (File.Exists(FileUtil.GetPluginModuleStateFile()))
                        {
                            foreach (var line in File.ReadAllLines(FileUtil.GetPluginModuleStateFile()))
                            {
                                if (line.Trim() is { Length: > 0 } id)
                                {
                                    _deactivatedModules.Add(id);
                                }
                            }
                        }
                    }
                    catch { }
                }

                return _deactivatedModules;
            }
        }

        public bool IsModuleEnabled(PluginWrapper wrapper) => !DeactivatedModules.Contains(wrapper.Id);

        private static void SaveModuleState()
        {
            try
            {
                File.WriteAllLines(FileUtil.GetPluginModuleStateFile(), DeactivatedModules);
            }
            catch { }
        }

        /// <summary>Starts or stops a single module within its package and persists the choice.</summary>
        public async Task SetModuleEnabledAsync(PluginWrapper wrapper, bool enabled)
        {
            if (enabled)
            {
                DeactivatedModules.Remove(wrapper.Id);
                SaveModuleState();
                if (!wrapper.IsRunning)
                {
                    await InitializeWrapperAsync(wrapper);
                }
            }
            else
            {
                DeactivatedModules.Add(wrapper.Id);
                SaveModuleState();
                await ShutdownWrapperAsync(wrapper);
            }
        }

        private async Task InitializeWrapperAsync(PluginWrapper wrapper)
        {
            await wrapper.Initialize();
            Log.Information("Plugin {PluginName} loaded successfully", wrapper.Name);

            // Demand-driven polling: pause this plugin's updates while none of its
            // sensors, tables or images are used by a consumed profile.
            wrapper.UpdateGate = () => IsPluginDemanded(wrapper.Id);

            PluginConfigStore.LoadAndApply(wrapper);
            SetupImageProvider(wrapper);

            int indexOrder = 0;
            foreach (var container in wrapper.PluginContainers)
            {
                foreach (var entry in container.Entries)
                {
                    var id = BuildEntryId(wrapper, container, entry);
                    SENSORHASH[id] = new()
                    {
                        Id = id,
                        Name = entry.Name,
                        ContainerId = container.Id,
                        ContainerName = container.Name,
                        PluginId = wrapper.Id,
                        PluginName = wrapper.Name,
                        Data = entry,
                        IndexOrder = indexOrder++
                    };
                }
            }
        }

        private async Task ShutdownWrapperAsync(PluginWrapper wrapper)
        {
            foreach (var container in wrapper.PluginContainers)
            {
                foreach (var entry in container.Entries)
                {
                    var id = BuildEntryId(wrapper, container, entry);
                    SENSORHASH.TryRemove(id, out _);
                }
            }

            await wrapper.StopAsync();
            TeardownImageProvider(wrapper);
        }

        // ================= plugin image providers (in-proc) =================

        /// <summary>Live image writers per plugin id, for future display-item consumption.</summary>
        public static readonly ConcurrentDictionary<string, Dictionary<string, InfoPanel.Plugins.Graphics.InProcPluginImageWriter>> IMAGEWRITERS = new();

        private static void SetupImageProvider(PluginWrapper wrapper)
        {
            if (wrapper.Plugin is not InfoPanel.Plugins.Graphics.IPluginImageProvider provider)
            {
                return;
            }

            try
            {
                var writers = new Dictionary<string, InfoPanel.Plugins.Graphics.InProcPluginImageWriter>();
                var forPlugin = new Dictionary<string, InfoPanel.Plugins.Graphics.IPluginImageWriter>();
                foreach (var descriptor in provider.ImageDescriptors)
                {
                    var writer = new InfoPanel.Plugins.Graphics.InProcPluginImageWriter(descriptor.Width, descriptor.Height);
                    writers[descriptor.Id] = writer;
                    forPlugin[descriptor.Id] = writer;
                }

                IMAGEWRITERS[wrapper.Id] = writers;
                provider.OnImageBuffersReady(forPlugin);

                // Expose each image as a text sensor carrying its plugin-image:// URI so it
                // appears in the sensor tree and can be added as a live image display item.
                // Container/entry ids match the Windows host for profile compatibility.
                if (provider.ImageDescriptors.Count > 0)
                {
                    var imagesContainer = new PluginContainer($"__images_{wrapper.Id}", "Images");
                    foreach (var descriptor in provider.ImageDescriptors)
                    {
                        imagesContainer.Entries.Add(new PluginText(descriptor.Id, descriptor.Name,
                            $"plugin-image://{wrapper.Id}/{descriptor.Id}"));
                    }
                    wrapper.PluginContainers.Add(imagesContainer);
                }

                Log.Information("Plugin {PluginName}: {Count} image buffer(s) ready", wrapper.Name, writers.Count);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Plugin {PluginName}: image provider setup failed", wrapper.Name);
            }
        }

        private static void TeardownImageProvider(PluginWrapper wrapper)
        {
            if (IMAGEWRITERS.TryRemove(wrapper.Id, out var writers))
            {
                foreach (var writer in writers.Values)
                {
                    writer.Dispose();
                }
            }
        }

        public async Task StartPluginModulesAsync(PluginDescriptor pluginDescriptor)
        {
            foreach (var wrapper in pluginDescriptor.PluginWrappers.Values)
            {
                if (!IsModuleEnabled(wrapper))
                {
                    Log.Information("Plugin {PluginName} is disabled, skipping", wrapper.Name);
                    continue;
                }

                try
                {
                    await InitializeWrapperAsync(wrapper);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Plugin {PluginName} failed to load", wrapper.Name);
                }
            }
        }



        public async Task ReloadPluginModule(PluginWrapper wrapper)
        {
            foreach (var container in wrapper.PluginContainers)
            {
                foreach (var entry in container.Entries)
                {
                    var id = BuildEntryId(wrapper, container, entry);
                    SENSORHASH.TryRemove(id, out _);
                }
            }

            await wrapper.StopAsync();
            TeardownImageProvider(wrapper);

            try
            {
                await wrapper.Initialize();
                Log.Information("Plugin {PluginName} reloaded successfully", wrapper.Name);

                PluginConfigStore.LoadAndApply(wrapper);
                SetupImageProvider(wrapper);

                int indexOrder = 0;
                foreach (var container in wrapper.PluginContainers)
                {
                    foreach (var entry in container.Entries)
                    {
                        var id = BuildEntryId(wrapper, container, entry);
                        SENSORHASH[id] = new()
                        {
                            Id = id,
                            Name = entry.Name,
                            ContainerId = container.Id,
                            ContainerName = container.Name,
                            PluginId = wrapper.Id,
                            PluginName = wrapper.Name,
                            Data = entry,
                            IndexOrder = indexOrder++
                        };
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Plugin {PluginName} failed to load", wrapper.Name);
            }
        }

        private static string BuildEntryId(PluginWrapper wrapper, IPluginContainer container, IPluginData entry)
        {
            if(container.IsEphemeralPath)
            {
                return $"/{wrapper.Id}/{entry.Id}";
            }

            return $"/{wrapper.Id}/{container.Id}/{entry.Id}";
        }

        public static List<PluginReading> GetOrderedList()
        {
            List<PluginReading> OrderedList = [.. SENSORHASH.Values.OrderBy(x => x.IndexOrder)];
            return OrderedList;
        }

        public record struct PluginReading
        {
            public string Id { get; set; }
            public string Name { get; set; }
            public string ContainerId { get; set; }
            public string ContainerName { get; set; }
            public string PluginId { get; set; }
            public string PluginName { get; set; }
            public IPluginData Data { get; set; }
            public int IndexOrder { get; set; }
        }
    }
}
