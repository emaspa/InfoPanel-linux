using Serilog;
using System.Runtime.InteropServices;

namespace InfoPanel.Platform.Linux
{
    /// <summary>
    /// SCSI pass-through via Linux SG_IO ioctl on /dev/sgN.
    /// Transport layer extracted from InfoPanel-linux@4879aa4 ScsiPanelDevice.
    /// </summary>
    public sealed class SgIoScsiTransportProvider : IScsiTransportProvider
    {
        private static readonly ILogger Logger = Log.ForContext<SgIoScsiTransportProvider>();

        public List<ScsiDeviceInfo> FindDevices(string vendorFilter)
        {
            var devices = new List<ScsiDeviceInfo>();

            for (int i = 0; i < 16; i++)
            {
                var devPath = $"/dev/sg{i}";
                var vendorPath = $"/sys/class/scsi_generic/sg{i}/device/vendor";
                var modelPath = $"/sys/class/scsi_generic/sg{i}/device/model";

                try
                {
                    if (!File.Exists(devPath) || !File.Exists(vendorPath))
                        continue;

                    var vendor = File.ReadAllText(vendorPath).Trim();
                    var model = File.Exists(modelPath) ? File.ReadAllText(modelPath).Trim() : "";

                    if (vendor.Contains(vendorFilter, StringComparison.OrdinalIgnoreCase))
                    {
                        Logger.Information("SgIoScsiTransport: Found {Filter} device at {Path} (Vendor={Vendor}, Model={Model})",
                            vendorFilter, devPath, vendor, model);

                        devices.Add(new ScsiDeviceInfo(devPath, vendor, model));
                    }
                }
                catch (Exception ex)
                {
                    Logger.Debug("SgIoScsiTransport: Error probing {Path}: {Error}", devPath, ex.Message);
                }
            }

            return devices;
        }

        public IScsiTransport? Open(string devicePath)
        {
            int fd = SgIoScsiTransport.LinuxOpen(devicePath, SgIoScsiTransport.O_RDWR);

            if (fd < 0)
            {
                int errno = Marshal.GetLastSystemError();
                Logger.Warning("SgIoScsiTransport: Failed to open {Path}, errno {Error}", devicePath, errno);
                return null;
            }

            Logger.Information("SgIoScsiTransport: Opened {Path} (fd={Fd})", devicePath, fd);
            return new SgIoScsiTransport(fd, devicePath);
        }
    }

    public sealed class SgIoScsiTransport : IScsiTransport
    {
        private static readonly ILogger Logger = Log.ForContext<SgIoScsiTransport>();

        // Linux SG_IO constants
        private const uint SG_IO = 0x2285;
        private const int SG_DXFER_NONE = -1;
        private const int SG_DXFER_FROM_DEV = -3;
        private const int SG_DXFER_TO_DEV = -2;
        internal const int O_RDWR = 2;
        private const int SENSE_BUFFER_SIZE = 32;

        [DllImport("libc", EntryPoint = "open", SetLastError = true)]
        internal static extern int LinuxOpen([MarshalAs(UnmanagedType.LPStr)] string pathname, int flags);

        [DllImport("libc", EntryPoint = "close", SetLastError = true)]
        private static extern int LinuxClose(int fd);

        [DllImport("libc", EntryPoint = "ioctl", SetLastError = true)]
        private static extern int LinuxIoctl(int fd, uint request, IntPtr argp);

        /// <summary>
        /// Linux sg_io_hdr_t structure for SG_IO ioctl (x64 layout).
        /// See linux/sg.h - total size 88 bytes on x86_64.
        /// </summary>
        [StructLayout(LayoutKind.Explicit, Size = 88)]
        private struct sg_io_hdr_t
        {
            [FieldOffset(0)] public int interface_id;       // 'S' for SCSI
            [FieldOffset(4)] public int dxfer_direction;    // SG_DXFER_FROM_DEV or SG_DXFER_TO_DEV
            [FieldOffset(8)] public byte cmd_len;           // CDB length
            [FieldOffset(9)] public byte mx_sb_len;         // max sense buffer length
            [FieldOffset(10)] public ushort iovec_count;    // 0 for simple I/O
            [FieldOffset(12)] public uint dxfer_len;        // data transfer length
            [FieldOffset(16)] public IntPtr dxferp;         // pointer to data buffer
            [FieldOffset(24)] public IntPtr cmdp;           // pointer to CDB
            [FieldOffset(32)] public IntPtr sbp;            // pointer to sense buffer
            [FieldOffset(40)] public uint timeout;          // timeout in milliseconds
            [FieldOffset(44)] public uint flags;            // 0
            [FieldOffset(48)] public int pack_id;           // unused
            [FieldOffset(56)] public IntPtr usr_ptr;        // unused
            [FieldOffset(64)] public byte status;           // SCSI status
            [FieldOffset(65)] public byte masked_status;
            [FieldOffset(66)] public byte msg_status;
            [FieldOffset(67)] public byte sb_len_wr;        // sense buffer bytes written
            [FieldOffset(68)] public ushort host_status;
            [FieldOffset(70)] public ushort driver_status;
            [FieldOffset(72)] public int resid;
            [FieldOffset(76)] public uint duration;
            [FieldOffset(80)] public uint info;
        }

        private int _fd;

        public string DevicePath { get; }

        internal SgIoScsiTransport(int fd, string devicePath)
        {
            _fd = fd;
            DevicePath = devicePath;
        }

        public bool SendCommand(byte[] cdb, byte[] data, ScsiDataDirection direction)
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
                    // zero-length data phases (e.g. TEST UNIT READY) must use SG_DXFER_NONE
                    dxfer_direction = data.Length == 0 ? SG_DXFER_NONE
                        : direction == ScsiDataDirection.FromDevice ? SG_DXFER_FROM_DEV : SG_DXFER_TO_DEV,
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
                        Logger.Warning("SgIoScsiTransport: SG_IO ioctl failed, errno {Error}", errno);
                        return false;
                    }

                    // Read back the header to check SCSI status
                    var result = Marshal.PtrToStructure<sg_io_hdr_t>(hdrPtr);
                    if (result.status != 0)
                    {
                        Logger.Warning("SgIoScsiTransport: SCSI command failed with status 0x{Status:X2}", result.status);
                        return false;
                    }

                    if (result.host_status != 0 || result.driver_status != 0)
                    {
                        Logger.Warning("SgIoScsiTransport: Transport error host=0x{Host:X4} driver=0x{Driver:X4}",
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
                Logger.Debug("SgIoScsiTransport: Closed {Path}", DevicePath);
                _fd = -1;
            }
        }
    }

    /// <summary>Registers all Linux platform implementations. Call once at host startup.</summary>
    public static class LinuxPlatform
    {
        public static void Register()
        {
            PlatformServices.ScsiTransport = new SgIoScsiTransportProvider();
            PlatformServices.Autostart = new XdgAutostartService();
            PlatformServices.Hotkeys = new X11GlobalHotkeyService();
            PlatformServices.ForegroundApp = new X11ForegroundAppService();
        }
    }
}
