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

        /// <summary>True when the last Produce returned the cached payload (or null)
        /// instead of freshly produced content. Lets callers pace differently:
        /// a cached result means the shared frame was not stale yet, so polling
        /// again shortly is cheap.</summary>
        public bool LastWasCached { get; private set; }

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

        public T? Produce<T>(Profile profile, int maxAgeMs, Func<SKBitmap, T> produce,
            int renderWidth = 0, int renderHeight = 0) where T : class
        {
            T? result = null;
            var wasCached = true;
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
                wasCached = false;
                _lastResult = ResendCachedOnSkip ? result : null;
                _lastVersion = version;
                _lastProducedMs = Environment.TickCount64;
            }, renderWidth, renderHeight);
            LastWasCached = wasCached;
            return result;
        }

        /// <summary>
        /// Two-stage produce: <paramref name="extract"/> runs under the shared-frame
        /// read lock and must return an owned snapshot (typically the resize);
        /// <paramref name="transform"/> runs after the lock is released, so expensive
        /// work (e.g. JPEG encode) no longer blocks the profile renderer or other
        /// consumers. Returns null when skipped (same semantics as Produce).
        /// </summary>
        public T? Produce<TMid, T>(Profile profile, int maxAgeMs, Func<SKBitmap, TMid?> extract, Func<TMid, T> transform,
            int renderWidth = 0, int renderHeight = 0)
            where TMid : class
            where T : class
        {
            TMid? mid = null;
            T? cached = null;
            SharedFrameCache.WithFrame(profile, maxAgeMs, (bitmap, version) =>
            {
                if (version == _lastVersion && Environment.TickCount64 - _lastProducedMs < HeartbeatMs)
                {
                    if (ResendCachedOnSkip)
                    {
                        cached = _lastResult as T;
                    }
                    return;
                }

                mid = extract(bitmap);
                _lastVersion = version;
                _lastProducedMs = Environment.TickCount64;
            }, renderWidth, renderHeight);

            if (mid == null)
            {
                LastWasCached = true;
                return cached;
            }

            var result = transform(mid);
            LastWasCached = false;
            _lastResult = ResendCachedOnSkip ? result : null;
            return result;
        }

        /// <summary>Forces the next Produce call to run (e.g. after brightness/rotation changes).</summary>
        public void Invalidate() => _lastVersion = -1;
    }
}
