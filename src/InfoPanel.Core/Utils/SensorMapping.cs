namespace InfoPanel.Utils
{
    internal class SensorMapping
    {
        public static string? FindMatchingIdentifier(string sensorPanelKey)
        {
            // Sensor mapping was based on LibreHardwareMonitor types (Windows-only).
            // On Linux, hwmon and plugin sensors are used directly.
            return null;
        }
    }
}
