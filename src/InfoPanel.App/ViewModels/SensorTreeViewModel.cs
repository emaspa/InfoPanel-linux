using CommunityToolkit.Mvvm.ComponentModel;
using InfoPanel.Enums;
using InfoPanel.Monitors;
using InfoPanel.Plugins;
using InfoPanel.Services;
using System.Collections.ObjectModel;

namespace InfoPanel.ViewModels
{
    public partial class SensorTreeItem : ObservableObject
    {
        [ObservableProperty]
        private string _name = string.Empty;

        [ObservableProperty]
        private string _value = string.Empty;

        [ObservableProperty]
        private bool _isExpanded;

        public string? SensorId { get; init; }
        public SensorType SensorType { get; init; }
        public string? Unit { get; init; }

        public bool IsSensor => SensorId != null;

        partial void OnValueChanged(string value) => OnPropertyChanged(nameof(DisplayValue));

        public string DisplayValue => IsSensor && Value.Length > 0 ? $"{Value} {Unit}".TrimEnd() : "";

        public ObservableCollection<SensorTreeItem> Children { get; } = [];
    }

    /// <summary>
    /// Unified live sensor tree (hwmon devices + plugins), shared by the designer's
    /// Sensors tab and the Sensors &amp; Plugins page.
    /// </summary>
    public partial class SensorTreeViewModel : ObservableObject
    {
        public ObservableCollection<SensorTreeItem> Roots { get; } = [];

        [ObservableProperty]
        private SensorTreeItem? _selectedItem;

        private readonly Dictionary<string, SensorTreeItem> _leavesById = [];

        public void Rebuild()
        {
            Roots.Clear();
            _leavesById.Clear();

            // ---- hwmon ----
            var hardwareRoot = new SensorTreeItem { Name = "Hardware", IsExpanded = true };
            foreach (var deviceGroup in HwmonMonitor.GetOrderedList().GroupBy(s => s.DeviceName))
            {
                var deviceNode = new SensorTreeItem { Name = deviceGroup.Key };
                foreach (var categoryGroup in deviceGroup.GroupBy(s => s.Category))
                {
                    var categoryNode = new SensorTreeItem { Name = categoryGroup.Key };
                    foreach (var sensor in categoryGroup)
                    {
                        var leaf = new SensorTreeItem
                        {
                            Name = sensor.Label,
                            SensorId = sensor.SensorId,
                            SensorType = SensorType.Hwmon,
                            Unit = sensor.Unit
                        };
                        _leavesById[$"h:{sensor.SensorId}"] = leaf;
                        categoryNode.Children.Add(leaf);
                    }

                    deviceNode.Children.Add(categoryNode);
                }

                hardwareRoot.Children.Add(deviceNode);
            }

            if (hardwareRoot.Children.Count > 0)
            {
                Roots.Add(hardwareRoot);
            }

            // ---- plugins ----
            var pluginsRoot = new SensorTreeItem { Name = "Plugins", IsExpanded = true };
            foreach (var pluginGroup in PluginMonitor.SENSORHASH.Values.OrderBy(r => r.IndexOrder).GroupBy(r => r.PluginName))
            {
                var pluginNode = new SensorTreeItem { Name = pluginGroup.Key ?? "Unknown" };
                foreach (var containerGroup in pluginGroup.GroupBy(r => r.ContainerName))
                {
                    var containerNode = new SensorTreeItem { Name = containerGroup.Key ?? "Default" };
                    foreach (var reading in containerGroup)
                    {
                        var leaf = new SensorTreeItem
                        {
                            Name = reading.Name ?? reading.Id,
                            SensorId = reading.Id,
                            SensorType = SensorType.Plugin,
                            Unit = (reading.Data as IPluginSensor)?.Unit ?? ""
                        };
                        _leavesById[$"p:{reading.Id}"] = leaf;
                        containerNode.Children.Add(leaf);
                    }

                    pluginNode.Children.Add(containerNode);
                }

                pluginsRoot.Children.Add(pluginNode);
            }

            if (pluginsRoot.Children.Count > 0)
            {
                Roots.Add(pluginsRoot);
            }

            RefreshValues();
        }

        /// <summary>Updates leaf values in place (keeps expansion/selection state).</summary>
        public void RefreshValues()
        {
            foreach (var (key, leaf) in _leavesById)
            {
                if (key.StartsWith("h:"))
                {
                    if (HwmonMonitor.SENSORHASH.TryGetValue(leaf.SensorId!, out var reading))
                    {
                        leaf.Value = $"{reading.ValueNow:0.#}";
                    }
                }
                else if (PluginMonitor.SENSORHASH.TryGetValue(leaf.SensorId!, out var pluginReading))
                {
                    leaf.Value = pluginReading.Data switch
                    {
                        IPluginSensor sensor => $"{sensor.Value:0.#}",
                        IPluginText text => text.Value ?? "",
                        _ => ""
                    };
                }
            }
        }
    }
}
