namespace InfoPanel.Platform
{
    /// <summary>
    /// Foreground application detection (for program-specific profiles).
    /// Implementations return the process name of the currently focused window.
    /// </summary>
    public interface IForegroundAppService
    {
        /// <summary>False when the platform can't observe the foreground window.</summary>
        bool IsAvailable { get; }

        /// <summary>
        /// Human-readable caveat for the current environment (e.g. Wayland only
        /// exposes X11/XWayland windows), or null when detection is complete.
        /// </summary>
        string? Limitation { get; }

        /// <summary>
        /// Gets the process name of the current foreground window (e.g. "steam",
        /// "Cyberpunk2077.exe" for Proton games). Returns null if unavailable.
        /// </summary>
        string? GetForegroundProcessName();
    }
}
