using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;

namespace InfoPanel.ViewModels
{
    public partial class AboutPageViewModel : ObservableObject
    {
        public string Version { get; }

        public ObservableCollection<InfoLink> InfoLinks { get; } = [];
        public ObservableCollection<ThirdPartyLicense> ThirdPartyLicenses { get; } = [];
        public ObservableCollection<Contributor> Contributors { get; } = [];
        public string ReleaseNotes { get; } = """
            1.4.0 - Initial Linux Release

            Ported from the original Windows InfoPanel to Linux using Avalonia UI.

            Supported features:
            - Hardware monitoring via hwmon/sysfs (CPU, memory, disk, network, power)
            - Intel iGPU monitoring (frequency via sysfs, utilization via PMU perf events)
            - NVIDIA GPU monitoring via NVML (nvidia-smi)
            - AMD GPU monitoring via ROCm SMI
            - Filesystem, CPU frequency, uptime, process count, load average sensors
            - Block I/O and network throughput sensors
            - USB panel support (Turing, BeadaPanel, Thermalright) via libusb
            - Full design editor with sensor browser, property editors, and profiles
            - Plugin system for extensible sensor sources
            - SkiaSharp rendering for display items (text, gauges, charts, images)
            - Built-in web server for remote access
            """;

        public AboutPageViewModel()
        {
            var assembly = Assembly.GetExecutingAssembly();
            var buildTime = File.GetLastWriteTime(assembly.Location);
            Version = $"{assembly.GetName().Version?.ToString(3) ?? "unknown"} Experimental {buildTime:dd MMM yyyy HH:mm}";
            InitializeCollections();
        }

        [RelayCommand]
        private void OpenUrl(string url)
        {
            try
            {
                Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
            }
            catch { }
        }

