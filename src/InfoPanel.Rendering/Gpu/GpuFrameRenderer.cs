using InfoPanel.Drawing;
using InfoPanel.Models;
using Serilog;
using SkiaSharp;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace InfoPanel.Gpu
{
    /// <summary>
    /// Renders profiles on one GPU through a headless EGL context and reads the
    /// result back into a caller-owned bitmap, so everything downstream (frame
    /// diffing, rotation, JPEG/RGB565 encode) is unchanged.
    ///
    /// A GL context is current on one thread at a time and the panel loops are
    /// async (they resume on arbitrary pool threads), so the context lives on a
    /// dedicated thread and every render is posted to it. Renders are short
    /// (a few ms) and serialise naturally there.
    ///
    /// Images are drawn from the raster image caches, not the Avalonia GL
    /// texture caches: those belong to the UI's GL context and are disposed on
    /// its thread. Skia uploads raster images to this context itself and keeps
    /// the textures in its resource cache, keyed by image id, so static images
    /// upload once.
    /// </summary>
    internal sealed class GpuFrameRenderer : IDisposable
    {
        private static readonly ILogger Logger = Log.ForContext<GpuFrameRenderer>();

        private sealed class Work(Action action)
        {
            public readonly Action Action = action;
            public readonly ManualResetEventSlim Done = new(false);
            public Exception? Error;
        }

        private readonly GpuDeviceInfo _device;
        private readonly BlockingCollection<Work> _queue = new();
        private readonly Thread _thread;
        private readonly ManualResetEventSlim _ready = new(false);
        private readonly Dictionary<(int W, int H), (SKSurface Surface, long LastUseMs)> _surfaces = [];

        private IntPtr _display;
        private IntPtr _context;
        private IntPtr _pbuffer;
        private GRGlInterface? _glInterface;
        private GRContext? _grContext;
        private string? _initError;
        private long _lastPurgeMs;
        private volatile bool _disposed;

        public string RendererName { get; private set; } = "";

        /// <summary>Completed renders; the first few get a longer timeout (shader compiles, uploads).</summary>
        public int FramesRendered => Volatile.Read(ref _framesRendered);
        private int _framesRendered;

        public GpuDeviceInfo Device => _device;

        private GpuFrameRenderer(GpuDeviceInfo device)
        {
            _device = device;
            _thread = new Thread(ThreadMain)
            {
                IsBackground = true,
                Name = "GPU render",
            };
        }

        /// <summary>Creates the context on its thread; throws with the reason when the GPU can't be used.</summary>
        public static GpuFrameRenderer Create(GpuDeviceInfo device, TimeSpan timeout)
        {
            var renderer = new GpuFrameRenderer(device);
            renderer._thread.Start();
            if (!renderer._ready.Wait(timeout))
            {
                // Leave the thread: it is stuck in the driver and owns nothing we can free safely.
                throw new TimeoutException($"GPU context creation on {device.DisplayName} did not finish in {timeout.TotalSeconds:0} s");
            }
            if (renderer._initError != null)
            {
                renderer._queue.CompleteAdding();
                throw new InvalidOperationException(renderer._initError);
            }
            return renderer;
        }

        /// <summary>
        /// Renders <paramref name="profile"/> into <paramref name="target"/> (its
        /// size is the render size; the profile is scaled to fit like the CPU
        /// path). Returns false on timeout; rethrows GPU-side exceptions.
        /// </summary>
        public bool Render(Profile profile, SKBitmap target, string cacheHint, int timeoutMs)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var work = new Work(() => RenderOnThread(profile, target, cacheHint));
            try
            {
                _queue.Add(work);
            }
            catch (InvalidOperationException)
            {
                throw new ObjectDisposedException(nameof(GpuFrameRenderer));
            }

            if (!work.Done.Wait(timeoutMs))
            {
                // The caller falls back to the CPU and stops using this renderer.
                // The target bitmap may still be written by the late render; it
                // is reused by the cache and fully redrawn on the next frame.
                return false;
            }
            work.Done.Dispose();
            if (work.Error != null)
            {
                throw new InvalidOperationException("GPU render failed: " + work.Error.Message, work.Error);
            }
            return true;
        }

        private void ThreadMain()
        {
            try
            {
                Initialise();
            }
            catch (Exception ex)
            {
                _initError = ex.Message;
            }
            finally
            {
                _ready.Set();
            }

            if (_initError != null)
            {
                TearDown();
                return;
            }

            foreach (var work in _queue.GetConsumingEnumerable())
            {
                try
                {
                    work.Action();
                }
                catch (Exception ex)
                {
                    work.Error = ex;
                }
                finally
                {
                    work.Done.Set();
                }
            }

            TearDown();
        }

        private void Initialise()
        {
            var getDisplay = Egl.Proc<Egl.GetPlatformDisplayExt>("eglGetPlatformDisplayEXT")
                ?? throw new InvalidOperationException("eglGetPlatformDisplayEXT missing");

            _display = getDisplay(Egl.EGL_PLATFORM_DEVICE_EXT, _device.Handle, null);
            if (_display == IntPtr.Zero || !Egl.eglInitialize(_display, out _, out _))
            {
                throw new InvalidOperationException("eglInitialize failed " + Egl.ErrorText());
            }
            if (!Egl.eglBindAPI(Egl.EGL_OPENGL_API))
            {
                throw new InvalidOperationException("eglBindAPI(OpenGL) failed " + Egl.ErrorText());
            }

            var configs = new IntPtr[1];
            int[] configAttribs =
            [
                Egl.EGL_SURFACE_TYPE, Egl.EGL_PBUFFER_BIT,
                Egl.EGL_RENDERABLE_TYPE, Egl.EGL_OPENGL_BIT,
                Egl.EGL_RED_SIZE, 8, Egl.EGL_GREEN_SIZE, 8, Egl.EGL_BLUE_SIZE, 8, Egl.EGL_ALPHA_SIZE, 8,
                Egl.EGL_NONE,
            ];
            if (!Egl.eglChooseConfig(_display, configAttribs, configs, 1, out var numConfigs) || numConfigs == 0)
            {
                throw new InvalidOperationException("no suitable EGL config " + Egl.ErrorText());
            }

            _context = Egl.eglCreateContext(_display, configs[0], IntPtr.Zero, [Egl.EGL_NONE]);
            if (_context == IntPtr.Zero)
            {
                throw new InvalidOperationException("eglCreateContext failed " + Egl.ErrorText());
            }

            // Skia renders into its own FBOs; the pbuffer only gives the context
            // something to be current on where surfaceless contexts are missing.
            _pbuffer = Egl.eglCreatePbufferSurface(_display, configs[0], [Egl.EGL_WIDTH, 16, Egl.EGL_HEIGHT, 16, Egl.EGL_NONE]);
            if (!Egl.eglMakeCurrent(_display, _pbuffer, _pbuffer, _context))
            {
                throw new InvalidOperationException("eglMakeCurrent failed " + Egl.ErrorText());
            }

            var glGetString = Egl.Proc<Egl.GlGetString>("glGetString");
            RendererName = Egl.String(glGetString?.Invoke(Egl.GL_RENDERER) ?? IntPtr.Zero) ?? _device.Name;

            _glInterface = GRGlInterface.Create(name => Egl.eglGetProcAddress(name))
                ?? throw new InvalidOperationException("Skia could not load the GL functions");
            _grContext = GRContext.CreateGl(_glInterface)
                ?? throw new InvalidOperationException("Skia could not create a GL context on " + RendererName);

            // Panels need a handful of static images plus one video frame per
            // tick; 128 MB keeps them resident without hoarding VRAM.
            _grContext.SetResourceCacheLimit(128L * 1024 * 1024);
            _lastPurgeMs = Environment.TickCount64;
        }

        private void RenderOnThread(Profile profile, SKBitmap target, string cacheHint)
        {
            var grContext = _grContext!;
            if (grContext.IsAbandoned)
            {
                // Driver reset or suspend: this context is gone for good.
                throw new InvalidOperationException("the GPU context was lost");
            }
            var now = Environment.TickCount64;
            var key = (target.Width, target.Height);

            if (!_surfaces.TryGetValue(key, out var slot))
            {
                var info = new SKImageInfo(target.Width, target.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
                var surface = SKSurface.Create(grContext, false, info)
                    ?? throw new InvalidOperationException($"could not allocate a {target.Width}x{target.Height} GPU surface");
                slot = (surface, now);
            }
            _surfaces[key] = (slot.Surface, now);

            var canvas = slot.Surface.Canvas;
            // Profiles normally clear to their background; this keeps a profile
            // without a valid background colour from showing another profile's
            // frame when two same-sized profiles share the surface.
            canvas.Clear(SKColors.Black);
            canvas.Save();
            if (target.Width != profile.Width || target.Height != profile.Height)
            {
                canvas.Scale((float)target.Width / profile.Width, (float)target.Height / profile.Height);
            }

            // Not disposed: SkiaGraphics.Dispose would dispose the surface's canvas.
            var g = new SkiaGraphics(canvas, profile.FontScale) { UseGpuImageCache = false };
            PanelDraw.Run(profile, g, preview: false, cacheHint: cacheHint);
            canvas.Restore();

            grContext.Flush();
            if (!slot.Surface.ReadPixels(target.Info, target.GetPixels(), target.RowBytes, 0, 0))
            {
                throw new InvalidOperationException("reading the frame back from the GPU failed");
            }
            Interlocked.Increment(ref _framesRendered);

            if (now - _lastPurgeMs > 10_000)
            {
                _lastPurgeMs = now;
                PurgeIdle(now);
            }
        }

        private void PurgeIdle(long now)
        {
            List<(int, int)>? stale = null;
            foreach (var (key, slot) in _surfaces)
            {
                if (now - slot.LastUseMs > 30_000)
                {
                    (stale ??= []).Add(key);
                }
            }
            if (stale != null)
            {
                foreach (var key in stale)
                {
                    _surfaces[key].Surface.Dispose();
                    _surfaces.Remove(key);
                }
            }
            // Video frames upload a new texture per frame; return the old ones.
            _grContext?.PurgeUnusedResources(10_000);
        }

        private void TearDown()
        {
            try
            {
                foreach (var slot in _surfaces.Values)
                {
                    slot.Surface.Dispose();
                }
                _surfaces.Clear();
                _grContext?.Dispose();
                _glInterface?.Dispose();
                if (_display != IntPtr.Zero)
                {
                    Egl.eglMakeCurrent(_display, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                    if (_pbuffer != IntPtr.Zero) Egl.eglDestroySurface(_display, _pbuffer);
                    if (_context != IntPtr.Zero) Egl.eglDestroyContext(_display, _context);
                    // No eglTerminate: the device display is shared process-wide,
                    // and a later renderer on the same GPU reuses it.
                }
                Egl.eglReleaseThread();
            }
            catch (Exception ex)
            {
                Logger.Debug(ex, "GPU renderer teardown");
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _queue.CompleteAdding();
        }
    }
}
