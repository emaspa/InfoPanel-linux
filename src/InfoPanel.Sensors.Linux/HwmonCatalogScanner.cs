using System.Collections.Immutable;
using System.Text.RegularExpressions;
using InfoPanel.Sensors;

namespace InfoPanel.Services;

public sealed record HwmonPollEntry(string StableId, string ValuePath, string RealPath, string Unit, double Divisor);
public sealed record HwmonPollPlan(ImmutableArray<HwmonPollEntry> Entries);
public sealed record HwmonScanResult(SensorCatalogSnapshot Snapshot, HwmonPollPlan PollPlan, bool IsIdentical);

/// <summary>Builds immutable scan candidates without reading a single input value.</summary>
public sealed class HwmonCatalogScanner(SysfsAccess sysfs)
{
    private readonly HwmonIdentity _identity = new(sysfs);
    private static readonly Dictionary<string, (string Category, string Unit, double Divisor)> Channels = new(StringComparer.Ordinal)
    {
        ["temp"] = ("Temperature", "°C", 1000), ["fan"] = ("Fan", "RPM", 1), ["in"] = ("Voltage", "V", 1000),
        ["curr"] = ("Current", "A", 1000), ["power"] = ("Power", "W", 1000000),
        ["freq"] = ("Frequency", "MHz", 1000000), ["humidity"] = ("Humidity", "%", 1000)
    };

    public HwmonScanResult Scan(SensorCatalogSnapshot? previous = null)
    {
        var descriptors = new List<SensorDescriptor>();
        var zones = new List<ThermalZoneIdentity>();
        // Listing failures other than disappearance propagate: the worker retains its old catalog.
        foreach (var path in sysfs.GetDirectories(sysfs.PathUnderRoot("class", "thermal"), "thermal_zone*"))
        {
            if (!Regex.IsMatch(Path.GetFileName(path), @"\Athermal_zone[0-9]+\z")) continue;
            var real = sysfs.ResolvePath(path);
            if (real == null) continue;
            var zone = _identity.Thermal(path, real);
            zones.Add(zone);
            if (!sysfs.FileExists(Path.Combine(path, "temp"))) continue;
            descriptors.Add(new SensorDescriptor(SensorId.Thermal(zone.Identity.Anchor, zone.Identity.Secondary))
            {
                RawChipName = zone.RawType, ChipDisplayName = zone.Identity.Chip, Label = zone.RawType, RawLabel = zone.RawType,
                IsLabelSynthetic = zone.RawType == "unknown", Category = "Temperature", Unit = "°C", Divisor = 1000,
                IdentityStrength = zone.Identity.Strength, LegacyAliases = ["thermal/" + Path.GetFileName(path)],
                ValuePath = Path.Combine(path, "temp"), RealPath = real
            });
        }
        foreach (var path in sysfs.GetDirectories(sysfs.PathUnderRoot("class", "hwmon"), "hwmon*"))
        {
            if (!Regex.IsMatch(Path.GetFileName(path), @"\Ahwmon[0-9]+\z")) continue;
            var real = sysfs.ResolvePath(path);
            if (real == null) continue;
            var rawChip = sysfs.ReadMetadata(Path.Combine(path, "name"));
            if (string.IsNullOrWhiteSpace(rawChip)) rawChip = "unknown";
            var channels = new List<(string Channel, string? Label)>();
            foreach (var file in sysfs.GetFiles(path, "*_input"))
            {
                var channel = Path.GetFileName(file)[..^"_input".Length];
                if (!SensorId.IsValidChannel(channel)) continue;
                var label = sysfs.ReadMetadata(Path.Combine(path, channel + "_label"));
                channels.Add((channel, string.IsNullOrWhiteSpace(label) ? null : label));
            }
            var chipLabel = sysfs.ReadMetadata(Path.Combine(path, "label"));
            var identity = _identity.Derive(real, rawChip, channels, zones, string.IsNullOrWhiteSpace(chipLabel) ? null : chipLabel);
            foreach (var (channel, label) in channels)
            {
                var prefix = Regex.Match(channel, @"\A[a-z]+").Value;
                var (category, unit, divisor) = Channels[prefix];
                descriptors.Add(new SensorDescriptor(SensorId.Hwmon(identity.Chip, identity.Anchor, channel, identity.Secondary))
                {
                    RawChipName = rawChip, ChipDisplayName = identity.Chip, Label = label ?? channel, RawLabel = label,
                    IsLabelSynthetic = label == null, Unit = unit, Category = category, Divisor = divisor,
                    IdentityStrength = identity.Strength, LegacyAliases = [Path.GetFileName(path) + "/" + channel],
                    ValuePath = Path.Combine(path, channel + "_input"), RealPath = real,
                    IsAmbiguous = identity.IsAmbiguous, AmbiguityReason = identity.AmbiguityReason
                });
            }
        }
        var identical = previous?.HasSameDescriptors(descriptors) == true;
        var snapshot = identical ? previous! : new SensorCatalogSnapshot((previous?.Generation ?? 0) + 1, descriptors);
        var plan = new HwmonPollPlan(snapshot.Descriptors.Where(d => !d.IsAmbiguous)
            .Select(d => new HwmonPollEntry(d.StableId, d.ValuePath, d.RealPath, d.Unit, d.Divisor)).ToImmutableArray());
        return new(snapshot, plan, identical);
    }
}
