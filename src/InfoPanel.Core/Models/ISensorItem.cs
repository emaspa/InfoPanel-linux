using InfoPanel.Enums;
using InfoPanel.Sensors;
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
        HardwareSensorIdentity? HardwareSensorIdentity { get; set; }
    }

    internal static class SensorItemExtensions
    {
        internal static SensorReference GetSensorReference(this ISensorItem item)
        {
            var identity = item.HardwareSensorIdentity;
            return new(item.LibreSensorId, item.SensorName, identity?.ChipName,
                identity?.ChannelLabel, identity?.OriginalId, item.SensorType);
        }
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
