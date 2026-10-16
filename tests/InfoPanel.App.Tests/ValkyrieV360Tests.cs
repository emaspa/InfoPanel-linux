using System.Security.Cryptography;
using System.Xml.Serialization;
using InfoPanel.Models;
using InfoPanel.VmaxPanel;
using Xunit;

namespace InfoPanel.App.Tests;

public class ValkyrieV360Tests
{
    [Theory]
    [InlineData(320, 240, VmaxPanelModel.ValkyrieV360)]
    [InlineData(320, 960, VmaxPanelModel.Vmax46Inch)]
    [InlineData(376, 960, VmaxPanelModel.Unknown)] // Jonsbo DS339 shares the VID/PID
    [InlineData(480, 480, VmaxPanelModel.Unknown)]
    public void UsesEdidGeometryInsteadOfSharedUsbId(int width, int height, VmaxPanelModel expected)
    {
        Assert.Equal(expected,
            VmaxPanelModelDatabase.GetModelByPanelSize(width, height)?.Model ?? VmaxPanelModel.Unknown);
    }

    [Fact]
    public void InitializationMatchesHardwareValidatedTrace()
    {
        var commands = new List<byte[]>();
        ValkyrieV360Protocol.Initialize((payload, readReply) =>
        {
            // Fixture digest from the independently hardware-tested Linux script:
            // [read-reply flag][8-byte padded feature payload] for all 77 commands.
            var entry = new byte[9];
            entry[0] = readReply ? (byte)1 : (byte)0;
            payload.CopyTo(entry, 1);
            commands.Add(entry);
        });
        Assert.Equal(77, commands.Count);
        Assert.Equal("4F2310227A5313251AA18B5EF6333B0967E2669A36F566AA064F36F99C57F8A2",
            Convert.ToHexString(SHA256.HashData(commands.SelectMany(c => c).ToArray())));
    }

    [Fact]
    public void EnablesScanOutOnlyThroughPostFrameSequence()
    {
        var commands = new List<string>();
        ValkyrieV360Protocol.PostFrame((payload, read) =>
            commands.Add($"{Convert.ToHexString(payload)}:{read}"));
        Assert.Equal(new[] { "B5F507:True", "B6F50700:False", "A6050100:False", "B5C555:True" }, commands);
    }

    [Fact]
    public void PreservesBothModelsWhenSettingsRoundTrip()
    {
        var settings = new Settings { VmaxPanelMultiDeviceMode = true };
        settings.VmaxPanelDevices.Add(new VmaxPanelDevice { Model = VmaxPanelModel.Vmax46Inch });
        settings.VmaxPanelDevices.Add(new VmaxPanelDevice { Model = VmaxPanelModel.ValkyrieV360 });
        var serializer = new XmlSerializer(typeof(Settings));
        using var writer = new StringWriter();
        serializer.Serialize(writer, settings);
        using var reader = new StringReader(writer.ToString());
        var restored = Assert.IsType<Settings>(serializer.Deserialize(reader));
        Assert.True(restored.VmaxPanelMultiDeviceMode);
        Assert.Equal(new[] { VmaxPanelModel.Vmax46Inch, VmaxPanelModel.ValkyrieV360 },
            restored.VmaxPanelDevices.Select(d => d.Model));
        Assert.Equal(320, restored.VmaxPanelDevices[1].DisplayWidth);
        Assert.Equal(240, restored.VmaxPanelDevices[1].DisplayHeight);
        Assert.Equal(960, restored.VmaxPanelDevices[0].DisplayHeight);
    }
}
