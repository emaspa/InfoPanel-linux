using Serilog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace InfoPanel.ThermalrightPanel
{
    /// <summary>
    /// SCSI pass-through wrapper for Thermalright LCD panels that present as USB Mass Storage devices.
    /// Uses Linux SG_IO ioctl to send F5-prefixed CDB commands via /dev/sgN.
    /// Protocol reference: Lexonight1/thermalright-trcc-linux USBLCD_PROTOCOL.md
    /// </summary>
    public sealed class ScsiPanelDevice : IDisposable
    {
        private static readonly ILogger Logger = Log.ForContext(typeof(ScsiPanelDevice));

        // SCSI protocol constants
        private const int POLL_BUFFER_SIZE = 0xE100;   // 57,600 bytes — poll/init buffer size
        private const int FRAME_CHUNK_SIZE = 0x10000;  // 65,536 bytes — 64KB frame chunks
        private const byte SCSI_PROTOCOL_MARKER = 0xF5;

        // Linux SG_IO constants
        private const uint SG_IO = 0x2285;
        private const int SG_DXFER_FROM_DEV = -3;
        private const int SG_DXFER_TO_DEV = -2;
        private const int O_RDWR = 2;
        private const int SENSE_BUFFER_SIZE = 32;

        #region P/Invoke (libc)

        [DllImport("libc", EntryPoint = "open", SetLastError = true)]
        private static extern int LinuxOpen([MarshalAs(UnmanagedType.LPStr)] string pathname, int flags);

        [DllImport("libc", EntryPoint = "close", SetLastError = true)]
        private static extern int LinuxClose(int fd);

        [DllImport("libc", EntryPoint = "ioctl", SetLastError = true)]
        private static extern int LinuxIoctl(int fd, uint request, IntPtr argp);

        #endregion

        #region Native Structures

        /// <summary>
        /// Linux sg_io_hdr_t structure for SG_IO ioctl (x64 layout).
        /// See linux/sg.h — total size 88 bytes on x86_64.
        /// </summary>
        [StructLayout(LayoutKind.Explicit, Size = 88)]
        private struct sg_io_hdr_t
        {
            [FieldOffset(0)]  public int interface_id;       // 'S' for SCSI
            [FieldOffset(4)]  public int dxfer_direction;    // SG_DXFER_FROM_DEV or SG_DXFER_TO_DEV
            [FieldOffset(8)]  public byte cmd_len;           // CDB length
            [FieldOffset(9)]  public byte mx_sb_len;         // max sense buffer length
            [FieldOffset(10)] public ushort iovec_count;     // 0 for simple I/O
            [FieldOffset(12)] public uint dxfer_len;         // data transfer length
            [FieldOffset(16)] public IntPtr dxferp;          // pointer to data buffer
            [FieldOffset(24)] public IntPtr cmdp;            // pointer to CDB
            [FieldOffset(32)] public IntPtr sbp;             // pointer to sense buffer
            [FieldOffset(40)] public uint timeout;           // timeout in milliseconds
            [FieldOffset(44)] public uint flags;             // 0
            [FieldOffset(48)] public int pack_id;            // unused
            [FieldOffset(56)] public IntPtr usr_ptr;         // unused
            [FieldOffset(64)] public byte status;            // SCSI status
            [FieldOffset(65)] public byte masked_status;
            [FieldOffset(66)] public byte msg_status;
            [FieldOffset(67)] public byte sb_len_wr;         // sense buffer bytes written
            [FieldOffset(68)] public ushort host_status;
            [FieldOffset(70)] public ushort driver_status;
            [FieldOffset(72)] public int resid;
            [FieldOffset(76)] public uint duration;
            [FieldOffset(80)] public uint info;
        }

        #endregion

        private int _fd = -1;
        private readonly string _devicePath;

        private ScsiPanelDevice(int fd, string devicePath)
        {
            _fd = fd;
            _devicePath = devicePath;
        }

        /// <summary>
        /// Information about a discovered SCSI LCD panel device.
        /// </summary>
        public class ScsiDeviceInfo
        {
            public string DevicePath { get; set; } = string.Empty;
            public string VendorId { get; init; } = string.Empty;
            public string ProductId { get; init; } = string.Empty;
        }

        /// <summary>
        /// Enumerates /dev/sg0 through /dev/sg15 and returns any that have "USBLCD" as the vendor string
        /// (read from /sys/class/scsi_generic/sgN/device/vendor).
        /// </summary>
        public static List<ScsiDeviceInfo> FindDevices()
        {
            var devices = new List<ScsiDeviceInfo>();

            for (int i = 0; i < 16; i++)
            {
                var devPath = $"/dev/sg{i}";
                var vendorPath = $"/sys/class/scsi_generic/sg{i}/device/vendor";
                var modelPath = $"/sys/class/scsi_generic/sg{i}/device/model";

                try
                {
                    if (!File.Exists(devPath))
                        continue;

                    if (!File.Exists(vendorPath))
                        continue;

                    var vendor = File.ReadAllText(vendorPath).Trim();
                    var model = File.Exists(modelPath) ? File.ReadAllText(modelPath).Trim() : "";

                    if (vendor.Contains("USBLCD", StringComparison.OrdinalIgnoreCase))
                    {
                        Logger.Information("ScsiPanelDevice: Found USBLCD device at {Path} (Vendor={Vendor}, Model={Model})",
                            devPath, vendor, model);

                        devices.Add(new ScsiDeviceInfo
                        {
                            DevicePath = devPath,
                            VendorId = vendor,
                            ProductId = model
                        });
                    }
                }
                catch (Exception ex)
                {
                    Logger.Debug("ScsiPanelDevice: Error probing {Path}: {Error}", devPath, ex.Message);
                }
            }

            return devices;
        }

        /// <summary>
        /// Opens a SCSI generic device at the given path (e.g., /dev/sg2).
        /// </summary>
        public static ScsiPanelDevice? Open(string devicePath)
        {
            int fd = LinuxOpen(devicePath, O_RDWR);

            if (fd < 0)
            {
                int errno = Marshal.GetLastSystemError();
                Logger.Warning("ScsiPanelDevice: Failed to open {Path}, errno {Error}", devicePath, errno);
                return null;
            }

            Logger.Information("ScsiPanelDevice: Opened {Path} (fd={Fd})", devicePath, fd);
            return new ScsiPanelDevice(fd, devicePath);
        }

        /// <summary>
        /// Polls the device by sending CDB F5 00 00 00, reading 0xE100 bytes.
        /// Returns the poll response or null on failure.
        /// </summary>
        public byte[]? Poll()
        {
            var cdb = new byte[16];
            cdb[0] = SCSI_PROTOCOL_MARKER; // F5
            cdb[1] = 0x00; // poll/read
            cdb[2] = 0x00;
            cdb[3] = 0x00;

            var response = new byte[POLL_BUFFER_SIZE];
            if (SendScsiCommand(cdb, response, SG_DXFER_FROM_DEV))
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
            var cdb = new byte[16];
            cdb[0] = SCSI_PROTOCOL_MARKER; // F5
            cdb[1] = 0x01; // write/send
            cdb[2] = 0x00; // init mode
            cdb[3] = 0x00;

            var data = new byte[POLL_BUFFER_SIZE]; // 0xE100 zero bytes
            return SendScsiCommand(cdb, data, SG_DXFER_TO_DEV);
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

                var cdb = new byte[16];
                cdb[0] = SCSI_PROTOCOL_MARKER; // F5
                cdb[1] = 0x01; // write/send
                cdb[2] = 0x01; // raw frame chunk mode
                cdb[3] = (byte)chunkIndex;

                var chunk = new byte[chunkSize];
                Array.Copy(rgb565Data, offset, chunk, 0, chunkSize);

                if (!SendScsiCommand(cdb, chunk, SG_DXFER_TO_DEV))
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

        /// <summary>
        /// Sends a SCSI CDB command with data transfer via Linux SG_IO ioctl.
        /// </summary>
        private bool SendScsiCommand(byte[] cdb, byte[] data, int direction)
        {
            var cdbHandle = GCHandle.Alloc(cdb, GCHandleType.Pinned);
            var dataHandle = GCHandle.Alloc(data, GCHandleType.Pinned);
            var senseBuffer = new byte[SENSE_BUFFER_SIZE];
            var senseHandle = GCHandle.Alloc(senseBuffer, GCHandleType.Pinned);

            try
            {
                var hdr = new sg_io_hdr_t
                {
                    interface_id = 'S',
                    dxfer_direction = direction,
                    cmd_len = (byte)cdb.Length,
                    mx_sb_len = SENSE_BUFFER_SIZE,
                    dxfer_len = (uint)data.Length,
                    dxferp = dataHandle.AddrOfPinnedObject(),
                    cmdp = cdbHandle.AddrOfPinnedObject(),
                    sbp = senseHandle.AddrOfPinnedObject(),
                    timeout = 10000 // 10 seconds in milliseconds
                };

                int hdrSize = Marshal.SizeOf<sg_io_hdr_t>();
                var hdrPtr = Marshal.AllocHGlobal(hdrSize);
                try
                {
                    Marshal.StructureToPtr(hdr, hdrPtr, false);

                    int ret = LinuxIoctl(_fd, SG_IO, hdrPtr);
                    if (ret < 0)
                    {
                        int errno = Marshal.GetLastSystemError();
                        Logger.Warning("ScsiPanelDevice: SG_IO ioctl failed, errno {Error}", errno);
                        return false;
                    }

                    // Read back the header to check SCSI status
                    var result = Marshal.PtrToStructure<sg_io_hdr_t>(hdrPtr);
                    if (result.status != 0)
                    {
                        Logger.Warning("ScsiPanelDevice: SCSI command failed with status 0x{Status:X2}", result.status);
                        return false;
                    }

                    if (result.host_status != 0 || result.driver_status != 0)
                    {
                        Logger.Warning("ScsiPanelDevice: Transport error host=0x{Host:X4} driver=0x{Driver:X4}",
                            result.host_status, result.driver_status);
                        return false;
                    }

                    return true;
                }
                finally
                {
                    Marshal.FreeHGlobal(hdrPtr);
                }
            }
            finally
            {
                cdbHandle.Free();
                dataHandle.Free();
                senseHandle.Free();
            }
        }

        public void Dispose()
        {
            if (_fd >= 0)
            {
                LinuxClose(_fd);
                Logger.Debug("ScsiPanelDevice: Closed {Path}", _devicePath);
                _fd = -1;
            }
        }
    }
}
