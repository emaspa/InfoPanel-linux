using CommunityToolkit.Mvvm.ComponentModel;
using InfoPanel.Monitors;
using InfoPanel.Plugins;
using System.Collections.ObjectModel;
using System.Linq;

namespace InfoPanel.ViewModels.Components;

public partial class PluginSensorsViewModel : ObservableObject
{
    public ObservableCollection<TreeItem> SensorTree { get; } = [];

    [ObservableProperty]
    private TreeItem? _selectedItem;

    public void Refresh()
    {
        SensorTree.Clear();

        var sensors = PluginMonitor.SENSORHASH;
        if (sensors.IsEmpty) return;

        // Group by plugin name
        var grouped = sensors.Values
            .OrderBy(r => r.IndexOrder)
            .GroupBy(r => r.PluginName);

        foreach (var pluginGroup in grouped)
        {
            var pluginNode = new TreeItem(pluginGroup.Key ?? "Unknown") { IsExpanded = true };

            // Group by container
            var containerGroups = pluginGroup.GroupBy(r => r.ContainerName);
            foreach (var containerGroup in containerGroups)
            {
                var containerNode = new TreeItem(containerGroup.Key ?? "Default");
                foreach (var reading in containerGroup)
                {
                    string value = "";
                    string unit = "";

                    if (reading.Data is IPluginSensor sensor)
                    {
                        value = $"{sensor.Value:0.#}";
                        unit = sensor.Unit ?? "";
                    }
                    else if (reading.Data is IPluginText text)
                    {
                        value = text.Value ?? "";
                    }

                    var leaf = new TreeItem(reading.Name ?? reading.Id, reading.Id, value, unit);
                    containerNode.Children.Add(leaf);
                }
                pluginNode.Children.Add(containerNode);
            }

            SensorTree.Add(pluginNode);
        }
    }
}
