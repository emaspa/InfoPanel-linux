using InfoPanel.Drawing;
using InfoPanel.Extensions;
using InfoPanel.JonsboPanel;
using InfoPanel.Models;
using InfoPanel.Utils;
using Serilog;
using SkiaSharp;
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace InfoPanel.Services
{
    public sealed class JonsboPanelDeviceTask : BackgroundTask
    {
        private static readonly ILogger Logger = Log.ForContext<JonsboPanelDeviceTask>();

        private readonly JonsboPanelDevice _device;
        private int _panelWidth;   // native portrait width  (462 on DS916)
        private int _panelHeight;  // native portrait height (1920 on DS916)

        public JonsboPanelDeviceTask(JonsboPanelDevice device)
        {
            _device = device;
        }

        protected override async Task DoWorkAsync(CancellationToken token)
        {
            var modelInfo = _device.ModelInfo;
            if (modelInfo == null)
            {
                _device.UpdateRuntimeProperties(errorMessage: "Unknown model");
                return;
            }

            _panelWidth = modelInfo.Width;
            _panelHeight = modelInfo.Height;

            _device.UpdateRuntimeProperties(isRunning: false, errorMessage: string.Empty);
            _device.RuntimeProperties.Name = $"{modelInfo.Name} ({_panelWidth}x{_panelHeight})";

            if (modelInfo.TransportType == JonsboTransportType.Ms9132)
            {
                await DoMs9132WorkAsync(modelInfo, token);
                return;
            }

            int retryCount = 0;
            while (!token.IsCancellationRequested)
            {
                JonsboSerialDevice? serial = null;
                try
                {
                    Logger.Information("JonsboDevice {Device}: Opening (attempt {Retry})", _device, retryCount + 1);
                    serial = JonsboSerialDevice.Open(_device.DeviceLocation);

                    if (serial == null)
                    {
                        _device.UpdateRuntimeProperties(errorMessage: $"Cannot open {_device.DeviceLocation}");
                        await Task.Delay(retryCount < 3 ? 1000 : 5000, token);
                        retryCount++;
                        continue;
                    }

                    // Handshake: F0 A5 5A 0F -> ASCII identity with model, native resolution, serial.
                    // The OEM app retries after 1 s of silence; do the same once before giving up.
                    var identity = serial.GetIdentity();
                    if (identity == null)
                    {
                        await Task.Delay(1000, token);
                        identity = serial.GetIdentity();
                    }

                    if (identity == null)
                    {
                        _device.UpdateRuntimeProperties(errorMessage: "No identity reply");
                        serial.Dispose();
                        await Task.Delay(2000, token);
                        retryCount++;
                        continue;
                    }

                    Logger.Information("JonsboDevice {Device}: model={Model} serial={Serial} native {W}x{H}",
                        _device, identity.Model, identity.Serial, identity.Width, identity.Height);

                    // Trust the device-reported native resolution.
                    if (identity.Width > 0 && identity.Height > 0)
                    {
                        _panelWidth = identity.Width;
                        _panelHeight = identity.Height;
                    }

                    _device.RuntimeProperties.Name = $"{modelInfo.Name} {identity.Model} ({_panelWidth}x{_panelHeight})";

                    retryCount = 0;
                    await RunRenderSendLoop(serial, token);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    Logger.Error(ex, "JonsboDevice {Device}: Error", _device);
                    _device.UpdateRuntimeProperties(errorMessage: ex.Message);
                    retryCount++;
                }
                finally
                {
                    serial?.Dispose();
                    _device.UpdateRuntimeProperties(isRunning: false);
                }

                if (!token.IsCancellationRequested)
                    await Task.Delay(retryCount < 3 ? 1000 : 5000, token);
            }
        }

        private async Task RunRenderSendLoop(JonsboSerialDevice serial, CancellationToken token)
        {
            FpsCounter fpsCounter = new(60);
            byte[]? latestFrame = null;
            byte[]? lastSentFrame = null;
            long lastRenderMs = 0;
            AutoResetEvent frameAvailable = new(false);

            var renderCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            var renderToken = renderCts.Token;

            _device.UpdateRuntimeProperties(isRunning: true, errorMessage: string.Empty);

            // Render thread: produce JPEG frames at the target frame rate.
            var renderTask = Task.Run(async () =>
            {
                Thread.CurrentThread.Name ??= $"Jonsbo-Render-{_device.DeviceLocation}";
                try
                {
                    var stopwatch = new Stopwatch();
                    while (!renderToken.IsCancellationRequested)
                    {
                        stopwatch.Restart();

                        var frame = GenerateJpegBuffer();
                        if (frame != null)
                        {
                            Interlocked.Exchange(ref lastRenderMs, stopwatch.ElapsedMilliseconds);
                            Interlocked.Exchange(ref latestFrame, frame);
                            frameAvailable.Set();
                        }
                        else
                        {
                            // Content unchanged: skip encode; the send loop resends the
                            // previous frame after 1 s, which keeps the panel awake.
                            fpsCounter.Update(0);
                            _device.UpdateRuntimeProperties(frameRate: fpsCounter.FramesPerSecond, frameTime: fpsCounter.FrameTime);
                        }

                        var targetFrameTime = 1000 / Math.Max(1, _device.TargetFrameRate);
                        var elapsedMs = (int)stopwatch.ElapsedMilliseconds;
                        var remaining = targetFrameTime - elapsedMs;
                        if (remaining > 0) await Task.Delay(remaining, renderToken);
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception e)
                {
                    Logger.Error(e, "JonsboDevice {Device}: Render error", _device);
                    _device.UpdateRuntimeProperties(errorMessage: e.Message);
                    renderCts.Cancel();
                }
            }, renderToken);

            // Send thread: drain frames to the serial port. The firmware has no keep-alive
            // command; the OEM app simply resends the current image every ~500 ms when the
            // content is static, so we resend the last frame if nothing new shows up.
            var sendTask = Task.Run(() =>
            {
                Thread.CurrentThread.Name ??= $"Jonsbo-Send-{_device.DeviceLocation}";
                try
                {
                    var stopwatch = new Stopwatch();
                    // renderToken: a dead render task must end this loop too,
                    // or the device task never exits and is never restarted.
                    while (!renderToken.IsCancellationRequested)
                    {
                        byte[]? jpegData;
                        if (frameAvailable.WaitOne(1000))
                        {
                            jpegData = Interlocked.Exchange(ref latestFrame, null);
                        }
                        else
                        {
                            // No new frame in 1 s - resend the previous one so the panel
                            // doesn't fall back to its boot animation.
                            jpegData = lastSentFrame;
                        }

                        if (jpegData != null)
                        {
                            stopwatch.Restart();
                            serial.SendJpegFrame(jpegData);
                            lastSentFrame = jpegData;

                            // The firmware sends no reply, so the buffered serial write
                            // returns in ~0 ms; report render+encode+send as the frame
                            // time instead of a meaningless constant zero.
                            fpsCounter.Update(Interlocked.Read(ref lastRenderMs) + stopwatch.ElapsedMilliseconds);
                            _device.UpdateRuntimeProperties(frameRate: fpsCounter.FramesPerSecond, frameTime: fpsCounter.FrameTime);
                        }
                    }
                }
                catch (Exception e)
                {
                    Logger.Error(e, "JonsboDevice {Device}: Send error", _device);
                    _device.UpdateRuntimeProperties(errorMessage: e.Message);
                }
                finally
                {
                    renderCts.Cancel();
                }
            }, token);

            await Task.WhenAll(renderTask, sendTask);

            frameAvailable.Dispose();
            renderCts.Dispose();
        }

        // Resend the cached payload at full cadence when content is unchanged:
        // wire behavior stays identical to pre-0.1.3 builds (some panel firmwares
        // treat a slow stream as stopped); only render/resize/encode is skipped.
        private readonly SharedFrameConsumer _frameConsumer = new() { ResendCachedOnSkip = true };

        /// <summary>Returns null when the profile content has not changed (frame skipped).</summary>
        private byte[]? GenerateJpegBuffer()
        {
            var modelInfo = _device.ModelInfo;
            int maxBytes = modelInfo?.MaxJpegBytes ?? 256 * 1024;

            if (DeviceRuntime.GetProfile(_device.ProfileGuid) is Profile profile)
            {
                var frameInterval = 1000 / Math.Max(1, _device.TargetFrameRate);
                return _frameConsumer.Produce(profile, frameInterval, bitmap => EncodeFromShared(bitmap, maxBytes));
            }

            return GenerateBlackJpeg();
        }

        private byte[] EncodeFromShared(SKBitmap bitmap, int maxBytes)
        {
            var rotation = _device.Rotation;

            // The JPEG must be in the panel's native portrait orientation; the rotation
            // setting maps the (usually landscape) profile onto it.
            var resizedBitmap = SKBitmapExtensions.EnsureBitmapSize(bitmap, _panelWidth, _panelHeight, rotation);
            try
            {
                SKBitmap encodeBitmap = resizedBitmap;
                SKBitmap? dimmed = null;
                try
                {
                    if (_device.Brightness < 100)
                    {
                        // No hardware backlight command is known for this firmware -
                        // brightness is applied in-pixel.
                        dimmed = ApplyBrightness(resizedBitmap);
                        encodeBitmap = dimmed;
                    }

                    int quality = _device.JpegQuality;
                    using var image = SKImage.FromBitmap(encodeBitmap);

                    // Drop quality until the JPEG fits the soft payload cap.
                    for (int attempt = 0; attempt < 5; attempt++)
                    {
                        using var data = image.Encode(SKEncodedImageFormat.Jpeg, quality);
                        if (data.Size <= maxBytes || quality <= 30)
                            return data.ToArray();
                        quality = Math.Max(30, quality - 15);
                    }
                    using var fallback = image.Encode(SKEncodedImageFormat.Jpeg, 30);
                    return fallback.ToArray();
                }
                finally
                {
                    dimmed?.Dispose();
                }
            }
            finally
            {
                if (!ReferenceEquals(resizedBitmap, bitmap))
                {
                    resizedBitmap.Dispose();
                }
            }
        }

        private SKBitmap ApplyBrightness(SKBitmap source)
        {
            float scale = Math.Clamp(_device.Brightness, 0, 100) / 100f;
            var result = new SKBitmap(source.Width, source.Height, source.ColorType, source.AlphaType);
            using var canvas = new SKCanvas(result);
            using var paint = new SKPaint();
            paint.ColorFilter = SKColorFilter.CreateColorMatrix(
            [
                scale, 0,     0,     0, 0,
                0,     scale, 0,     0, 0,
                0,     0,     scale, 0, 0,
                0,     0,     0,     1, 0
            ]);
            canvas.DrawBitmap(source, 0, 0, paint);
            return result;
        }

        private byte[]? _cachedBlackJpeg;

        private byte[] GenerateBlackJpeg()
        {
            if (_cachedBlackJpeg != null) return _cachedBlackJpeg;
            using var bitmap = new SKBitmap(_panelWidth, _panelHeight, SKColorType.Rgba8888, SKAlphaType.Opaque);
            using var canvas = new SKCanvas(bitmap);
            canvas.Clear(SKColors.Black);
            using var image = SKImage.FromBitmap(bitmap);
            using var data = image.Encode(SKEncodedImageFormat.Jpeg, 50);
            _cachedBlackJpeg = data.ToArray();
            return _cachedBlackJpeg;
        }

        // ==================== MS9132 transport (DS339) ====================

        private async Task DoMs9132WorkAsync(JonsboPanelModelInfo modelInfo, CancellationToken token)
        {
            int retryCount = 0;
            while (!token.IsCancellationRequested)
            {
                JonsboMs9132Device? ms = null;
                try
                {
                    Logger.Information("JonsboDevice {Device}: Opening MS9132 (attempt {Retry})", _device, retryCount + 1);
                    ms = JonsboMs9132Device.Open();

                    if (ms == null)
                    {
                        _device.UpdateRuntimeProperties(errorMessage: "Cannot open MS9132");
                        await Task.Delay(retryCount < 3 ? 1000 : 5000, token);
                        retryCount++;
                        continue;
                    }

                    if (!ms.IsPanelConnected())
                    {
                        Logger.Warning("JonsboDevice {Device}: MS9132 reports no panel connected (reg 0x32)", _device);
                        // Continue anyway - some firmware revisions may not report status.
                    }

                    ms.SetMode(_panelWidth, _panelHeight, modelInfo.Vic);
                    _device.RuntimeProperties.Name = $"{modelInfo.Name} ({_panelWidth}x{_panelHeight})";
                    _device.UpdateRuntimeProperties(isRunning: true, errorMessage: string.Empty);

                    retryCount = 0;
                    await RunMs9132RenderSendLoop(ms, token);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    Logger.Error(ex, "JonsboDevice {Device}: MS9132 error", _device);
                    _device.UpdateRuntimeProperties(errorMessage: ex.Message);
                    retryCount++;
                }
                finally
                {
                    ms?.Dispose();
                    _device.UpdateRuntimeProperties(isRunning: false);
                }

                if (!token.IsCancellationRequested)
                    await Task.Delay(retryCount < 3 ? 1000 : 5000, token);
            }
        }

        private async Task RunMs9132RenderSendLoop(JonsboMs9132Device ms, CancellationToken token)
        {
            FpsCounter fpsCounter = new(60);
            var stopwatch = new Stopwatch();
            byte[]? uyvyBuffer = null;

            while (!token.IsCancellationRequested)
            {
                stopwatch.Restart();

                if (GenerateUyvyFrame(ref uyvyBuffer))
                {
                    ms.SendFrame(uyvyBuffer!, _panelWidth, _panelHeight);
                    fpsCounter.Update(stopwatch.ElapsedMilliseconds);
                }
                else
                {
                    // Content unchanged: the panel holds the last frame.
                    fpsCounter.Update(0);
                }
                _device.UpdateRuntimeProperties(frameRate: fpsCounter.FramesPerSecond, frameTime: fpsCounter.FrameTime);

                var targetFrameTime = 1000 / Math.Max(1, _device.TargetFrameRate);
                var remaining = targetFrameTime - (int)stopwatch.ElapsedMilliseconds;
                if (remaining > 0) await Task.Delay(remaining, token);
            }
        }

        /// <summary>
        /// Renders the profile and converts to UYVY422 (2 bytes/pixel, U Y0 V Y1 per
        /// pixel pair, BT.601 coefficients from the ms912x kernel driver) at the panel's
        /// native portrait resolution, reusing <paramref name="uyvyBuffer"/> across frames.
        /// The chip consumes UYVY only - RGB payloads desync into flashing garbage.
        /// </summary>
        // Same resend-on-skip policy; the cached payload is the reused UYVY buffer.
        private readonly SharedFrameConsumer _ms9132FrameConsumer = new() { ResendCachedOnSkip = true };

        /// <summary>Fills the UYVY buffer; returns false when the content has not changed.</summary>
        private bool GenerateUyvyFrame(ref byte[]? uyvyBuffer)
        {
            int pixelCount = _panelWidth * _panelHeight;
            uyvyBuffer ??= new byte[pixelCount * 2];
            var target = uyvyBuffer;

            if (DeviceRuntime.GetProfile(_device.ProfileGuid) is Profile profile)
            {
                var frameInterval = 1000 / Math.Max(1, _device.TargetFrameRate);
                return _ms9132FrameConsumer.Produce(profile, frameInterval, bitmap =>
                {
                    var rendered = SKBitmapExtensions.EnsureBitmapSize(bitmap, _panelWidth, _panelHeight, _device.Rotation);
                    try
                    {
                        ConvertToUyvy(rendered, target);
                    }
                    finally
                    {
                        if (!ReferenceEquals(rendered, bitmap))
                        {
                            rendered.Dispose();
                        }
                    }
                    return target;
                }) != null;
            }

            // No profile: black frame once per heartbeat is enough
            using (var black = new SKBitmap(_panelWidth, _panelHeight, SKColorType.Rgba8888, SKAlphaType.Opaque))
            {
                black.Erase(SKColors.Black);
                ConvertToUyvy(black, target);
            }
            return true;
        }

        private void ConvertToUyvy(SKBitmap rendered, byte[] uyvyBuffer)
        {
            int scale256 = Math.Clamp(_device.Brightness, 0, 100) * 256 / 100;
                bool dim = scale256 < 256;

                unsafe
                {
                    byte* src = (byte*)rendered.GetPixels().ToPointer();
                    int srcRowBytes = rendered.RowBytes;
                    fixed (byte* dstBase = uyvyBuffer)
                    {
                        byte* dst = dstBase;
                        for (int y = 0; y < _panelHeight; y++)
                        {
                            byte* row = src + y * srcRowBytes;
                            for (int x = 0; x < _panelWidth; x += 2)
                            {
                                int o = x * 4;            // Rgba8888: R,G,B,A
                                int r1 = row[o], g1 = row[o + 1], b1 = row[o + 2];
                                int r2 = row[o + 4], g2 = row[o + 5], b2 = row[o + 6];
                                if (dim)
                                {
                                    r1 = r1 * scale256 >> 8; g1 = g1 * scale256 >> 8; b1 = b1 * scale256 >> 8;
                                    r2 = r2 * scale256 >> 8; g2 = g2 * scale256 >> 8; b2 = b2 * scale256 >> 8;
                                }
                                int y1 = (16 << 16) + 16763 * r1 + 32904 * g1 + 6391 * b1;
                                int y2 = (16 << 16) + 16763 * r2 + 32904 * g2 + 6391 * b2;
                                int u = ((128 << 17) - 9676 * (r1 + r2) - 18996 * (g1 + g2) + 28672 * (b1 + b2)) >> 1;
                                int v = ((128 << 17) + 28672 * (r1 + r2) - 24009 * (g1 + g2) - 4663 * (b1 + b2)) >> 1;
                                *dst++ = (byte)(u >> 16);
                                *dst++ = (byte)(y1 >> 16);
                                *dst++ = (byte)(v >> 16);
                                *dst++ = (byte)(y2 >> 16);
                            }
                        }
                    }
                }
        }
    }
}
