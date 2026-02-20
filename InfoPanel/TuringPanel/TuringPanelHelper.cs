using InfoPanel.Models;
using LibUsbDotNet;
using LibUsbDotNet.Main;
using Serilog;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace InfoPanel.TuringPanel
{
    internal partial class TuringPanelHelper
    {
        private static readonly ILogger Logger = Log.ForContext(typeof(TuringPanelHelper));
        private static readonly SemaphoreSlim _semaphore = new(1, 1);

        public static async Task<List<TuringPanelDevice>> GetUsbDevices()
        {
            await _semaphore.WaitAsync();

            try
            {
                List<TuringPanelDevice> devices = [];
                var allDevices = UsbDevice.AllDevices;

                foreach (UsbRegistry deviceReg in allDevices)
                {
                    if (TuringPanelModelDatabase.TryGetModelInfo(deviceReg.Vid, deviceReg.Pid, true, out var modelInfo))
                    {
                        var deviceId = deviceReg.DeviceProperties["DeviceID"] as string
                            ?? deviceReg.DevicePath ?? "";
                        var deviceLocation = deviceReg.DeviceProperties["LocationInformation"] as string
                            ?? deviceReg.DevicePath ?? "";

                        if (!string.IsNullOrEmpty(deviceId) && !string.IsNullOrEmpty(deviceLocation))
                        {
                            Logger.Information("Found Turing panel device: {Name} at {Location} (ID: {DeviceId})",
                                modelInfo.Name, deviceLocation, deviceId);

                            TuringPanelDevice device = new()
                            {
                                DeviceId = deviceId,
                                DeviceLocation = deviceLocation,
                                Model = modelInfo.Model.ToString()
                            };

                            devices.Add(device);
                        }
                    }
                }

                return devices;

            }
            catch (Exception ex)
            {
                Logger.Error(ex, "TuringPanelHelper: Error getting USB devices");
                return [];
            }
            finally
            {
                _semaphore.Release();
            }
        }


        /// <summary>
        /// Discovers Linux serial ports backed by USB devices via sysfs.
        /// Enumerates /sys/bus/usb-serial/devices/ (ttyUSB*) and /sys/class/tty/ (ttyACM*).
        /// </summary>
        private static List<(string portPath, int vid, int pid)> GetLinuxSerialPorts()
        {
            var results = new List<(string portPath, int vid, int pid)>();

            // ttyUSB devices (e.g. CH340-based Turing panels)
            string usbSerialPath = "/sys/bus/usb-serial/devices";
            if (Directory.Exists(usbSerialPath))
            {
                foreach (var entry in Directory.GetDirectories(usbSerialPath))
                {
                    var name = Path.GetFileName(entry);
                    var portPath = $"/dev/{name}";
                    var vidPath = Path.Combine(entry, "..", "..", "idVendor");
                    var pidPath = Path.Combine(entry, "..", "..", "idProduct");

                    if (TryReadSysfsHex(vidPath, out var vid) && TryReadSysfsHex(pidPath, out var pid))
                    {
                        results.Add((portPath, vid, pid));
                    }
                }
            }

            // ttyACM devices (CDC ACM class)
            string ttyClassPath = "/sys/class/tty";
            if (Directory.Exists(ttyClassPath))
            {
                foreach (var entry in Directory.GetDirectories(ttyClassPath, "ttyACM*"))
                {
                    var name = Path.GetFileName(entry);
                    var portPath = $"/dev/{name}";

                    // Walk up from /sys/class/tty/ttyACMN/device to find the USB device with idVendor/idProduct
                    var deviceLink = Path.Combine(entry, "device");
                    if (!Directory.Exists(deviceLink))
                        continue;

                    var resolved = Path.GetFullPath(deviceLink);
                    // Walk up until we find idVendor
                    var current = resolved;
                    while (current != null && current != "/")
                    {
                        var vidPath = Path.Combine(current, "idVendor");
                        var pidPath = Path.Combine(current, "idProduct");
                        if (File.Exists(vidPath) && File.Exists(pidPath)
                            && TryReadSysfsHex(vidPath, out var vid) && TryReadSysfsHex(pidPath, out var pid))
                        {
                            results.Add((portPath, vid, pid));
                            break;
                        }
                        current = Path.GetDirectoryName(current);
                    }
                }
            }

            return results;
        }

        private static bool TryReadSysfsHex(string path, out int value)
        {
            value = 0;
            try
            {
                var fullPath = Path.GetFullPath(path);
                if (!File.Exists(fullPath))
                    return false;
                var text = File.ReadAllText(fullPath).Trim();
                value = Convert.ToInt32(text, 16);
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static async Task<List<TuringPanelDevice>> GetSerialDevices()
        {
            await _semaphore.WaitAsync();
            try
            {
                var wakeCount = await WakeSerialDevices();
                var attempts = 1;
                while (wakeCount > 0)
                {
                    await Task.Delay(1000); // Wait a bit before checking again
                    wakeCount = await WakeSerialDevices();
                    attempts++;
                    if (attempts >= 5)
                    {
                        Logger.Warning("Max attempts reached while waking devices.");
                        break;
                    }
                }

                Logger.Information("No more sleeping devices to wake. Proceeding to search for Turing panel devices.");

                return await Task.Run(() =>
                {
                    List<TuringPanelDevice> devices = [];
                    var serialPorts = GetLinuxSerialPorts();

                    // Check for CT13INCH identifier port (VID=0x1A86, PID=0xCA11)
                    // When present, the companion 0525:A4A7 port is a 10.2" panel, not an 8.8"
                    bool hasCt13Inch = serialPorts.Any(p => p.vid == 0x1a86 && p.pid == 0xca11);

                    if (hasCt13Inch)
                    {
                        Logger.Information("Detected CT13INCH identifier port");
                    }

                    foreach (var (portPath, vid, pid) in serialPorts)
                    {
                        // Skip CT13INCH CH340 port from normal matching
                        if (vid == 0x1a86 && pid == 0xca11)
                        {
                            continue;
                        }

                        foreach (var kv in TuringPanelModelDatabase.Models)
                        {
                            if (kv.Value.VendorId == vid && kv.Value.ProductId == pid && !kv.Value.IsUsbDevice)
                            {
                                var model = kv.Key;
                                // Override to 10.2" when CT13INCH is present
                                if (hasCt13Inch && vid == 0x0525 && pid == 0xa4a7)
                                {
                                    model = TuringPanelModel.REV_13INCH_USB;
                                }

                                var modelInfo = TuringPanelModelDatabase.Models[model];
                                Logger.Information("Found Turing panel device: {Name} on {PortPath}", modelInfo.Name, portPath);

                                TuringPanelDevice device = new()
                                {
                                    DeviceId = $"USB\\VID_{vid:X4}&PID_{pid:X4}",
                                    DeviceLocation = portPath,
                                    Model = model.ToString()
                                };

                                devices.Add(device);
                                break;
                            }
                        }
                    }

                    Logger.Information("Found {Count} Turing panel devices", devices.Count);
                    return devices;
                });
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "TuringPanelHelper: Error getting Turing panel devices");
                return [];
            }
            finally
            {
                _semaphore.Release();
            }
        }

        private static async Task<int> WakeSerialDevices()
        {
            try
            {
                return await Task.Run(() =>
                {
                    var count = 0;
                    var serialPorts = GetLinuxSerialPorts();

                    foreach (var (portPath, vid, pid) in serialPorts)
                    {
                        // Only wake CH340 USB to Serial converters
                        if (vid != 0x1a86 || pid != 0x5722)
                            continue;

                        try
                        {
                            using var serialPort = new SerialPort(portPath, 115200);
                            serialPort.Open();
                            serialPort.Close();
                        }
                        catch (Exception ex)
                        {
                            Logger.Warning(ex, "TuringPanelHelper: Error opening device on {PortPath}", portPath);
                        }
                        count++;
                    }

                    Logger.Information("Found {Count} sleeping devices", count);

                    return count;
                });
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "TuringPanelHelper: Error waking sleeping devices");
                return 0;
            }
        }

        private static bool TryParseVidPid(string pnpDeviceId, out int vid, out int pid)
        {
            vid = 0;
            pid = 0;
            var match = MyRegex().Match(pnpDeviceId);
            if (match.Success)
            {
                vid = Convert.ToInt32(match.Groups[1].Value, 16);
                pid = Convert.ToInt32(match.Groups[2].Value, 16);
                return true;
            }
            return false;
        }

        [GeneratedRegex(@"VID_([0-9A-Fa-f]{4})&PID_([0-9A-Fa-f]{4})")]
        private static partial Regex MyRegex();
    }
}