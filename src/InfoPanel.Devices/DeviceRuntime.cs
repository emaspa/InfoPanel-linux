using InfoPanel.Models;

namespace InfoPanel
{
    /// <summary>
    /// Runtime seams device tasks use to reach application state without referencing
    /// the app layer: profile lookup for rendering and the live Settings object for
    /// device collections/modes. The host wires these at startup.
    /// </summary>
    public static class DeviceRuntime
    {
        public static Func<Guid, Profile?> GetProfile { get; set; } = static _ => null;

        private static Settings? _settings;

        public static Settings Settings
        {
            get => _settings ?? throw new InvalidOperationException("DeviceRuntime.Settings not configured; set it at host startup.");
            set => _settings = value;
        }
    }
}
