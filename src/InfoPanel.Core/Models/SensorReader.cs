namespace InfoPanel.Models
{
    /// <summary>
    /// Sensor read seam for display items. Sensor backends (plugin monitor, hwmon, and
    /// later Windows providers) register their lookup functions at startup; unregistered
    /// sources simply return no reading.
    /// </summary>
    public static class SensorReader
    {
        private static Func<string, SensorReading?>? _pluginSource;
        private static Func<string, SensorReading?>? _hwmonSource;

        public static void ConfigurePluginSource(Func<string, SensorReading?> source)
        {
            _pluginSource = source;
        }

        public static void ConfigureHwmonSource(Func<string, SensorReading?> source)
        {
            _hwmonSource = source;
        }

        public static SensorReading? ReadPluginSensor(string sensorId)
        {
            return _pluginSource?.Invoke(sensorId);
        }

        public static SensorReading? ReadHwmonSensor(string sensorId)
        {
            return _hwmonSource?.Invoke(sensorId);
        }
    }
}
