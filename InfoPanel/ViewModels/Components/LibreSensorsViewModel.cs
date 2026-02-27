using CommunityToolkit.Mvvm.ComponentModel;
using InfoPanel.Monitors;
using System.Collections.ObjectModel;
using System.Linq;

namespace InfoPanel.ViewModels.Components;

public partial class LibreSensorsViewModel : ObservableObject
{
    public ObservableCollection<TreeItem> SensorTree { get; } = [];

    [ObservableProperty]
    private TreeItem? _selectedItem;

    public void Refresh()
    {
        SensorTree.Clear();

        var sensors = LibreMonitor.SENSORHASH;
        if (sensors.IsEmpty) return;

        // Group by hardware
        var grouped = sensors.Values
            .OrderBy(s => s.Hardware.HardwareType)
            .ThenBy(s => s.Index)
            .GroupBy(s => s.Hardware.Name);

        foreach (var hwGroup in grouped)
        {
            var hwNode = new TreeItem(hwGroup.Key) { IsExpanded = true };

            // Group by sensor type
            var typeGroups = hwGroup.GroupBy(s => s.SensorType);
            foreach (var typeGroup in typeGroups)
            {
                var typeNode = new TreeItem(typeGroup.Key.ToString());
                foreach (var sensor in typeGroup.OrderBy(s => s.Index))
                {
                    var leaf = new TreeItem(
                        sensor.Name,
                        sensor.Identifier.ToString(),
                        $"{sensor.Value:0.#}",
                        sensor.GetUnit()
                    );
                    typeNode.Children.Add(leaf);
                }
                hwNode.Children.Add(typeNode);
            }

            SensorTree.Add(hwNode);
        }
    }
}
