using InfoPanel.Models;
using LibUsbDotNet.Main;
using Serilog;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace InfoPanel.LianLiPanel
{
    public static class LianLiPanelHelper
    {
        private static readonly ILogger Logger = Log.ForContext(typeof(LianLiPanelHelper));

        public static Task<List<LianLiPanelDevice>> GetUsbDevices()
        {
            try
            {
                List<LianLiPanelDevice> devices = [];

                foreach (UsbRegistry deviceReg in LibUsbDotNet.UsbDevice.AllDevices)
                {
                    var modelInfo = LianLiPanelModelDatabase.GetModelByVidPid(deviceReg.Vid, deviceReg.Pid);
                    if (modelInfo == null)
                    {
                        continue;
                    }

                    var deviceId = deviceReg.DeviceProperties.TryGetValue("DeviceID", out var devIdObj) && devIdObj is string devIdStr
                        ? devIdStr : deviceReg.DevicePath;
                    var deviceLocation = deviceReg.DeviceProperties.TryGetValue("LocationInformation", out var locObj) && locObj is string locStr
                        ? locStr : deviceReg.DevicePath;

                    if (string.IsNullOrEmpty(deviceId) || string.IsNullOrEmpty(deviceLocation))
                    {
                        Logger.Warning("LianLiPanel Discovery: Skipping device with missing properties at {Path}", deviceReg.DevicePath);
                        continue;
                    }

                    Logger.Information("Found Lian Li panel device: {Name} at {Location} (ID: {DeviceId})",
                        modelInfo.Name, deviceLocation, deviceId);

                    devices.Add(new LianLiPanelDevice
                    {
                        DeviceId = deviceId,
                        DeviceLocation = deviceLocation,
                        Model = modelInfo.Model,
                    });
                }

                return Task.FromResult(devices);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "LianLiPanelHelper: Error getting USB devices");
                return Task.FromResult(new List<LianLiPanelDevice>());
            }
        }
    }
}
