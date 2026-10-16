using LibUsbDotNet;
using LibUsbDotNet.Main;
using Serilog;
using System.Buffers.Binary;

namespace InfoPanel.VmaxPanel;

/// <summary>Experimental MC360 mode-2 transport; restricted to 374A:A021, 640x480.</summary>
internal sealed class ValkyrieUsbDevice : IValkyriePanelDevice
{
    private readonly UsbDevice _usb;
    private readonly IUsbDevice _whole;
    private UsbEndpointWriter? _writer;
    private UsbEndpointReader? _reader;
    private bool _claimed;
    private bool _disposed;
    private bool _enabled = true;
    private bool _hasFrame;
    private readonly byte[] _black = new byte[ValkyrieUsbProtocol.PixelDataLength];
    private DateTime _lastActivity = DateTime.MinValue;
    private const int TimeoutMs = 2000;

    private ValkyrieUsbDevice(UsbDevice usb)
    {
        _usb = usb;
        _whole = usb as IUsbDevice ?? throw new IOException("MC360 requires a libusb whole-device handle.");
    }

    private static bool Matches(UsbRegistry entry) => entry.Vid == 0x374A && entry.Pid == 0xA021;

    private static string DeviceId(UsbRegistry entry, string serial) => string.IsNullOrEmpty(serial)
        ? entry.DevicePath : $@"USB\VID_374A&PID_A021\{serial}";

    public static List<VmaxPanelDiscoveryInfo> ScanDevices()
    {
        var found = new List<VmaxPanelDiscoveryInfo>();
        foreach (UsbRegistry entry in UsbDevice.AllDevices)
        {
            if (!Matches(entry)) continue;
            try
            {
                using var device = OpenEntry(entry, null);
                if (device == null) continue;
                string serial = device._usb.Info.SerialString ?? "";
                var info = VmaxPanelModelDatabase.Models[VmaxPanelModel.ValkyrieMc360];
                found.Add(new VmaxPanelDiscoveryInfo
                {
                    DeviceId = DeviceId(entry, serial), DeviceLocation = string.IsNullOrEmpty(serial) ? entry.DevicePath : serial,
                    DevicePath = entry.DevicePath, SerialNumber = serial, VendorId = entry.Vid, ProductId = entry.Pid,
                    Model = info.Model, ModelInfo = info,
                });
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "MC360 probe failed for {Path}", entry.DevicePath);
            }
        }
        return found;
    }

    public static ValkyrieUsbDevice? Open(string deviceId)
    {
        foreach (UsbRegistry entry in UsbDevice.AllDevices)
        {
            if (!Matches(entry)) continue;
            var device = OpenEntry(entry, deviceId);
            if (device != null) return device;
        }
        return null;
    }

    private static ValkyrieUsbDevice? OpenEntry(UsbRegistry entry, string? expectedId)
    {
        var usb = entry.Device;
        if (usb == null) return null;
        ValkyrieUsbDevice? device = null;
        try
        {
            if (!ValkyrieUsbProtocol.IsMc360Descriptor(entry.Vid, entry.Pid, usb.Info.ProductString))
            {
                usb.Close();
                return null;
            }
            if (expectedId != null && !string.Equals(expectedId, DeviceId(entry, usb.Info.SerialString ?? ""), StringComparison.Ordinal))
            {
                usb.Close();
                return null;
            }
            device = new ValkyrieUsbDevice(usb);
            device._claimed = device._whole.ClaimInterface(0);
            if (!device._claimed) throw new IOException("Cannot claim MC360 USB interface 0; check udev permissions.");
            device._writer = usb.OpenEndpointWriter(WriteEndpointID.Ep02);
            device._reader = usb.OpenEndpointReader(ReadEndpointID.Ep01);
            device.Status(0x1003, [0x45, 0x23, 0x01, 0x00]);
            ValkyrieUsbProtocol.ValidateScreenInfo(device.Transact(0x0001, [], 28));
            // Do not set brightness or blank the screen while only probing it.
            if (expectedId != null) device.Status(0x0002, [100]);
            return device;
        }
        catch
        {
            if (device != null) device.Dispose(); else usb.Close();
            throw;
        }
    }

    private byte[] Transact(ushort command, byte[] payload, int replyLength = 4)
    {
        Write(ValkyrieUsbProtocol.Request(command, payload));
        byte[] response = new byte[6 + replyLength];
        var result = _reader!.Read(response, TimeoutMs, out int read);
        if (result != ErrorCode.None) throw new IOException($"MC360 read failed: {result}.");
        _lastActivity = DateTime.UtcNow;
        return ValkyrieUsbProtocol.Reply(command, response.AsSpan(0, read), replyLength);
    }

    private void Status(ushort command, byte[] payload)
    {
        var reply = Transact(command, payload);
        if (BinaryPrimitives.ReadInt32LittleEndian(reply) != 0)
            throw new IOException($"MC360 command {command:X4} failed: {Convert.ToHexString(reply)}.");
    }

    private void Write(byte[] bytes, int offset = 0, int? count = null)
    {
        int length = count ?? bytes.Length;
        var result = _writer!.Write(bytes, offset, length, TimeoutMs, out int sent);
        if (result != ErrorCode.None || sent != length)
            throw new IOException($"MC360 write failed: {result}, {sent}/{length} bytes.");
    }

    public void SendFrame(byte[] pixels, int length)
    {
        ValkyrieUsbProtocol.WriteFrame(_enabled ? pixels : _black, length,
            size => Status(0x1001, size), (buffer, offset, count) => Write(buffer, offset, count));
        _lastActivity = DateTime.UtcNow;
        _hasFrame = true;
    }

    public void SetScreenSwitch(bool enabled)
    {
        if (_enabled == enabled) return;
        _enabled = enabled;
        if (!enabled && _hasFrame) SendFrame(_black, _black.Length);
        // Enabling invalidates the render cache in the task, sending real pixels.
    }

    public void KeepAlive()
    {
        if ((DateTime.UtcNow - _lastActivity).TotalSeconds >= 1)
            Status(0x000F, [0x78, 0x56, 0x34, 0x12]);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // Retain the last image, as the upstream V360 path does on exit.
        _reader?.Dispose();
        _writer?.Dispose();
        if (_claimed) { try { _whole.ReleaseInterface(0); } catch { } }
        _usb.Close();
    }
}
