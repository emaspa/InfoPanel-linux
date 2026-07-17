using SkiaSharp;

namespace InfoPanel.Plugins.Graphics
{
    /// <summary>
    /// Heap-backed IPluginImageWriter for the in-process plugin host. The Windows
    /// build's writer is shared-memory backed for its out-of-process host; here the
    /// plugin and host share the process, so a double-buffered pair of SKBitmaps is
    /// enough. The host reads the last invalidated frame via TryAccessFrame.
    /// </summary>
    public sealed class InProcPluginImageWriter : IPluginImageWriter
    {
        private SKBitmap _front;
        private SKBitmap _back;
        private readonly object _sync = new();
        private bool _hasFrame;
        private bool _disposed;

        public SKBitmap Bitmap { get; private set; }
        public int Width { get; private set; }
        public int Height { get; private set; }

        /// <summary>Bumped on every Invalidate; lets consumers skip unchanged frames.</summary>
        public long FrameCounter { get; private set; }

        public InProcPluginImageWriter(int width, int height)
        {
            Width = width;
            Height = height;
            _front = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
            _back = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
            Bitmap = _back;
        }

        public void Invalidate()
        {
            lock (_sync)
            {
                if (_disposed) return;
                (_front, _back) = (_back, _front);
                Bitmap = _back;
                _hasFrame = true;
                FrameCounter++;
            }
        }

        public void Resize(int width, int height)
        {
            lock (_sync)
            {
                if (_disposed) return;
                _front.Dispose();
                _back.Dispose();
                Width = width;
                Height = height;
                _front = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
                _back = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
                Bitmap = _back;
                _hasFrame = false;
            }
        }

        /// <summary>
        /// Runs <paramref name="access"/> against the most recently invalidated frame
        /// while holding the swap lock. Returns false if no frame has been produced yet.
        /// </summary>
        public bool TryAccessFrame(Action<SKBitmap> access)
        {
            lock (_sync)
            {
                if (_disposed || !_hasFrame) return false;
                access(_front);
                return true;
            }
        }

        public void Dispose()
        {
            lock (_sync)
            {
                if (_disposed) return;
                _disposed = true;
                _front.Dispose();
                _back.Dispose();
            }
        }
    }
}
