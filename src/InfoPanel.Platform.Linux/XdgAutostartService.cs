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
                    Exec={BuildExec(exePath, delaySeconds)}
                    X-GNOME-Autostart-enabled=true
                    """;
                File.WriteAllText(DesktopFile, content);
                Logger.Information("Autostart enabled ({Path}, delay {Delay}s)", DesktopFile, delaySeconds);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Failed to update autostart entry");
            }
        }

        /// <summary>
        /// Builds the Exec line. The delay is a shell sleep rather than X-GNOME-Autostart-Delay,
        /// which only GNOME honors: KDE Plasma and other desktops ignored it and started the
        /// app at login with no delay at all (#14).
        /// </summary>
        public static string BuildExec(string exePath, int delaySeconds)
        {
            // Inside a double-quoted Exec argument, ", `, $ and \ need escaping that also
            // interacts with desktop-file string escapes; a path with any of those (or a
            // single quote, used below) gets no delay rather than a broken Exec line.
            bool quotable = exePath.IndexOfAny(['"', '`', '$', '\\', '\'', '\n', '\r']) < 0;

            if (delaySeconds <= 0 || !quotable)
            {
                return quotable && exePath.Contains(' ') ? $"\"{exePath}\"" : exePath;
            }

            return $"sh -c \"sleep {delaySeconds}; exec '{exePath}'\"";
        }
    }
}
