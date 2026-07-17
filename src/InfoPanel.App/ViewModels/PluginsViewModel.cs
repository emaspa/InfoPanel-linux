using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using InfoPanel.Extensions;
using InfoPanel.Monitors;
using InfoPanel.Plugins;
using InfoPanel.Plugins.Loader;
using InfoPanel.Utils;
using System;
using System.Collections.ObjectModel;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Input;

namespace InfoPanel.ViewModels
{
    public partial class PluginsPageViewModel : ObservableObject
    {
        private readonly DispatcherTimer _timer;

        [ObservableProperty]
        private string _pluginFolder = FileUtil.GetExternalPluginFolder();

        [ObservableProperty]
        private bool _showRestartBanner = false;

        public ObservableCollection<PluginViewModel> BundledPlugins { get; } = [];
        public ObservableCollection<PluginViewModel> ExternalPlugins { get; } = [];

        public PluginsPageViewModel()
        {
            _timer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _timer.Tick += OnTimerTick;
            _timer.Start();

            BuildPluginModels();
        }

        private void OnTimerTick(object? sender, EventArgs e)
        {
            BuildPluginModels();
        }

        private void BuildPluginModels()
        {
            foreach (var pluginDescriptor in PluginMonitor.Instance.Plugins)
            {
                if (pluginDescriptor.FolderPath?.IsSubdirectoryOf(FileUtil.GetBundledPluginFolder()) ?? false)
                {
                    var model = BundledPlugins.SingleOrDefault(x => x.FilePath == pluginDescriptor.FilePath);

                    if (model != null)
                    {
                        model.Refresh();
                    }
                    else
                    {
                        model = new PluginViewModel(pluginDescriptor);
                        BundledPlugins.Add(model);
                    }
                }
                else
                {
                    var model = ExternalPlugins.SingleOrDefault(x => x.FilePath == pluginDescriptor.FilePath);

                    if (model != null)
                    {
                        model.Refresh();
                    }
                    else
                    {
                        model = new PluginViewModel(pluginDescriptor);
                        ExternalPlugins.Add(model);
                    }
                }
            }
        }

        [RelayCommand]
        public async Task AddPluginFromZip()
        {
            var topLevel = Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
                ? desktop.MainWindow : null;

            if (topLevel == null) return;

            var files = await topLevel.StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
            {
                Title = "Select Plugin Archive",
                AllowMultiple = false,
                FileTypeFilter = [new Avalonia.Platform.Storage.FilePickerFileType("InfoPanel Plugin Archive") { Patterns = ["InfoPanel.*.zip"] }]
            });

            if (files.Count == 0) return;

            var file = files[0];
            var localPath = file.Path.LocalPath;
            if (string.IsNullOrEmpty(localPath)) return;

