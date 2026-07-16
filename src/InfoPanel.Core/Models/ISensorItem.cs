using InfoPanel.Enums;
using System;

namespace InfoPanel.Models
{
    public enum SensorValueType
    {
        NOW, MIN, MAX, AVERAGE
    }

    internal interface ISensorItem: IPluginSensorItem
    {
        string LibreSensorId { get; set; }
    }

    internal interface IPluginSensorItem
    {
        string SensorName { get; set; }
        SensorType SensorType { get; set; }
        SensorValueType ValueType { get; set; }
        SensorReading? GetValue();
        string PluginSensorId { get; set; }
    }
}
