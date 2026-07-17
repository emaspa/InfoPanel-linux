using InfoPanel.Models;
using InfoPanel.Platform;
using InfoPanel.Utils;
using Serilog;
using System;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;

namespace InfoPanel.Services
{
    /// <summary>
    /// Applies Settings.HotkeyBindings through the platform hotkey service
    /// (the Linux counterpart of the Windows build's GlobalHotkeyService):
    /// each hotkey switches one panel device to a target profile. Re-registers
    /// whenever the collection or any binding changes.
    /// </summary>
    public sealed class HotkeyManager
    {
        private static readonly ILogger Logger = Log.ForContext<HotkeyManager>();

        private readonly Settings _settings;
        private readonly Action _saveSettings;
        private bool _started;

        public HotkeyManager(Settings settings, Action saveSettings)
        {
            _settings = settings;
            _saveSettings = saveSettings;
        }

        public static string? Limitation => PlatformServices.Hotkeys?.Limitation;
        public static bool IsAvailable => PlatformServices.Hotkeys?.IsAvailable == true;

        public void Start()
        {
            if (_started) return;
            _started = true;

            if (!IsAvailable)
            {
                Logger.Information("HotkeyManager: global hotkeys unavailable on this platform");
                return;
            }

            _settings.HotkeyBindings.CollectionChanged += OnBindingsChanged;
            foreach (var binding in _settings.HotkeyBindings)
            {
                binding.PropertyChanged += OnBindingPropertyChanged;
            }

            RegisterAll();
        }

        public void Stop()
        {
            if (!_started) return;
            _started = false;

            _settings.HotkeyBindings.CollectionChanged -= OnBindingsChanged;
            foreach (var binding in _settings.HotkeyBindings)
            {
                binding.PropertyChanged -= OnBindingPropertyChanged;
            }

            PlatformServices.Hotkeys?.UnregisterAll();
        }

        private void OnBindingsChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.OldItems != null)
            {
                foreach (HotkeyBinding binding in e.OldItems) binding.PropertyChanged -= OnBindingPropertyChanged;
            }

            if (e.NewItems != null)
            {
                foreach (HotkeyBinding binding in e.NewItems) binding.PropertyChanged += OnBindingPropertyChanged;
            }

            RegisterAll();
        }

        private void OnBindingPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName is nameof(HotkeyBinding.Key) or nameof(HotkeyBinding.ModifierKeys))
            {
                RegisterAll();
            }
        }

        private void RegisterAll()
        {
            var service = PlatformServices.Hotkeys;
            if (service == null || !service.IsAvailable) return;

            service.UnregisterAll();

            foreach (var binding in _settings.HotkeyBindings)
            {
                if (string.IsNullOrEmpty(binding.Key) || binding.Key == "None") continue;

                var modifiers = HotkeyModifierMask.None;
                if (binding.HasControl) modifiers |= HotkeyModifierMask.Control;
                if (binding.HasAlt) modifiers |= HotkeyModifierMask.Alt;
                if (binding.HasShift) modifiers |= HotkeyModifierMask.Shift;
                if (binding.HasWindows) modifiers |= HotkeyModifierMask.Super;

                // Same rule as the Windows build: require a modifier so bare keys
                // aren't intercepted system-wide.
                if (modifiers == HotkeyModifierMask.None)
                {
                    Logger.Warning("HotkeyManager: skipping {Hotkey} — at least one modifier is required", binding.HotkeyDisplayText);
                    continue;
                }

                var captured = binding;
                if (service.Register(modifiers, binding.Key, () => UiThread.Post(() => ApplyHotkey(captured))))
                {
                    Logger.Debug("HotkeyManager: registered {Hotkey} -> {DeviceType} {DeviceId} -> {Profile}",
                        binding.HotkeyDisplayText, binding.DeviceType, binding.DeviceId, binding.ProfileGuid);
                }
                else
                {
                    Logger.Warning("HotkeyManager: failed to register {Hotkey}", binding.HotkeyDisplayText);
                }
            }
        }

        private void ApplyHotkey(HotkeyBinding binding)
        {
            Logger.Information("Hotkey {Hotkey} pressed: switching {DeviceType} {DeviceId} to profile {Profile}",
                binding.HotkeyDisplayText, binding.DeviceType, binding.DeviceId, binding.ProfileGuid);

            var profile = DeviceRuntime.GetProfile(binding.ProfileGuid);
            if (profile == null)
            {
                Logger.Warning("Hotkey target profile {Profile} not found", binding.ProfileGuid);
                return;
            }

            bool applied = binding.DeviceType switch
            {
                "Beada" => SetProfile(_settings.BeadaPanelDevices.FirstOrDefault(d => d.DeviceId == binding.DeviceId), d => d.ProfileGuid = binding.ProfileGuid),
                "Turing" => SetProfile(_settings.TuringPanelDevices.FirstOrDefault(d => d.DeviceId == binding.DeviceId), d => d.ProfileGuid = binding.ProfileGuid),
                "Thermalright" => SetProfile(_settings.ThermalrightPanelDevices.FirstOrDefault(d => d.DeviceId == binding.DeviceId), d => d.ProfileGuid = binding.ProfileGuid),
                "Thermaltake" => SetProfile(_settings.ThermaltakePanelDevices.FirstOrDefault(d => d.DeviceId == binding.DeviceId), d => d.ProfileGuid = binding.ProfileGuid),
                "Jl" => SetProfile(_settings.JlPanelDevices.FirstOrDefault(d => d.DeviceId == binding.DeviceId), d => d.ProfileGuid = binding.ProfileGuid),
                _ => false,
            };

            if (applied)
            {
                _saveSettings();
            }
            else
            {
                Logger.Warning("Hotkey target device {DeviceType} {DeviceId} not found", binding.DeviceType, binding.DeviceId);
            }
        }

        private static bool SetProfile<T>(T? device, Action<T> apply) where T : class
        {
            if (device == null) return false;
            apply(device);
            return true;
        }
    }
}
