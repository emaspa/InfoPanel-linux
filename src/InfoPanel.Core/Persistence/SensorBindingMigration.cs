using InfoPanel.Enums;
using InfoPanel.Models;
using InfoPanel.Sensors;
using Serilog;

namespace InfoPanel.Persistence;

/// <summary>Migrated contains id rewrites; Enriched contains exact bindings with newly filled hints.</summary>
public sealed record MigrationReport(IReadOnlyList<DisplayItem> Migrated, IReadOnlyList<DisplayItem> Unresolved,
    IReadOnlyList<DisplayItem> Deferred, IReadOnlyList<DisplayItem> Enriched)
{
    public bool HasChanges => Migrated.Count != 0 || Enriched.Count != 0;
}

/// <summary>Owner-thread, in-memory migration. Never saves or removes a binding.</summary>
public static class SensorBindingMigration
{
    private static readonly ILogger Logger = Log.ForContext(typeof(SensorBindingMigration));
    private static readonly HashSet<(Guid Item, string Id)> Warned = [];
    private static readonly Queue<(Guid Item, string Id)> WarningOrder = [];
    private static readonly Lock WarningLock = new();
    internal const int WarningLimit = 4096;
    internal static int WarningCount { get { lock (WarningLock) return Warned.Count; } }

    public static MigrationReport Migrate(IEnumerable<DisplayItem> items)
    {
        List<DisplayItem> migrated = [], unresolved = [], deferred = [], enriched = [];
        foreach (var item in items) Visit(item);
        return new(migrated.AsReadOnly(), unresolved.AsReadOnly(), deferred.AsReadOnly(), enriched.AsReadOnly());

        void Visit(DisplayItem item)
        {
            if (item is GroupDisplayItem group)
            {
                foreach (var child in group.DisplayItemsCopy) Visit(child);
            }
            if (item is not ISensorItem sensor || sensor.SensorType != SensorType.Hwmon
                || string.IsNullOrEmpty(sensor.LibreSensorId)
                || sensor.LibreSensorId.StartsWith("system/", StringComparison.Ordinal)) return;

            var oldId = sensor.LibreSensorId;
            var result = SensorReader.ResolveHwmonSensor(sensor.GetSensorReference());
            switch (result.Status)
            {
                case SensorResolutionStatus.NotReady:
                    deferred.Add(item);
                    break;
                case SensorResolutionStatus.Unresolved:
                case SensorResolutionStatus.Ambiguous:
                    unresolved.Add(item);
                    lock (WarningLock)
                    {
                        if (Warned.Add((item.Guid, oldId)))
                        {
                            WarningOrder.Enqueue((item.Guid, oldId));
                            if (WarningOrder.Count > WarningLimit) Warned.Remove(WarningOrder.Dequeue());
                            Logger.Warning("Hardware sensor binding {ItemGuid} {Id} is {Status}", item.Guid, oldId, result.Status);
                        }
                    }
                    break;
                case SensorResolutionStatus.Resolved when result.Descriptor is { } descriptor:
                    var identity = sensor.HardwareSensorIdentity;
                    if (result.MatchReason == SensorMatchReason.Exact)
                    {
                        if (identity?.ChipName != null && identity.ChannelLabel != null) break;
                        var hints = identity?.Copy() ?? new HardwareSensorIdentity();
                        hints.ChipName ??= descriptor.ChipName;
                        hints.ChannelLabel ??= descriptor.Label;
                        sensor.HardwareSensorIdentity = hints;
                        enriched.Add(item);
                    }
                    else if (result.CanRewrite && !string.IsNullOrEmpty(result.CanonicalId))
                    {
                        var hints = identity?.Copy() ?? new HardwareSensorIdentity();
                        hints.OriginalId ??= oldId;
                        hints.ChipName = descriptor.ChipName;
                        hints.ChannelLabel = descriptor.Label;
                        sensor.HardwareSensorIdentity = hints;
                        sensor.LibreSensorId = result.CanonicalId;
                        migrated.Add(item);
                        Logger.Information("Migrated hardware sensor binding {ItemGuid}: {OldId} → {NewId} ({Reason})",
                            item.Guid, oldId, result.CanonicalId, result.MatchReason);
                    }
                    break;
            }
        }
    }

    internal static void ResetForTests()
    {
        lock (WarningLock)
        {
            Warned.Clear();
            WarningOrder.Clear();
        }
    }
}
