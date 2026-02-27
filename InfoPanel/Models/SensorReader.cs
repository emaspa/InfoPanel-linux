using InfoPanel.Monitors;
using InfoPanel.Plugins;
using InfoPanel.Services;

namespace InfoPanel.Models
{
    internal class SensorReader
    {
        public static SensorReading? ReadPluginSensor(string sensorId)
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
                }else if(reading.Data is IPluginTable table)
                {
                    return new SensorReading(table.Value, table.DefaultFormat, table.ToString());
                }
            }
            return null;
        }

        public static SensorReading? ReadHwmonSensor(string sensorId)
        {
            if (HwmonMonitor.SENSORHASH.TryGetValue(sensorId, out SensorReading reading))
            {
                return reading;
            }
            return null;
        }
    }
}
