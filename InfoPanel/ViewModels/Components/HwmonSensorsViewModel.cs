using CommunityToolkit.Mvvm.ComponentModel;
using InfoPanel.Services;
using System.Collections.ObjectModel;
using System.Linq;

namespace InfoPanel.ViewModels.Components;

public partial class HwmonSensorsViewModel : ObservableObject
{
    public ObservableCollection<TreeItem> SensorTree { get; } = [];

    [ObservableProperty]
    private TreeItem? _selectedItem;

    public void Refresh()
    {
        SensorTree.Clear();

        var sensors = HwmonMonitor.GetOrderedList();
        if (sensors.Count == 0) return;

        // Group by device
        var deviceGroups = sensors.GroupBy(s => s.DeviceName);

        foreach (var deviceGroup in deviceGroups)
        {
            var deviceNode = new TreeItem(deviceGroup.Key) { IsExpanded = true };

            // Group by category
            var categoryGroups = deviceGroup.GroupBy(s => s.Category);
            foreach (var categoryGroup in categoryGroups)
            {
                var categoryNode = new TreeItem(categoryGroup.Key);
                foreach (var sensor in categoryGroup)
                {
                    string value = "";
                    if (HwmonMonitor.SENSORHASH.TryGetValue(sensor.SensorId, out var reading))
                    {
                        value = $"{reading.ValueNow:0.#}";
                    }

                    var leaf = new TreeItem(sensor.Label, sensor.SensorId, value, sensor.Unit);
                    categoryNode.Children.Add(leaf);
                }
                deviceNode.Children.Add(categoryNode);
            }

            SensorTree.Add(deviceNode);
        }
    }
}
