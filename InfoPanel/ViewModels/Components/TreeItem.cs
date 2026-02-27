using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.ObjectModel;

namespace InfoPanel.ViewModels.Components;

public partial class TreeItem : ObservableObject
{
    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _value = string.Empty;

    [ObservableProperty]
    private bool _isExpanded;

    public string? SensorId { get; set; }
    public string? Unit { get; set; }

    public ObservableCollection<TreeItem> Children { get; } = [];

    public TreeItem() { }

    public TreeItem(string name)
    {
        Name = name;
    }

    public TreeItem(string name, string sensorId, string value, string unit)
    {
        Name = name;
        SensorId = sensorId;
        Value = value;
        Unit = unit;
    }
}
