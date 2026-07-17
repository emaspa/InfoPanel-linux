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
        public VmaxPanelModel Model { get; set; }
        public VmaxPanelModelInfo? ModelInfo { get; set; }
    }

    public static class VmaxPanelHelper
    {
        private static readonly ILogger Logger = Log.ForContext(typeof(VmaxPanelHelper));

        public static List<VmaxPanelDiscoveryInfo> ScanDevices()
        {
            var devices = new List<VmaxPanelDiscoveryInfo>();

            foreach (var (vendorId, productId) in VmaxPanelModelDatabase.SupportedDevices)
            {
                try
                {
                    devices.AddRange(ScanUsbDevices(vendorId, productId));
                }
                catch (Exception ex)
                {
                    Logger.Warning(ex, "VmaxPanelHelper: LibUsb scan failed for VID_{VendorId:X4}&PID_{ProductId:X4}", vendorId, productId);
                }
            }

            Logger.Information("VmaxPanelHelper: Found {Count} device(s)", devices.Count);
            return devices;
        }

        private static List<VmaxPanelDiscoveryInfo> ScanUsbDevices(int vendorId, int productId)
        {
            var devices = new List<VmaxPanelDiscoveryInfo>();
            var modelInfo = VmaxPanelModelDatabase.GetModelByVidPid(vendorId, productId);
            if (modelInfo == null) return devices;

            foreach (UsbRegistry deviceReg in UsbDevice.AllDevices)
            {
                if (deviceReg.Vid != vendorId || deviceReg.Pid != productId)
                    continue;

                deviceReg.DeviceProperties.TryGetValue("DeviceID", out var deviceIdValue);
                deviceReg.DeviceProperties.TryGetValue("LocationInformation", out var locationValue);

                var deviceId = deviceIdValue as string ?? deviceReg.DevicePath;
                var location = locationValue as string ?? deviceReg.SymbolicName ?? string.Empty;

                Logger.Information("VmaxPanelHelper: Found {Model} at {DeviceId}", modelInfo.Name, deviceId);

                devices.Add(new VmaxPanelDiscoveryInfo
                {
                    DeviceId = deviceId,
                    DeviceLocation = location,
                    DevicePath = deviceReg.DevicePath,
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
