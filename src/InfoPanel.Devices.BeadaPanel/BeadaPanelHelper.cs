using InfoPanel.BeadaPanel.StatusLink;
using InfoPanel.Utils;
using LibUsbDotNet;
using LibUsbDotNet.Main;
using Microsoft.Extensions.Caching.Memory;
using Serilog;
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace InfoPanel.BeadaPanel
{
    public static class BeadaPanelHelper
    {
        private static readonly ILogger Logger = Log.ForContext(typeof(BeadaPanelHelper));
        private static readonly TypedMemoryCache<BeadaPanelInfo> _panelInfoCache = new();
        private static readonly SemaphoreSlim _semaphore = new(1, 1);

        public static async Task<BeadaPanelInfo?> GetPanelInfoAsync(UsbRegistry usbRegistry)
        {
            await _semaphore.WaitAsync();

            try
            {
                return await Task.Run(() => {
                    return GetPanelInfo(usbRegistry);
                });
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "BeadaPanelHelper: Error claiming USB interface");
            }
            finally
            {
                _semaphore.Release();
            }

            return null;
        }

        /// <summary>
        /// Selects configuration 1 only when it is not already active, then claims interface 0.
        /// On Linux, SET_CONFIGURATION for the configuration that is already active is not a
        /// no-op: usbfs turns it into usb_reset_configuration(), which resets the panel's
        /// endpoints. Sending it on every probe kept a freshly powered panel (cold boot or
        /// replug) from ever answering StatusLink, so the probe timed out forever (#14).
        /// The Windows app talks to the panel through WinUSB and never sends it at all.
        /// </summary>
        internal static void ClaimStatusInterface(UsbDevice usbDevice)
        {
            if (usbDevice is not IUsbDevice wholeUsbDevice)
            {
                return;
            }

            if (!usbDevice.GetConfiguration(out byte config) || config != 1)
            {
                Logger.Information("BeadaPanel: active configuration is {Config}, selecting configuration 1", config);
                wholeUsbDevice.SetConfiguration(1);
            }

            if (!wholeUsbDevice.ClaimInterface(0))
            {
                Logger.Warning("BeadaPanel: claiming interface 0 failed: {Error}", UsbDevice.LastErrorString);
            }
        }

        private static BeadaPanelInfo? GetPanelInfo(UsbRegistry usbRegistry)
        {
            if (_panelInfoCache.TryGetValue(usbRegistry.DevicePath, out var result))
            {
                return result;
            }

            // Try to open the specific device
            using var usbDevice = usbRegistry.Device;

            if (usbDevice == null)
            {
                Logger.Warning("StatusLink Query: Could not open USB device {DevicePath}", usbRegistry.DevicePath);
                return null;
            }

            ClaimStatusInterface(usbDevice);

            var infoMessage = new StatusLinkMessage
            {
                Type = StatusLinkMessageType.GetPanelInfo
            };

            using var writer = usbDevice.OpenEndpointWriter(WriteEndpointID.Ep02);
            var writeResult = writer.Write(infoMessage.ToBuffer(), 1000, out int _);

            if (writeResult != ErrorCode.None)
            {
                Logger.Error("StatusLink Query: Write failed with error {ErrorCode}", writeResult);
                return null;
            }

            // Read response
            byte[] responseBuffer = new byte[100];
            using var reader = usbDevice.OpenEndpointReader(ReadEndpointID.Ep02);
            var readResult = reader.Read(responseBuffer, 1000, out int bytesRead);

            if (readResult != ErrorCode.None || bytesRead == 0)
            {
                Logger.Error("StatusLink Query: Read failed on {DevicePath} with error {ErrorCode}, bytes read: {BytesRead}", usbRegistry.DevicePath, readResult, bytesRead);
                return null;
            }

            // Parse panel info
            var panelInfo = BeadaPanelParser.ParsePanelInfoResponse(responseBuffer);
            if (panelInfo != null)
            {

                _panelInfoCache.Set(usbRegistry.DevicePath, panelInfo, new MemoryCacheEntryOptions()
                {
                    AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(2)
                });

                Log.Information("StatusLink Query: Successfully parsed panel info for serial {SerialNumber}", panelInfo.SerialNumber);
            }

            return panelInfo;
        }
    }
}
