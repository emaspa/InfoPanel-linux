using Avalonia.Controls;
using System;
using System.IO;
using System.Reflection;

namespace InfoPanel.Views.Pages
{
    public partial class AboutPage : UserControl
    {
        public AboutPage()
        {
            InitializeComponent();

            try
            {
                var assembly = Assembly.GetExecutingAssembly();
                var version = assembly.GetName().Version?.ToString(3) ?? "unknown";
                var buildTime = File.GetLastWriteTime(assembly.Location);
                var versionText = this.FindControl<TextBlock>("VersionText");
                if (versionText != null)
                    versionText.Text = $"Version {version} - Built {buildTime:dd MMM yyyy HH:mm}";
            }
            catch { }
        }
    }
}
