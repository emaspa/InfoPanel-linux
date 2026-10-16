using Serilog;
using System;

namespace InfoPanel;

/// <summary>Reads EDID geometry without programming or enabling the shared MS9132 bridge.</summary>
public static class Ms9132PanelProbe
{
    private static readonly ILogger Logger = Log.ForContext(typeof(Ms9132PanelProbe));

    public static (int Width, int Height)? ReadSize(string hidPath)
    {
        try
        {
            var raw = LinuxHidRawFeature.Open(hidPath);
            if (raw == null) return null;

            using (raw)
            {
                int ReadEdidByte(int offset)
                {
                    ushort address = (ushort)(0xC000 + offset);
                    raw.SetFeature([0xB5, (byte)(address >> 8), (byte)(address & 0xFF), 0, 0, 0, 0, 0]);
                    return raw.GetFeature()[4];
                }

                // EDID header sanity: 00 FF FF FF FF FF FF 00.
                if (ReadEdidByte(0) != 0x00 || ReadEdidByte(1) != 0xFF || ReadEdidByte(7) != 0x00)
                {
                    Logger.Debug("Ms9132PanelProbe: MS9132 EDID header mismatch");
                    return null;
                }

                // First detailed timing descriptor at offset 54. A zero pixel
                // clock means no DTD is present.
                if (ReadEdidByte(54) == 0 && ReadEdidByte(55) == 0) return null;

                int hactive = ReadEdidByte(56) | ((ReadEdidByte(58) & 0xF0) << 4);
                int vactive = ReadEdidByte(59) | ((ReadEdidByte(61) & 0xF0) << 4);
                Logger.Information("Ms9132PanelProbe: MS9132 panel EDID reports {W}x{H}", hactive, vactive);

                if (hactive <= 0 || vactive <= 0 || hactive > 4096 || vactive > 4096) return null;
                return (hactive, vactive);
            }
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "Ms9132PanelProbe: MS9132 EDID probe failed");
            return null;
        }
    }
}
