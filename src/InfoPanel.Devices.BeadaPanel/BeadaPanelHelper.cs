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

            UsbConfiguration.EnsureConfiguration(usbDevice);

            if (!wholeUsbDevice.ClaimInterface(0))
            {
                Logger.Warning("BeadaPanel: claiming interface 0 failed: {Error}", UsbDevice.LastErrorString);
            }
        }

        private const int MaxPacket = 512;

        /// <summary>
        /// Sends GET_PANEL_INFO and returns the parsed reply. The interface must already be claimed.
        /// Whatever the panel already has queued on its IN endpoints is read first: a reply that
        /// arrived after an earlier probe gave up, or the status packet firmware 7.x and later
        /// pushes on 0x81 by itself. Until that data is read the panel stops taking commands on
        /// 0x02, so every later probe's write times out and the panel never comes up (#14). The
        /// reply is taken from 0x82, or from 0x81 when the panel answers there instead.
        /// </summary>
        internal static BeadaPanelInfo? QueryPanelInfo(UsbDevice usbDevice, string devicePath)
        {
            using var statusReader = usbDevice.OpenEndpointReader(ReadEndpointID.Ep02);
            using var pushReader = usbDevice.OpenEndpointReader(ReadEndpointID.Ep01);
            var queued = Drain(statusReader, devicePath) ?? Drain(pushReader, devicePath);

            var infoMessage = new StatusLinkMessage
            {
                Type = StatusLinkMessageType.GetPanelInfo
            };

            using var writer = usbDevice.OpenEndpointWriter(WriteEndpointID.Ep02);
            var writeResult = writer.Write(infoMessage.ToBuffer(), 1000, out int _);

            if (writeResult != ErrorCode.None)
            {
                Logger.Error("StatusLink Query: Write failed on {DevicePath} with error {ErrorCode}", devicePath, writeResult);
                if (queued != null)
                {
                    Logger.Information("StatusLink Query: using the panel info {DevicePath} had queued", devicePath);
                }
                return queued;
            }

            var reply = Read(statusReader, 1000, out var readResult);
            if (reply == null)
            {
                reply = Read(pushReader, 500, out var pushResult);
                if (reply != null)
                {
                    Logger.Information("StatusLink Query: {DevicePath} answered on endpoint 0x81 instead of 0x82", devicePath);
                }
                else
                {
                    Logger.Error("StatusLink Query: Read failed on {DevicePath} with error {ErrorCode} (0x82), {PushError} (0x81)",
                        devicePath, readResult, pushResult);
                    return queued;
                }
            }

            return BeadaPanelParser.ParsePanelInfoResponse(reply) ?? queued;
        }

        /// <summary>Reads and discards up to four queued packets; returns the first one that is panel info.</summary>
        private static BeadaPanelInfo? Drain(UsbEndpointReader reader, string devicePath)
        {
            BeadaPanelInfo? info = null;
            for (var i = 0; i < 4; i++)
            {
                var packet = Read(reader, 50, out _);
                if (packet == null)
                {
                    break;
                }

                var parsed = IsStatusLink(packet) ? BeadaPanelParser.ParsePanelInfoResponse(packet) : null;
                info ??= parsed;
                Logger.Information("StatusLink Query: read {Bytes} queued bytes from {DevicePath} endpoint 0x{Endpoint:x2}{Kind}",
                    packet.Length, devicePath, (byte)reader.EpNum, parsed != null ? " (panel info)" : "");
            }
            return info;
        }

        private static byte[]? Read(UsbEndpointReader reader, int timeoutMs, out ErrorCode result)
        {
            var buffer = new byte[MaxPacket];
            result = reader.Read(buffer, timeoutMs, out int bytesRead);
            if (result != ErrorCode.None || bytesRead == 0)
            {
                return null;
            }
            // The parser expects at least the 100-byte GET_PANEL_INFO reply; keep the zero padding.
            return buffer[..Math.Max(bytesRead, 100)];
        }

        private static bool IsStatusLink(byte[] packet) =>
            packet.Length >= 13 && System.Text.Encoding.ASCII.GetString(packet, 0, 11) == "STATUS-LINK";

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

            var panelInfo = QueryPanelInfo(usbDevice, usbRegistry.DevicePath);
            if (panelInfo == null)
            {
                return null;
            }

            _panelInfoCache.Set(usbRegistry.DevicePath, panelInfo, new MemoryCacheEntryOptions()
            {
                AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(2)
            });

            Log.Information("StatusLink Query: Successfully parsed panel info for serial {SerialNumber}", panelInfo.SerialNumber);
            return panelInfo;
        }
    }
}