            using var fs = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var za = new ZipArchive(fs, ZipArchiveMode.Read);
            var entry = za.Entries[0];
            if (Regex.IsMatch(entry.FullName, @"InfoPanel\.[a-zA-Z0-9]+\/"))
            {
                try
                {
                    File.Copy(localPath, Path.Combine(FileUtil.GetExternalPluginFolder(), Path.GetFileName(localPath)), true);
                    ShowRestartBanner = true;
                }
                catch { }
            }
        }
    }

    public partial class PluginModuleViewModel : ObservableObject
    {
        private readonly PluginWrapper _wrapper;
        public string Id { get; set; }

        [ObservableProperty]
        private string _name;

        [ObservableProperty]
        private string _description;

        [ObservableProperty]
        private string? _configFilePath;

        public ObservableCollection<PluginActionCommand> Actions { get; } = [];

        [RelayCommand]
        public async Task Reload()
        {
            await PluginMonitor.Instance.ReloadPluginModule(_wrapper);
        }

        public bool IsConfigurable => _wrapper.Plugin is IPluginConfigurable;

        public ObservableCollection<PluginConfigEntryViewModel> ConfigEntries { get; } = [];

        /// <summary>Set on single-module packages so the merged tile can show version · author.</summary>
        [ObservableProperty]
        private string? _packageInfo;

        public bool HasPackageInfo => !string.IsNullOrEmpty(PackageInfo);

        partial void OnPackageInfoChanged(string? value) => OnPropertyChanged(nameof(HasPackageInfo));

        [ObservableProperty]
        private bool _moduleControlEnabled = true;

        private bool _moduleEnabled;
        public bool ModuleEnabled
        {
            get => _moduleEnabled;
            set
            {
                if (SetProperty(ref _moduleEnabled, value) && !_syncingEnabled)
                {
                    _ = OnModuleEnabledChanged(value);
                }
            }
        }

        private bool _syncingEnabled;

        private async Task OnModuleEnabledChanged(bool enabled)
        {
            ModuleControlEnabled = false;
            try
            {
                await PluginMonitor.Instance.SetModuleEnabledAsync(_wrapper, enabled);
                if (enabled)
                {
                    RebuildConfigEntries();
                }
            }
            finally
            {
                ModuleControlEnabled = true;
            }
        }

        public PluginModuleViewModel(PluginWrapper wrapper)
        {
            _wrapper = wrapper;
            Id = wrapper.Id;
            Name = wrapper.Name;
            Description = wrapper.Description;
            ConfigFilePath = wrapper.ConfigFilePath;

            var methods = wrapper.Plugin.GetType().GetMethods()
                .Where(m => m.GetCustomAttributes(typeof(PluginActionAttribute), false).Length > 0);

            foreach (var method in methods)
            {
                var attribute = (PluginActionAttribute)method.GetCustomAttributes(typeof(PluginActionAttribute), false).First();
                string displayName = attribute.DisplayName;
                var command = new RelayCommand(() => method.Invoke(wrapper.Plugin, null));
                Actions.Add(new PluginActionCommand { DisplayName = displayName, Command = command });
            }

            _syncingEnabled = true;
            _moduleEnabled = PluginMonitor.Instance.IsModuleEnabled(wrapper);
            _syncingEnabled = false;

            RebuildConfigEntries();
        }

        public void Refresh()
        {
            Id = _wrapper.Id;
            Name = _wrapper.Name;
            Description = _wrapper.Description;
            ConfigFilePath = _wrapper.ConfigFilePath;
        }

        /// <summary>
        /// Re-reads ConfigProperties (plugins rebuild the list with current values
        /// after ApplyConfig) and syncs the editor rows.
        /// </summary>
        public void RebuildConfigEntries()
        {
            if (_wrapper.Plugin is not IPluginConfigurable configurable)
            {
                return;
            }

            var props = configurable.ConfigProperties;

            // keep row objects stable so focus isn't lost while typing
            while (ConfigEntries.Count > props.Count) ConfigEntries.RemoveAt(ConfigEntries.Count - 1);
            for (int i = 0; i < props.Count; i++)
            {
                if (i < ConfigEntries.Count)
                {
                    ConfigEntries[i].Sync(props[i]);
                }
                else
                {
                    ConfigEntries.Add(new PluginConfigEntryViewModel(_wrapper, props[i], RebuildConfigEntries));
                }
            }
        }
    }

    /// <summary>One editable config property; applies + persists on change (autosave, like 1.4.x).</summary>
    public partial class PluginConfigEntryViewModel : ObservableObject
    {
        private readonly PluginWrapper _wrapper;
        private readonly Action _refreshAll;
        private bool _syncing;

        public PluginConfigEntryViewModel(PluginWrapper wrapper, PluginConfigProperty property, Action refreshAll)
        {
            _wrapper = wrapper;
            _refreshAll = refreshAll;
            Property = property;
            Sync(property);
        }

        public PluginConfigProperty Property { get; private set; }

        public string DisplayName => Property.DisplayName;
        public string? Description => Property.Description;
        public bool HasDescription => !string.IsNullOrEmpty(Property.Description);

        public bool IsString => Property.Type == PluginConfigType.String;
        public bool IsInteger => Property.Type == PluginConfigType.Integer;
        public bool IsDouble => Property.Type == PluginConfigType.Double;
        public bool IsNumeric => IsInteger || IsDouble;
        public bool IsBoolean => Property.Type == PluginConfigType.Boolean;
        public bool IsChoice => Property.Type == PluginConfigType.Choice;

        public string[] Options => Property.Options ?? [];
        public decimal Minimum => (decimal)(Property.MinValue ?? -1_000_000_000);
        public decimal Maximum => (decimal)(Property.MaxValue ?? 1_000_000_000);
        public decimal Increment => (decimal)(Property.Step ?? (IsInteger ? 1 : 0.1));
        public string NumberFormat => IsInteger ? "0" : "0.###";

        [ObservableProperty]
        private string? _stringValue;

        [ObservableProperty]
        private decimal? _numberValue;

        [ObservableProperty]
        private bool _boolValue;

        [ObservableProperty]
        private string? _choiceValue;

        public void Sync(PluginConfigProperty property)
        {
            _syncing = true;
            try
            {
                Property = property;
                StringValue = property.Value?.ToString();
                ChoiceValue = property.Value?.ToString();
                BoolValue = property.Value is bool b ? b : bool.TryParse(property.Value?.ToString(), out var pb) && pb;
                NumberValue = property.Value switch
                {
                    int i => i,
                    double d => (decimal)d,
                    float f => (decimal)f,
                    long l => l,
                    _ => decimal.TryParse(property.Value?.ToString(), System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var parsed) ? parsed : null,
                };

                OnPropertyChanged(string.Empty); // refresh computed properties
            }
            finally
            {
                _syncing = false;
            }
        }

        partial void OnStringValueChanged(string? value)
        {
            if (!_syncing && IsString) Apply(value);
        }

        partial void OnNumberValueChanged(decimal? value)
        {
            if (!_syncing && IsNumeric && value.HasValue)
            {
                Apply(IsInteger ? (object)(int)value.Value : (object)(double)value.Value);
            }
        }

        partial void OnBoolValueChanged(bool value)
        {
            if (!_syncing && IsBoolean) Apply(value);
        }

        partial void OnChoiceValueChanged(string? value)
        {
            if (!_syncing && IsChoice && value != null) Apply(value);
        }

        private void Apply(object? value)
        {
            PluginConfigStore.Apply(_wrapper, Property.Key, value);
            _refreshAll();
        }
    }

    public class PluginActionCommand
    {
        public required string DisplayName { get; set; }
        public required ICommand Command { get; set; }
    }

    public partial class PluginViewModel : ObservableObject
    {
        private readonly PluginDescriptor _pluginDescriptor;
        public string FilePath { get; set; }

        [ObservableProperty]
        private string _name;

        [ObservableProperty]
        private string? _description;

        [ObservableProperty]
        private string? _author;

        [ObservableProperty]
        private string? _version;

        [ObservableProperty]
        private string? _website;

        private bool _activated;
        public bool Activated
        {
            get => _activated;
            set
            {
                if (SetProperty(ref _activated, value))
                {
                    _ = OnActivatedChanged();
                }
            }
        }

        public ObservableCollection<PluginModuleViewModel> Plugins { get; set; } = [];

        /// <summary>Modules without a config panel - listed inside the package card.</summary>
        public ObservableCollection<PluginModuleViewModel> SimpleModules { get; } = [];

        /// <summary>Modules with a config panel - each rendered as its own tile.</summary>
        public ObservableCollection<PluginModuleViewModel> ConfigurableModules { get; } = [];

        [ObservableProperty]
        private bool _controlEnabled = true;

        private async Task OnActivatedChanged()
        {
            ControlEnabled = false;
            if (!_activated)
            {
                await PluginMonitor.Instance.StopPluginModulesAsync(_pluginDescriptor);
            }
            else
            {
                await PluginMonitor.Instance.StartPluginModulesAsync(_pluginDescriptor);
            }

            PluginMonitor.Instance.SavePluginState();
            ControlEnabled = true;
        }

        public PluginViewModel(PluginDescriptor pluginDescriptor)
        {
            _pluginDescriptor = pluginDescriptor;

            FilePath = pluginDescriptor.FilePath;
            Name = pluginDescriptor.PluginInfo?.Name ?? pluginDescriptor.FolderName ?? pluginDescriptor.FileName;
            Author = pluginDescriptor.PluginInfo?.Author;
            Description = pluginDescriptor.PluginInfo?.Description;
            Version = pluginDescriptor.PluginInfo?.Version;
            Website = pluginDescriptor.PluginInfo?.Website;
            _activated = pluginDescriptor.PluginWrappers.Any(x => x.Value.IsRunning);

            foreach (var wrapper in pluginDescriptor.PluginWrappers.Values)
            {
                AddModule(new PluginModuleViewModel(wrapper));
            }
        }

        /// <summary>
        /// True when the package's only module is configurable - the package card is
        /// hidden and the module tile carries the package identity/toggle instead.
        /// </summary>
        public bool ShowPackageCard => !(Plugins.Count == 1 && Plugins[0].IsConfigurable);

        private void AddModule(PluginModuleViewModel module)
        {
            Plugins.Add(module);
            (module.IsConfigurable ? ConfigurableModules : SimpleModules).Add(module);

            if (Plugins.Count == 1 && module.IsConfigurable)
            {
                module.PackageInfo = $"{Version} · {Author}";
            }
            else if (Plugins.Count > 1)
            {
                foreach (var m in Plugins) m.PackageInfo = null;
            }

            OnPropertyChanged(nameof(ShowPackageCard));
        }

        public void Refresh()
        {
            if (!ControlEnabled) return;

            _activated = _pluginDescriptor.PluginWrappers.Any(x => x.Value.IsRunning);
            OnPropertyChanged(nameof(Activated));

            foreach (var wrapper in _pluginDescriptor.PluginWrappers.Values)
            {
                var plugin = Plugins.SingleOrDefault(x => x.Id == wrapper.Id);
                if (plugin != null)
                {
                    plugin.Refresh();
                }
                else
                {
                    AddModule(new PluginModuleViewModel(wrapper));
                }
            }
        }
    }
}
