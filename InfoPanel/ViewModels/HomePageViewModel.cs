using Avalonia.Data.Converters;
using CommunityToolkit.Mvvm.ComponentModel;
using InfoPanel.Monitors;
using System;
using System.Globalization;
using System.IO;
using System.Reflection;

namespace InfoPanel.ViewModels
{
    public partial class HomePageViewModel : ObservableObject
    {
        public string Version { get; }
        public int ProfileCount => ConfigModel.Instance.Profiles.Count;
        public int PluginCount => PluginMonitor.Instance.Plugins.Count;
        public bool IsLibreMonitorEnabled => ConfigModel.Instance.Settings.LibreHardwareMonitor;
        public bool IsWebServerEnabled => ConfigModel.Instance.Settings.WebServer;

        public HomePageViewModel()
        {
            var assembly = Assembly.GetExecutingAssembly();
            var buildTime = File.GetLastWriteTime(assembly.Location);
            Version = $"v{assembly.GetName().Version?.ToString(3) ?? "unknown"} Experimental {buildTime:dd MMM yyyy HH:mm}";
        }
    }

    public class BoolToStatusConverter : IValueConverter
    {
        public static readonly BoolToStatusConverter Instance = new();

        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            return value is true ? "Enabled" : "Disabled";
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }
}