        private void InitializeCollections()
        {
            InfoLinks.Add(new InfoLink
            {
                Title = "Website",
                Description = "https://infopanel.net",
                ButtonText = "Launch",
                NavigateUri = "https://infopanel.net/"
            });

            InfoLinks.Add(new InfoLink
            {
                Title = "GitHub",
                Description = "Source code for the Linux port.",
                ButtonText = "Open",
                NavigateUri = "https://github.com/emaspa/InfoPanel-linux"
            });

            InfoLinks.Add(new InfoLink
            {
                Title = "Discord",
                Description = "Join in conversations with others regarding InfoPanel.",
                ButtonText = "Join",
                NavigateUri = "https://discord.gg/cQnjdMC7Qc"
            });

            InfoLinks.Add(new InfoLink
            {
                Title = "Reddit",
                Description = "Help grow the /r/InfoPanel community.",
                ButtonText = "Launch",
                NavigateUri = "https://www.reddit.com/r/InfoPanel/"
            });

            InfoLinks.Add(new InfoLink
            {
                Title = "Support Development",
                Description = "Show appreciation and help to offset costs incurred such web and certificate fees.",
                ButtonText = "Donate",
                NavigateUri = "https://www.buymeacoffee.com/urfath3r"
            });

            ThirdPartyLicenses.Add(new ThirdPartyLicense { Name = "TuringSmartScreenLib", License = "MIT License. Copyright (c) 2021 machi_pon.", ProjectUrl = "https://github.com/usausa/turing-smart-screen" });
            ThirdPartyLicenses.Add(new ThirdPartyLicense { Name = "Avalonia", License = "MIT License. Copyright (c) Avalonia Team.", ProjectUrl = "https://github.com/AvaloniaUI/Avalonia" });
            ThirdPartyLicenses.Add(new ThirdPartyLicense { Name = "AutoMapper", License = "MIT License. Copyright (c) 2010 Jimmy Bogard.", ProjectUrl = "https://github.com/AutoMapper/AutoMapper" });
            ThirdPartyLicenses.Add(new ThirdPartyLicense { Name = "BouncyCastle.NetCore", License = "MIT X Consortium License. Copyright (c) The Legion of the Bouncy Castle.", ProjectUrl = "https://www.bouncycastle.org/" });
            ThirdPartyLicenses.Add(new ThirdPartyLicense { Name = "CommunityToolkit.Mvvm", License = "MIT License. Copyright (c) .NET Foundation and Contributors.", ProjectUrl = "https://github.com/CommunityToolkit/dotnet" });
            ThirdPartyLicenses.Add(new ThirdPartyLicense { Name = "Flurl.Http", License = "MIT License. Copyright (c) 2023 Todd Menier.", ProjectUrl = "https://github.com/tmenier/Flurl" });
            ThirdPartyLicenses.Add(new ThirdPartyLicense { Name = "HidSharp", License = "Apache License 2.0. Copyright (c) 2012 James F. Bellinger.", ProjectUrl = "https://www.zer7.com/software/hidsharp" });
            ThirdPartyLicenses.Add(new ThirdPartyLicense { Name = "ini-parser-netstandard", License = "MIT License. Copyright (c) 2008 Ricardo Amores Hernandez.", ProjectUrl = "https://github.com/rickyah/ini-parser" });
            ThirdPartyLicenses.Add(new ThirdPartyLicense { Name = "LibUsbDotNet", License = "LGPL v2 / GPL v2 (dual licensed). Copyright (c) LibUsbDotNet Contributors.", ProjectUrl = "https://github.com/LibUsbDotNet/LibUsbDotNet" });
            ThirdPartyLicenses.Add(new ThirdPartyLicense { Name = "Microsoft.Extensions.*", License = "MIT License. Copyright (c) Microsoft Corporation.", ProjectUrl = "https://github.com/dotnet/runtime" });
            ThirdPartyLicenses.Add(new ThirdPartyLicense { Name = "SecureStore", License = "MIT License. Copyright (c) 2016 Dmitry Lokshin.", ProjectUrl = "https://github.com/dscoduc/SecureStore" });
            ThirdPartyLicenses.Add(new ThirdPartyLicense { Name = "Serilog", License = "Apache License 2.0. Copyright (c) Serilog Contributors.", ProjectUrl = "https://github.com/serilog/serilog" });
            ThirdPartyLicenses.Add(new ThirdPartyLicense { Name = "SkiaSharp", License = "MIT License. Copyright (c) 2015-2016 Xamarin, Inc., Microsoft Corporation.", ProjectUrl = "https://github.com/mono/SkiaSharp" });
            ThirdPartyLicenses.Add(new ThirdPartyLicense { Name = "Svg.Skia", License = "MIT License. Copyright (c) Wieslaw Soltes.", ProjectUrl = "https://github.com/wieslawsoltes/Svg.Skia" });
            ThirdPartyLicenses.Add(new ThirdPartyLicense { Name = "System.IO.Ports", License = "MIT License. Copyright (c) Microsoft Corporation.", ProjectUrl = "https://github.com/dotnet/runtime" });

            Contributors.Add(new Contributor { Name = "habibrehmansg", Description = "Creator of the original InfoPanel for Windows." });
            Contributors.Add(new Contributor { Name = "emaspa", Description = "Linux port and Thermalright panel support." });
            Contributors.Add(new Contributor { Name = "F3NN3X", Description = "For the countless support and awesome plugins." });
            Contributors.Add(new Contributor { Name = "/u/ME5ER", Description = "Special thanks for patiently troubleshooting the early and buggy software iterations over extended periods." });
            Contributors.Add(new Contributor { Name = "/u/DRA6N", Description = "Better known as RobOnTwoWheels our CM on Discord, without whom it would not have existed." });
            Contributors.Add(new Contributor { Name = "Everyone else", Description = "For those that messaged or posted questions, feedback and panel designs on Reddit, HWiNFO forums and Discord." });
        }
    }

    public class InfoLink
    {
        public required string Title { get; set; }
        public required string Description { get; set; }
        public required string ButtonText { get; set; }
        public required string NavigateUri { get; set; }
    }

    public class ThirdPartyLicense
    {
        public required string Name { get; set; }
        public required string License { get; set; }
        public required string ProjectUrl { get; set; }
    }

    public class Contributor
    {
        public required string Name { get; set; }
        public required string Description { get; set; }
    }
}
