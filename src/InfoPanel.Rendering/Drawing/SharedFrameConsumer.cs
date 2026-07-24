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
        private object? _lastResult;

        /// <summary>Interval after which an unchanged frame is produced anyway.</summary>
        public int HeartbeatMs { get; set; } = 1000;

        /// <summary>
        /// When set, an unchanged frame returns the previously produced payload
        /// instead of null, so the caller keeps sending at full cadence. Required
        /// for panels whose firmware treats a slow stream as stopped and falls
        /// back to the boot logo (Trofeo bulk panels do this below ~1 fps); the
        /// expensive render/resize/encode is still skipped. Only use with
        /// immutable payloads (byte[]) that the caller does not pool or dispose.
        /// </summary>
        public bool ResendCachedOnSkip { get; set; }

        public T? Produce<T>(Profile profile, int maxAgeMs, Func<SKBitmap, T> produce) where T : class
        {
            T? result = null;
            SharedFrameCache.WithFrame(profile, maxAgeMs, (bitmap, version) =>
            {
                if (version == _lastVersion && Environment.TickCount64 - _lastProducedMs < HeartbeatMs)
                {
                    if (ResendCachedOnSkip)
                    {
                        result = _lastResult as T;
                    }
                    return;
                }

                result = produce(bitmap);
                _lastResult = ResendCachedOnSkip ? result : null;
                _lastVersion = version;
                _lastProducedMs = Environment.TickCount64;
            });
            return result;
        }

        /// <summary>Forces the next Produce call to run (e.g. after brightness/rotation changes).</summary>
        public void Invalidate() => _lastVersion = -1;
    }
}
