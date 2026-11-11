using InfoPanel.Models;
using Serilog;
using SkiaSharp;
using System;
using System.Threading;

namespace InfoPanel.Gpu
{
    /// <summary>
    /// Chooses where shared panel frames are rendered: on the CPU (Skia raster,
    /// the default) or on a selected GPU. Any GPU problem (no EGL, context
    /// creation failure, a render error or hang) falls back to the CPU for the
    /// rest of the session or until another device is selected, so a broken
    /// driver can cost frames but never stop the panels.
    /// </summary>
    public static class GpuRendering
    {
        private static readonly ILogger Logger = Log.ForContext(typeof(GpuRendering));

        public const string Cpu = "cpu";

        /// <summary>A render taking longer than this is treated as a hung GPU.</summary>
        private const int RenderTimeoutMs = 2000;
        /// <summary>First frames on a new context compile shaders and upload every image.</summary>
        private const int WarmupTimeoutMs = 15000;
        private const int WarmupFrames = 5;
        private const int MaxConsecutiveFailures = 3;
        /// <summary>After render errors (e.g. context lost across suspend) try a fresh context.</summary>
        private const int RetryAfterErrorMs = 5000;
        private const int MaxRetries = 5;

        private static readonly Lock _lock = new();
        private static string _selected = Cpu;
        private static GpuFrameRenderer? _renderer;
        private static bool _unavailable;
        private static long _retryAtMs = long.MaxValue;   // when _unavailable: next attempt, MaxValue = never
        private static int _retries;
        private static int _consecutiveFailures;
        private static string _status = "Rendering on the CPU";

        /// <summary>Raised (on any thread) when <see cref="Status"/> changes.</summary>
        public static event Action? StatusChanged;

        /// <summary>The device id currently selected ("cpu" or a <see cref="GpuDeviceInfo.Id"/>).</summary>
        public static string Selected
        {
            get { lock (_lock) return _selected; }
        }

        /// <summary>Human-readable state for the settings page.</summary>
        public static string Status
        {
            get { lock (_lock) return _status; }
        }

        /// <summary>True when frames are currently going through a GPU.</summary>
        public static bool IsActive
        {
            get { lock (_lock) return _renderer != null; }
        }

        /// <summary>
        /// Call first thing in Main, before any GL library loads. The UI draws
        /// through GLX, and glvnd lets the vendor of the first current context
        /// patch the shared GL entry points; while that context is current, an
        /// EGL context from another vendor library (the panel renderer, on
        /// either GPU) then fails eglMakeCurrent with no EGL error. Disabling
        /// patching keeps plain dispatch, which costs nothing measurable here.
        /// </summary>
        public static void PrepareProcess()
        {
            if (OperatingSystem.IsLinux())
            {
                // Native setenv: glvnd reads the variable with getenv when it loads,
                // which does not see Environment.SetEnvironmentVariable.
                _ = setenv("__GLVND_DISALLOW_PATCHING", "1", 0);
            }
        }

        [System.Runtime.InteropServices.DllImport("libc", CharSet = System.Runtime.InteropServices.CharSet.Ansi)]
        private static extern int setenv(string name, string value, int overwrite);

        /// <summary>
        /// Selects the device for panel rendering. Takes effect on the next
        /// frame; the GPU context is created lazily by the first render.
        /// </summary>
        public static void Select(string? deviceId)
        {
            var id = string.IsNullOrWhiteSpace(deviceId) ? Cpu : deviceId.Trim();
            GpuFrameRenderer? old;
            lock (_lock)
            {
                if (string.Equals(id, _selected, StringComparison.OrdinalIgnoreCase) && !_unavailable)
                {
                    return;
                }
                old = _renderer;
                _renderer = null;
                _selected = id;
                _unavailable = false;
                _retryAtMs = long.MaxValue;
                _retries = 0;
                _consecutiveFailures = 0;
                SetStatus(id == Cpu ? "Rendering on the CPU" : "GPU selected, starts with the next frame");
            }
            old?.Dispose();
            Logger.Information("Panel render device set to {Device}", id);
        }

