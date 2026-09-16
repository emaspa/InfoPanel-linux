using InfoPanel.Enums;
using InfoPanel.Models;
using InfoPanel.Sensors;
using Xunit;

namespace InfoPanel.Core.Tests;

[Collection("ConfigPersistence")]
public class SensorIdResolverTests(SensorStateFixture fixture) : SensorStateTest(fixture)
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

    [Theory]
    [InlineData("system/disk/nvme0n1/read_speed", "system/disk/nvme-serial+drive/read_speed")]
    [InlineData("system/block/sda/queue_depth", "system/block/block-wwid+naa.123/queue_depth")]
    [InlineData("system/gpu/temperature", "system/gpu/gpu-uuid+GPU-123~pci+0000-01-00.0/temperature")]
    [InlineData("system/gpu/0/memory_used", "system/gpu/pci+0000-01-00.0/memory_used")]
    [InlineData("system/amdgpu/temperature", "system/amdgpu/pci+0000-03-00.0/temperature")]
    [InlineData("system/amdgpu/1/clock_graphics", "system/amdgpu/pci+0000-04-00.0/clock_graphics")]
    public void SystemAliasesUseCurrentBootUniquenessWithoutLabelEvidence(string alias, string stable)
    {
        var descriptor = new SensorDescriptor(SensorId.Parse(stable)) { LegacyAliases = [alias], Label = "Product name" };
        var other = descriptor with { Id = SensorId.System(descriptor.ChipName, "pci+0000-09-00.0", descriptor.Channel), LegacyAliases = [] };
        var resolver = Resolver(descriptor, other);
        var result = resolver.Resolve(new(alias, "Arbitrary saved label", "Old chip hint"));
        Assert.Equal(stable, result.CanonicalId);
        Assert.Equal(SensorMatchReason.LegacyAlias, result.MatchReason);
        Assert.True(result.CanRewrite);
        var exact = resolver.Resolve(new(stable));
        Assert.Equal(SensorMatchReason.Exact, exact.MatchReason);
        Assert.Same(descriptor, exact.Descriptor);
        Assert.Equal(SensorResolutionStatus.NotReady, new SensorIdResolver().Resolve(new(alias)).Status);
        resolver.Publish(new(2, [descriptor, other with { LegacyAliases = [alias] }]));
        Assert.Equal(SensorResolutionStatus.Unresolved, resolver.Resolve(new(alias)).Status);
        resolver.Publish(new(3, []));
        Assert.Equal(SensorResolutionStatus.Unresolved, resolver.Resolve(new(alias)).Status);
        Assert.Equal(SensorResolutionStatus.Unresolved, resolver.Resolve(new(stable)).Status);
    }

    [Theory]
    [InlineData("system/cpu/total")]
    [InlineData("system/memory/used")]
    [InlineData("system/network/enp1s0/rx_speed")]
    [InlineData("system/load/1min")]
    [InlineData("system/power/BAT0/capacity")]
    [InlineData("system/rapl/package-0/power")]
    [InlineData("system/filesystem/root/used")]
    [InlineData("system/uptime/seconds")]
    [InlineData("system/processes/count")]
    [InlineData("system/cpufreq/core0")]
    [InlineData("system/igpu/utilization")]
    public void OtherSystemKeysKeepExactPassthroughWithoutCatalog(string id)
    {
        var result = new SensorIdResolver().Resolve(new(id));
        Assert.Equal(SensorResolutionStatus.Resolved, result.Status);
        Assert.Equal(SensorMatchReason.Exact, result.MatchReason);
        Assert.Equal(id, result.CanonicalId);
        Assert.False(result.CanRewrite);
        Assert.Null(result.Descriptor);
    }

    [Fact]
    public void StrongSystemRelocationStaysWithinFamilyAndCannotCrossUuid()
    {
        var id = SensorId.System("gpu", "gpu-uuid+GPU-A", "temperature", "pci+0000-01-00.0");
        var moved = new SensorDescriptor(SensorId.System("gpu", id.Anchor, id.Channel, "pci+0000-02-00.0"));
        Assert.Equal(moved.StableId, Resolver(moved).Resolve(new(id.Value)).CanonicalId);
        var wrong = moved with { Id = SensorId.System("gpu", "gpu-uuid+GPU-B", id.Channel) };
        Assert.Equal(SensorResolutionStatus.Unresolved, Resolver(wrong).Resolve(new(id.Value)).Status);
        var otherFamily = moved with { Id = SensorId.System("amdgpu", id.Anchor, id.Channel) };
        Assert.Equal(SensorResolutionStatus.Unresolved, Resolver(otherFamily).Resolve(new(id.Value)).Status);
        Assert.Equal(SensorResolutionStatus.Unresolved, Resolver(moved).Resolve(new("system/gpu/garbage/temperature")).Status);
    }

    [Fact]
    public void MissingLocationAndWeakBindingsCannotMoveToAnotherMatchingDevice()
    {
        foreach (var (anchor, secondary) in new[]
        {
            ("platform+coretemp.0", (string?)null), ("pci+0000-01-00.0", null),
            ("type+acpitz", "acpi+TZ00"), ("name+chip", "meta+original")
        })
        {
            var original = Sensor(anchor: anchor, secondary: secondary);
            var other = original with { Id = SensorId.Hwmon(original.ChipName,
                secondary == null ? "platform+other" : anchor, original.Channel, secondary == null ? null : "meta+other") };
            Assert.Equal(SensorResolutionStatus.Unresolved, Resolver(other).Resolve(new(original.StableId, original.Label)).Status);
            Assert.Equal(original.StableId, Resolver(original, other).Resolve(new(original.StableId)).CanonicalId);
        }
    }

    [Fact]
    public async Task ResolutionCacheStaysBoundedAcrossEditsWithoutARescan()
    {
        var descriptor = Sensor();
        var resolver = Resolver(descriptor);
        await Task.WhenAll(Enumerable.Range(0, 4).Select(worker => Task.Run(() =>
        {
            for (var i = 0; i < SensorIdResolver.CacheLimit; i++)
                resolver.Resolve(new("hwmon4/curr5", $"edited-{worker}-{i}"));
        })));
        Assert.InRange(resolver.CachedReferenceCount, 1, SensorIdResolver.CacheLimit);
        Assert.Equal(descriptor.StableId, resolver.Resolve(new(descriptor.LegacyAlias!, descriptor.Label)).CanonicalId);
        resolver.Publish(new(2, []));
        Assert.Equal(0, resolver.CachedReferenceCount);
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
