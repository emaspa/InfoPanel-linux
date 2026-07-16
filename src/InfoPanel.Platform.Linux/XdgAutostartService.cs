using Serilog;

namespace InfoPanel.Platform.Linux
{
    /// <summary>XDG autostart via ~/.config/autostart/infopanel.desktop (from InfoPanel-linux@4879aa4).</summary>
    public sealed class XdgAutostartService : IAutostartService
    {
        private static readonly ILogger Logger = Log.ForContext<XdgAutostartService>();

        private static string AutostartDir => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "autostart");

        private static string DesktopFile => Path.Combine(AutostartDir, "infopanel.desktop");

        public bool IsEnabled => File.Exists(DesktopFile);

        public void Apply(bool enabled, int delaySeconds)
        {
            try
            {
                if (!enabled)
                {
                    if (File.Exists(DesktopFile))
                    {
                        File.Delete(DesktopFile);
                        Logger.Information("Autostart disabled");
                    }

                    return;
                }

                Directory.CreateDirectory(AutostartDir);
                var exePath = Environment.ProcessPath ?? "infopanel";
                var content = $"""
                    [Desktop Entry]
                    Type=Application
                    Name=InfoPanel
                    Comment=Hardware monitoring dashboard
                    Exec={exePath}
                    X-GNOME-Autostart-enabled=true
                    X-GNOME-Autostart-Delay={delaySeconds}
                    """;
                File.WriteAllText(DesktopFile, content);
                Logger.Information("Autostart enabled ({Path}, delay {Delay}s)", DesktopFile, delaySeconds);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Failed to update autostart entry");
            }
        }
    }
}
