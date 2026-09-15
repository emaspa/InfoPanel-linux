using InfoPanel.Sensors;
using System.Collections.Frozen;
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
            HashSet<string> PluginIds,
            long Generation = 0);

        // The provider may add hotkey plugin demand before returning. Publish only frozen copies.
        private sealed record PublishedSnapshot(FrozenSet<string> HwmonIds, FrozenSet<string> PluginSensorIds,
            FrozenSet<string> PluginIds, long Generation, long InvalidationVersion);

        private static PublishedSnapshot Freeze(Snapshot snapshot, long version) => new(
            snapshot.HwmonIds.ToFrozenSet(StringComparer.Ordinal),
            snapshot.PluginSensorIds.ToFrozenSet(StringComparer.Ordinal),
            snapshot.PluginIds.ToFrozenSet(StringComparer.Ordinal), snapshot.Generation, version);

        private static PublishedSnapshot _current = Freeze(new([], [], []), 0);
        private static readonly Lock RebuildLock = new();
        private static long _invalidationVersion;
        private static int _collectionFailed;
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

        /// <summary>Rebuild on the next tick, bypassing the normal one-second throttle.</summary>
        public static void Invalidate() => Interlocked.Increment(ref _invalidationVersion);

        /// <summary>Called from the monitor tick; catalog changes and invalidation bypass the throttle.</summary>
        public static void RebuildIfDue()
        {
            lock (RebuildLock)
            {
                var provider = DemandProvider;
                if (provider == null) return;
                var now = Environment.TickCount64;
                var version = Volatile.Read(ref _invalidationVersion);
                var previous = Volatile.Read(ref _current);
                if (now - _rebuiltAtMs < 1000 && previous.Generation == SensorReader.HwmonCatalogGeneration
                    && previous.InvalidationVersion == version) return;
                _rebuiltAtMs = now;
                try
                {
                    var current = Freeze(provider(), version);
                    Volatile.Write(ref _current, current);
                    Volatile.Write(ref _collectionFailed, 0);
                    if (current.HwmonIds.Count != previous.HwmonIds.Count
                        || current.PluginSensorIds.Count != previous.PluginSensorIds.Count
                        || current.PluginIds.Count != previous.PluginIds.Count)
                    {
                        Serilog.Log.Information(
                            "SensorDemand: polling {Hwmon} hwmon sensor(s), {PluginSensors} plugin sensor(s), {Plugins} plugin(s) directly{All}",
                            current.HwmonIds.Count, current.PluginSensorIds.Count, current.PluginIds.Count,
                            PollAll ? " (full polling active)" : "");
                    }
                }
                catch (Exception ex)
                {
                    Volatile.Write(ref _collectionFailed, 1);
                    Serilog.Log.Warning(ex, "SensorDemand: rebuild failed, polling all hardware until demand recovers");
                }
            }
        }

        public static bool IsHwmonUsed(string sensorId)
        {
            var current = Volatile.Read(ref _current);
            return PollAll || Volatile.Read(ref _collectionFailed) != 0
                || current.Generation != SensorReader.HwmonCatalogGeneration
                || current.InvalidationVersion != Volatile.Read(ref _invalidationVersion)
                || current.HwmonIds.Contains(sensorId);
        }

        public static bool IsPluginSensorUsed(string sensorId) =>
            PollAll || Volatile.Read(ref _current).PluginSensorIds.Contains(sensorId);

        /// <summary>Plugin ids referenced directly (e.g. plugin-image:// display items).</summary>
        public static bool IsPluginIdUsed(string pluginId) =>
            PollAll || Volatile.Read(ref _current).PluginIds.Contains(pluginId);

        public static IReadOnlyCollection<string> UsedPluginSensorIds => Volatile.Read(ref _current).PluginSensorIds;
        public static IReadOnlyCollection<string> UsedPluginIds => Volatile.Read(ref _current).PluginIds;

        /// <summary>
        /// Collects the demanded sensor ids from the given profiles' display items.
        /// Lives here because the sensor item interfaces are internal to Core.
        /// </summary>
        public static Snapshot Collect(IEnumerable<Profile> profiles, Func<Profile, ImmutableList<DisplayItem>> itemsOf)
        {
            var catalog = SensorReader.HwmonCatalog;
            var generation = catalog?.Generation ?? 0;
            var hwmon = new HashSet<string>(StringComparer.Ordinal);
            var pluginSensors = new HashSet<string>();
            var pluginIds = new HashSet<string>();

            foreach (var profile in profiles)
            {
                foreach (var item in itemsOf(profile))
                {
                    CollectItem(item, hwmon, pluginSensors, pluginIds, ref generation);
                }
            }

            // A publication during traversal must not bless a mixture of generations.
            if (!ReferenceEquals(catalog, SensorReader.HwmonCatalog)) generation = -1;
            return new Snapshot(hwmon, pluginSensors, pluginIds, generation);
        }

        internal static void ResetForTests()
        {
            lock (RebuildLock)
            {
                DemandProvider = null;
                ForcePollAll = false;
                _uiViewers = 0;
                _rebuiltAtMs = -1_000_000;
                _invalidationVersion = 0;
                _collectionFailed = 0;
                Volatile.Write(ref _current, Freeze(new([], [], []), 0));
            }
        }

        private static void CollectItem(DisplayItem item, HashSet<string> hwmon, HashSet<string> pluginSensors, HashSet<string> pluginIds, ref long generation)
        {
            if (item is GroupDisplayItem group)
            {
                foreach (var child in group.DisplayItemsCopy)
                {
                    CollectItem(child, hwmon, pluginSensors, pluginIds, ref generation);
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
                    case Enums.SensorType.Hwmon:
                        if (sensorItem is ISensorItem full && !string.IsNullOrEmpty(full.LibreSensorId))
                        {
                            var resolution = SensorReader.ResolveHwmonSensor(full.GetSensorReference());
                            if (resolution.Generation != generation) generation = -1;
                            if (resolution.Status == SensorResolutionStatus.Resolved && resolution.CanonicalId != null)
                                hwmon.Add(resolution.CanonicalId);
                        }
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
