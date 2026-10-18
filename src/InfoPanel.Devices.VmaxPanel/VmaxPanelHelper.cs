using HidSharp;
using LibUsbDotNet;
using LibUsbDotNet.Main;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;

namespace InfoPanel.VmaxPanel
{
    public class VmaxPanelDiscoveryInfo
    {
        public string DeviceId { get; set; } = "";
        public string DeviceLocation { get; set; } = "";
        public string DevicePath { get; set; } = "";
        public int VendorId { get; set; }
        public int ProductId { get; set; }
        public string SerialNumber { get; set; } = "";
        /// <summary>The ID earlier releases saved for this device, which added every MS9132 as a VMAX 4.6".</summary>
        public string LegacyDeviceId { get; set; } = "";
        public VmaxPanelModel Model { get; set; }
        public VmaxPanelModelInfo? ModelInfo { get; set; }
    }

    public static class VmaxPanelHelper
    {
        private static readonly ILogger Logger = Log.ForContext(typeof(VmaxPanelHelper));

        public static List<VmaxPanelDiscoveryInfo> ScanDevices(List<string>? issues = null)
        {
            var devices = new List<VmaxPanelDiscoveryInfo>();

            foreach (var (vendorId, productId) in VmaxPanelModelDatabase.SupportedDevices)
            {
                try
                {
                    devices.AddRange(ScanUsbDevices(vendorId, productId, issues));
                }
                catch (Exception ex)
                {
                    Logger.Warning(ex, "VmaxPanelHelper: LibUsb scan failed for VID_{VendorId:X4}&PID_{ProductId:X4}", vendorId, productId);
                }
            }

            Logger.Information("VmaxPanelHelper: Found {Count} device(s)", devices.Count);
            return devices;
        }

        public static VmaxPanelModelInfo? ModelFromSize((int Width, int Height)? size) =>
            size is { } geometry
                ? VmaxPanelModelDatabase.GetModelByPanelSize(geometry.Width, geometry.Height)
                : null;

        /// <summary>True when the EDID positively identifies a non-VMAX panel on the shared bridge.</summary>
        public static bool IsOtherMs9132Panel((int Width, int Height)? size) =>
            size is (376, 960) || ModelFromSize(size) is { Model: not VmaxPanelModel.Vmax46Inch };

        /// <summary>
        /// Model for a readable EDID. The VMAX 4.6" EDID has not been captured, so any
        /// geometry that is neither the DS339 nor a known Valkyrie keeps the VMAX path
        /// earlier releases always used.
        /// </summary>
        public static VmaxPanelModelInfo? ClassifyMs9132((int Width, int Height) size) =>
            size is (376, 960) ? null // Jonsbo DS339
                : ModelFromSize(size) ?? VmaxPanelModelDatabase.Models[VmaxPanelModel.Vmax46Inch];

        private static string SafeSerial(HidDevice hid)
        {
            try { return hid.GetSerialNumber() ?? ""; } catch { return ""; }
        }

        private static List<VmaxPanelDiscoveryInfo> ScanUsbDevices(int vendorId, int productId, List<string>? issues)
        {
            var devices = new List<VmaxPanelDiscoveryInfo>();
            if (vendorId == 0x374A && productId == 0xA021)
                return ValkyrieUsbDevice.ScanDevices();
            var hidDevices = DeviceList.Local.GetHidDevices(vendorId, productId).ToList();
            var usbEntries = UsbDevice.AllDevices.Cast<UsbRegistry>()
                .Where(r => r.Vid == vendorId && r.Pid == productId).ToList();

            foreach (UsbRegistry deviceReg in usbEntries)
            {
                using var usb = deviceReg.Device;
                if (usb == null)
                {
                    issues?.Add("LCD USB device could not be opened. Check USB permissions, then scan again.");
                    Logger.Warning("VmaxPanelHelper: Cannot open USB device; check udev permissions");
                    continue;
                }
                var serial = usb.Info.SerialString ?? "";
                // Match both interfaces of the same physical panel before probing. Without
                // a serial, a single attached bridge is unambiguous.
                var hid = !string.IsNullOrEmpty(serial)
                    ? hidDevices.FirstOrDefault(h => SafeSerial(h) == serial)
                    : usbEntries.Count == 1 && hidDevices.Count == 1 ? hidDevices[0] : null;
                var size = hid == null ? null : Ms9132PanelProbe.ReadSize(hid.DevicePath);
                if (size == null)
                {
                    issues?.Add("MS9132 panel could not be identified. Check USB/HID permissions, then scan again.");
                    continue;
                }
                var modelInfo = ClassifyMs9132(size.Value);
                if (modelInfo == null) continue;
                if (modelInfo.Model == VmaxPanelModel.ValkyrieV360 && string.IsNullOrEmpty(serial))
                {
                    // The V360 driver pairs its HID and video interfaces by serial.
                    issues?.Add("Valkyrie V360 reports no USB serial, so it cannot be paired safely.");
                    continue;
                }

                deviceReg.DeviceProperties.TryGetValue("DeviceID", out var deviceIdValue);
                deviceReg.DeviceProperties.TryGetValue("LocationInformation", out var locationValue);

                var deviceId = deviceIdValue as string ?? deviceReg.DevicePath;
                var legacyDeviceId = deviceId;
                var location = locationValue as string ?? deviceReg.SymbolicName ?? string.Empty;
                if (modelInfo.Model == VmaxPanelModel.ValkyrieV360)
                {
                    deviceId = $@"USB\VID_{vendorId:X4}&PID_{productId:X4}\{serial}";
                    location = serial;
                }

                Logger.Information("VmaxPanelHelper: Found {Model} at {DeviceId}", modelInfo.Name, deviceId);

                devices.Add(new VmaxPanelDiscoveryInfo
                {
                    DeviceId = deviceId,
                    DeviceLocation = location,
                    DevicePath = deviceReg.DevicePath,
                    SerialNumber = serial,
                    LegacyDeviceId = legacyDeviceId,
                    VendorId = vendorId,
                    ProductId = productId,
                    Model = modelInfo.Model,
                    ModelInfo = modelInfo,
                });
            }

            return devices;
        }

    }
}
