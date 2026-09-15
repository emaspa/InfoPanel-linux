using System.Collections.Concurrent;
using System.Collections.Immutable;
using InfoPanel.Enums;

namespace InfoPanel.Sensors;

/// <summary>One captured publication and cache per lookup; publication invalidates positive and negative results atomically.</summary>
public sealed class SensorIdResolver
{
    private sealed class Publication(SensorCatalogSnapshot snapshot)
    {
        public SensorCatalogSnapshot Snapshot { get; } = snapshot;
        public ConcurrentDictionary<SensorReference, SensorResolution> Cache { get; } = new();
    }
    private Publication? _current;
    public SensorCatalogSnapshot? Snapshot => Volatile.Read(ref _current)?.Snapshot;
    public void Publish(SensorCatalogSnapshot snapshot) => Interlocked.Exchange(ref _current, new Publication(snapshot));

    public SensorResolution Resolve(SensorReference reference)
    {
        var publication = Volatile.Read(ref _current);
        if (reference.SourceType == SensorType.Hwmon && reference.Id?.StartsWith("system/", StringComparison.Ordinal) == true)
            return new(SensorResolutionStatus.Resolved, reference.Id, null, publication?.Snapshot.Generation ?? 0, SensorMatchReason.Exact);
        if (publication == null) return new(SensorResolutionStatus.NotReady, null, null, 0);
        return publication.Cache.GetOrAdd(reference, r => Resolve(r, publication.Snapshot));
    }

    private static SensorResolution Resolve(SensorReference reference, SensorCatalogSnapshot catalog)
    {
        SensorResolution Missing(SensorResolutionStatus status = SensorResolutionStatus.Unresolved) => new(status, null, null, catalog.Generation);
        SensorResolution Found(SensorDescriptor d, SensorMatchReason reason) =>
            new(SensorResolutionStatus.Resolved, d.StableId, d, catalog.Generation, reason, true);
        if (reference.SourceType != SensorType.Hwmon || string.IsNullOrEmpty(reference.Id)) return Missing();
        if (catalog.StableIdIndex.TryGetValue(reference.Id, out var exact)) return Found(exact, SensorMatchReason.Exact);
        if (catalog.AmbiguityIndex.ContainsKey(reference.Id)) return Missing(SensorResolutionStatus.Ambiguous);

        var stable = SensorId.TryParse(reference.Id, out var parsed);
        var legacy = SensorId.IsLegacy(reference.Id);
        if (!stable && !legacy) return Missing(); // Unknown versions and foreign ids must never be guessed.
        var source = stable ? parsed!.Source : reference.Id.StartsWith("thermal/", StringComparison.Ordinal) ? "thermal" : "hwmon";
        var channel = stable ? parsed!.Channel : source == "thermal" ? "temp" : reference.Id.Split('/')[1];
        var chip = stable ? parsed!.Chip : string.IsNullOrWhiteSpace(reference.ChipName) ? null : SensorId.NormalizeChipName(reference.ChipName);
        var label = SensorId.NormalizeLabel(reference.ChannelLabel ?? reference.SensorName);
        if (label == SensorId.NormalizeLabel(channel)) label = ""; // Synthetic labels supply no evidence.

        ImmutableArray<SensorDescriptor> Candidates(string? chipHint, string labelHint)
        {
            if (chipHint != null)
                return labelHint.Length > 0 ? catalog.ChipChannelLabelIndex.GetValueOrDefault((chipHint, channel, labelHint), [])
                    : catalog.ChipChannelIndex.GetValueOrDefault((chipHint, channel), []);
            return labelHint.Length > 0 ? catalog.ChannelLabelIndex.GetValueOrDefault((source, channel, labelHint), [])
                : catalog.ChannelIndex.GetValueOrDefault((source, channel), []);
        }

        if (legacy && catalog.LegacyAliasIndex.TryGetValue(reference.Id, out var aliases))
        {
            // The current alias supplies a chip hint, never uniqueness by itself. With no label,
            // scope decision 8 requires a unique channel across the entire namespace.
            foreach (var alias in aliases)
            {
                if (chip != null && alias.ChipName != chip) continue;
                var evidence = Candidates(label.Length == 0 ? null : chip ?? alias.ChipName, label)
                    .Where(d => d.Id.Source == source).ToArray();
                if (evidence.Length == 1 && !evidence[0].IsAmbiguous && evidence[0] == alias)
                    return Found(alias, SensorMatchReason.LegacyAlias);
            }
        }

        var candidates = Candidates(chip, label).Where(d => d.Id.Source == source
            && (!stable || !parsed!.HasStrongIdentity || d.Id.Anchor == parsed.Anchor)).ToArray();
        // An ambiguous descriptive match remains unresolved; only an explicit colliding identity
        // reports Ambiguous. Neither outcome has a canonical key.
        if (candidates.Length != 1 || candidates[0].IsAmbiguous) return Missing();
        // A legacy reference without a real label cannot evade the unique-channel evidence rule.
        if (legacy && label.Length == 0 && catalog.ChannelIndex.GetValueOrDefault((source, channel), []).Length != 1) return Missing();
        return Found(candidates[0], SensorMatchReason.Fuzzy);
    }
}
