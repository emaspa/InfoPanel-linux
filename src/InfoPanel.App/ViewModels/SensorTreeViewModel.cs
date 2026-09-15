using CommunityToolkit.Mvvm.ComponentModel;
using InfoPanel.Enums;
using InfoPanel.Monitors;
using InfoPanel.Plugins;
using InfoPanel.Services;
using InfoPanel.Sensors;
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

        public string ChipName { get; init; } = "";
        public bool IsAmbiguous { get; init; }
        internal string StateKey { get; init; } = "";

        public bool IsSensor => SensorId != null && !IsAmbiguous;
        public string DisplayName => IsAmbiguous ? $"{Name} (ambiguous)" : Name;
        public double DisplayOpacity => IsAmbiguous ? 0.45 : 1;

        partial void OnNameChanged(string value) => OnPropertyChanged(nameof(DisplayName));

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

        public void Rebuild() => Rebuild(HwmonMonitor.GetOrderedList());

        public void Rebuild(IEnumerable<HwmonSensorInfo> sensors)
        {
            var expansion = Walk(Roots).Where(n => n.StateKey.Length > 0)
                .ToDictionary(n => n.StateKey, n => n.IsExpanded, StringComparer.Ordinal);
            var selectedId = SelectedItem?.SensorId;
            var selectedType = SelectedItem?.SensorType;
            Roots.Clear();
            _leavesById.Clear();

            // ---- hwmon ----
            var hardwareRoot = new SensorTreeItem { Name = "Hardware", StateKey = "hardware", IsExpanded = true };
            var devices = sensors.GroupBy(s => string.IsNullOrEmpty(s.ChipKey) ? s.DeviceName : s.ChipKey, StringComparer.Ordinal).ToArray();
            foreach (var deviceGroup in devices)
            {
                var first = deviceGroup.First();
                var sameName = devices.Where(g => g.First().DeviceName == first.DeviceName).ToArray();
                var title = first.DeviceName;
                if (sameName.Length > 1)
                {
                    var anchor = Disambiguator(first);
                    if (sameName.Count(g => Disambiguator(g.First()) == anchor) > 1
                        && SensorId.TryParse(first.SensorId, out var parsed) && parsed!.Secondary is { } secondary)
                        anchor += " / " + string.Join(" / ", secondary.Split('+').Skip(1).Select(SensorId.DecodeToken));
                    title += $" ({anchor})";
                }
                var deviceNode = new SensorTreeItem { Name = title, StateKey = $"h:{deviceGroup.Key}" };
                foreach (var categoryGroup in deviceGroup.GroupBy(s => s.Category))
                {
                    var categoryNode = new SensorTreeItem { Name = categoryGroup.Key, StateKey = $"{deviceNode.StateKey}/{categoryGroup.Key}" };
                    foreach (var sensor in categoryGroup)
                    {
                        var leaf = new SensorTreeItem
                        {
                            Name = sensor.Label,
                            ChipName = sensor.DeviceName,
                            IsAmbiguous = sensor.IsAmbiguous,
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
            var pluginsRoot = new SensorTreeItem { Name = "Plugins", StateKey = "plugins", IsExpanded = true };
            foreach (var pluginGroup in PluginMonitor.SENSORHASH.Values.OrderBy(r => r.IndexOrder).GroupBy(r => r.PluginName))
            {
                var pluginNode = new SensorTreeItem { Name = pluginGroup.Key ?? "Unknown", StateKey = $"p:{pluginGroup.Key}" };
                foreach (var containerGroup in pluginGroup.GroupBy(r => r.ContainerName))
                {
                    var containerNode = new SensorTreeItem { Name = containerGroup.Key ?? "Default", StateKey = $"{pluginNode.StateKey}/{containerGroup.Key}" };
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

            foreach (var node in Walk(Roots))
                if (expansion.TryGetValue(node.StateKey, out var expanded)) node.IsExpanded = expanded;
            SelectedItem = Walk(Roots).FirstOrDefault(n => n.SensorId != null
                && n.SensorId == selectedId && n.SensorType == selectedType);
            RefreshValues();
        }

        private static string Disambiguator(HwmonSensorInfo sensor) =>
            SensorId.TryParse(sensor.SensorId, out var id)
                ? string.Join(" / ", id!.AnchorTokens) : sensor.ChipKey;

        private static IEnumerable<SensorTreeItem> Walk(IEnumerable<SensorTreeItem> nodes)
        {
            foreach (var node in nodes)
            {
                yield return node;
                foreach (var child in Walk(node.Children)) yield return child;
            }
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
                    else leaf.Value = "";
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
