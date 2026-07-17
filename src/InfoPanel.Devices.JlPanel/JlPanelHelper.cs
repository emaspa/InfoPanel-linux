using Serilog;
using System;
using System.Collections.Generic;
using System.IO;

namespace InfoPanel.JlPanel
{
    public class JlPanelDiscoveryInfo
    {
        public string DeviceId { get; set; } = "";
        public string DeviceLocation { get; set; } = ""; // serial port path (e.g. "/dev/ttyACM0")
        public int VendorId { get; set; }
        public int ProductId { get; set; }
        public JlPanelModel Model { get; set; }
        public JlPanelModelInfo? ModelInfo { get; set; }
    }

    /// <summary>
    /// Discovers Jungle Leopard / Hongtai CDC serial panels. The Windows build scans
    /// Win32_SerialPort via WMI; here we walk sysfs (/sys/class/tty/ttyACM*) up to the
    /// owning USB device to read idVendor/idProduct — same approach as TuringPanelHelper.
    /// </summary>
    public static class JlPanelHelper
    {
        private static readonly ILogger Logger = Log.ForContext(typeof(JlPanelHelper));

        public static List<JlPanelDiscoveryInfo> ScanDevices()
        {
            var devices = new List<JlPanelDiscoveryInfo>();

            try
            {
                foreach (var (portPath, vid, pid) in GetLinuxCdcPorts())
                {
                    var modelInfo = JlPanelModelDatabase.GetModelByVidPid(vid, pid);
                    if (modelInfo == null) continue;

                    Logger.Information("JlPanelHelper: Found {Name} on {Port} (VID={Vid:X4} PID={Pid:X4})",
                        modelInfo.Name, portPath, vid, pid);

                    devices.Add(new JlPanelDiscoveryInfo
                    {
                        DeviceId = $"USB\\VID_{vid:X4}&PID_{pid:X4}\\{Path.GetFileName(portPath)}",
                        DeviceLocation = portPath,
                        VendorId = vid,
                        ProductId = pid,
                        Model = modelInfo.Model,
                        ModelInfo = modelInfo,
                    });
                }
            }
            catch (Exception ex)
            {
                Logger.Warning(ex, "JlPanelHelper: Error scanning serial ports");
            }

            return devices;
        }

        /// <summary>Enumerates ttyACM ports and resolves the owning USB device's VID/PID via sysfs.</summary>
        private static List<(string portPath, int vid, int pid)> GetLinuxCdcPorts()
        {
            var results = new List<(string, int, int)>();

            const string ttyClassPath = "/sys/class/tty";
            if (!Directory.Exists(ttyClassPath))
            {
                return results;
            }

            foreach (var entry in Directory.GetDirectories(ttyClassPath, "ttyACM*"))
            {
                var name = Path.GetFileName(entry);
                var portPath = $"/dev/{name}";

                var deviceLink = Path.Combine(entry, "device");
                if (!Directory.Exists(deviceLink))
                    continue;

                // Walk up from the tty's device node to the USB device that has idVendor/idProduct
                var current = Path.GetFullPath(deviceLink);
                while (!string.IsNullOrEmpty(current) && current != "/")
                {
                    var vidPath = Path.Combine(current, "idVendor");
                    var pidPath = Path.Combine(current, "idProduct");
                    if (File.Exists(vidPath) && File.Exists(pidPath)
                        && TryReadSysfsHex(vidPath, out var vid) && TryReadSysfsHex(pidPath, out var pid))
                    {
                        results.Add((portPath, vid, pid));
                        break;
                    }

                    current = Path.GetDirectoryName(current);
                }
            }

            return results;
        }

        private static bool TryReadSysfsHex(string path, out int value)
        {
            value = 0;
            try
            {
                if (!File.Exists(path))
                    return false;
                value = Convert.ToInt32(File.ReadAllText(path).Trim(), 16);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
