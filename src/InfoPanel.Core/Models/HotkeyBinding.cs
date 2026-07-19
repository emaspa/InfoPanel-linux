using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Serialization;

namespace InfoPanel.Models
{
    /// <summary>
    /// A global hotkey that switches one panel device to a target profile.
    ///
    /// Wire-format note: the Windows build serializes WPF's ModifierKeys/Key enums,
    /// which XmlSerializer writes as their member names ("Control, Alt", "F5").
    /// Here both properties are strings carrying that exact text, so settings.xml
    /// round-trips unchanged between platforms - including key names this build
    /// doesn't recognize.
    /// </summary>
    public partial class HotkeyBinding : ObservableObject
    {
        /// <summary>Comma-separated WPF modifier names: Control, Alt, Shift, Windows.</summary>
        [ObservableProperty]
        private string _modifierKeys = "None";

        /// <summary>WPF Key enum member name, e.g. "F5", "A", "D1", "NumPad3".</summary>
        [ObservableProperty]
        private string _key = "None";

        /// <summary>Device type: "Beada", "Turing", "Thermalright", "Thermaltake", "Jl", "Vmax", or "Jonsbo".</summary>
        [ObservableProperty]
        private string _deviceType = string.Empty;

        /// <summary>Stable device identifier (persists across restarts).</summary>
        [ObservableProperty]
        private string _deviceId = string.Empty;

        /// <summary>Device location (used with DeviceId to disambiguate devices).</summary>
        [ObservableProperty]
        private string _deviceLocation = string.Empty;

        /// <summary>Target profile to switch to when the hotkey is pressed.</summary>
        [ObservableProperty]
        private Guid _profileGuid = Guid.Empty;

        public bool HasControl => HasModifier("Control") || HasModifier("Ctrl");
        public bool HasAlt => HasModifier("Alt");
        public bool HasShift => HasModifier("Shift");
        public bool HasWindows => HasModifier("Windows") || HasModifier("Win");

        private bool HasModifier(string name) =>
            ModifierKeys.Split(',').Any(m => m.Trim().Equals(name, StringComparison.OrdinalIgnoreCase));

        [XmlIgnore]
        public string HotkeyDisplayText
        {
            get
            {
                if (string.IsNullOrEmpty(Key) || Key == "None") return "(not set)";
                var parts = new List<string>();
                if (HasControl) parts.Add("Ctrl");
                if (HasAlt) parts.Add("Alt");
                if (HasShift) parts.Add("Shift");
                if (HasWindows) parts.Add("Super");
                parts.Add(Key);
                return string.Join("+", parts);
            }
        }

        partial void OnModifierKeysChanged(string value) => OnPropertyChanged(nameof(HotkeyDisplayText));
        partial void OnKeyChanged(string value) => OnPropertyChanged(nameof(HotkeyDisplayText));
    }
}
