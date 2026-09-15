using InfoPanel.Enums;
using InfoPanel.Models;
using InfoPanel.Sensors;
using Xunit;

namespace InfoPanel.Core.Tests;

public class SensorIdResolverTests
{
    private static SensorDescriptor Sensor(string chip = "wireview", string channel = "curr5", string? label = "Pin 5",
        string alias = "hwmon4/curr5", string anchor = "platform+wireview_hwmon", string? secondary = null) =>
        new(SensorId.Hwmon(chip, anchor, channel, secondary))
        { Label = label ?? channel, RawLabel = label, IsLabelSynthetic = label == null, LegacyAliases = [alias] };
    private static SensorIdResolver Resolver(params SensorDescriptor[] descriptors)
    {
        var resolver = new SensorIdResolver();
        resolver.Publish(new(1, descriptors));
        return resolver;
    }

    [Fact]
    public void NotReadyBeforePublicationAndSystemPassesThrough()
    {
        var resolver = new SensorIdResolver();
        Assert.Equal(SensorResolutionStatus.NotReady, resolver.Resolve(new("hwmon4/curr5")).Status);
        var system = resolver.Resolve(new("system/cpu/total"));
        Assert.Equal(SensorResolutionStatus.Resolved, system.Status);
        Assert.Equal(SensorMatchReason.Exact, system.MatchReason);
        Assert.Equal("system/cpu/total", system.CanonicalId);
    }

    [Fact]
    public void ExactDoesNotDependOnLabelOrReading()
    {
        var descriptor = Sensor();
        var result = Resolver(descriptor).Resolve(new(descriptor.StableId, "obsolete label"));
        Assert.Equal(SensorMatchReason.Exact, result.MatchReason);
        Assert.Same(descriptor, result.Descriptor);
    }

    [Fact]
    public void LegacyRequiresUniqueEvidenceAcrossChipAndChannel()
    {
        var descriptor = Sensor();
        var result = Resolver(descriptor).Resolve(new("hwmon4/curr5", "  PIN   5  "));
        Assert.Equal(SensorMatchReason.LegacyAlias, result.MatchReason);
        Assert.Equal(descriptor.StableId, result.CanonicalId);
    }

    [Fact]
    public void ContradictoryAliasFallsThroughToFuzzy()
    {
        var a = Sensor(label: "Pin 5");
        var b = Sensor(label: "Other", alias: "hwmon8/curr5", anchor: "platform+other");
        var result = Resolver(a, b).Resolve(new("hwmon4/curr5", "Other"));
        Assert.Equal(SensorMatchReason.Fuzzy, result.MatchReason);
        Assert.Equal(b.StableId, result.CanonicalId);
    }

    [Fact]
    public void NoHintAcceptsOnlyUniqueChannel()
    {
        var a = Sensor();
        Assert.Equal(SensorMatchReason.LegacyAlias, Resolver(a).Resolve(new(a.LegacyAlias!)).MatchReason);
        var b = Sensor("other", alias: "hwmon8/curr5", anchor: "platform+other");
        Assert.Equal(SensorResolutionStatus.Unresolved, Resolver(a, b).Resolve(new(a.LegacyAlias!, ChipName: a.ChipName)).Status);
    }

    [Fact]
    public void TwoCompositeNvmeCandidatesAreUnresolved()
    {
        var a = Sensor("nvme", "temp1", "Composite", "hwmon2/temp1", "nvme-serial+A");
        var b = Sensor("nvme", "temp1", "Composite", "hwmon3/temp1", "nvme-serial+B");
        Assert.Equal(SensorResolutionStatus.Unresolved, Resolver(a, b).Resolve(new("hwmon2/temp1", "Composite")).Status);
    }

    [Fact]
    public void FuzzyFindsUniqueSensorAfterRenumbering()
    {
        var a = Sensor(alias: "hwmon9/curr5");
        var result = Resolver(a).Resolve(new("hwmon4/curr5", "Pin 5", "wireview"));
        Assert.Equal(SensorMatchReason.Fuzzy, result.MatchReason);
        Assert.Equal(a.StableId, result.CanonicalId);
    }

    [Theory]
    [InlineData("nvme-serial")][InlineData("block-wwid")][InlineData("block-serial")][InlineData("usb-serial")]
    public void StrongIdentityNeverCrossesSerialButAllowsRelocation(string kind)
    {
        var a = Sensor("nvme", "temp1", "Composite", "hwmon2/temp1", kind + "+A", "pci+0000-01-00.0");
        var b = Sensor("nvme", "temp1", "Composite", "hwmon3/temp1", kind + "+B", "pci+0000-02-00.0");
        Assert.Equal(SensorResolutionStatus.Unresolved, Resolver(b).Resolve(new(a.StableId, "Composite")).Status);
        var relocated = a with { Id = SensorId.Hwmon("nvme", kind + "+A", "temp1", "pci+0000-03-00.0") };
        Assert.Equal(relocated.StableId, Resolver(b, relocated).Resolve(new(a.StableId, "Composite")).CanonicalId);
    }

