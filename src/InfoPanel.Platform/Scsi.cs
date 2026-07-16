namespace InfoPanel.Platform
{
    public enum ScsiDataDirection
    {
        FromDevice,
        ToDevice
    }

    /// <summary>A discovered SCSI generic device.</summary>
    public sealed record ScsiDeviceInfo(string DevicePath, string VendorId, string ProductId);

    /// <summary>
    /// An open SCSI pass-through channel to one device. Implementations:
    /// Linux SG_IO ioctl on /dev/sgN; Windows SPTI (later).
    /// </summary>
    public interface IScsiTransport : IDisposable
    {
        string DevicePath { get; }

        /// <summary>Sends a CDB with a data phase in the given direction. Returns false on any SCSI/transport error.</summary>
        bool SendCommand(byte[] cdb, byte[] data, ScsiDataDirection direction);
    }

    public interface IScsiTransportProvider
    {
        /// <summary>Enumerates SCSI generic devices whose vendor string contains <paramref name="vendorFilter"/>.</summary>
        List<ScsiDeviceInfo> FindDevices(string vendorFilter);

        IScsiTransport? Open(string devicePath);
    }

    /// <summary>
    /// Platform service registry. The platform-specific assembly registers its
    /// implementations at host startup.
    /// </summary>
    public static class PlatformServices
    {
        public static IScsiTransportProvider? ScsiTransport { get; set; }

        public static IAutostartService? Autostart { get; set; }
    }
}
