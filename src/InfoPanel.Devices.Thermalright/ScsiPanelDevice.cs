using InfoPanel.Platform;
using Serilog;

namespace InfoPanel.ThermalrightPanel
{
    /// <summary>
    /// SCSI pass-through protocol for Thermalright LCD panels that present as USB Mass
    /// Storage devices (20-byte CRC32-signed CDBs; protocol from emaspa/infopanel-1 all-changes).
    /// The OS transport (Linux SG_IO, Windows SPTI) comes from PlatformServices.ScsiTransport.
    /// Protocol reference: Lexonight1/thermalright-trcc-linux USBLCD_PROTOCOL.md
    /// </summary>
    public sealed class ScsiPanelDevice : IDisposable
    {
        private static readonly ILogger Logger = Log.ForContext<ScsiPanelDevice>();

        private const int POLL_BUFFER_SIZE = 0xE100;        // 57,600 bytes - poll/init buffer size
        private const int FRAME_CHUNK_SIZE_LARGE = 0x10000; // 65,536 bytes (for displays > 76,800 pixels)
        private const int FRAME_CHUNK_SIZE_SMALL = 0xE100;  // 57,600 bytes (for displays <= 76,800 pixels)
        private const int SMALL_DISPLAY_PIXELS = 76800;     // 320x240 threshold
        private const uint CMD_POLL = 0xF5;
        private const uint CMD_INIT = 0x1F5;
        private const uint CMD_FRAME_BASE = 0x101F5;
        private const string VENDOR_FILTER = "USBLCD";

        private readonly IScsiTransport _transport;

        private ScsiPanelDevice(IScsiTransport transport)
        {
            _transport = transport;
        }

        public class ScsiDeviceInfo
        {
            public string DevicePath { get; set; } = string.Empty;
            public string VendorId { get; init; } = string.Empty;
            public string ProductId { get; init; } = string.Empty;
        }

        private static IScsiTransportProvider Transport =>
            PlatformServices.ScsiTransport
            ?? throw new InvalidOperationException("No IScsiTransportProvider registered; call the platform Register() at startup.");

        /// <summary>Enumerates SCSI devices whose vendor string is USBLCD.</summary>
        public static List<ScsiDeviceInfo> FindDevices()
        {
            return [.. Transport.FindDevices(VENDOR_FILTER).Select(d => new ScsiDeviceInfo
            {
                DevicePath = d.DevicePath,
                VendorId = d.VendorId,
                ProductId = d.ProductId
            })];
        }

        public static ScsiPanelDevice? Open(string devicePath)
        {
            var transport = Transport.Open(devicePath);
            return transport == null ? null : new ScsiPanelDevice(transport);
        }

        /// <summary>Standard SCSI TEST UNIT READY (6 zero bytes, no data phase).</summary>
        public bool TestUnitReady()
        {
            var cdb = new byte[] { 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };
            return _transport.SendCommand(cdb, [], ScsiDataDirection.FromDevice);
        }

        /// <summary>
        /// Builds a 20-byte CDB: cmd(4 LE) + zeros(8) + size(4 LE) + crc32(4 LE).
        /// CRC32 covers the first 16 bytes only.
        /// </summary>
        private static byte[] BuildCdb(uint cmd, uint dataSize)
        {
            var cdb = new byte[20];
            BitConverter.GetBytes(cmd).CopyTo(cdb, 0);       // bytes 0-3: command LE
            // bytes 4-11: zeros (already)
            BitConverter.GetBytes(dataSize).CopyTo(cdb, 12);  // bytes 12-15: data size LE

            // CRC32 over first 16 bytes
            uint crc = Crc32(cdb, 0, 16);
            BitConverter.GetBytes(crc).CopyTo(cdb, 16);       // bytes 16-19: CRC32 LE
            return cdb;
        }

        private static uint Crc32(byte[] data, int offset, int length)
        {
            uint crc = 0xFFFFFFFF;
            for (int i = offset; i < offset + length; i++)
            {
                crc ^= data[i];
                for (int j = 0; j < 8; j++)
                    crc = (crc >> 1) ^ (0xEDB88320 & ~((crc & 1) - 1));
            }
            return ~crc;
        }

        /// <summary>
        /// Polls the device by sending cmd=0xF5, reading 0xE100 bytes.
        /// Returns the poll response or null on failure.
        /// </summary>
        public byte[]? Poll()
        {
            var cdb = BuildCdb(CMD_POLL, (uint)POLL_BUFFER_SIZE);
            var response = new byte[POLL_BUFFER_SIZE];
            if (_transport.SendCommand(cdb, response, ScsiDataDirection.FromDevice))
                return response;

            return null;
        }

        /// <summary>
        /// Checks if poll response indicates device is still booting (bytes 4-7 = 0xA1A2A3A4).
        /// </summary>
        public static bool IsDeviceBooting(byte[] pollResponse)
        {
            return pollResponse.Length >= 8
                && pollResponse[4] == 0xA1
                && pollResponse[5] == 0xA2
                && pollResponse[6] == 0xA3
                && pollResponse[7] == 0xA4;
        }

        /// <summary>
        /// Initializes the display controller by sending cmd=0x1F5 with 0xE100 zero bytes.
        /// </summary>
        public bool Init()
        {
            var cdb = BuildCdb(CMD_INIT, (uint)POLL_BUFFER_SIZE);
            var data = new byte[POLL_BUFFER_SIZE]; // 0xE100 zero bytes
            return _transport.SendCommand(cdb, data, ScsiDataDirection.ToDevice);
        }

        /// <summary>
        /// Sends a complete RGB565 frame by splitting into chunks.
        /// cmd = 0x101F5 | (chunkIndex &lt;&lt; 24) for each chunk.
        /// Chunk size is 57,600 for small displays (&lt;= 76,800 pixels), 65,536 for larger.
        /// </summary>
        public bool SendFrame(byte[] rgb565Data, int width, int height)
        {
            int pixels = width * height;
            int chunkSize = pixels <= SMALL_DISPLAY_PIXELS ? FRAME_CHUNK_SIZE_SMALL : FRAME_CHUNK_SIZE_LARGE;

            int offset = 0;
            int chunkIndex = 0;

            while (offset < rgb565Data.Length)
            {
                int remaining = rgb565Data.Length - offset;
                int thisChunkSize = Math.Min(chunkSize, remaining);

                uint cmd = CMD_FRAME_BASE | ((uint)chunkIndex << 24);
                var cdb = BuildCdb(cmd, (uint)thisChunkSize);

                var chunk = new byte[thisChunkSize];
                Array.Copy(rgb565Data, offset, chunk, 0, thisChunkSize);

                if (!_transport.SendCommand(cdb, chunk, ScsiDataDirection.ToDevice))
                {
                    Logger.Warning("ScsiPanelDevice: Failed to send frame chunk {Index} ({Size} bytes)",
                        chunkIndex, thisChunkSize);
                    return false;
                }

                offset += thisChunkSize;
                chunkIndex++;
            }

            return true;
        }

        public void Dispose()
        {
            _transport.Dispose();
        }
    }
}
