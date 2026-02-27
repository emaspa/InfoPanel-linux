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
                Version = "v1.3.1",
                Expanded = true,
                Title = "Bug Fixes and Enhancements",
                Items = [
                    new UpdateVersionItem { Title = "USB Panel Improvements", Description = [
                        "Added support for additional TuringPanel models.",
                        "Improved BeadaPanel stability and communication.",
                        "Enhanced device detection and validation."
                    ] },
                    new UpdateVersionItem { Title = "Video Player Enhancements", Description = [
                        "Improved video playback performance."
                    ] },
                    new UpdateVersionItem { Title = "UI & UX Improvements", Description = [
                        "Increased maximum window and profile sizes to 10000x10000 pixels.",
                        "Improved resource cleanup and error logging."
                    ] }
                ]
            });

            UpdateVersions.Add(new UpdateVersion
            {
                Version = "v1.3.0",
                Title = "Video streaming, multiple panel support, and new design tools",
                Items = [
                    new UpdateVersionItem { Title = "Multiple USB Panel Support", Description = [
                        "Connect multiple BeadaPanel AND Turing displays simultaneously.",
                        "Support for latest Turing 8.8\" Rev 1.1 models.",
                        "Each panel works independently with automatic detection."
                    ] },
                    new UpdateVersionItem { Title = "Creative Design Tools", Description = [
                        "Custom shapes with animated gradients and rounded bar charts.",
                        "SVG support for crisp icons and a design grid for alignment.",
                        "Animated live bars for dynamic visualizations."
                    ] },
                    new UpdateVersionItem { Title = "Performance & Reliability", Description = [
                        "New high-performance graphics engine for smoother animations.",
                        "Auto-start delay option and better plugin support."
                    ] }
                ]
            });

            UpdateVersions.Add(new UpdateVersion
            {
                Version = "v1.2.9",
                Title = "Plugins, additional features and bug fixes.",
                Items = [
                    new UpdateVersionItem { Title = "Plugin Support", Description = [
                        "Introduced new plugin support, enabling developers to create custom sensors.",
                        "Includes bundled InfoPanel plugins for features beyond HwInfo & Libre."
                    ] },
                    new UpdateVersionItem { Title = "Display Updates", Description = [
                        "Added width option to text items for length limitation and auto-wrapping.",
                        "Added support for static and animated WebP formats in images.",
                        "Introduced cache option for images to enhance performance."
                    ] }
                ]
            });

            UpdateVersions.Add(new UpdateVersion
            {
                Version = "v1.2.8",
                Title = "Performance & Feature Updates",
                Items = [
                    new UpdateVersionItem { Title = "Enhanced USB LCD Support", Description = [
                        "Added support for Turing (Turzx) 8.8\" LCD.",
                        "Improved LCD stability and performance."
                    ] },
                    new UpdateVersionItem { Title = "Donut Chart", Description = [
                        "Introduced a new customizable circular chart design option."
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
