namespace InfoPanel.Video
{
    /// <summary>
    /// Picks the size ffmpeg scales video to. Scaling inside ffmpeg saves the per-frame
    /// in-app resize, but every size change is a process restart (the loop jumps back
    /// to the start), so the output only ever grows: the first request sets it, a
    /// larger request that persists for <see cref="GrowDelay"/> (designer opened, a
    /// second panel) grows it, and smaller consumers downscale from it in-app.
    /// Always clamped to the source size. Not thread-safe; the decoder serialises calls.
    /// </summary>
    internal sealed class VideoOutputSizePolicy
    {
        public static readonly TimeSpan DefaultGrowDelay = TimeSpan.FromSeconds(1);

        private readonly int _sourceWidth;
        private readonly int _sourceHeight;

        private (int W, int H)? _pending;
        private TimeSpan _pendingSince;
        private TimeSpan _pendingLastSeen;

        public TimeSpan GrowDelay { get; }

        /// <summary>Current output size, null until the first request.</summary>
        public (int Width, int Height)? Output { get; private set; }

        public VideoOutputSizePolicy(int sourceWidth, int sourceHeight, TimeSpan? growDelay = null)
        {
            _sourceWidth = Math.Max(1, sourceWidth);
            _sourceHeight = Math.Max(1, sourceHeight);
            GrowDelay = growDelay ?? DefaultGrowDelay;
        }

        /// <summary>
        /// Records a consumer's request at time <paramref name="now"/>. Returns true when
        /// <see cref="Output"/> changed (first request or growth) and the decoder has to
        /// (re)start at the new size.
        /// </summary>
        public bool Request(int width, int height, TimeSpan now)
        {
            var w = Math.Clamp(width, 1, _sourceWidth);
            var h = Math.Clamp(height, 1, _sourceHeight);

            if (Output is not { } current)
            {
                Output = (w, h);
                return true;
            }

            // Smaller or equal requests are served by an in-app downscale. They do not
            // cancel a pending growth: a small preview and a large panel interleave
            // their requests every frame.
            if (w <= current.Width && h <= current.Height)
            {
                return false;
            }

            // A larger request restarts the window if the last one is stale, so a
            // one-off large draw long ago does not count toward a later growth.
            if (_pending is not { } pending || now - _pendingLastSeen > GrowDelay)
            {
                _pending = (w, h);
                _pendingSince = now;
            }
            else
            {
                _pending = (Math.Max(pending.W, w), Math.Max(pending.H, h));
            }

            _pendingLastSeen = now;

            if (now - _pendingSince < GrowDelay)
            {
                return false;
            }

            var grown = _pending.Value;
            Output = (Math.Max(current.Width, grown.W), Math.Max(current.Height, grown.H));
            _pending = null;
            return true;
        }
    }
}
