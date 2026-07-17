using Avalonia.Controls;
using Avalonia.Interactivity;
using InfoPanel.Persistence;
using System.Diagnostics;
using System.Reflection;

namespace InfoPanel.Views.Pages
{
    public partial class AboutPage : UserControl
    {
        public AboutPage()
        {
            InitializeComponent();

            Loaded += (_, _) => Populate();
        }

        private void Populate()
        {
            if (ContributorsList.Children.Count == 0)
            {
                foreach (var (name, text, github) in new (string, string, string?)[]
                {
                    ("habibrehmansg", "Creator of the original InfoPanel for Windows.", "https://github.com/habibrehmansg"),
                    ("emaspa", "Linux port and Thermalright panel support.", "https://github.com/emaspa"),
                    ("F3NN3X", "For the countless support and awesome plugins.", "https://github.com/F3NN3X"),
                    ("mrZoSo", "For the beta testing.", "https://github.com/mrZoSo"),
                    ("CyberFreek", "Lian Li panel support and weather units.", "https://github.com/CyberFreek"),
                    ("yattuLizard", "VMAX / AuyiHomu panel support.", "https://github.com/yattuLizard"),
                    ("fweepa", "Stopwatch plugin and hotkeys.", "https://github.com/fweepa"),
                    ("Orkunowski", "Designer UX improvements.", "https://github.com/Orkunowski"),
                    ("Everyone else", "For those that messaged or posted questions, feedback and panel designs on Reddit, HWiNFO forums and Discord.", null),
                })
                {
                    if (github != null)
                    {
                        var link = new TextBlock
                        {
                            FontSize = 11,
                            Text = name,
                            TextDecorations = Avalonia.Media.TextDecorations.Underline,
                            Foreground = this.TryFindResource("AccentTextFillColorPrimaryBrush", ActualThemeVariant, out var brush)
                                ? brush as Avalonia.Media.IBrush : Avalonia.Media.Brushes.CornflowerBlue,
                            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
                        };
                        var url = github;
                        link.PointerPressed += (_, _) => OpenUrl(url);

                        var line = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 0 };
                        line.Children.Add(link);
                        line.Children.Add(new TextBlock { FontSize = 11, Text = $": {text}", TextWrapping = Avalonia.Media.TextWrapping.Wrap });
                        ContributorsList.Children.Add(line);
                    }
                    else
                    {
                        ContributorsList.Children.Add(new TextBlock { FontSize = 11, TextWrapping = Avalonia.Media.TextWrapping.Wrap, Text = $"{name}: {text}" });
                    }
                }

                foreach (var (name, license) in new (string, string)[]
                {
                    ("Avalonia + FluentAvalonia", "MIT License."),
                    ("SkiaSharp / Svg.Skia / RichTextKit", "MIT License."),
                    ("CommunityToolkit.Mvvm", "MIT License. (c) .NET Foundation."),
                    ("LibUsbDotNet", "LGPL v2 / GPL v2 (dual licensed)."),
                    ("HidSharp", "Apache License 2.0. (c) James F. Bellinger."),
                    ("TuringSmartScreenLib", "MIT License. (c) machi_pon."),
                    ("BouncyCastle.Cryptography", "MIT X Consortium License."),
                    ("ini-parser-netstandard", "MIT License. (c) Ricardo Amores Hernandez."),
                    ("NAudio", "MIT License. (c) Mark Heath."),
                    ("Serilog", "Apache License 2.0."),
                    ("ffmpeg (runtime dependency)", "LGPL/GPL, system binary, not distributed."),
                })
                {
                    LicensesList.Children.Add(new TextBlock { FontSize = 11, TextWrapping = Avalonia.Media.TextWrapping.Wrap, Text = $"{name}: {license}" });
                }
            }

            var version = Assembly.GetExecutingAssembly().GetName().Version;
            VersionText.Text = $"InfoPanel Linux v{version?.ToString(3) ?? "0.0.1"} alpha";
        }

        private static void OpenUrl(string url)
        {
            try
            {
                Process.Start(new ProcessStartInfo("xdg-open", url) { UseShellExecute = false });
            }
            catch
            {
            }
        }

        private void OpenLink_Click(object? sender, RoutedEventArgs e)
        {
            if ((sender as Button)?.Tag is string url)
            {
                try
                {
                    Process.Start(new ProcessStartInfo("xdg-open", url) { UseShellExecute = false });
                }
                catch
                {
                }
            }
        }

        private void OpenDataFolder_Click(object? sender, RoutedEventArgs e) => OpenFolder(ConfigPersistence.BaseFolder);

        private void OpenLogFolder_Click(object? sender, RoutedEventArgs e) => OpenFolder(Path.Combine(ConfigPersistence.BaseFolder, "logs"));

        private static void OpenFolder(string path)
        {
            try
            {
                Process.Start(new ProcessStartInfo("xdg-open", path) { UseShellExecute = false });
            }
            catch
            {
            }
        }
    }
}
