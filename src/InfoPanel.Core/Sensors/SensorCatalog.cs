using System.Collections.Frozen;
using System.Collections.Immutable;
using InfoPanel.Enums;

namespace InfoPanel.Sensors;

public enum SensorIdentityStrength { Weak, Location, Strong }
public enum SensorResolutionStatus { NotReady, Resolved, Unresolved, Ambiguous }
public enum SensorMatchReason { Exact, LegacyAlias, Fuzzy }

public sealed record SensorDescriptor(SensorId Id)
{
    public string StableId => Id.Value;
    public string ChipKey => Id.ChipKey;
    public string ChipName => Id.Chip;
    public string Channel => Id.Channel;
    public string RawChipName { get; init; } = Id.Chip;
    public string ChipDisplayName { get; init; } = Id.Chip;
    public string Label { get; init; } = Id.Channel;
    public string? RawLabel { get; init; }
    public bool IsLabelSynthetic { get; init; } = true;
    public string NormalizedLabel => IsLabelSynthetic ? "" : SensorId.NormalizeLabel(Label);
    public string Unit { get; init; } = "";
    public string Category { get; init; } = "";
    public SensorIdentityStrength IdentityStrength { get; init; }
    public ImmutableArray<string> LegacyAliases { get; init; } = [];
    public string? LegacyAlias => LegacyAliases.FirstOrDefault();
    // Opaque discovery locations; Core never accesses them.
    public string ValuePath { get; init; } = "";
    public string RealPath { get; init; } = "";
    public double Divisor { get; init; } = 1;
    public bool IsAmbiguous { get; init; }
    public string? AmbiguityReason { get; init; }
}

public sealed record SensorAmbiguity(string StableId, ImmutableArray<SensorDescriptor> Candidates, string Reason);

/// <summary>All indexes include ambiguity evidence except StableIdIndex, which is bindable only.</summary>
public sealed class SensorCatalogSnapshot
{
    public long Generation { get; }
    public ImmutableArray<SensorDescriptor> Descriptors { get; }
    public FrozenDictionary<string, SensorDescriptor> StableIdIndex { get; }
    public FrozenDictionary<string, ImmutableArray<SensorDescriptor>> LegacyAliasIndex { get; }
    public FrozenDictionary<(string Chip, string Channel), ImmutableArray<SensorDescriptor>> ChipChannelIndex { get; }
    public FrozenDictionary<(string Chip, string Channel, string Label), ImmutableArray<SensorDescriptor>> ChipChannelLabelIndex { get; }
    public FrozenDictionary<(string Source, string Channel), ImmutableArray<SensorDescriptor>> ChannelIndex { get; }
    public FrozenDictionary<(string Source, string Channel, string Label), ImmutableArray<SensorDescriptor>> ChannelLabelIndex { get; }
    public FrozenDictionary<string, ImmutableArray<SensorDescriptor>> StrongAnchorIndex { get; }
    public ImmutableArray<SensorAmbiguity> Ambiguities { get; }
    public FrozenDictionary<string, SensorAmbiguity> AmbiguityIndex { get; }

    public SensorCatalogSnapshot(long generation, IEnumerable<SensorDescriptor> descriptors)
    {
        Generation = generation;
        Descriptors = Prepare(descriptors);
        StableIdIndex = Descriptors.Where(d => !d.IsAmbiguous).ToFrozenDictionary(d => d.StableId, StringComparer.Ordinal);
        LegacyAliasIndex = Descriptors.SelectMany(d => d.LegacyAliases.Select(a => (Alias: a, Descriptor: d)))
            .GroupBy(x => x.Alias, StringComparer.Ordinal).ToFrozenDictionary(g => g.Key, g => g.Select(x => x.Descriptor).ToImmutableArray(), StringComparer.Ordinal);
        ChipChannelIndex = Index(Descriptors, d => (d.ChipName, d.Channel));
        ChipChannelLabelIndex = Index(Descriptors, d => (d.ChipName, d.Channel, d.NormalizedLabel));
        ChannelIndex = Index(Descriptors, d => (d.Id.Source, d.Channel));
        ChannelLabelIndex = Index(Descriptors, d => (d.Id.Source, d.Channel, d.NormalizedLabel));
        StrongAnchorIndex = Descriptors.Where(d => d.Id.HasStrongIdentity).GroupBy(d => d.Id.Anchor, StringComparer.Ordinal)
            .ToFrozenDictionary(g => g.Key, g => g.ToImmutableArray(), StringComparer.Ordinal);
        Ambiguities = Descriptors.Where(d => d.IsAmbiguous).GroupBy(d => d.StableId, StringComparer.Ordinal)
            .Select(g => new SensorAmbiguity(g.Key, g.ToImmutableArray(), g.First().AmbiguityReason ?? "Ambiguous identity")).ToImmutableArray();
        AmbiguityIndex = Ambiguities.ToFrozenDictionary(a => a.StableId, StringComparer.Ordinal);
    }

    /// <summary>Discovery can discard an identical candidate before building any new indexes.</summary>
    public bool HasSameDescriptors(IEnumerable<SensorDescriptor> descriptors)
    {
        var ordered = Prepare(descriptors);
        return ordered.Length == Descriptors.Length && ordered.Zip(Descriptors).All(p =>
            (p.First with { LegacyAliases = [] }) == (p.Second with { LegacyAliases = [] })
            && p.First.LegacyAliases.SequenceEqual(p.Second.LegacyAliases));
    }

    private static ImmutableArray<SensorDescriptor> Prepare(IEnumerable<SensorDescriptor> descriptors) =>
        descriptors.GroupBy(d => d.StableId, StringComparer.Ordinal).SelectMany(g => g.Count() > 1
                ? g.Select(d => d with { IsAmbiguous = true, AmbiguityReason = "Identical full identity tuples" }) : g)
            .OrderBy(d => d.Id.Source, StringComparer.Ordinal).ThenBy(d => d.ChipName, StringComparer.Ordinal)
            .ThenBy(d => d.Id.Anchor, StringComparer.Ordinal).ThenBy(d => d.Id.Secondary, StringComparer.Ordinal)
            .ThenBy(d => d.Channel, StringComparer.Ordinal).ThenBy(d => d.RealPath, StringComparer.Ordinal).ToImmutableArray();

    private static FrozenDictionary<TKey, ImmutableArray<SensorDescriptor>> Index<TKey>(IEnumerable<SensorDescriptor> descriptors,
        Func<SensorDescriptor, TKey> key) where TKey : notnull => descriptors.GroupBy(key).ToFrozenDictionary(g => g.Key, g => g.ToImmutableArray());
}

/// <summary>Value equality covers the complete persisted reference, including every optional hint.</summary>
public readonly record struct SensorReference(string Id, string? SensorName = null, string? ChipName = null,
    string? ChannelLabel = null, string? OriginalId = null, SensorType SourceType = SensorType.Hwmon);

public sealed record SensorResolution(SensorResolutionStatus Status, string? CanonicalId, SensorDescriptor? Descriptor,
    long Generation, SensorMatchReason? MatchReason = null, bool CanRewrite = false);
