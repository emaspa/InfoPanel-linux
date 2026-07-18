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
    public partial class TuringPanelHelper
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
                        string deviceId;
                        string deviceLocation;

                        if (deviceReg.DeviceProperties.TryGetValue("DeviceID", out var devIdObj) && devIdObj is string devIdStr)
                            deviceId = devIdStr;
                        else
                            deviceId = deviceReg.DevicePath ?? $"USB\\VID_{deviceReg.Vid:X4}&PID_{deviceReg.Pid:X4}";

                        if (deviceReg.DeviceProperties.TryGetValue("LocationInformation", out var locObj) && locObj is string locStr)
                            deviceLocation = locStr;
                        else
                            deviceLocation = deviceReg.DevicePath ?? deviceId;

                        if (!string.IsNullOrEmpty(deviceId) && !string.IsNullOrEmpty(deviceLocation))
                        {
                            Logger.Information("Found Turing panel device: {Name} at {Location} (ID: {DeviceId})",
                                modelInfo.Name, deviceLocation, deviceId);

                            TuringPanelDevice device = new()
                            {
                                DeviceId = deviceId,
                                DeviceLocation = deviceLocation,
                                Model = modelInfo.Model.ToString(),
                                // Portrait-native strips are usually mounted landscape
                                Rotation = modelInfo.Height > modelInfo.Width
                                    ? LCD_ROTATION.Rotate90FlipNone
                                    : LCD_ROTATION.RotateNone,
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

            // ttyUSB devices (e.g. CH340-based Turing panels) and ttyACM devices
            // (CDC ACM class). Each class/bus entry is a symlink into the sysfs
            // device tree; resolve it (its parent directory is real, so the
            // relative link target resolves correctly) and walk up to the USB
            // device that carries idVendor/idProduct. Resolving deeper links like
            // entry/device instead fails: their relative targets would resolve
            // against a path that is itself a symlink (issue #1, "Found 0 devices").
            foreach (var (root, pattern) in new[] { ("/sys/bus/usb-serial/devices", "*"), ("/sys/class/tty", "ttyACM*") })
            {
                if (!Directory.Exists(root))
                {
                    continue;
                }

                foreach (var entry in Directory.GetDirectories(root, pattern))
                {
                    var name = Path.GetFileName(entry);
                    var portPath = $"/dev/{name}";

                    string? current;
                    try
                    {
                        current = Directory.ResolveLinkTarget(entry, returnFinalTarget: true)?.FullName ?? entry;
                    }
                    catch
                    {
                        current = entry;
                    }

                    while (!string.IsNullOrEmpty(current) && current != "/")
                    {
                        if (TryReadSysfsHex(Path.Combine(current, "idVendor"), out var vid)
                            && TryReadSysfsHex(Path.Combine(current, "idProduct"), out var pid))
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
                // No GetFullPath: it strips ".." segments lexically, but sysfs
                // paths only resolve correctly when the kernel walks them through
                // the symlinks (".." applies to the link target, not the link).
                if (!File.Exists(path))
                    return false;
                var text = File.ReadAllText(path).Trim();
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

                    // Check for CT21INCH identifier port (VID_1A86&PID_CA21) - companion of Turzx 5"
                    // panels (from emaspa/infopanel-1@2ce848f; the RevisionE model override was
                    // reverted in 636525e, so today it is detection + skip only)
                    bool hasCt21Inch = serialPorts.Any(p => p.vid == 0x1a86 && p.pid == 0xca21);

                    if (hasCt21Inch)
                    {
                        Logger.Information("Detected CT21INCH identifier port");
                    }

                    foreach (var (portPath, vid, pid) in serialPorts)
                    {
                        // Skip CT13INCH/CT21INCH CH340 companion ports from normal matching
                        if (vid == 0x1a86 && (pid == 0xca11 || pid == 0xca21))
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
                                    Model = model.ToString(),
                                    // Portrait-native strips are usually mounted landscape
                                    Rotation = modelInfo.Height > modelInfo.Width
                                        ? LCD_ROTATION.Rotate90FlipNone
                                        : LCD_ROTATION.RotateNone,
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