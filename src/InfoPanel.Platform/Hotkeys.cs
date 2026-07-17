using System;

namespace InfoPanel.Platform
{
    [Flags]
    public enum HotkeyModifierMask
    {
        None = 0,
        Shift = 1,
        Control = 2,
        Alt = 4,
        Super = 8,
    }

    /// <summary>
    /// System-wide hotkey registration. Key names use the WPF Key enum vocabulary
    /// ("F5", "A", "D1", "NumPad3", "Left") for settings compatibility with the
    /// Windows build; implementations translate to their native key codes.
    /// </summary>
    public interface IGlobalHotkeyService : IDisposable
    {
        /// <summary>False when the platform can't deliver global hotkeys (e.g. no X display).</summary>
        bool IsAvailable { get; }

        /// <summary>
        /// Human-readable caveat for the current environment (e.g. Wayland focus
        /// limitation), or null when hotkeys are fully global.
        /// </summary>
        string? Limitation { get; }

        /// <summary>Registers a hotkey. Returns false if the key is unknown or the grab failed.</summary>
        bool Register(HotkeyModifierMask modifiers, string keyName, Action callback);

        /// <summary>Removes every registration made through <see cref="Register"/>.</summary>
        void UnregisterAll();
    }
}
