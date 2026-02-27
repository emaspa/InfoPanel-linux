using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.ObjectModel;
using System.IO;
using System.Reflection;

namespace InfoPanel.ViewModels
{
    public partial class UpdatesPageViewModel : ObservableObject
    {
        public string Version { get; }

        public ObservableCollection<UpdateVersion> UpdateVersions { get; } = [];

        public UpdatesPageViewModel()
        {
            var assembly = Assembly.GetExecutingAssembly();
            var buildTime = File.GetLastWriteTime(assembly.Location);
            Version = $"{assembly.GetName().Version?.ToString(3) ?? "unknown"} Experimental {buildTime:dd MMM yyyy HH:mm}";

            UpdateVersions.Add(new UpdateVersion
            {
                Version = "v1.4.0",
                Expanded = true,
                Title = "Initial Linux Release",
                Items = [
                    new UpdateVersionItem { Title = "Linux Port", Description = [
                        "Ported from the original Windows InfoPanel using Avalonia UI.",
                        "Full design editor with sensor browser, property editors, and profiles.",
                        "SkiaSharp rendering for display items (text, gauges, charts, images).",
                        "Plugin system for extensible sensor sources.",
                        "Built-in web server for remote access."
                    ] },
                    new UpdateVersionItem { Title = "Hardware Monitoring", Description = [
                        "CPU, memory, disk, network, and power monitoring via hwmon/sysfs.",
                        "Filesystem, CPU frequency, uptime, process count, and load average sensors.",
                        "Block I/O and network throughput sensors.",
                        "Intel iGPU monitoring (frequency via sysfs, utilization via PMU perf events).",
                        "NVIDIA GPU monitoring via NVML.",
                        "AMD GPU monitoring via ROCm SMI."
                    ] },
                    new UpdateVersionItem { Title = "USB Panel Support", Description = [
                        "Turing Smart Screen panels via libusb.",
                        "BeadaPanel (NXElec) panels via libusb.",
                        "Thermalright / ChiZhu panels.",
                        "Includes udev rules for non-root device access."
                    ] }
                ]
            });
        }
    }

    public class UpdateVersion
    {
        public required string Version { get; set; }
        public required string Title { get; set; }
        public bool Expanded { get; set; } = false;
        public required ObservableCollection<UpdateVersionItem> Items { get; set; }
    }

    public class UpdateVersionItem
    {
        public required string Title { get; set; }
        public required ObservableCollection<string> Description { get; set; }
    }
}
