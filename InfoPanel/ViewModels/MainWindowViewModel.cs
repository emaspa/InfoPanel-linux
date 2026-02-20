using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using InfoPanel.Views.Pages;
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace InfoPanel.ViewModels
{
    public enum NavigationPage
    {
        Home,
        Profiles,
        Design,
        Updates,
        Plugins,
        UsbPanels,
        Settings,
        Logs,
        About
    }

    public class NavigationItem(string icon, string label, NavigationPage page)
    {
        public string Icon { get; } = icon;
        public string Label { get; } = label;
        public NavigationPage Page { get; } = page;
    }

    public partial class MainWindowViewModel : ObservableObject
    {
        private readonly Dictionary<NavigationPage, UserControl> _pageCache = new();

        [ObservableProperty]
        private NavigationPage _selectedPage = NavigationPage.Home;

        [ObservableProperty]
        private UserControl? _currentPageControl;

        [ObservableProperty]
        private string _title = "InfoPanel";

        [ObservableProperty]
        private bool _isPaneOpen = true;

        public NavigationItem[] TopMenuItems { get; } =
        [
            new("\u2302", "Home", NavigationPage.Home),
            new("\u2630", "Profiles", NavigationPage.Profiles),
            new("\u270E", "Design", NavigationPage.Design),
        ];

        public NavigationItem[] FooterMenuItems { get; } =
        [
            new("\u2191", "Updates", NavigationPage.Updates),
            new("\u2699", "Plugins", NavigationPage.Plugins),
            new("\u2B14", "USB Panels", NavigationPage.UsbPanels),
            new("\u2638", "Settings", NavigationPage.Settings),
            new("\u2263", "Logs", NavigationPage.Logs),
            new("\u24D8", "About", NavigationPage.About),
        ];

        public MainWindowViewModel()
        {
            var assembly = Assembly.GetExecutingAssembly();
            var version = assembly.GetName().Version?.ToString(3);
            if (version != null)
            {
                try
                {
                    var buildTime = File.GetLastWriteTime(assembly.Location);
                    Title = $"InfoPanel - v{version} Experimental {buildTime:dd MMM yyyy HH:mm}";
                }
                catch
                {
                    Title = $"InfoPanel - v{version}";
                }
            }

            IsPaneOpen = ConfigModel.Instance.Settings.IsPaneOpen;

            Navigate(NavigationPage.Home);
        }

        [RelayCommand]
        public void Navigate(NavigationPage page)
        {
            SelectedPage = page;
            CurrentPageControl = GetOrCreatePage(page);
        }

        [RelayCommand]
        private void TogglePane()
        {
            IsPaneOpen = !IsPaneOpen;
        }

        private UserControl GetOrCreatePage(NavigationPage page)
        {
            if (_pageCache.TryGetValue(page, out var cached))
                return cached;

            UserControl control = page switch
            {
                NavigationPage.Home => new HomePage(),
                NavigationPage.Profiles => new ProfilesPage(),
                NavigationPage.Design => new DesignPage(),
                NavigationPage.Updates => new UpdatesPage(),
                NavigationPage.Plugins => new PluginsPage(),
                NavigationPage.UsbPanels => new UsbPanelsPage(),
                NavigationPage.Settings => new SettingsPage(),
                NavigationPage.Logs => new LogsPage(),
                NavigationPage.About => new AboutPage(),
                _ => new HomePage()
            };

            _pageCache[page] = control;
            return control;
        }

        partial void OnIsPaneOpenChanged(bool value)
        {
            ConfigModel.Instance.Settings.IsPaneOpen = value;
        }
    }
}
