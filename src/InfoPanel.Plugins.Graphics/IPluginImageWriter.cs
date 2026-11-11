using SkiaSharp;

namespace InfoPanel.Plugins.Graphics
{
    /// <summary>
    /// Provided by the host. Plugin draws on Bitmap, calls Invalidate() when done.
    /// The bitmap is backed by shared memory for zero-copy transfer.
    /// </summary>
    public interface IPluginImageWriter : IDisposable
    {
        /// <summary>
        /// The bitmap to draw on. Backed by shared memory (inactive buffer).
        /// </summary>
        SKBitmap Bitmap { get; }

        int Width { get; }
        int Height { get; }

        /// <summary>
        /// Atomically swaps the double buffer, making the current frame visible to consumers.
        /// After calling this, Bitmap points to the new inactive buffer for the next frame.
        /// </summary>
        void Invalidate();

        /// <summary>
        /// Resizes the image buffer. Creates a new shared memory region with the new dimensions.
        /// After this call, Bitmap points to a new buffer with the specified size.
        /// </summary>
        void Resize(int width, int height);

        /// <summary>
        /// False while the frame published by the last <see cref="Invalidate"/> has
        /// not been read by any consumer yet. A plugin that renders faster than it
        /// is displayed can skip drawing until it turns true. Hosts that cannot
        /// tell report true.
        /// </summary>
        bool LastFrameConsumed => true;
    }
}
