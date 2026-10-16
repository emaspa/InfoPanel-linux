using System.Buffers.Binary;

namespace InfoPanel.VmaxPanel;

/// <summary>
/// MC360 / 374A:A021 mode-2 bulk protocol. Independently implemented from the
/// wire facts at https://github.com/codehz/msdisplay/blob/main/bowei_aio/display.py
/// MC360 identity and Windows validation: ZGMFX01A/MythCool-Lite README.
/// No proprietary driver or binaries are required.
/// </summary>
public static class ValkyrieUsbProtocol
{
    public const int Width = 640;
    public const int Height = 480;
    public const int PixelDataLength = Width * Height * 2;

    public static bool IsMc360Descriptor(int vendorId, int productId, string? productName) =>
        vendorId == 0x374A && productId == 0xA021
        && string.Equals(productName, "Vk-Copl-08", StringComparison.OrdinalIgnoreCase);

    public static byte[] Request(ushort command, ReadOnlySpan<byte> payload)
    {
        var packet = new byte[6 + payload.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(packet, command);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(2), (uint)payload.Length);
        payload.CopyTo(packet.AsSpan(6));
        return packet;
    }

    public static byte[] Reply(ushort command, ReadOnlySpan<byte> packet, int payloadLength)
    {
        if (packet.Length != 6 + payloadLength
            || BinaryPrimitives.ReadUInt16LittleEndian(packet) != (command | 0x8000)
            || BinaryPrimitives.ReadUInt32LittleEndian(packet[2..]) != payloadLength)
            throw new IOException($"Invalid MC360 reply for command {command:X4}.");
        return packet[6..].ToArray();
    }

    public static void ValidateScreenInfo(ReadOnlySpan<byte> info)
    {
        if (info.Length != 28
            || BinaryPrimitives.ReadUInt32LittleEndian(info[12..]) != Width
            || BinaryPrimitives.ReadUInt32LittleEndian(info[16..]) != Height
            || BinaryPrimitives.ReadUInt32LittleEndian(info[24..]) != PixelDataLength
            || BinaryPrimitives.ReadUInt32LittleEndian(info) == 0xF001)
            throw new IOException("MC360 requires an explicit 640x480 / 614400-byte BGR565 screen-info reply.");
    }

    public static void WriteFrame(byte[] pixels, int length, Action<byte[]> announce,
        Action<byte[], int, int> write)
    {
        if (length != PixelDataLength || length > pixels.Length)
            throw new ArgumentException("MC360 requires exactly 640x480 BGR565 pixels.", nameof(length));
        byte[] size = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(size, (uint)length);
        announce(size); // The caller must validate command 0x1001's acknowledgement.
        const int chunkSize = 0x20000;
        for (int offset = 0; offset < length; offset += chunkSize)
        {
            int count = Math.Min(chunkSize, length - offset);
            write(pixels, offset, count);
            // Each aligned bulk transfer, rather than only the full frame, needs a ZLP.
            if (count % 0x1000 == 0) write([], 0, 0);
        }
    }

    /// <summary>Encodes little-endian BGR565, respecting bitmap stride.</summary>
    public static void ConvertRgba(ReadOnlySpan<byte> rgba, int width, int height, int stride,
        Span<byte> output, int brightness)
    {
        if (width <= 0 || height <= 0 || stride < width * 4
            || rgba.Length < (height - 1) * stride + width * 4 || output.Length < width * height * 2)
            throw new ArgumentException("Invalid MC360 pixel buffer.");
        int scale = Math.Clamp(brightness, 0, 100);
        int target = 0;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int source = y * stride + x * 4;
                int r = rgba[source] * scale / 100, g = rgba[source + 1] * scale / 100, b = rgba[source + 2] * scale / 100;
                ushort pixel = (ushort)(((b >> 3) << 11) | ((g >> 2) << 5) | (r >> 3));
                BinaryPrimitives.WriteUInt16LittleEndian(output[target..], pixel);
                target += 2;
            }
        }
    }
}
