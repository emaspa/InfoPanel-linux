using HidSharp;
using LibUsbDotNet;
using LibUsbDotNet.Main;
using Serilog;
using System.Collections.Generic;
using System.Linq;

namespace InfoPanel.ThermalrightPanel
{
    public static class ThermalrightPanelHelper
    {
        private static readonly ILogger Logger = Log.ForContext(typeof(ThermalrightPanelHelper));

        /// <summary>
        /// Scans for all connected Thermalright panel devices.
        /// Tries both USB (LibUsbDotNet) and HID (HidSharp) discovery.
        /// </summary>
        /// <returns>List of discovered Thermalright panel device info</returns>
        public static List<ThermalrightPanelDiscoveryInfo> ScanDevices()
        {
            var devices = new List<ThermalrightPanelDiscoveryInfo>();

            // Scan for all supported VID/PID pairs
            foreach (var (vendorId, productId) in ThermalrightPanelModelDatabase.SupportedDevices)
            {
                Logger.Information("ThermalrightPanelHelper: Scanning for USB devices VID={VendorId:X4} PID={ProductId:X4}",
                    vendorId, productId);

                foreach (UsbRegistry deviceReg in UsbDevice.AllDevices)
                {
                    if (deviceReg.Vid == vendorId && deviceReg.Pid == productId)
                    {
                        var deviceId = deviceReg.DeviceProperties["DeviceID"] as string;
                        var deviceLocation = deviceReg.DeviceProperties["LocationInformation"] as string;

                        Logger.Information("ThermalrightPanelHelper: USB device found - Path: {Path}", deviceReg.DevicePath);

                        if (string.IsNullOrEmpty(deviceId) || string.IsNullOrEmpty(deviceLocation))
                        {
                            Logger.Warning("ThermalrightPanelHelper: Found device but missing DeviceID or LocationInformation");
                            continue;
                        }

                        // Get model info based on VID/PID (works for unique VID/PID like Trofeo)
                        var modelInfo = ThermalrightPanelModelDatabase.GetModelByVidPid(vendorId, productId);

                        var discoveryInfo = new ThermalrightPanelDiscoveryInfo
                        {
                            DeviceId = deviceId,
                            DeviceLocation = deviceLocation,
                            DevicePath = deviceReg.DevicePath,
                            VendorId = vendorId,
                            ProductId = productId,
                            Model = modelInfo?.Model ?? ThermalrightPanelModel.Unknown,
                            ModelInfo = modelInfo
                        };

                        Logger.Information("ThermalrightPanelHelper: Found {Model} at {Location}",
                            modelInfo?.Name ?? "Unknown", deviceLocation);

                        devices.Add(discoveryInfo);
                    }
                }
            }

            Logger.Information("ThermalrightPanelHelper: Scan complete, found {Count} devices", devices.Count);
            return devices;
        }
    }

    public class ThermalrightPanelDiscoveryInfo
    {
        public string DeviceId { get; init; } = string.Empty;
        public string DeviceLocation { get; init; } = string.Empty;
        public string DevicePath { get; init; } = string.Empty;
        public int VendorId { get; init; }
        public int ProductId { get; init; }
        public ThermalrightPanelModel Model { get; init; }
        public ThermalrightPanelModelInfo? ModelInfo { get; init; }
    }
}