    [Fact]
    public void WholeReferenceIsCacheKeyAndPublicationInvalidatesBothResults()
    {
        var a = Sensor();
        var b = Sensor(label: "Other", alias: "hwmon9/curr5", anchor: "platform+other");
        var resolver = Resolver(a, b);
        Assert.Equal(a.StableId, resolver.Resolve(new("hwmon4/curr5", "Pin 5")).CanonicalId);
        Assert.Equal(b.StableId, resolver.Resolve(new("hwmon4/curr5", "Other")).CanonicalId);
        Assert.Equal(SensorResolutionStatus.Unresolved, resolver.Resolve(new("hwmon4/curr5", "Missing")).Status);
        resolver.Publish(new(2, [b with { Label = "Missing" }]));
        Assert.Equal(SensorResolutionStatus.Unresolved, resolver.Resolve(new("hwmon4/curr5", "Pin 5")).Status);
        Assert.Equal(b.StableId, resolver.Resolve(new("hwmon4/curr5", "Missing")).CanonicalId);
        Assert.Equal(2, resolver.Resolve(new("hwmon4/curr5", "Missing")).Generation);
    }

    [Fact]
    public void SyntheticLabelsAndDuplicateIdentitiesCannotSupplyEvidence()
    {
        var a = Sensor(channel: "temp1", label: null, alias: "hwmon4/temp1");
        var b = a with { LegacyAliases = ["hwmon9/temp1"], RealPath = "/other" };
        var resolver = Resolver(a, b);
        Assert.Equal(SensorResolutionStatus.Ambiguous, resolver.Resolve(new(a.StableId)).Status);
        Assert.Equal(SensorResolutionStatus.Unresolved, resolver.Resolve(new(a.LegacyAlias!, "temp1")).Status);
        Assert.Empty(resolver.Snapshot!.StableIdIndex);
        Assert.Single(resolver.Snapshot.Ambiguities);
    }

    [Fact]
    public void ThermalLegacyAndUnknownVersionHandling()
    {
        var thermal = new SensorDescriptor(SensorId.Thermal("type+acpitz"))
        { Label = "acpitz", IsLabelSynthetic = false, LegacyAliases = ["thermal/thermal_zone8"] };
        var resolver = Resolver(thermal);
        Assert.Equal(SensorMatchReason.LegacyAlias, resolver.Resolve(new("thermal/thermal_zone8", "acpitz")).MatchReason);
        Assert.Equal(SensorResolutionStatus.Unresolved, resolver.Resolve(new("thermal/v2/type+acpitz/temp", "acpitz")).Status);
        Assert.Equal(SensorResolutionStatus.Unresolved, resolver.Resolve(new(thermal.StableId, SourceType: SensorType.Libre)).Status);
    }

    [Fact]
    public async Task ConcurrentPublicationUsesOneGenerationPerResult()
    {
        var a = Sensor();
        var b = Sensor(anchor: "platform+other");
        var resolver = Resolver(a);
        var writer = Task.Run(() => { for (var i = 2; i <= 500; i++) resolver.Publish(new(i, [i % 2 == 0 ? b : a])); });
        var readers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < 2000; i++)
            {
                var result = resolver.Resolve(new("hwmon4/curr5", "Pin 5"));
                Assert.Equal(result.Generation % 2 == 0 ? b.StableId : a.StableId, result.CanonicalId);
            }
        }));
        await Task.WhenAll(readers.Append(writer));
    }

    [Fact]
    public void ReaderResolvesBeforeUsingExistingSourceAndRejectsRacingPublication()
    {
        var a = Sensor();
        var resolver = Resolver(a);
        SensorReader.ConfigureResolver(resolver);
        string? key = null;
        SensorReader.ConfigureHwmonSource(id => { key = id; return new SensorReading(1, 1, 1, 1, "A"); });
        try
        {
            Assert.NotNull(SensorReader.ReadHwmonSensor(new SensorReference("hwmon4/curr5", "Pin 5")));
            Assert.Equal(a.StableId, key);
            Assert.NotNull(SensorReader.ReadHwmonSensor("system/cpu/total"));
            SensorReader.ConfigureHwmonSource(_ => { resolver.Publish(new(2, [])); return new SensorReading(1, 1, 1, 1, "A"); });
            Assert.Null(SensorReader.ReadHwmonSensor(new SensorReference(a.StableId)));
        }
        finally
        {
            SensorReader.ConfigureResolver(new());
            SensorReader.ConfigureHwmonSource(_ => null);
        }
    }
}
