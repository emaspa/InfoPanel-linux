using InfoPanel.Extensions;
using InfoPanel.Models;
using InfoPanel.ThermalrightPanel;
using InfoPanel.Utils;
using LibUsbDotNet;
using LibUsbDotNet.Main;
using Serilog;
using SkiaSharp;
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace InfoPanel.Services
{
    public sealed class ThermalrightPanelDeviceTask : BackgroundTask
    {
        private static readonly ILogger Logger = Log.ForContext<ThermalrightPanelDeviceTask>();

        // ChiZhu Tech USBDISPLAY Protocol constants
        // Based on USB capture analysis of TRCC software at boot
        private static readonly byte[] MAGIC_BYTES = { 0x12, 0x34, 0x56, 0x78 };
        private const int HEADER_SIZE = 64;
        private const int COMMAND_DISPLAY = 0x02;
        private const int JPEG_QUALITY = 85;

        // Default resolution (updated after device identification)
        private const int DEFAULT_WIDTH = 480;
        private const int DEFAULT_HEIGHT = 480;

        private readonly ThermalrightPanelDevice _device;
        private int _panelWidth = DEFAULT_WIDTH;
        private int _panelHeight = DEFAULT_HEIGHT;
        private ThermalrightPanelModelInfo? _detectedModel;

        public ThermalrightPanelDevice Device => _device;

        public ThermalrightPanelDeviceTask(ThermalrightPanelDevice device)
        {
            _device = device ?? throw new ArgumentNullException(nameof(device));

            // Initialize from device's ModelInfo if available (handles unique VID/PID devices like Trofeo Vision)
            if (_device.ModelInfo != null)
            {
                _panelWidth = _device.ModelInfo.RenderWidth;
                _panelHeight = _device.ModelInfo.RenderHeight;
                _detectedModel = _device.ModelInfo;
            }
        }

        /// <summary>
        /// Builds the 64-byte init command for the ChiZhu Tech USB Display protocol.
        /// Based on actual USB capture: magic + zeros + 0x01 at offset 56
        /// </summary>
        private byte[] BuildInitCommand()
        {
            var header = new byte[HEADER_SIZE];

            // Bytes 0-3: Magic 0x12345678
            Array.Copy(MAGIC_BYTES, 0, header, 0, 4);

            // Bytes 4-55: All zeros (already zeroed)

            // Bytes 56-59: Init flag = 0x01 (critical!)
            BitConverter.GetBytes(1).CopyTo(header, 56);

            // Bytes 60-63: Zero (already zeroed)

            return header;
        }

        /// <summary>
        /// Builds a 64-byte display header for the ChiZhu Tech USB Display protocol.
        /// </summary>
        private byte[] BuildDisplayHeader(int jpegSize)
        {
            var header = new byte[HEADER_SIZE];

            // Bytes 0-3: Magic 0x12345678
            Array.Copy(MAGIC_BYTES, 0, header, 0, 4);

            // Bytes 4-7: Command = 2 (display frame)
            BitConverter.GetBytes(COMMAND_DISPLAY).CopyTo(header, 4);

            // Bytes 8-11: Width
            BitConverter.GetBytes(_panelWidth).CopyTo(header, 8);

            // Bytes 12-15: Height
            BitConverter.GetBytes(_panelHeight).CopyTo(header, 12);

            // Bytes 16-55: Zero padding (already zeroed)

            // Bytes 56-59: Command repeated = 2
            BitConverter.GetBytes(COMMAND_DISPLAY).CopyTo(header, 56);

            // Bytes 60-63: JPEG size (little-endian)
            BitConverter.GetBytes(jpegSize).CopyTo(header, 60);

            return header;
        }

        public byte[]? GenerateJpegBuffer()
        {
            var profileGuid = _device.ProfileGuid;

            if (ConfigModel.Instance.GetProfile(profileGuid) is Profile profile)
            {
                var rotation = _device.Rotation;

                // Render to RGBA bitmap
                using var bitmap = PanelDrawTask.RenderSK(profile, false,
                    colorType: SKColorType.Rgba8888,
                    alphaType: SKAlphaType.Opaque);

                // Resize to panel resolution with rotation
                using var resizedBitmap = SKBitmapExtensions.EnsureBitmapSize(bitmap, _panelWidth, _panelHeight, rotation);

                // Encode as JPEG
                using var image = SKImage.FromBitmap(resizedBitmap);
                using var data = image.Encode(SKEncodedImageFormat.Jpeg, JPEG_QUALITY);

                return data.ToArray();
            }

            return null;
        }

        protected override async Task DoWorkAsync(CancellationToken token)
        {
            await Task.Delay(300, token);

            var transportType = _device.ModelInfo?.TransportType ?? ThermalrightTransportType.WinUsb;
            Logger.Information("ThermalrightPanelDevice {Device}: Using {Transport} transport", _device, transportType);

            if (transportType == ThermalrightTransportType.Hid)
                await DoWorkHidAsync(token);
            else
                await DoWorkWinUsbAsync(token);
        }

        /// <summary>
        /// Finds the matching UsbRegistry for this device by scanning all connected USB devices.
        /// </summary>
        private UsbRegistry? FindUsbRegistry(int vendorId, int productId)
        {
            foreach (UsbRegistry deviceReg in UsbDevice.AllDevices)
            {
                if (deviceReg.Vid == vendorId && deviceReg.Pid == productId)
                {
                    var deviceId = deviceReg.DeviceProperties["DeviceID"] as string;

                    // Match by DeviceId if we have one, otherwise take first match
                    if (string.IsNullOrEmpty(_device.DeviceId) ||
                        (deviceId != null && deviceId.Equals(_device.DeviceId, StringComparison.OrdinalIgnoreCase)))
                    {
                        return deviceReg;
                    }
                }
            }
            return null;
        }

        private async Task DoWorkWinUsbAsync(CancellationToken token)
        {
            try
            {
                var vendorId = _device.ModelInfo?.VendorId ?? ThermalrightPanelModelDatabase.THERMALRIGHT_VENDOR_ID;
                var productId = _device.ModelInfo?.ProductId ?? ThermalrightPanelModelDatabase.THERMALRIGHT_PRODUCT_ID;
                Logger.Information("ThermalrightPanelDevice {Device}: Opening device via LibUsbDotNet (VID={Vid:X4} PID={Pid:X4})...",
                    _device, vendorId, productId);

                // Find the matching UsbRegistry
                var usbRegistry = FindUsbRegistry(vendorId, productId);
                if (usbRegistry == null)
                {
                    Logger.Warning("ThermalrightPanelDevice {Device}: USB device not found", _device);
                    _device.UpdateRuntimeProperties(errorMessage:
                        "USB device not found. Make sure:\n" +
                        "1. WinUSB driver is installed (use Zadig)\n" +
                        "2. The device is connected");
                    return;
                }

                using var usbDevice = usbRegistry.Device;
                if (usbDevice == null)
                {
                    Logger.Warning("ThermalrightPanelDevice {Device}: Failed to open USB device", _device);
                    _device.UpdateRuntimeProperties(errorMessage:
                        "Failed to open USB device. Make sure:\n" +
                        "1. No other application is using the device\n" +
                        "2. Try running as Administrator");
                    return;
                }

                // Claim the interface (required for WinUSB devices)
                if (usbDevice is IUsbDevice wholeUsbDevice)
                {
                    wholeUsbDevice.SetConfiguration(1);
                    wholeUsbDevice.ClaimInterface(0);
                }

                Logger.Information("ThermalrightPanelDevice {Device}: Device opened successfully!", _device);

                using var writer = usbDevice.OpenEndpointWriter(WriteEndpointID.Ep01);
                using var reader = usbDevice.OpenEndpointReader(ReadEndpointID.Ep01);

                // Send initialization command (magic + zeros + 0x01 at offset 56)
                var initCommand = BuildInitCommand();
                Logger.Information("ThermalrightPanelDevice {Device}: Sending init command (64 bytes)", _device);

                var ec = writer.Write(initCommand, 5000, out int initWritten);
                if (ec != ErrorCode.None)
                {
                    Logger.Error("ThermalrightPanelDevice {Device}: Init command failed: {Error}", _device, ec);
                    _device.UpdateRuntimeProperties(errorMessage: $"Init command failed: {ec}");
                    return;
                }
                Logger.Information("ThermalrightPanelDevice {Device}: Init command sent ({Bytes} bytes)", _device, initWritten);

                // Read device response to identify panel type (SSCRM-V1, SSCRM-V3, etc.)
                var responseBuffer = new byte[64];
                ec = reader.Read(responseBuffer, 5000, out int bytesRead);
                if (ec == ErrorCode.None && bytesRead > 0)
                {
                    var responseHex = BitConverter.ToString(responseBuffer, 0, Math.Min(bytesRead, 32)).Replace("-", "");
                    Logger.Information("ThermalrightPanelDevice {Device}: Device response ({Bytes} bytes): {Hex}",
                        _device, bytesRead, responseHex);

                    // Extract device identifier (e.g., "SSCRM-V1", "SSCRM-V3" at offset 4)
                    if (bytesRead >= 12)
                    {
                        var deviceIdentifier = System.Text.Encoding.ASCII.GetString(responseBuffer, 4, 8).TrimEnd('\0');
                        Logger.Information("ThermalrightPanelDevice {Device}: Device identifier: {Id}", _device, deviceIdentifier);

                        // Detect panel model based on identifier
                        _detectedModel = ThermalrightPanelModelDatabase.GetModelByIdentifier(deviceIdentifier);
                        if (_detectedModel != null)
                        {
                            _panelWidth = _detectedModel.RenderWidth;
                            _panelHeight = _detectedModel.RenderHeight;

                            // Update device model so UI shows correct dimensions
                            _device.Model = _detectedModel.Model;

                            Logger.Information("ThermalrightPanelDevice {Device}: Detected {Model} - using {Width}x{Height}",
                                _device, _detectedModel.Name, _panelWidth, _panelHeight);
                        }
                        else
                        {
                            Logger.Warning("ThermalrightPanelDevice {Device}: Unknown identifier '{Id}', using default {Width}x{Height}",
                                _device, deviceIdentifier, _panelWidth, _panelHeight);
                        }
                    }
                }
                else
                {
                    Logger.Warning("ThermalrightPanelDevice {Device}: No response from device (ec={Error}), using default {Width}x{Height}",
                        _device, ec, _panelWidth, _panelHeight);
                }

                // Update device display name with detected model (show native resolution)
                UpdateDeviceDisplayName();

                await Task.Delay(100, token); // Small delay after init

                // Run the render+send loop using LibUsbDotNet bulk transfers
                await RunRenderSendLoop(jpegData =>
                {
                    // Build display header with JPEG size
                    var header = BuildDisplayHeader(jpegData.Length);

                    // Combine header + JPEG into single buffer
                    var packet = new byte[HEADER_SIZE + jpegData.Length];
                    Array.Copy(header, 0, packet, 0, HEADER_SIZE);
                    Array.Copy(jpegData, 0, packet, HEADER_SIZE, jpegData.Length);

                    // Send as single bulk write
                    var writeEc = writer.Write(packet, 5000, out int bytesWritten);
                    if (writeEc != ErrorCode.None)
                    {
                        throw new Exception($"USB write failed: {writeEc}");
                    }
                }, token);
            }
            catch (TaskCanceledException)
            {
                Logger.Debug("ThermalrightPanelDevice {Device}: Task cancelled", _device);
            }
            catch (Exception e)
            {
                Logger.Error(e, "ThermalrightPanelDevice {Device}: Error", _device);
                _device.UpdateRuntimeProperties(errorMessage: e.Message);
            }
            finally
            {
                _device.UpdateRuntimeProperties(isRunning: false);
            }
        }

        private async Task DoWorkHidAsync(CancellationToken token)
        {
            try
            {
                var vendorId = _device.ModelInfo?.VendorId ?? 0;
                var productId = _device.ModelInfo?.ProductId ?? 0;
                Logger.Information("ThermalrightPanelDevice {Device}: Opening device via HID (VID={Vid:X4} PID={Pid:X4})...",
                    _device, vendorId, productId);

                using var hidDevice = HidPanelDevice.Open(vendorId, productId);

                if (hidDevice == null)
                {
                    Logger.Warning("ThermalrightPanelDevice {Device}: Failed to open HID device", _device);
                    _device.UpdateRuntimeProperties(errorMessage:
                        "Failed to open HID device. Make sure:\n" +
                        "1. The device is connected\n" +
                        "2. No other application is using the device");
                    return;
                }

                Logger.Information("ThermalrightPanelDevice {Device}: HID device opened successfully!", _device);

                // Send HID init command
                if (!hidDevice.SendInit())
                {
                    Logger.Error("ThermalrightPanelDevice {Device}: HID init failed", _device);
                    _device.UpdateRuntimeProperties(errorMessage: "HID init command failed");
                    return;
                }

                // Read init response (optional - for logging)
                var response = hidDevice.ReadInitResponse();
                if (response != null && response.Length >= 20)
                {
                    // Log the identifier portion (bytes 20+ are typically an ASCII identifier like "BP13288")
                    var identifierBytes = new byte[Math.Min(8, response.Length - 20)];
                    Array.Copy(response, 20, identifierBytes, 0, identifierBytes.Length);
                    var identifier = System.Text.Encoding.ASCII.GetString(identifierBytes).TrimEnd('\0');
                    Logger.Information("ThermalrightPanelDevice {Device}: HID device identifier: {Id}", _device, identifier);
                }

                // Model is already known from VID/PID (no identifier-based detection needed)
                UpdateDeviceDisplayName();

                await Task.Delay(100, token); // Small delay after init

                // Run the render+send loop using HID reports
                var width = _panelWidth;
                var height = _panelHeight;
                await RunRenderSendLoop(jpegData =>
                {
                    if (!hidDevice.SendJpegFrame(jpegData, width, height))
                    {
                        throw new Exception("HID frame send failed");
                    }
                }, token);
            }
            catch (TaskCanceledException)
            {
                Logger.Debug("ThermalrightPanelDevice {Device}: Task cancelled", _device);
            }
            catch (Exception e)
            {
                Logger.Error(e, "ThermalrightPanelDevice {Device}: Error", _device);
                _device.UpdateRuntimeProperties(errorMessage: e.Message);
            }
            finally
            {
                _device.UpdateRuntimeProperties(isRunning: false);
            }
        }

        private void UpdateDeviceDisplayName()
        {
            var modelName = _detectedModel?.Name ?? "Panel";
            var nativeWidth = _detectedModel?.Width ?? _panelWidth;
            var nativeHeight = _detectedModel?.Height ?? _panelHeight;
            _device.RuntimeProperties.Name = $"Thermalright {modelName} ({nativeWidth}x{nativeHeight})";
            Logger.Information("ThermalrightPanelDevice {Device}: Connected to {Name} (native {NativeW}x{NativeH}, rendering at {RenderW}x{RenderH})",
                _device, modelName, nativeWidth, nativeHeight, _panelWidth, _panelHeight);
        }

        /// <summary>
        /// Shared render+send loop used by both WinUSB and HID protocols.
        /// The sendFrame action receives JPEG data and handles protocol-specific sending.
        /// </summary>
        private async Task RunRenderSendLoop(Action<byte[]> sendFrame, CancellationToken token)
        {
            FpsCounter fpsCounter = new(60);
            byte[]? _latestFrame = null;
            AutoResetEvent _frameAvailable = new(false);

            var renderCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            var renderToken = renderCts.Token;

            _device.UpdateRuntimeProperties(isRunning: true);

            var renderTask = Task.Run(async () =>
            {
                Thread.CurrentThread.Name ??= $"Thermalright-Render-{_device.DeviceLocation}";
                var stopwatch = new Stopwatch();

                while (!renderToken.IsCancellationRequested)
                {
                    stopwatch.Restart();
                    var frame = GenerateJpegBuffer();

                    if (frame != null)
                    {
                        Interlocked.Exchange(ref _latestFrame, frame);
                        _frameAvailable.Set();
                    }

                    var targetFrameTime = 1000 / ConfigModel.Instance.Settings.TargetFrameRate;
                    var desiredFrameTime = Math.Max((int)(fpsCounter.FrameTime * 0.9), targetFrameTime);
                    var adaptiveFrameTime = 0;

                    var elapsedMs = (int)stopwatch.ElapsedMilliseconds;

                    if (elapsedMs < desiredFrameTime)
                    {
                        adaptiveFrameTime = desiredFrameTime - elapsedMs;
                    }

                    if (adaptiveFrameTime > 0)
                    {
                        await Task.Delay(adaptiveFrameTime, token);
                    }
                }
            }, renderToken);

            var sendTask = Task.Run(() =>
            {
                Thread.CurrentThread.Name ??= $"Thermalright-Send-{_device.DeviceLocation}";
                try
                {
                    var stopwatch = new Stopwatch();

                    while (!token.IsCancellationRequested)
                    {
                        if (_frameAvailable.WaitOne(100))
                        {
                            var jpegData = Interlocked.Exchange(ref _latestFrame, null);
                            if (jpegData != null)
                            {
                                stopwatch.Restart();

                                sendFrame(jpegData);

                                fpsCounter.Update(stopwatch.ElapsedMilliseconds);
                                _device.UpdateRuntimeProperties(frameRate: fpsCounter.FramesPerSecond, frameTime: fpsCounter.FrameTime);
                            }
                        }
                    }
                }
                catch (Exception e)
                {
                    Logger.Error(e, "ThermalrightPanelDevice {Device}: Error in send task", _device);
                    _device.UpdateRuntimeProperties(errorMessage: e.Message);
                }
                finally
                {
                    renderCts.Cancel();
                }
            }, token);

            await Task.WhenAll(renderTask, sendTask);
        }
    }
}
