using InfoPanel.Extensions;
using InfoPanel.Models;
using InfoPanel.TuringPanel;
using InfoPanel.Utils;
using LibUsbDotNet;
using LibUsbDotNet.Main;
using Serilog;
using SkiaSharp;
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace InfoPanel.Services
{
    public sealed class TuringPanelUsbDeviceTask : BackgroundTask
    {
        private static readonly ILogger Logger = Log.ForContext<TuringPanelUsbDeviceTask>();
        private readonly TuringPanelDevice _device;
        private readonly int _panelWidth;
        private readonly int _panelHeight;

        public TuringPanelDevice Device => _device;

        private bool IsLianLi => _device.ModelInfo?.Model == TuringPanelModel.LIANLI_88INCH_USB;

        /// <summary>Adapts the fork's TuringDevice to the shared IUsbScreenDevice loop (frames are PNG).</summary>
        private sealed class TuringScreenAdapter(TuringDevice inner) : IUsbScreenDevice
        {
            public bool Sync()
            {
                inner.SendSyncCommand();
                return true;
            }

            public bool StopMedia()
            {
                inner.SendStopMediaCommand();
                return true;
            }

            public bool SetBrightness(byte value)
            {
                inner.SendBrightnessCommand(value);
                return true;
            }

            public bool DrawFrame(byte[] imageBytes)
            {
                inner.SendJpegBytes(imageBytes);
                return true;
            }

            public void Dispose() => inner.Dispose();
        }

        public TuringPanelUsbDeviceTask(TuringPanelDevice device)
        {
            _device = device ?? throw new ArgumentNullException(nameof(device));

            if(device.ModelInfo == null)
            {
                throw new ArgumentException("Device model info cannot be null", nameof(device));
            }

            _panelWidth = device.ModelInfo.Width;
            _panelHeight = device.ModelInfo.Height;
        }

        public byte[]? GenerateLcdBuffer()
        {
            var profileGuid = _device.ProfileGuid;

            if (DeviceRuntime.GetProfile(profileGuid) is Profile profile)
            {
                var rotation = _device.Rotation;

                // The Lian Li 8.8" sends frames as JPEG like the vendor app.
                if (IsLianLi)
                {
                    using var lianLiBitmap = PanelRenderer.RenderSK(profile, false);
                    using var lianLiResized = SKBitmapExtensions.EnsureBitmapSize(lianLiBitmap, _panelWidth, _panelHeight, rotation);
                    using var lianLiPixmap = lianLiResized.PeekPixels();
                    using var lianLiData = lianLiPixmap.Encode(SKEncodedImageFormat.Jpeg, 95);

                    if (lianLiData == null || lianLiData.IsEmpty)
                    {
                        Logger.Error("TuringPanelDevice {Device}: Failed to encode bitmap to JPEG", _device);
                        return null;
                    }

                    return lianLiData.ToArray();
                }

                // JPEG like the Windows build (command 101): much cheaper to encode
                // and for the panel to decode than PNG, which capped frame rate.
                using var bitmap = PanelRenderer.RenderSK(profile, false);
                using var resizedBitmap = SKBitmapExtensions.EnsureBitmapSize(bitmap, _panelWidth, _panelHeight, rotation);

                using var pixmap = resizedBitmap.PeekPixels();
                using var data = pixmap.Encode(SKEncodedImageFormat.Jpeg, _device.JpegQuality);

                if (data == null || data.IsEmpty)
                {
                    Logger.Error("TuringPanelDevice {Device}: Failed to encode bitmap to JPEG", _device);
                    return null;
                }

                return data.ToArray();
            }

            return null;
        }

        private static byte[] EncodeSolidFrame(int width, int height, SKColor color, SKEncodedImageFormat format, int quality)
        {
            using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
            bitmap.Erase(color);

            using var pixmap = bitmap.PeekPixels();
            using var data = pixmap.Encode(format, quality);
            return data?.ToArray() ?? [];
        }

        private void PrepareLianLiImageLayers(LianLiUsbScreenDevice lianLiDevice)
        {
            try
            {
                // Match the vendor ApplyTemplate() prep sequence:
                // SyncClockOnly, StopClock, clear PNG overlay, clear JPG background,
                // then set the frame rate like the Linux driver does.
                var syncClockOk = lianLiDevice.SyncClockOnly();
                Thread.Sleep(50);

                var stopClockOk = lianLiDevice.StopClock();
                Thread.Sleep(50);

                var clearPng = EncodeSolidFrame(_panelWidth, _panelHeight, SKColors.Transparent, SKEncodedImageFormat.Png, 100);
                var clearPngOk = clearPng.Length > 0 && lianLiDevice.DrawPngLayer(clearPng);
                Thread.Sleep(50);

                var clearJpeg = EncodeSolidFrame(_panelWidth, _panelHeight, SKColors.Black, SKEncodedImageFormat.Jpeg, 95);
                var clearJpegOk = clearJpeg.Length > 0 && lianLiDevice.DrawJpegLayer(clearJpeg);
                Thread.Sleep(50);

                var frameRateOk = lianLiDevice.SetFrameRate(30);

                Logger.Information(
                    "TuringPanelDevice {Device}: Lian Li prep results: SyncClockOnly={SyncClockOk}, StopClock={StopClockOk}, ClearPng={ClearPngOk}, ClearJpeg={ClearJpegOk}, SetFrameRate={FrameRateOk}",
                    _device, syncClockOk, stopClockOk, clearPngOk, clearJpegOk, frameRateOk);
            }
            catch (Exception ex)
            {
                Logger.Warning(ex, "TuringPanelDevice {Device}: Lian Li prep sequence failed", _device);
            }
        }

        private Task<UsbRegistry?> FindTargetDeviceAsync()
        {
            return Task.FromResult(FindTargetDevice());
        }

        private UsbRegistry? FindTargetDevice()
        {
            if(_device.ModelInfo == null)
            {
                Logger.Error("TuringPanelDevice {Device}: ModelInfo is null", _device);
                return null;
            }

            UsbRegistry? vidPidMatch = null;

            foreach (UsbRegistry deviceReg in UsbDevice.AllDevices)
            {
                if (deviceReg.Vid == _device.ModelInfo.VendorId && deviceReg.Pid == _device.ModelInfo.ProductId)
                {
                    string? deviceId = deviceReg.DeviceProperties.TryGetValue("DeviceID", out var devIdObj) && devIdObj is string devIdStr
                        ? devIdStr : deviceReg.DevicePath;

                    if (string.IsNullOrEmpty(deviceId))
                    {
                        Logger.Debug("TuringPanelDevice {Device}: Unable to get DeviceId for device {DevicePath}", _device, deviceReg.DevicePath);
                        continue;
                    }

                    if(_device.IsMatching(deviceId))
                    {
                        Logger.Information("TuringPanelDevice {Device}: Found matching device with DeviceId {DeviceId}", _device, deviceId);
                        return deviceReg;
                    }

                    // On Linux, device paths change after reset/replug. Keep the first
                    // VID/PID match as a fallback.
                    vidPidMatch ??= deviceReg;
                }
            }

            // Fallback: if exact DeviceId didn't match but we found a device with the
            // right VID/PID, use it and update the stored DeviceId.
            if (vidPidMatch != null && OperatingSystem.IsLinux())
            {
                string? newDeviceId = vidPidMatch.DeviceProperties.TryGetValue("DeviceID", out var obj) && obj is string s
                    ? s : vidPidMatch.DevicePath;
                Logger.Information("TuringPanelDevice {Device}: Exact DeviceId not found, falling back to VID/PID match at {NewId}", _device, newDeviceId);
                if (!string.IsNullOrEmpty(newDeviceId))
                    _device.DeviceId = newDeviceId;
                return vidPidMatch;
            }

            return null;
        }

        protected override async Task DoWorkAsync(CancellationToken token)
        {
            await Task.Delay(300, token);
            
            try
            {
                var usbRegistry = await FindTargetDeviceAsync();

                if (usbRegistry == null)
                {
                    Logger.Warning("TuringPanelDevice {Device}: USB Device not found.", _device);
                    _device.UpdateRuntimeProperties(errorMessage: "Device not found");
                    return;
                }

                IUsbScreenDevice screen;
                if (IsLianLi)
                {
                    if (!usbRegistry.Open(out var usbDevice))
                    {
                        Logger.Error("TuringPanelDevice {Device}: Failed to open USB device", _device);
                        _device.UpdateRuntimeProperties(errorMessage: "Failed to open USB device");
                        return;
                    }

                    screen = new LianLiUsbScreenDevice(usbDevice);
                }
                else
                {
                    var turingDevice = new TuringDevice();
                    try
                    {
                        turingDevice.Initialize(usbRegistry);
                    }
                    catch (TuringDeviceException ex)
                    {
                        Logger.Error("TuringPanelDevice {Device}: Failed to initialize - {Error}", _device, ex.Message);
                        _device.UpdateRuntimeProperties(errorMessage: ex.Message);
                        turingDevice.Dispose();
                        return;
                    }

                    screen = new TuringScreenAdapter(turingDevice);
                }

                using var device = screen;

                Logger.Information("TuringPanelDevice {Device}: Initialized successfully", _device);
                _device.UpdateRuntimeProperties(isRunning: true);

                try
                {
                    // Delay for sync; bail if device firmware is not ready
                    if (!device.Sync())
                    {
                        Logger.Warning("TuringPanelDevice {Device}: Sync failed, device not ready", _device);
                        _device.UpdateRuntimeProperties(errorMessage: "Device not ready (sync failed)");
                        return;
                    }
                    Thread.Sleep(200);

                    if (!device.Sync())
                    {
                        Logger.Warning("TuringPanelDevice {Device}: Sync failed, device not ready", _device);
                        _device.UpdateRuntimeProperties(errorMessage: "Device not ready (sync failed)");
                        return;
                    }
                    Thread.Sleep(200);

                    // Stop any on-device media playback: a panel playing its stored
                    // media ignores streamed frames (frozen on an old image).
                    device.StopMedia();
                    Thread.Sleep(200);

                    // Clear the Lian Li image layers (vendor ApplyTemplate prep
                    // sequence), then set the frame rate.
                    if (device is LianLiUsbScreenDevice lianLi)
                    {
                        PrepareLianLiImageLayers(lianLi);
                        Thread.Sleep(200);
                    }

                    // Set brightness
                    var brightness = _device.Brightness;
                    device.SetBrightness((byte)brightness);

                    device.Sync();
                    Thread.Sleep(200);

                    FpsCounter fpsCounter = new(60);
                    byte[]? _latestFrame = null;
                    AutoResetEvent _frameAvailable = new(false);

                    var frameBufferPool = new ConcurrentBag<byte[]>();

                    var renderCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                    var renderToken = renderCts.Token;

                    var renderTask = Task.Run(async () =>
                    {
                        Thread.CurrentThread.Name ??= $"TuringPanel-Render-{_device.DeviceId}";
                        var stopwatch1 = new Stopwatch();

                        while (!renderToken.IsCancellationRequested)
                        {
                            stopwatch1.Restart();
                            var frame = GenerateLcdBuffer();

                            if (frame != null)
                            {
                                var oldFrame = Interlocked.Exchange(ref _latestFrame, frame);
                                _frameAvailable.Set();
                            }

                            var targetFrameTime = 1000 / Math.Max(1, _device.TargetFrameRate);
                            var desiredFrameTime = Math.Max((int)(fpsCounter.FrameTime), targetFrameTime);
                            var adaptiveFrameTime = 0;

                            var elapsedMs = (int)stopwatch1.ElapsedMilliseconds;

                            if (elapsedMs < desiredFrameTime)
                            {
                                adaptiveFrameTime = desiredFrameTime - elapsedMs;
                            }

                            if (adaptiveFrameTime > 0)
                            {
                                await Task.Delay(adaptiveFrameTime, renderToken);
                            }
                        }
                    }, renderToken);

                    var sendTask = Task.Run(() =>
                    {
                        Thread.CurrentThread.Name ??= $"TuringPanel-Send-{_device.DeviceId}";
                        try
                        {
                            var stopwatch2 = new Stopwatch();

                            while (!token.IsCancellationRequested)
                            {
                                if (brightness != _device.Brightness)
                                {
                                    brightness = _device.Brightness;
                                    device.SetBrightness((byte)brightness);
                                    device.Sync();
                                    Thread.Sleep(200);
                                }

                                if (_frameAvailable.WaitOne(100))
                                {
                                    var frame = Interlocked.Exchange(ref _latestFrame, null);
                                    if (frame != null)
                                    {
                                        stopwatch2.Restart();
                                        if (!device.DrawFrame(frame))
                                        {
                                            Logger.Warning("TuringPanelDevice {Device}: Frame draw failed", _device);
                                            _device.UpdateRuntimeProperties(errorMessage: "Draw failed");
                                            break;
                                        }

                                        fpsCounter.Update(stopwatch2.ElapsedMilliseconds);
                                        _device.UpdateRuntimeProperties(frameRate: fpsCounter.FramesPerSecond, frameTime: fpsCounter.FrameTime);
                                    }
                                }
                            }
                        }
                        catch(Exception e)
                        {
                            Logger.Error(e, "TuringPanelDevice {Device}: Error in send task", _device);
                        }
                        finally
                        {
                            renderCts.Cancel();
                        }
                    }, token);

                    await Task.WhenAll(renderTask, sendTask);
                }
                catch (TaskCanceledException)
                {
                    Logger.Debug("TuringPanelDevice {Device}: Task cancelled", _device);
                }
                catch (Exception ex)
                {
                    Logger.Error(ex, "TuringPanelDevice {Device}: Exception during work", _device);
                    _device.UpdateRuntimeProperties(errorMessage: ex.Message);
                }
                finally
                {
                    try
                    {
                        device.SetBrightness(0);
                    }
                    catch (Exception ex)
                    {
                        Logger.Error(ex, "TuringPanelDevice {Device}: Exception when setting brightness to 0", _device);
                    }
                }
            }
            catch (Exception e)
            {
                Logger.Error(e, "TuringPanelDevice {Device}: Init error", _device);
                _device.UpdateRuntimeProperties(errorMessage: e.Message);
            }
            finally
            {
                _device.UpdateRuntimeProperties(isRunning: false);
            }
        }
    }
}