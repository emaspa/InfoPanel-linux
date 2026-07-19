using Serilog;
using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace InfoPanel
{
    /// <summary>
    /// Direct hidraw feature-report I/O for MacroSilicon MS9132-class chips with
    /// 8-byte unnumbered reports (Jonsbo DS339, VMAX 4.6"). HidSharp's Linux
    /// GetFeature issues HIDIOCGFEATURE with length-1 (8), which the kernel rejects
    /// with EOVERFLOW for these devices; the chip needs the full 9-byte convention
    /// ([0]=report number 0, then 8 data bytes) on both set and get - verified
    /// against real DS339 hardware.
    /// </summary>
    public sealed class LinuxHidRawFeature : IDisposable
    {
        private static readonly ILogger Logger = Log.ForContext<LinuxHidRawFeature>();

        // _IOC(READ|WRITE, 'H', nr, 9): 0xC000_0000 | (9 << 16) | ('H' << 8) | nr
        private const uint HIDIOCSFEATURE_9 = 0xC0094806;
        private const uint HIDIOCGFEATURE_9 = 0xC0094807;
        private const int O_RDWR = 2;

        [DllImport("libc", EntryPoint = "open", SetLastError = true)]
        private static extern int NativeOpen(string path, int flags);

        [DllImport("libc", EntryPoint = "ioctl", SetLastError = true)]
        private static extern int NativeIoctl(int fd, uint request, byte[] buf);

        [DllImport("libc", EntryPoint = "close")]
        private static extern int NativeClose(int fd);

        private int _fd;

        private LinuxHidRawFeature(int fd) => _fd = fd;

        /// <summary>
        /// Opens the /dev/hidrawN node for a HID device path (accepts HidSharp's
        /// sysfs-style path ending in .../hidraw/hidrawN, or /dev/hidrawN directly).
        /// </summary>
        public static LinuxHidRawFeature? Open(string devicePath)
        {
            var name = devicePath.Split('/').LastOrDefault() ?? "";
            if (!name.StartsWith("hidraw", StringComparison.Ordinal))
            {
                Logger.Warning("LinuxHidRawFeature: Unexpected device path {Path}", devicePath);
                return null;
            }

            var fd = NativeOpen("/dev/" + name, O_RDWR);
            if (fd < 0)
            {
                Logger.Warning("LinuxHidRawFeature: Cannot open /dev/{Name} (errno {Errno})",
                    name, Marshal.GetLastWin32Error());
                return null;
            }
            return new LinuxHidRawFeature(fd);
        }

        /// <summary>Sends an 8-byte feature report.</summary>
        public void SetFeature(byte[] payload8)
        {
            var buf = new byte[9];
            Array.Copy(payload8, 0, buf, 1, Math.Min(payload8.Length, 8));
            if (NativeIoctl(_fd, HIDIOCSFEATURE_9, buf) < 0)
                throw new IOException($"HIDIOCSFEATURE failed (errno {Marshal.GetLastWin32Error()})");
        }

        /// <summary>Reads the 8-byte feature report; data starts at the returned index 1
        /// ([1]=echoed opcode, [4]=first data byte for B5 register reads).</summary>
        public byte[] GetFeature()
        {
            var buf = new byte[9];
            if (NativeIoctl(_fd, HIDIOCGFEATURE_9, buf) < 0)
                throw new IOException($"HIDIOCGFEATURE failed (errno {Marshal.GetLastWin32Error()})");
            return buf;
        }

        public void Dispose()
        {
            if (_fd >= 0)
            {
                NativeClose(_fd);
                _fd = -1;
            }
        }
    }
}
