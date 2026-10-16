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

        private static List<VmaxPanelDiscoveryInfo> ScanUsbDevices(int vendorId, int productId, List<string>? issues)
        {
            var devices = new List<VmaxPanelDiscoveryInfo>();
            if (vendorId == 0x374A && productId == 0xA021)
                return ValkyrieUsbDevice.ScanDevices();
            var hidDevices = DeviceList.Local.GetHidDevices(vendorId, productId).ToList();

            foreach (UsbRegistry deviceReg in UsbDevice.AllDevices)
            {
                if (deviceReg.Vid != vendorId || deviceReg.Pid != productId)
                    continue;

                using var usb = deviceReg.Device;
                if (usb == null)
                {
                    issues?.Add("LCD USB device could not be opened. Check USB permissions, then scan again.");
                    Logger.Warning("VmaxPanelHelper: Cannot open USB device; check udev permissions");
                    continue;
                }
                var serial = usb.Info.SerialString ?? "";
                var hid = hidDevices.FirstOrDefault(h =>
                    !string.IsNullOrEmpty(serial) && h.GetSerialNumber() == serial);
                // Match both interfaces of the same physical panel before probing.
                var size = hid == null ? null : Ms9132PanelProbe.ReadSize(hid.DevicePath);
                var modelInfo = ModelFromSize(size);
                if (size == null)
                    issues?.Add("MS9132 panel could not be identified. Check USB/HID permissions, then scan again.");
                if (modelInfo == null) continue; // e.g. Jonsbo DS339, 376x960

                deviceReg.DeviceProperties.TryGetValue("DeviceID", out var deviceIdValue);
                deviceReg.DeviceProperties.TryGetValue("LocationInformation", out var locationValue);

                var deviceId = deviceIdValue as string ?? deviceReg.DevicePath;
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
