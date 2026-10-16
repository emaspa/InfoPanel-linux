using System.Buffers.Binary;
using System.Xml.Serialization;
using InfoPanel.Models;
using InfoPanel.VmaxPanel;
using Xunit;

namespace InfoPanel.App.Tests;

public class ValkyrieMc360Tests
{
    [Fact]
    public void Mc360IsNotAnMs9132GeometryAlias()
    {
        Assert.Null(VmaxPanelModelDatabase.GetModelByPanelSize(640, 480));
        var model = VmaxPanelModelDatabase.GetModelByVidPid(0x374A, 0xA021)!;
        Assert.Equal(VmaxPanelModel.ValkyrieMc360, model.Model);
        Assert.Equal("Valkyrie MC360 LCD", model.Name);
        Assert.True(model.UsesBgr565);
        Assert.True(model.Experimental);
        Assert.Equal(640, model.Width);
        Assert.Equal(480, model.Height);
    }

    [Theory]
    [InlineData(0x374A, 0xA021, "Vk-Copl-08", true)]
    [InlineData(0x374A, 0xA021, "another screen", false)]
    [InlineData(0x374A, 0xA001, "Vk-Copl-08", false)]
    [InlineData(0x345F, 0x9132, "Vk-Copl-08", false)]
    public void RequiresTheKnownMc360UsbIdentity(int vid, int pid, string name, bool expected)
    {
        Assert.Equal(expected, ValkyrieUsbProtocol.IsMc360Descriptor(vid, pid, name));
    }

    [Fact]
    public void SerializesMc360WithoutChangingV360()
    {
        var serializer = new XmlSerializer(typeof(VmaxPanelDevice));
        using var text = new StringWriter();
        serializer.Serialize(text, new VmaxPanelDevice { Model = VmaxPanelModel.ValkyrieMc360 });
        using var input = new StringReader(text.ToString());
        var device = Assert.IsType<VmaxPanelDevice>(serializer.Deserialize(input));
        Assert.Equal(VmaxPanelModel.ValkyrieMc360, device.Model);
        Assert.Equal(640, device.DisplayWidth);
        Assert.Equal(240, new VmaxPanelDevice { Model = VmaxPanelModel.ValkyrieV360 }.DisplayHeight);
    }

    [Fact]
    public void UsesModeTwoLittleEndianCommands()
    {
        Assert.Equal("03100400000045230100", Convert.ToHexString(ValkyrieUsbProtocol.Request(0x1003, [0x45, 0x23, 1, 0])));
        Assert.Equal("01100400000000600900", Convert.ToHexString(ValkyrieUsbProtocol.Request(0x1001, [0, 0x60, 9, 0])));
        Assert.Equal(new byte[4], ValkyrieUsbProtocol.Reply(0x1003, [3, 0x90, 4, 0, 0, 0, 0, 0, 0, 0], 4));
    }

    [Theory]
    [InlineData("039004000000000000")] // truncated status
    [InlineData("01900400000000000000")] // another command's response
    [InlineData("03900300000000000000")] // incorrect declared size
    [InlineData("0390040000000000000000")] // trailing payload
    public void RejectsMalformedReply(string hex)
    {
        Assert.Throws<IOException>(() => ValkyrieUsbProtocol.Reply(0x1003, Convert.FromHexString(hex), 4));
    }

    private static byte[] ScreenInfo(uint width = 640, uint height = 480, uint size = 614400, uint format = 0)
    {
        var bytes = new byte[28];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, format);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), width);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16), height);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(24), size);
        return bytes;
    }

    [Fact]
    public void RequiresExplicitScreenInfoWithoutGuessingFromBufferSize()
    {
        ValkyrieUsbProtocol.ValidateScreenInfo(ScreenInfo());
        Assert.Throws<IOException>(() => ValkyrieUsbProtocol.ValidateScreenInfo(ScreenInfo(width: 480, height: 640)));
        Assert.Throws<IOException>(() => ValkyrieUsbProtocol.ValidateScreenInfo(ScreenInfo(size: 153600)));
        Assert.Throws<IOException>(() => ValkyrieUsbProtocol.ValidateScreenInfo(ScreenInfo(format: 0xF001)));
        Assert.Throws<IOException>(() => ValkyrieUsbProtocol.ValidateScreenInfo(new byte[27]));
    }

    [Fact]
    public void ConvertsPrimaryColorsAndRespectsRowStride()
    {
        // BGR565 on the wire, not the RGB565 channel ordering of many other panels.
        byte[] rgba = [255, 0, 0, 255, 0, 255, 0, 255, 9, 9, 9, 9,
            0, 0, 255, 255, 255, 255, 255, 255];
        byte[] pixels = new byte[8];
        ValkyrieUsbProtocol.ConvertRgba(rgba, 2, 2, 12, pixels, 100);
        Assert.Equal("1F00E00700F8FFFF", Convert.ToHexString(pixels));
        ValkyrieUsbProtocol.ConvertRgba(rgba, 2, 2, 12, pixels, 0);
        Assert.Equal(new byte[8], pixels);
    }

    [Fact]
    public void AnnouncesSizeBeforeDataAndTerminatesEveryAlignedChunk()
    {
        var calls = new List<string>();
        var pixels = new byte[ValkyrieUsbProtocol.PixelDataLength];
        ValkyrieUsbProtocol.WriteFrame(pixels, pixels.Length,
            size => calls.Add("size:" + Convert.ToHexString(size)),
            (buffer, offset, count) => calls.Add($"{offset}:{count}"));
        Assert.Equal(new[] { "size:00600900", "0:131072", "0:0", "131072:131072", "0:0",
            "262144:131072", "0:0", "393216:131072", "0:0", "524288:90112", "0:0" }, calls);
    }

    [Fact]
    public void DoesNotTransferDataWhenSizeAcknowledgementFails()
    {
        bool wrote = false;
        Assert.Throws<IOException>(() => ValkyrieUsbProtocol.WriteFrame(new byte[614400], 614400,
            _ => throw new IOException("Rejected"), (_, _, _) => wrote = true));
        Assert.False(wrote);
        Assert.Throws<ArgumentException>(() => ValkyrieUsbProtocol.WriteFrame(new byte[1], 614400,
            _ => wrote = true, (_, _, _) => wrote = true));
        Assert.False(wrote);
    }
}