        /// <summary>
        /// Renders into <paramref name="target"/> on the selected GPU. Returns
        /// false when the CPU should render this frame instead.
        /// </summary>
        public static bool TryRender(Profile profile, SKBitmap target, string cacheHint)
        {
            var renderer = GetRenderer();
            if (renderer == null)
            {
                return false;
            }

            string? failure;
            try
            {
                var timeout = renderer.FramesRendered < WarmupFrames ? WarmupTimeoutMs : RenderTimeoutMs;
                if (renderer.Render(profile, target, cacheHint, timeout))
                {
                    if (_consecutiveFailures != 0)
                    {
                        Interlocked.Exchange(ref _consecutiveFailures, 0);
                    }
                    if (_retries != 0 && renderer.FramesRendered > 1000)
                    {
                        // Healthy again for a while: a later suspend gets fresh retries.
                        lock (_lock) _retries = 0;
                    }
                    return true;
                }
                failure = $"a frame took longer than {timeout} ms";
                // A hung driver call will not come back; stop queueing behind it
                // and don't retry: a new context on a wedged driver hangs too.
                Disable(renderer, failure, retry: false);
                return false;
            }
            catch (Exception ex)
            {
                failure = ex.Message;
                Logger.Warning(ex, "GPU render failed on {Device}", renderer.Device.DisplayName);
            }

            if (Interlocked.Increment(ref _consecutiveFailures) >= MaxConsecutiveFailures)
            {
                Disable(renderer, failure, retry: true);
            }
            return false;
        }

        private static GpuFrameRenderer? GetRenderer()
        {
            lock (_lock)
            {
                if (_renderer != null)
                {
                    return _renderer;
                }
                if (_selected == Cpu)
                {
                    return null;
                }
                if (_unavailable)
                {
                    if (Environment.TickCount64 < _retryAtMs)
                    {
                        return null;
                    }
                    _unavailable = false;
                    _retryAtMs = long.MaxValue;
                    Logger.Information("Retrying GPU rendering on {Device} (attempt {Attempt} of {Max})", _selected, _retries, MaxRetries);
                }

                var device = GpuDeviceCatalog.Find(_selected);
                if (device == null)
                {
                    _unavailable = true;
                    SetStatus("The selected GPU was not found, rendering on the CPU");
                    Logger.Warning("Panel render device {Device} not found; rendering on the CPU", _selected);
                    return null;
                }

                // Created under the lock so concurrent panels don't each build a
                // context; this happens once per selection and takes ~100 ms.
                try
                {
                    _renderer = GpuFrameRenderer.Create(device, TimeSpan.FromSeconds(10));
                    _consecutiveFailures = 0;
                    SetStatus($"Rendering on {device.Name} ({device.PciAddress})");
                    Logger.Information("Rendering panels on {Renderer} [{Device}]", _renderer.RendererName, device.Id);
                    return _renderer;
                }
                catch (Exception ex)
                {
                    _unavailable = true;
                    SetStatus($"Could not use {device.Name}: {ex.Message}. Rendering on the CPU");
                    Logger.Warning(ex, "GPU rendering unavailable on {Device}; rendering on the CPU", device.DisplayName);
                    return null;
                }
            }
        }

        private static void Disable(GpuFrameRenderer renderer, string reason, bool retry)
        {
            lock (_lock)
            {
                if (!ReferenceEquals(_renderer, renderer))
                {
                    return;
                }
                _renderer = null;
                _unavailable = true;
                if (retry && _retries < MaxRetries)
                {
                    _retries++;
                    _retryAtMs = Environment.TickCount64 + RetryAfterErrorMs;
                }
                SetStatus($"GPU rendering stopped ({reason}). Rendering on the CPU");
            }
            Logger.Error("GPU rendering on {Device} stopped: {Reason}. Falling back to the CPU", renderer.Device.DisplayName, reason);
            renderer.Dispose();
        }

        // Caller holds _lock.
        private static void SetStatus(string status)
        {
            if (_status == status) return;
            _status = status;
            var handler = StatusChanged;
            if (handler != null)
            {
                ThreadPool.QueueUserWorkItem(_ => handler());
            }
        }
    }
}
