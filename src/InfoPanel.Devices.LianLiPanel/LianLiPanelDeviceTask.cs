using InfoPanel.Extensions;
using InfoPanel.LianLiPanel;
using InfoPanel.Models;
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
    public sealed class LianLiPanelDeviceTask : BackgroundTask
    {
        private static readonly ILogger Logger = Log.ForContext<LianLiPanelDeviceTask>();
        private readonly LianLiPanelDevice _device;
        private readonly int _panelWidth;
        private readonly int _panelHeight;

        public LianLiPanelDevice Device => _device;

        public LianLiPanelDeviceTask(LianLiPanelDevice device)
        {
            _device = device ?? throw new ArgumentNullException(nameof(device));

            if (device.ModelInfo == null)
            {
                throw new ArgumentException("Device model info cannot be null", nameof(device));
            }

            _panelWidth = device.ModelInfo.Width;
            _panelHeight = device.ModelInfo.Height;
        }

        // Resend the cached payload at full cadence when content is unchanged:
        // only render/resize/encode is skipped, the wire keeps its frame rate.
        private readonly SharedFrameConsumer _frameConsumer = new() { ResendCachedOnSkip = true };

        /// <summary>Returns null when there is no profile or the content has not changed.</summary>
        public byte[]? GenerateLcdBuffer()
        {
            var profileGuid = _device.ProfileGuid;

            if (DeviceRuntime.GetProfile(profileGuid) is Profile profile)
            {
                var frameInterval = 1000 / Math.Max(1, _device.TargetFrameRate);
                return _frameConsumer.Produce(profile, frameInterval, bitmap =>
                {
                    var rotation = GetDeviceRotation();

                    var resizedBitmap = SKBitmapExtensions.EnsureBitmapSize(bitmap, _panelWidth, _panelHeight, rotation);
                    try
                    {
                        using var pixmap = resizedBitmap.PeekPixels();
                        using var data = pixmap.Encode(SKEncodedImageFormat.Jpeg, _device.JpegQuality);

                        if (data == null || data.IsEmpty)
                        {
                            Logger.Error("LianLiPanelDevice {Device}: Failed to encode bitmap to JPEG", _device);
                            return null!;
                        }

                        return data.ToArray();
                    }
                    finally
                    {
                        if (!ReferenceEquals(resizedBitmap, bitmap))
                        {
                            resizedBitmap.Dispose();
                        }
                    }
                });
            }

            return null;
        }

        private LCD_ROTATION GetDeviceRotation()
        {
            if (_device.ModelInfo?.RequiresPortraitRotationOffset != true)
            {
                return _device.Rotation;
            }

            return _device.Rotation switch
            {
                LCD_ROTATION.RotateNone => LCD_ROTATION.Rotate90FlipNone,
                LCD_ROTATION.Rotate90FlipNone => LCD_ROTATION.Rotate180FlipNone,
                LCD_ROTATION.Rotate180FlipNone => LCD_ROTATION.Rotate270FlipNone,
                LCD_ROTATION.Rotate270FlipNone => LCD_ROTATION.RotateNone,
                _ => _device.Rotation
            };
        }

        private static byte[] EncodeSolidFrame(int width, int height, SKColor color, SKEncodedImageFormat format, int quality)
        {
            using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
            bitmap.Erase(color);

            using var pixmap = bitmap.PeekPixels();
            using var data = pixmap.Encode(format, quality);
            return data?.ToArray() ?? [];
        }

        private void PrepareImageLayers(LianLiUsbScreenDevice screenDevice)
        {
            try
            {
                // Match the vendor ApplyTemplate() prep sequence:
                // SyncClockOnly, StopClock, clear PNG overlay, clear JPG background,
                // then set the frame rate like the vendor Linux driver does.
                var syncClockOk = screenDevice.SyncClockOnly();
                Thread.Sleep(50);

                var stopClockOk = screenDevice.StopClock();
                Thread.Sleep(50);

                var clearPng = EncodeSolidFrame(_panelWidth, _panelHeight, SKColors.Transparent, SKEncodedImageFormat.Png, 100);
                var clearPngOk = clearPng.Length > 0 && screenDevice.DrawPngLayer(clearPng);
                Thread.Sleep(50);

                var clearJpeg = EncodeSolidFrame(_panelWidth, _panelHeight, SKColors.Black, SKEncodedImageFormat.Jpeg, 95);
                var clearJpegOk = clearJpeg.Length > 0 && screenDevice.DrawJpegLayer(clearJpeg);
                Thread.Sleep(50);

                var frameRateOk = screenDevice.SetFrameRate((byte)Math.Clamp(_device.TargetFrameRate, 1, 60));

                Logger.Information(
                    "LianLiPanelDevice {Device}: Prep results: SyncClockOnly={SyncClockOk}, StopClock={StopClockOk}, ClearPng={ClearPngOk}, ClearJpeg={ClearJpegOk}, SetFrameRate={FrameRateOk}",
                    _device, syncClockOk, stopClockOk, clearPngOk, clearJpegOk, frameRateOk);
            }
            catch (Exception ex)
            {
                Logger.Warning(ex, "LianLiPanelDevice {Device}: Prep sequence failed", _device);
            }
        }

        private UsbRegistry? FindTargetDevice()
        {
            if (_device.ModelInfo == null)
            {
                Logger.Error("LianLiPanelDevice {Device}: ModelInfo is null", _device);
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
                        Logger.Debug("LianLiPanelDevice {Device}: Unable to get DeviceId for device {DevicePath}", _device, deviceReg.DevicePath);
                        continue;
                    }

                    if (_device.IsMatching(deviceId))
                    {
                        Logger.Information("LianLiPanelDevice {Device}: Found matching device with DeviceId {DeviceId}", _device, deviceId);
                        return deviceReg;
                    }

                    // On Linux, device paths change after reset/replug. Keep the first
                    // VID/PID match as a fallback.
                    vidPidMatch ??= deviceReg;
                }
            }

            if (vidPidMatch != null)
            {
                string? newDeviceId = vidPidMatch.DeviceProperties.TryGetValue("DeviceID", out var obj) && obj is string s
                    ? s : vidPidMatch.DevicePath;
                Logger.Information("LianLiPanelDevice {Device}: Exact DeviceId not found, falling back to VID/PID match at {NewId}", _device, newDeviceId);
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
                var usbRegistry = FindTargetDevice();

                if (usbRegistry == null)
                {
                    Logger.Warning("LianLiPanelDevice {Device}: USB device not found.", _device);
                    _device.UpdateRuntimeProperties(errorMessage: "Device not found");
                    return;
                }

                if (!usbRegistry.Open(out var usbDevice))
                {
                    Logger.Error("LianLiPanelDevice {Device}: Failed to open USB device", _device);
                    _device.UpdateRuntimeProperties(errorMessage: "Failed to open USB device");
                    return;
                }

                using var screenDevice = new LianLiUsbScreenDevice(usbDevice);

                Logger.Information("LianLiPanelDevice {Device}: Initialized successfully", _device);
                _device.UpdateRuntimeProperties(isRunning: true, errorMessage: string.Empty);

                try
                {
                    // Delay for sync; bail if device firmware is not ready
                    if (!screenDevice.Sync())
                    {
                        Logger.Warning("LianLiPanelDevice {Device}: Sync failed (1st attempt), device not ready", _device);
                        _device.UpdateRuntimeProperties(errorMessage: "Device not ready (sync failed)");
                        return;
                    }
                    Thread.Sleep(200);

                    if (!screenDevice.Sync())
                    {
                        Logger.Warning("LianLiPanelDevice {Device}: Sync failed (2nd attempt), device not ready", _device);
                        _device.UpdateRuntimeProperties(errorMessage: "Device not ready (sync failed)");
                        return;
                    }
                    Thread.Sleep(200);

                    // Stop any on-device media playback: a panel playing its stored
                    // media ignores streamed frames (frozen on an old image).
                    screenDevice.StopMedia();
                    Thread.Sleep(200);

                    PrepareImageLayers(screenDevice);
                    Thread.Sleep(200);

                    var brightness = _device.Brightness;
                    screenDevice.SetBrightness((byte)brightness);

                    if (!screenDevice.Sync())
                    {
                        Logger.Warning("LianLiPanelDevice {Device}: Sync failed after brightness set, device not ready", _device);
                        _device.UpdateRuntimeProperties(errorMessage: "Device not ready (sync failed)");
                        return;
                    }
                    Thread.Sleep(200);

                    FpsCounter fpsCounter = new(60);
                    byte[]? latestFrame = null;
                    AutoResetEvent frameAvailable = new(false);

                    var renderCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                    var renderToken = renderCts.Token;

                    var renderTask = Task.Run(async () =>
                    {
                        Thread.CurrentThread.Name ??= $"LianLiPanel-Render-{_device.DeviceId}";
                        var stopwatch = new Stopwatch();

                        while (!renderToken.IsCancellationRequested)
                        {
                            stopwatch.Restart();
                            var frame = GenerateLcdBuffer();

                            if (frame != null)
                            {
                                Interlocked.Exchange(ref latestFrame, frame);
                                frameAvailable.Set();
                            }
                            else
                            {
                                // Content unchanged: skip encode/send, keep showing the pace.
                                fpsCounter.Update(0);
                                _device.UpdateRuntimeProperties(frameRate: fpsCounter.FramesPerSecond, frameTime: fpsCounter.FrameTime);
                            }

                            var targetFrameTime = 1000 / Math.Max(1, _device.TargetFrameRate);
                            var desiredFrameTime = Math.Max((int)fpsCounter.FrameTime, targetFrameTime);
                            var elapsedMs = (int)stopwatch.ElapsedMilliseconds;
                            var adaptiveFrameTime = desiredFrameTime - elapsedMs;

                            if (adaptiveFrameTime > 0)
                            {
                                await Task.Delay(adaptiveFrameTime, renderToken);
                            }
                        }
                    }, renderToken);

                    var sendTask = Task.Run(() =>
                    {
                        Thread.CurrentThread.Name ??= $"LianLiPanel-Send-{_device.DeviceId}";
                        try
                        {
                            var stopwatch = new Stopwatch();

                            while (!token.IsCancellationRequested)
                            {
                                if (brightness != _device.Brightness)
                                {
                                    brightness = _device.Brightness;
                                    screenDevice.SetBrightness((byte)brightness);
                                    if (!screenDevice.Sync())
                                    {
                                        Logger.Warning("LianLiPanelDevice {Device}: Sync failed during brightness update", _device);
                                        _device.UpdateRuntimeProperties(errorMessage: "Sync failed");
                                        break;
                                    }
                                    Thread.Sleep(200);
                                }

                                if (frameAvailable.WaitOne(100))
                                {
                                    var frame = Interlocked.Exchange(ref latestFrame, null);
                                    if (frame != null)
                                    {
                                        stopwatch.Restart();
                                        if (!screenDevice.DrawJpeg(frame))
                                        {
                                            Logger.Warning("LianLiPanelDevice {Device}: DrawJpeg failed", _device);
                                            _device.UpdateRuntimeProperties(errorMessage: "Draw failed");
                                            break;
                                        }

                                        fpsCounter.Update(stopwatch.ElapsedMilliseconds);
                                        _device.UpdateRuntimeProperties(frameRate: fpsCounter.FramesPerSecond, frameTime: fpsCounter.FrameTime);
                                    }
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            Logger.Error(ex, "LianLiPanelDevice {Device}: Error in send task", _device);
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
                    Logger.Debug("LianLiPanelDevice {Device}: Task cancelled", _device);
                }
                catch (Exception ex)
                {
                    Logger.Error(ex, "LianLiPanelDevice {Device}: Exception during work", _device);
                    _device.UpdateRuntimeProperties(errorMessage: ex.Message);
                }
                finally
                {
                    try
                    {
                        screenDevice.StopMedia();
                    }
                    catch (Exception ex)
                    {
                        Logger.Error(ex, "LianLiPanelDevice {Device}: Exception when stopping media", _device);
                    }

                    try
                    {
                        screenDevice.SetBrightness(0);
                    }
                    catch (Exception ex)
                    {
                        Logger.Error(ex, "LianLiPanelDevice {Device}: Exception when setting brightness to 0", _device);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warning(ex, "LianLiPanelDevice {Device}: Init error", _device);
                _device.UpdateRuntimeProperties(errorMessage: ex.Message);
            }
            finally
            {
                _device.UpdateRuntimeProperties(isRunning: false);
            }
        }
    }
}
