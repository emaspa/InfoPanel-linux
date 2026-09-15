using InfoPanel.Sensors;

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
        private static SensorIdResolver _resolver = new();

        public static void ConfigureResolver(SensorIdResolver resolver) => Interlocked.Exchange(ref _resolver, resolver);

        public static SensorResolution ResolveHwmonSensor(SensorReference reference) => Volatile.Read(ref _resolver).Resolve(reference);

        public static SensorReading? ReadHwmonSensor(SensorReference reference)
        {
            var resolver = Volatile.Read(ref _resolver);
            var snapshot = resolver.Snapshot;
            var resolution = resolver.Resolve(reference);
            if (resolution.Status != SensorResolutionStatus.Resolved) return null;
            var reading = ReadHwmonSensor(resolution.CanonicalId!);
            // A removed/replaced endpoint must not return a reading captured across publication.
            return ReferenceEquals(snapshot, resolver.Snapshot) ? reading : null;
        }

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
