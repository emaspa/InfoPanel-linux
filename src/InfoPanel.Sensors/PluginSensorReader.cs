using InfoPanel.Models;
using InfoPanel.Monitors;
using InfoPanel.Plugins;

namespace InfoPanel.Sensors
{
    /// <summary>
    /// Adapts PluginMonitor readings to Core's SensorReading (v1 SensorReader logic).
    /// Registered into SensorReader.ConfigurePluginSource at host startup.
    /// </summary>
    public static class PluginSensorReader
    {
        public static SensorReading? Read(string sensorId)
        {
            if (PluginMonitor.SENSORHASH.TryGetValue(sensorId, out PluginMonitor.PluginReading reading))
            {
                if (reading.Data is IPluginSensor sensor)
                {
                    return new SensorReading(sensor.ValueMin, sensor.ValueMax, sensor.ValueAvg, sensor.Value, sensor.Unit ?? "");
                }
                else if (reading.Data is IPluginText text)
                {
                    return new SensorReading(text.Value);
                }
                else if (reading.Data is IPluginTable table)
                {
                    return new SensorReading(table.Value, table.DefaultFormat, table.ToString());
                }
            }

            return null;
        }
    }
}
