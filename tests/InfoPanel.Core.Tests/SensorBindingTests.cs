using InfoPanel.Enums;
using InfoPanel.Models;
using InfoPanel.Persistence;
using System.Xml.Linq;
using System.Xml.Serialization;
using Xunit;

namespace InfoPanel.Core.Tests;

[Collection("ConfigPersistence")]
public sealed class SensorBindingTests(SensorStateFixture fixture) : SensorStateTest(fixture)
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void BoundHardwareGaugeHasNoMinimumFrameWhenUnavailableAndRecovers(int imageCount)
    {
        var resolver = SensorStateFixture.PublishWireView();
        var gauge = new GaugeDisplayItem { LibreSensorId = SensorStateFixture.WireViewId, SensorType = SensorType.Hwmon };
        for (var i = 0; i < imageCount; i++) gauge.Images.Add(new ImageDisplayItem());
        SensorReader.ConfigureHwmonSource(_ => null);
        gauge.EvaluateImageFrame(out var a, out var b, out _);
        Assert.Null(a);
        Assert.Null(b);
        SensorReader.ConfigureHwmonSource(_ => new(0, 0, 0, 0, "A"));
        gauge.EvaluateImageFrame(out a, out _, out _);
        Assert.Same(gauge.Images[0], a);
        resolver.Publish(new(2, []));
        gauge.EvaluateImageFrame(out a, out _, out _);
        Assert.Null(a);
        gauge.SensorType = SensorType.Plugin;
        gauge.EvaluateImageFrame(out a, out _, out _);
        Assert.Same(gauge.Images[0], a); // existing plugin placeholder behavior
    }

    public static TheoryData<string> HardwareFamilies => new()
        { "text", "gauge", "graph", "bar", "donut", "image", "http" };

    internal static DisplayItem Item(string kind) => kind switch
    {
        "text" => new SensorDisplayItem(), "gauge" => new GaugeDisplayItem(),
        "graph" => new GraphDisplayItem(), "bar" => new BarDisplayItem(), "donut" => new DonutDisplayItem(),
        "image" => new SensorImageDisplayItem(), "http" => new HttpImageDisplayItem(),
        _ => throw new ArgumentException(kind)
    };

    [Theory, MemberData(nameof(HardwareFamilies))]
    public void XmlRoundTripIncludesOnlyThreeOptionalIdentityChildren(string kind)
    {
        var item = Item(kind);
        var sensor = (ISensorItem)item;
        sensor.HardwareSensorIdentity = new() { ChipName = "chip & name", ChannelLabel = "Label <5>", OriginalId = "hwmon4/curr5" };
        var xml = Serialize(item);
        var identity = XDocument.Parse(xml).Descendants("HardwareSensorIdentity").Single();
        Assert.Equal(new[] { "ChipName", "ChannelLabel", "OriginalId" }, identity.Elements().Select(e => e.Name.LocalName));
        var reloaded = (ISensorItem)Deserialize(xml);
        Assert.Equal("chip & name", reloaded.HardwareSensorIdentity!.ChipName);
        Assert.Equal("Label <5>", reloaded.HardwareSensorIdentity.ChannelLabel);
        Assert.Equal("hwmon4/curr5", reloaded.HardwareSensorIdentity.OriginalId);

        sensor.HardwareSensorIdentity = new() { ChipName = "chip" };
        identity = XDocument.Parse(Serialize(item)).Descendants("HardwareSensorIdentity").Single();
        Assert.Equal("ChipName", Assert.Single(identity.Elements()).Name.LocalName);

        sensor.HardwareSensorIdentity = null;
        xml = Serialize(item);
        Assert.DoesNotContain("HardwareSensorIdentity", xml);
        Assert.Null(((ISensorItem)Deserialize(xml)).HardwareSensorIdentity);
    }

    [Theory, MemberData(nameof(HardwareFamilies))]
    public void CaptureApplyAndCloneKeepIndependentMetadata(string kind)
    {
        var item = Item(kind);
        SensorBinding.Apply(item, SensorBinding.ForHardware(SensorStateFixture.WireViewId, "wireview", "Pin 5"));
        var sensor = (ISensorItem)item;
        sensor.HardwareSensorIdentity!.OriginalId = "hwmon4/curr5";
        var saved = SensorBinding.Capture(item)!;
        var clone = (DisplayItem)item.Clone();
        Assert.NotEqual(item.Guid, clone.Guid);
        var cloned = (ISensorItem)clone;
        Assert.NotSame(sensor.HardwareSensorIdentity, cloned.HardwareSensorIdentity);
        Assert.Equal("hwmon4/curr5", cloned.HardwareSensorIdentity!.OriginalId);
        cloned.HardwareSensorIdentity.ChipName = "changed";
        Assert.Equal("wireview", sensor.HardwareSensorIdentity.ChipName);
        sensor.HardwareSensorIdentity.ChannelLabel = "edited";
        Assert.Equal("Pin 5", saved.HardwareSensorIdentity!.ChannelLabel);

        SensorBinding.Apply(item, SensorBinding.ForPlugin("plugin/sensor", "Plugin"));
        Assert.Null(sensor.HardwareSensorIdentity);
        Assert.Equal("", sensor.LibreSensorId);
        Assert.Equal(SensorType.Plugin, sensor.SensorType);
        SensorBinding.Apply(item, saved);
        Assert.NotSame(saved.HardwareSensorIdentity, sensor.HardwareSensorIdentity);
        Assert.Equal("Pin 5", sensor.HardwareSensorIdentity!.ChannelLabel);
        Assert.Equal("hwmon4/curr5", sensor.HardwareSensorIdentity.OriginalId);
        Assert.Equal("", sensor.PluginSensorId);
    }

    [Theory, MemberData(nameof(HardwareFamilies))]
    public void ReadsResolveTheCompleteReferenceAndLeavePersistedIdAlone(string kind)
    {
        SensorStateFixture.PublishWireView();
        var item = Item(kind);
        var sensor = (ISensorItem)item;
        sensor.LibreSensorId = "hwmon4/curr5";
        sensor.SensorName = "Contradictory old name";
        sensor.HardwareSensorIdentity = new() { ChipName = "wireview", ChannelLabel = "Pin 5" };
        var keys = new List<string>();
        SensorReader.ConfigureHwmonSource(id => { keys.Add(id); return new(5, 5, 5, 5, "A"); });
        Assert.Equal(5, sensor.GetValue()!.Value.ValueNow);
        Assert.Equal(SensorStateFixture.WireViewId, Assert.Single(keys));
        Assert.Equal("hwmon4/curr5", sensor.LibreSensorId);
        sensor.HardwareSensorIdentity.ChannelLabel = "Missing";
        Assert.Null(sensor.GetValue());
        Assert.Single(keys);
        sensor.LibreSensorId = "system/cpu/total";
        Assert.NotNull(sensor.GetValue());
        Assert.Equal("system/cpu/total", keys[^1]);
    }

    [Fact]
    public void FacadeSupportsPluginTablesAndIgnoresNonSensorItems()
    {
        var table = new TableSensorDisplayItem();
        var binding = SensorBinding.ForPlugin("test/sensor", "Table");
        SensorBinding.Apply(table, binding);
        Assert.Equal(binding, SensorBinding.Capture(table));
        var text = new TextDisplayItem { Name = "Text" };
        Assert.Null(SensorBinding.Capture(text));
        SensorBinding.Apply(text, binding);
        Assert.Equal("Text", text.Name);
    }

    private static readonly XmlSerializer Serializer = new(typeof(List<DisplayItem>), ConfigPersistence.DisplayItemExtraTypes);
    internal static string Serialize(params DisplayItem[] items)
    {
        using var writer = new StringWriter();
        Serializer.Serialize(writer, items.ToList());
        return writer.ToString();
    }

    private static DisplayItem Deserialize(string xml)
    {
        using var reader = new StringReader(xml);
        return Assert.Single((List<DisplayItem>)Serializer.Deserialize(reader)!);
    }
}
