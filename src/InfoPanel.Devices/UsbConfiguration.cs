using LibUsbDotNet;
using Serilog;

namespace InfoPanel
{
    public static class UsbConfiguration
    {
        private static readonly ILogger Logger = Log.ForContext(typeof(UsbConfiguration));

        /// <summary>
        /// Selects <paramref name="config"/> only when it is not already the active configuration.
        /// On Linux, SET_CONFIGURATION for the active configuration is not a no-op: usbfs turns
        /// it into usb_reset_configuration(), which resets the device's endpoints. The Windows
        /// app reaches panels through WinUSB, whose device class is not IUsbDevice, so it never
        /// sends the request at all. Sending it on every probe kept a freshly powered BeadaPanel
        /// from ever answering (#14). Returns false only when a needed SetConfiguration failed.
        /// </summary>
        public static bool EnsureConfiguration(UsbDevice usbDevice, byte config = 1)
        {
            if (usbDevice is not IUsbDevice wholeUsbDevice)
            {
                return true;
            }

            if (usbDevice.GetConfiguration(out byte active) && active == config)
            {
                return true;
            }

            Logger.Information("USB {DevicePath}: active configuration is {Active}, selecting {Config}",
                usbDevice.DevicePath, active, config);

            if (!wholeUsbDevice.SetConfiguration(config))
            {
                Logger.Warning("USB {DevicePath}: SetConfiguration({Config}) failed: {Error}",
                    usbDevice.DevicePath, config, UsbDevice.LastErrorString);
                return false;
            }

            return true;
        }
    }
}
