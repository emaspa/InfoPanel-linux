using HidSharp;
using Serilog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace InfoPanel.JonsboPanel
{
    public class JonsboPanelDiscoveryInfo
    {
        public string DeviceId { get; set; } = "";
        public string DeviceLocation { get; set; } = ""; // serial port path or MS9132 serial number
        public int VendorId { get; set; }
        public int ProductId { get; set; }
        public JonsboPanelModel Model { get; set; }
        public JonsboPanelModelInfo? ModelInfo { get; set; }

        /// <summary>
        /// MS9132 entries only: true when the panel EDID confirmed DS339 geometry
        /// (376x960). Unreadable panels are omitted from discovery.
        /// </summary>
        public bool Ms9132Confirmed { get; set; } = true;
    }

    /// <summary>
    /// Discovers Jonsbo AIO panels: DS916 HLVMAX serial devices via sysfs (same
    /// ttyACM walk as JlPanelHelper), and DS339 MS9132 USB displays via their HID
    /// control interface. The MS9132 shares VID/PID 345F:9132 with the VMAX 4.6",
    /// so the panel EDID (register 0xC000, per the ms912x kernel driver) is read
    /// to tell the two apart: DS339 is 376x960, VMAX is 320x960.
    /// </summary>
    public static class JonsboPanelHelper
    {
        private static readonly ILogger Logger = Log.ForContext(typeof(JonsboPanelHelper));

        public static List<JonsboPanelDiscoveryInfo> ScanDevices(List<string>? issues = null)
        {
            var devices = new List<JonsboPanelDiscoveryInfo>();

            // DS916 (HLVMAX CDC ACM serial)
            try
            {
                foreach (var (portPath, vid, pid) in GetLinuxCdcPorts())
                {
                    var modelInfo = JonsboPanelModelDatabase.GetModelByVidPid(vid, pid);
                    if (modelInfo == null || modelInfo.TransportType != JonsboTransportType.Serial) continue;

                    Logger.Information("JonsboPanelHelper: Found {Name} on {Port} (VID={Vid:X4} PID={Pid:X4})",
                        modelInfo.Name, portPath, vid, pid);

                    devices.Add(new JonsboPanelDiscoveryInfo
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
                Logger.Warning(ex, "JonsboPanelHelper: Error scanning serial ports");
            }

            // DS339 (MacroSilicon MS9132): presence via the HID control interface,
            // identity via the panel EDID.
            try
            {
                foreach (var hid in DeviceList.Local.GetHidDevices(
                    JonsboPanelModelDatabase.MS9132_VENDOR_ID, JonsboPanelModelDatabase.MS9132_PRODUCT_ID))
                {
                    var modelInfo = JonsboPanelModelDatabase.Models[JonsboPanelModel.DS339];
                    var size = ProbeMs9132PanelSize(hid);

                    if (size == null)
                    {
                        issues?.Add("MS9132 panel could not be identified. Check USB/HID permissions, then scan again.");
                        Logger.Warning("JonsboPanelHelper: Unreadable MS9132 EDID; skipping unidentified panel");
                        continue;
                    }
                    if (!IsDs339(size))
                    {
                        // A different MS9132-bridged panel (e.g. VMAX 4.6" at 320x960):
                        // not ours, leave it to that family's scan.
                        Logger.Information(
                            "JonsboPanelHelper: MS9132 EDID reports {W}x{H}, not a DS339 - skipping",
                            size.Value.Width, size.Value.Height);
                    }
                    else
                    {
                        string serial = "";
                        try { serial = hid.GetSerialNumber(); } catch { }

                        var confirmed = size != null;
                        Logger.Information(
                            "JonsboPanelHelper: Found {Name} (MS9132, serial {Serial}, EDID {State})",
                            modelInfo.Name, serial, confirmed ? "confirmed" : "unreadable");

                        devices.Add(new JonsboPanelDiscoveryInfo
                        {
                            DeviceId = $"USB\\VID_{JonsboPanelModelDatabase.MS9132_VENDOR_ID:X4}&PID_{JonsboPanelModelDatabase.MS9132_PRODUCT_ID:X4}\\{serial}",
                            DeviceLocation = string.IsNullOrEmpty(serial) ? "MS9132" : serial,
                            VendorId = JonsboPanelModelDatabase.MS9132_VENDOR_ID,
                            ProductId = JonsboPanelModelDatabase.MS9132_PRODUCT_ID,
                            Model = modelInfo.Model,
                            ModelInfo = modelInfo,
                            Ms9132Confirmed = confirmed,
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warning(ex, "JonsboPanelHelper: Error scanning MS9132 devices");
            }

            return devices;
        }

        public static bool IsDs339((int Width, int Height)? size) => size is (376, 960);

        /// <summary>
        /// Reads the attached panel's native resolution from the first detailed timing
        /// descriptor of the EDID, which the MS9132 exposes one byte at a time at
        /// register 0xC000 + offset (B5 feature-report reads, per the ms912x driver).
        /// Returns null when the EDID cannot be read or parsed.
        /// </summary>
        public static (int Width, int Height)? ProbeMs9132PanelSize(HidDevice? hid = null)
        {
            hid ??= DeviceList.Local.GetHidDevices(
                JonsboPanelModelDatabase.MS9132_VENDOR_ID, JonsboPanelModelDatabase.MS9132_PRODUCT_ID)
                .FirstOrDefault();
            return hid == null ? null : Ms9132PanelProbe.ReadSize(hid.DevicePath);
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

                // Resolve the class symlink itself (its parent /sys/class/tty is a
                // real directory, so the relative target resolves correctly), then
                // walk up the real device tree to the USB device with idVendor.
                string? current;
                try
                {
                    current = Directory.ResolveLinkTarget(entry, returnFinalTarget: true)?.FullName ?? entry;
                }
                catch
                {
                    current = entry;
                }
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
