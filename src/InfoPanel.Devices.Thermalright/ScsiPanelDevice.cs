using InfoPanel.Platform;
using Serilog;

namespace InfoPanel.ThermalrightPanel
{
    /// <summary>
    /// SCSI pass-through protocol for Thermalright LCD panels that present as USB Mass
    /// Storage devices (F5-prefixed 6-byte CDBs; protocol from emaspa/infopanel-1@c30b3d7).
    /// The OS transport (Linux SG_IO, Windows SPTI) comes from PlatformServices.ScsiTransport.
    /// Protocol reference: Lexonight1/thermalright-trcc-linux USBLCD_PROTOCOL.md
    /// </summary>
    public sealed class ScsiPanelDevice : IDisposable
    {
        private static readonly ILogger Logger = Log.ForContext<ScsiPanelDevice>();

        private const int POLL_BUFFER_SIZE = 0xE100;   // 57,600 bytes — poll/init buffer size
        private const int FRAME_CHUNK_SIZE = 0x10000;  // 65,536 bytes — 64KB frame chunks
        private const byte SCSI_PROTOCOL_MARKER = 0xF5;
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
        /// Polls the device by sending CDB F5 00 00 00, reading 0xE100 bytes.
        /// Returns the poll response or null on failure.
        /// </summary>
        public byte[]? Poll()
        {
            var cdb = new byte[] { SCSI_PROTOCOL_MARKER, 0x00, 0x00, 0x00, 0x00, 0x00 };

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
        /// Initializes the display controller by sending CDB F5 01 00 00 with 0xE100 zero bytes.
        /// </summary>
        public bool Init()
        {
            var cdb = new byte[] { SCSI_PROTOCOL_MARKER, 0x01, 0x00, 0x00, 0x00, 0x00 };

            var data = new byte[POLL_BUFFER_SIZE]; // 0xE100 zero bytes
            return _transport.SendCommand(cdb, data, ScsiDataDirection.ToDevice);
        }

        /// <summary>
        /// Sends a complete RGB565 frame by splitting it into 64KB chunks.
        /// CDB: F5 01 01 [chunk_index] for each chunk.
        /// </summary>
        public bool SendFrame(byte[] rgb565Data)
        {
            int offset = 0;
            int chunkIndex = 0;

            while (offset < rgb565Data.Length)
            {
                int remaining = rgb565Data.Length - offset;
                int chunkSize = Math.Min(FRAME_CHUNK_SIZE, remaining);

                var cdb = new byte[] { SCSI_PROTOCOL_MARKER, 0x01, 0x01, (byte)chunkIndex, 0x00, 0x00 };

                var chunk = new byte[chunkSize];
                Array.Copy(rgb565Data, offset, chunk, 0, chunkSize);

                if (!_transport.SendCommand(cdb, chunk, ScsiDataDirection.ToDevice))
                {
                    Logger.Warning("ScsiPanelDevice: Failed to send frame chunk {Index} ({Size} bytes)",
                        chunkIndex, chunkSize);
                    return false;
                }

                offset += chunkSize;
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
