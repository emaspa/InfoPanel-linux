using InfoPanel.Enums;

namespace InfoPanel.Models;

/// <summary>A complete binding for designer add/replace/undo. Capture and Apply detach metadata.</summary>
public sealed record SensorBinding(SensorType SensorType, string LibreSensorId, string PluginSensorId,
    string SensorName, HardwareSensorIdentity? HardwareSensorIdentity)
{
    public static SensorBinding? Capture(DisplayItem item) => item is IPluginSensorItem sensor
        ? new(sensor.SensorType, (item as ISensorItem)?.LibreSensorId ?? "", sensor.PluginSensorId,
            sensor.SensorName, (item as ISensorItem)?.HardwareSensorIdentity?.Copy())
        : null;

    public static void Apply(DisplayItem item, SensorBinding binding)
    {
        if (item is not IPluginSensorItem sensor) return;
        sensor.SensorType = binding.SensorType;
        sensor.PluginSensorId = binding.PluginSensorId;
        sensor.SensorName = binding.SensorName;
        if (item is ISensorItem hardware)
        {
            hardware.HardwareSensorIdentity = binding.HardwareSensorIdentity?.Copy();
            hardware.LibreSensorId = binding.LibreSensorId;
        }
    }

    public static SensorBinding ForHardware(string stableId, string chipName, string label) =>
        new(SensorType.Hwmon, stableId, "", label,
            new HardwareSensorIdentity { ChipName = chipName, ChannelLabel = label });

    public static SensorBinding ForPlugin(string pluginSensorId, string name) =>
        new(SensorType.Plugin, "", pluginSensorId, name, null);
}
