using InfoPanel.Sensors;
using Xunit;

namespace InfoPanel.Core.Tests;

public class SensorIdTests
{
    [Theory]
    [InlineData(" hello world ", "hello%20world")]
    [InlineData("%+~/\\!", "%25%2B%7E%2F%5C%21")]
    [InlineData("é温", "%C3%A9%E6%B8%A9")]
    [InlineData(".", "%2E")]
    [InlineData("..", "%2E%2E")]
    [InlineData("test.", "test%2E")]
    [InlineData("CON", "%43ON")]
    [InlineData("nul.txt", "%6Eul.txt")]
    public void TokensRoundTrip(string raw, string encoded)
    {
        Assert.Equal(encoded, SensorId.EncodeToken(raw));
        Assert.Equal(raw.Trim(), SensorId.DecodeToken(encoded));
        var id = SensorId.Hwmon(raw, SensorId.Component("name", raw), "temp1");
        Assert.Equal(id, SensorId.Parse(id.Value));
        Assert.Equal(raw.Trim(), id.Chip);
        Assert.Equal(raw.Trim(), Assert.Single(id.AnchorTokens));
    }

    [Fact]
    public void LongTokensAreNeverShortened()
    {
        var token = new string('é', 100);
        var encoded = SensorId.EncodeToken(token);
        Assert.Equal(600, encoded.Length);
        Assert.DoesNotContain('!', encoded);
        Assert.Equal(token, SensorId.DecodeToken(encoded));
    }

    [Theory]
    [InlineData("temp0")][InlineData("fan1")][InlineData("in0")][InlineData("curr5")]
    [InlineData("power12")][InlineData("freq2")][InlineData("humidity1")]
    public void AllChannelsParse(string channel)
    {
        var id = SensorId.Hwmon("nvme", "nvme-serial+ABC", channel, "pci+0000-01-00.0");
        Assert.Equal(channel, id.Channel);
        Assert.Equal("nvme-serial", id.AnchorKind);
        Assert.Equal("pci+0000-01-00.0", id.Secondary);
        Assert.True(id.HasStrongIdentity);
    }

    [Fact]
    public void ThermalRoundTrip()
    {
        var id = SensorId.Thermal("type+INT3400%20Thermal", "acpi+%5C_TZ_.TZ00");
        Assert.Equal(id, SensorId.Parse(id.Value));
        Assert.Equal("INT3400 Thermal", id.Chip);
        Assert.Equal("temp", id.Channel);
    }

    [Theory]
    [InlineData("hwmon0/temp1")][InlineData("hwmon12/fan02")][InlineData("hwmon4/in0")]
    [InlineData("hwmon4/curr5")][InlineData("hwmon0/power2")][InlineData("hwmon3/freq5")]
    [InlineData("hwmon9/humidity1")][InlineData("thermal/thermal_zone07")]
    public void RecognizesOnlySpecifiedLegacyForms(string id) => Assert.True(SensorId.IsLegacy(id));

    [Theory]
    [InlineData("hwmon1/temp1\n")][InlineData("hwmon/temp1")][InlineData("thermal/thermal_zone")]
    [InlineData("system/cpu/temp")][InlineData("hwmon1/temp1_input")][InlineData("hwmon1/voltage1")]
    public void RejectsForeignLegacyForms(string id) => Assert.False(SensorId.IsLegacy(id));

    [Theory]
    [InlineData("hwmon/v2/nvme/name+nvme/temp1")]
    [InlineData("hwmon/v1/nvme/name+nvme/temp01")]
    [InlineData("hwmon/v1/nvme/other+x/temp1")]
    [InlineData("hwmon/v1/nvme/name+x~other+x/temp1")]
    [InlineData("hwmon/v1/nvme/name+x/temp-1")]
    [InlineData("hwmon/v1/%FF/name+x/temp1")]
    [InlineData("hwmon/v1/a%2fb/name+x/temp1")]
    [InlineData("hwmon/v1/a%/name+x/temp1")]
    [InlineData("hwmon/v1/../name+x/temp1")]
    [InlineData("hwmon/v1/CON/name+x/temp1")]
    [InlineData("thermal/v1/type+acpitz/temp1")]
    public void RejectsInvalidOrNoncanonicalIds(string id) => Assert.False(SensorId.TryParse(id, out _));
}
