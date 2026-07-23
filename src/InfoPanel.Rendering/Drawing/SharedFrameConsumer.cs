using InfoPanel.Models;
using SkiaSharp;
using System;

namespace InfoPanel
{
    /// <summary>
    /// A device task's view over <see cref="SharedFrameCache"/>: runs the produce
    /// callback (resize/convert/encode) only when the profile content actually
    /// changed since this consumer last produced, or when the heartbeat interval
    /// expired (panels still get a periodic frame so they never look wedged and
    /// protocol keep-alives keep flowing). Returns null when skipped.
    /// </summary>
    public sealed class SharedFrameConsumer
    {
        private long _lastVersion = -1;
        private long _lastProducedMs;

        /// <summary>Interval after which an unchanged frame is produced anyway.</summary>
        public int HeartbeatMs { get; set; } = 1000;

        public T? Produce<T>(Profile profile, int maxAgeMs, Func<SKBitmap, T> produce) where T : class
        {
            T? result = null;
            SharedFrameCache.WithFrame(profile, maxAgeMs, (bitmap, version) =>
            {
                if (version == _lastVersion && Environment.TickCount64 - _lastProducedMs < HeartbeatMs)
                {
                    return;
                }

                result = produce(bitmap);
                _lastVersion = version;
                _lastProducedMs = Environment.TickCount64;
            });
            return result;
        }

        /// <summary>Forces the next Produce call to run (e.g. after brightness/rotation changes).</summary>
        public void Invalidate() => _lastVersion = -1;
    }
}
