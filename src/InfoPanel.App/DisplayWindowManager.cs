using InfoPanel.Models;
using InfoPanel.Views;
using Avalonia.Threading;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;

namespace InfoPanel
{
    public class DisplayWindowManager
    {
        private static readonly ILogger Logger = Log.ForContext<DisplayWindowManager>();
        private static readonly Lazy<DisplayWindowManager> _instance = new(() => new DisplayWindowManager());
        public static DisplayWindowManager Instance => _instance.Value;

        private readonly Dictionary<Guid, DisplayWindow> _windows = new();
        private readonly object _lock = new();

        private DisplayWindowManager() { }

        public void ShowDisplayWindow(Profile profile)
        {
            Dispatcher.UIThread.Post(() =>
            {
                lock (_lock)
                {
                    if (_windows.ContainsKey(profile.Guid))
                    {
                        Logger.Debug("Display window already open for profile {ProfileGuid}", profile.Guid);
                        return;
                    }

                    var window = new DisplayWindow(profile);
                    window.Closed += (_, _) => OnWindowClosed(profile.Guid);
                    _windows[profile.Guid] = window;
                    window.Show();

                    Logger.Debug("Display window opened for profile {ProfileGuid}", profile.Guid);
                }
            });
        }

        private void OnWindowClosed(Guid profileGuid)
        {
            lock (_lock)
            {
                _windows.Remove(profileGuid);
                Logger.Debug("Display window closed for profile {ProfileGuid}", profileGuid);
            }
        }

        public void CloseDisplayWindow(Guid profileGuid)
        {
            Dispatcher.UIThread.Post(() =>
            {
                lock (_lock)
                {
                    if (_windows.TryGetValue(profileGuid, out var window))
                    {
                        _windows.Remove(profileGuid);
                        window.Close();
                        Logger.Debug("Display window close requested for profile {ProfileGuid}", profileGuid);
                    }
                }
            });
        }

        public bool IsWindowOpen(Guid profileGuid)
        {
            lock (_lock)
            {
                return _windows.ContainsKey(profileGuid);
            }
        }

        public void CloseAll()
        {
            Dispatcher.UIThread.Post(() =>
            {
                lock (_lock)
                {
                    foreach (var window in _windows.Values.ToList())
                    {
                        window.Close();
                    }
                    _windows.Clear();
                    Logger.Debug("All display windows closed");
                }
            });
        }
    }
}
