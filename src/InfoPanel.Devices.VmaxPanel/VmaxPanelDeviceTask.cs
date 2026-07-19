using InfoPanel.Drawing;
using InfoPanel.Extensions;
using InfoPanel.Models;
using InfoPanel.Utils;
using InfoPanel.VmaxPanel;
using Serilog;
using SkiaSharp;
using System;
using System.Buffers;
using System.Diagnostics;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace InfoPanel.Services
{
    public sealed class VmaxPanelDeviceTask : BackgroundTask
    {
        private static readonly ILogger Logger = Log.ForContext<VmaxPanelDeviceTask>();

        private readonly VmaxPanelDevice _device;
        private int _panelWidth;
        private int _panelHeight;

        public VmaxPanelDeviceTask(VmaxPanelDevice device)
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

            int retryCount = 0;
            while (!token.IsCancellationRequested)
            {
                try
                {
                    Logger.Information("VmaxPanelDevice {Device}: Opening USB device (attempt {Retry})",
                        _device, retryCount + 1);

                    using var vmaxDevice = VmaxUsbDevice.Open(_device.DeviceId);
                    if (vmaxDevice == null)
                    {
                        _device.UpdateRuntimeProperties(errorMessage: "Device not found");
                        await Task.Delay(2000, token);
                        retryCount++;
                        continue;
                    }

                    retryCount = 0;
                    await RunRenderSendLoop(vmaxDevice, token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Logger.Error(ex, "VmaxPanelDevice {Device}: Error", _device);
                    _device.UpdateRuntimeProperties(errorMessage: ex.Message);
                    retryCount++;
                }
                finally
                {
                    _device.UpdateRuntimeProperties(isRunning: false);
                }

                if (!token.IsCancellationRequested)
                    await Task.Delay(retryCount < 3 ? 1000 : 5000, token);
            }
        }

        private async Task RunRenderSendLoop(VmaxUsbDevice vmaxDevice, CancellationToken token)
        {
            using var renderCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            var renderToken = renderCts.Token;
            var frames = Channel.CreateBounded<RenderedFrame>(new BoundedChannelOptions(2)
            {
                SingleReader = true,
                SingleWriter = true,
                FullMode = BoundedChannelFullMode.Wait
            });

            var renderTask = Task.Run(async () =>
            {
                try
                {
                    while (!renderToken.IsCancellationRequested)
                    {
                        var frame = GenerateUyvyFrame();
                        try
                        {
                            await frames.Writer.WriteAsync(frame, renderToken);
                        }
                        catch
                        {
                            ReturnFrame(frame);
                            throw;
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    frames.Writer.TryComplete(ex);
                    return;
                }

                frames.Writer.TryComplete();
            }, System.Threading.CancellationToken.None);

            FpsCounter fpsCounter = new(30);
            _device.UpdateRuntimeProperties(isRunning: true, errorMessage: string.Empty);

            try
            {
                await foreach (var frame in frames.Reader.ReadAllAsync(token))
                {
                    try
                    {
                        var stopwatch = Stopwatch.StartNew();

                        vmaxDevice.SendUyvyFrame(frame.Buffer, frame.Length);

                        vmaxDevice.SetScreenSwitch(_device.ScreenSwitch);

                        fpsCounter.Update(stopwatch.ElapsedMilliseconds);
                        _device.UpdateRuntimeProperties(frameRate: fpsCounter.FramesPerSecond, frameTime: fpsCounter.FrameTime);

                        var targetFrameTime = 1000 / Math.Max(1, Math.Min(_device.TargetFrameRate, 30));
                        var delay = targetFrameTime - (int)stopwatch.ElapsedMilliseconds;

                        if (delay > 0)
                            await Task.Delay(delay, token);
                    }
                    finally
                    {
                        ReturnFrame(frame);
                    }
                }
            }
            finally
            {
                renderCts.Cancel();
                while (frames.Reader.TryRead(out var pendingFrame))
                    ReturnFrame(pendingFrame);

                try
                {
                    await renderTask;
                }
                catch (OperationCanceledException)
                {
                }
            }
        }

        private static void ReturnFrame(RenderedFrame frame)
        {
            if (frame.ReturnToPool)
                ArrayPool<byte>.Shared.Return(frame.Buffer);
        }

        private RenderedFrame GenerateUyvyFrame()
        {
            if (DeviceRuntime.GetProfile(_device.ProfileGuid) is Profile profile)
            {
                using var bitmap = PanelRenderer.RenderSK(profile, false,
                    colorType: SKColorType.Rgba8888,
                    alphaType: SKAlphaType.Opaque);

                using var resizedBitmap = SKBitmapExtensions.EnsureBitmapSize(bitmap, _panelWidth, _panelHeight, _device.Rotation);

                var length = resizedBitmap.Width * resizedBitmap.Height * 2;
                var output = ArrayPool<byte>.Shared.Rent(length);
                ToUyvy(resizedBitmap, output, _device.Brightness);
                return new RenderedFrame(output, length);
            }

            var blackFrame = GenerateBlackUyvy();
            return new RenderedFrame(blackFrame, blackFrame.Length, ReturnToPool: false);
        }

        /// <summary>
        /// Converts Rgba8888 to UYVY422 (U Y0 V Y1 per pixel pair, BT.601 integer
        /// coefficients from the ms912x kernel driver). The MS9132 consumes UYVY only;
        /// RGB payloads desync into flashing garbage (verified on the sibling DS339).
        /// </summary>
        private static void ToUyvy(SKBitmap bitmap, byte[] output, int brightness)
        {
            var pixels = bitmap.GetPixelSpan();
            var srcStride = bitmap.RowBytes;
            int scale256 = Math.Clamp(brightness, 0, 100) * 256 / 100;
            bool dim = scale256 < 256;
            int offset = 0;

            unsafe
            {
                fixed (byte* srcBase = pixels)
                fixed (byte* dstBase = output)
                {
                    for (int y = 0; y < bitmap.Height; y++)
                    {
                        byte* srcRow = srcBase + y * srcStride;
                        for (int x = 0; x < bitmap.Width; x += 2)
                        {
                            int si = x * 4;              // Rgba8888: R,G,B,A
                            int r1 = srcRow[si], g1 = srcRow[si + 1], b1 = srcRow[si + 2];
                            int r2 = srcRow[si + 4], g2 = srcRow[si + 5], b2 = srcRow[si + 6];
                            if (dim)
                            {
                                r1 = r1 * scale256 >> 8; g1 = g1 * scale256 >> 8; b1 = b1 * scale256 >> 8;
                                r2 = r2 * scale256 >> 8; g2 = g2 * scale256 >> 8; b2 = b2 * scale256 >> 8;
                            }
                            int y1 = (16 << 16) + 16763 * r1 + 32904 * g1 + 6391 * b1;
                            int y2 = (16 << 16) + 16763 * r2 + 32904 * g2 + 6391 * b2;
                            int u = ((128 << 17) - 9676 * (r1 + r2) - 18996 * (g1 + g2) + 28672 * (b1 + b2)) >> 1;
                            int v = ((128 << 17) + 28672 * (r1 + r2) - 24009 * (g1 + g2) - 4663 * (b1 + b2)) >> 1;
                            dstBase[offset++] = (byte)(u >> 16);
                            dstBase[offset++] = (byte)(y1 >> 16);
                            dstBase[offset++] = (byte)(v >> 16);
                            dstBase[offset++] = (byte)(y2 >> 16);
                        }
                    }
                }
            }
        }

        private byte[]? _cachedBlackUyvy;

        private byte[] GenerateBlackUyvy()
        {
            if (_cachedBlackUyvy == null)
            {
                // Black in UYVY is Y=16, U=V=128 - an all-zero buffer would be green.
                _cachedBlackUyvy = new byte[_panelWidth * _panelHeight * 2];
                for (int i = 0; i < _cachedBlackUyvy.Length; i += 2)
                {
                    _cachedBlackUyvy[i] = 0x80;
                    _cachedBlackUyvy[i + 1] = 0x10;
                }
            }
            return _cachedBlackUyvy;
        }

        private readonly record struct RenderedFrame(
            byte[] Buffer,
            int Length,
            bool ReturnToPool = true);
    }
}
