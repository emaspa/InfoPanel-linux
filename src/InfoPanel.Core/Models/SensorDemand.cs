using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;

namespace InfoPanel.Models
{
    /// <summary>
    /// Demand-driven sensor polling (issue #9): tracks which sensor ids are actually
    /// referenced by profiles that are being consumed (streamed to a panel, shown as
    /// an overlay, served to the web viewer). Monitors consult this to skip reading
    /// sensors nobody displays, and plugins whose sensors are all unused stop
    /// updating entirely.
    ///
    /// Safety posture: with no provider configured, or while any sensor-browsing UI
    /// is visible (Sensors page, designer sensor tree, dashboard thumbnails), or when
    /// forced (headless --dump-sensors), everything is polled.
    /// </summary>
    public static class SensorDemand
    {
        public sealed record Snapshot(
            HashSet<string> HwmonIds,
            HashSet<string> PluginSensorIds,
            HashSet<string> PluginIds);

        private static Snapshot _current = new([], [], []);
        private static int _uiViewers;
        // Not long.MinValue: TickCount64 - MinValue overflows negative and the
        // once-per-second guard would then skip every rebuild forever.
        private static long _rebuiltAtMs = -1_000_000;

        /// <summary>Set by the host; returns the currently demanded sensor ids.</summary>
        public static Func<Snapshot>? DemandProvider { get; set; }

        /// <summary>Forces full polling (headless --dump-sensors / --render-once).</summary>
        public static bool ForcePollAll { get; set; }

        public static bool PollAll => ForcePollAll || Volatile.Read(ref _uiViewers) > 0 || DemandProvider == null;

        /// <summary>A page that browses live sensors is visible; poll everything.</summary>
        public static void AddUiViewer() => Interlocked.Increment(ref _uiViewers);
        public static void RemoveUiViewer() => Interlocked.Decrement(ref _uiViewers);

        /// <summary>Called from the monitor tick; refreshes the demand set at most once per second.</summary>
        public static void RebuildIfDue()
        {
            var provider = DemandProvider;
            if (provider == null) return;
            var now = Environment.TickCount64;
            if (now - _rebuiltAtMs < 1000) return;
            _rebuiltAtMs = now;
            try
            {
                var previous = _current;
                _current = provider();
                if (_current.HwmonIds.Count != previous.HwmonIds.Count
                    || _current.PluginSensorIds.Count != previous.PluginSensorIds.Count
                    || _current.PluginIds.Count != previous.PluginIds.Count)
                {
                    Serilog.Log.Information(
                        "SensorDemand: polling {Hwmon} hwmon sensor(s), {PluginSensors} plugin sensor(s), {Plugins} plugin(s) directly{All}",
                        _current.HwmonIds.Count, _current.PluginSensorIds.Count, _current.PluginIds.Count,
                        PollAll ? " (full polling active)" : "");
                }
            }
            catch (Exception ex)
            {
                // Keep the previous set; PollAll stays available as the safety net.
                Serilog.Log.Warning(ex, "SensorDemand: rebuild failed, keeping previous demand set");
            }
        }

        public static bool IsHwmonUsed(string sensorId) =>
            PollAll || _current.HwmonIds.Contains(sensorId);

        public static bool IsPluginSensorUsed(string sensorId) =>
            PollAll || _current.PluginSensorIds.Contains(sensorId);

        /// <summary>Plugin ids referenced directly (e.g. plugin-image:// display items).</summary>
        public static bool IsPluginIdUsed(string pluginId) =>
            PollAll || _current.PluginIds.Contains(pluginId);

        public static IReadOnlyCollection<string> UsedPluginSensorIds => _current.PluginSensorIds;
        public static IReadOnlyCollection<string> UsedPluginIds => _current.PluginIds;

        /// <summary>
        /// Collects the demanded sensor ids from the given profiles' display items.
        /// Lives here because the sensor item interfaces are internal to Core.
        /// </summary>
        public static Snapshot Collect(IEnumerable<Profile> profiles, Func<Profile, ImmutableList<DisplayItem>> itemsOf)
        {
            var hwmon = new HashSet<string>();
            var pluginSensors = new HashSet<string>();
            var pluginIds = new HashSet<string>();

            foreach (var profile in profiles)
            {
                foreach (var item in itemsOf(profile))
                {
                    CollectItem(item, hwmon, pluginSensors, pluginIds);
                }
            }

            return new Snapshot(hwmon, pluginSensors, pluginIds);
        }

        private static void CollectItem(DisplayItem item, HashSet<string> hwmon, HashSet<string> pluginSensors, HashSet<string> pluginIds)
        {
            if (item is GroupDisplayItem group)
            {
                foreach (var child in group.DisplayItems.ToList())
                {
                    CollectItem(child, hwmon, pluginSensors, pluginIds);
                }
                return;
            }

            if (item is IPluginSensorItem sensorItem)
            {
                switch (sensorItem.SensorType)
                {
                    case Enums.SensorType.Plugin:
                        if (!string.IsNullOrEmpty(sensorItem.PluginSensorId))
                            pluginSensors.Add(sensorItem.PluginSensorId);
                        break;
                    default:
                        // Hwmon ids ride in LibreSensorId for settings.xml compatibility;
                        // Libre/HwInfo ids resolve through the same hwmon lookup on Linux.
                        if (sensorItem is ISensorItem full && !string.IsNullOrEmpty(full.LibreSensorId))
                            hwmon.Add(full.LibreSensorId);
                        break;
                }
            }

            if (item is ImageDisplayItem image)
            {
                // "plugin-image://{pluginId}/{imageId}" items need their plugin running.
                var path = image.CalculatedPath;
                const string scheme = "plugin-image://";
                if (path != null && path.StartsWith(scheme, StringComparison.Ordinal))
                {
                    var rest = path.AsSpan(scheme.Length);
                    var slash = rest.IndexOf('/');
                    if (slash > 0)
                    {
                        pluginIds.Add(rest[..slash].ToString());
                    }
                }
            }
        }
    }
}
