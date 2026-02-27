using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using InfoPanel.Enums;
using InfoPanel.Models;
using InfoPanel.ViewModels.Components;
using System;

namespace InfoPanel.Views.Components;

public partial class SensorActions : UserControl
{
    public static readonly StyledProperty<TreeItem?> SelectedSensorProperty =
        AvaloniaProperty.Register<SensorActions, TreeItem?>(nameof(SelectedSensor));

    public TreeItem? SelectedSensor
    {
        get => GetValue(SelectedSensorProperty);
        set => SetValue(SelectedSensorProperty, value);
    }

    public static readonly StyledProperty<SensorType> SensorSourceTypeProperty =
        AvaloniaProperty.Register<SensorActions, SensorType>(nameof(SensorSourceType));

    public SensorType SensorSourceType
    {
        get => GetValue(SensorSourceTypeProperty);
        set => SetValue(SensorSourceTypeProperty, value);
    }

    public SensorActions()
    {
        InitializeComponent();
    }

    private Profile? GetProfile() => SharedModel.Instance.SelectedProfile;

    private (string name, string id)? GetSensorInfo()
    {
        var sensor = SelectedSensor;
        if (sensor?.SensorId == null) return null;
        return (sensor.Name, sensor.SensorId);
    }

    private void AddAsText_Click(object? sender, RoutedEventArgs e)
    {
        var info = GetSensorInfo();
        var profile = GetProfile();
        if (info == null || profile == null) return;

        var item = new SensorDisplayItem(info.Value.name, profile)
        {
            SensorType = SensorSourceType,
            LibreSensorId = (SensorSourceType == SensorType.Libre || SensorSourceType == SensorType.Hwmon) ? info.Value.id : "",
            PluginSensorId = SensorSourceType == SensorType.Plugin ? info.Value.id : ""
        };
        SharedModel.Instance.AddDisplayItem(item);
        SharedModel.Instance.SaveDisplayItems();
    }

    private void AddAsBar_Click(object? sender, RoutedEventArgs e)
    {
        var info = GetSensorInfo();
        var profile = GetProfile();
        if (info == null || profile == null) return;

        var item = new BarDisplayItem(info.Value.name, profile)
        {
            SensorType = SensorSourceType,
            LibreSensorId = (SensorSourceType == SensorType.Libre || SensorSourceType == SensorType.Hwmon) ? info.Value.id : "",
            PluginSensorId = SensorSourceType == SensorType.Plugin ? info.Value.id : ""
        };
        SharedModel.Instance.AddDisplayItem(item);
        SharedModel.Instance.SaveDisplayItems();
    }

    private void AddAsDonut_Click(object? sender, RoutedEventArgs e)
    {
        var info = GetSensorInfo();
        var profile = GetProfile();
        if (info == null || profile == null) return;

        var item = new DonutDisplayItem(info.Value.name, profile)
        {
            SensorType = SensorSourceType,
            LibreSensorId = (SensorSourceType == SensorType.Libre || SensorSourceType == SensorType.Hwmon) ? info.Value.id : "",
            PluginSensorId = SensorSourceType == SensorType.Plugin ? info.Value.id : ""
        };
        SharedModel.Instance.AddDisplayItem(item);
        SharedModel.Instance.SaveDisplayItems();
    }

    private void AddAsGraph_Click(object? sender, RoutedEventArgs e)
    {
        var info = GetSensorInfo();
        var profile = GetProfile();
        if (info == null || profile == null) return;

        var item = new GraphDisplayItem(info.Value.name, profile, GraphDisplayItem.GraphType.LINE)
        {
            SensorType = SensorSourceType,
            LibreSensorId = (SensorSourceType == SensorType.Libre || SensorSourceType == SensorType.Hwmon) ? info.Value.id : "",
            PluginSensorId = SensorSourceType == SensorType.Plugin ? info.Value.id : ""
        };
        SharedModel.Instance.AddDisplayItem(item);
        SharedModel.Instance.SaveDisplayItems();
    }

    private void AddAsGauge_Click(object? sender, RoutedEventArgs e)
    {
        var info = GetSensorInfo();
        var profile = GetProfile();
        if (info == null || profile == null) return;

        var item = new GaugeDisplayItem(info.Value.name, profile)
        {
            SensorType = SensorSourceType,
            LibreSensorId = (SensorSourceType == SensorType.Libre || SensorSourceType == SensorType.Hwmon) ? info.Value.id : "",
            PluginSensorId = SensorSourceType == SensorType.Plugin ? info.Value.id : ""
        };
        SharedModel.Instance.AddDisplayItem(item);
        SharedModel.Instance.SaveDisplayItems();
    }

    private void AddAsSensorImage_Click(object? sender, RoutedEventArgs e)
    {
        var info = GetSensorInfo();
        var profile = GetProfile();
        if (info == null || profile == null) return;

        var item = new SensorImageDisplayItem
        {
            SensorType = SensorSourceType,
            SensorName = info.Value.name,
            LibreSensorId = (SensorSourceType == SensorType.Libre || SensorSourceType == SensorType.Hwmon) ? info.Value.id : "",
            PluginSensorId = SensorSourceType == SensorType.Plugin ? info.Value.id : ""
        };
        item.SetProfile(profile);
        item.Name = info.Value.name;
        SharedModel.Instance.AddDisplayItem(item);
        SharedModel.Instance.SaveDisplayItems();
    }

    private void ReplaceSensor_Click(object? sender, RoutedEventArgs e)
    {
        var info = GetSensorInfo();
        if (info == null) return;

        var selected = SharedModel.Instance.SelectedItem;
        if (selected == null) return;

        if (selected is SensorDisplayItem sensorItem)
        {
            sensorItem.SensorName = info.Value.name;
            sensorItem.SensorType = SensorSourceType;
            if (SensorSourceType == SensorType.Libre || SensorSourceType == SensorType.Hwmon)
                sensorItem.LibreSensorId = info.Value.id;
            else if (SensorSourceType == SensorType.Plugin)
                sensorItem.PluginSensorId = info.Value.id;
        }
        else if (selected is ChartDisplayItem chartItem)
        {
            chartItem.SensorName = info.Value.name;
            chartItem.SensorType = SensorSourceType;
            if (SensorSourceType == SensorType.Libre || SensorSourceType == SensorType.Hwmon)
                chartItem.LibreSensorId = info.Value.id;
            else if (SensorSourceType == SensorType.Plugin)
                chartItem.PluginSensorId = info.Value.id;
        }
        else if (selected is GaugeDisplayItem gaugeItem)
        {
            gaugeItem.SensorName = info.Value.name;
            gaugeItem.SensorType = SensorSourceType;
            if (SensorSourceType == SensorType.Libre || SensorSourceType == SensorType.Hwmon)
                gaugeItem.LibreSensorId = info.Value.id;
            else if (SensorSourceType == SensorType.Plugin)
                gaugeItem.PluginSensorId = info.Value.id;
        }

        SharedModel.Instance.SaveDisplayItems();
    }
}
