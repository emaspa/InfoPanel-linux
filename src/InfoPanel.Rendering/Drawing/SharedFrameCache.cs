using InfoPanel.Drawing;
using InfoPanel.Models;
using SkiaSharp;
using System;
using System.Collections.Concurrent;
using System.Threading;

namespace InfoPanel
{
    /// <summary>
    /// One rendered frame per profile and render resolution, shared by every panel
    /// consumer (issue #9).
    ///
    /// Instead of each device task rendering the profile independently every frame,
    /// consumers call <see cref="WithFrame"/>: the profile is rendered at most once
    /// per <c>maxAgeMs</c> into a reused Rgba8888 bitmap (no per-frame allocation),
    /// and a version counter bumps only when the rendered pixels actually changed.
    /// Consumers that see an unchanged version can skip their resize/encode/send
    /// work entirely, which is the common case: profile content typically changes
    /// at sensor rate (~1 Hz), far below panel frame rates.
    ///
    /// Consumers whose output resolution is an exact aspect match of the profile
    /// can pass renderWidth/renderHeight to get a dedicated entry rendered at that
    /// size via a canvas scale. For a panel smaller than the profile this replaces
    /// "render large, then downscale every frame" with rendering the small frame
    /// directly, which also shrinks the per-frame video resample inside the draw.
    ///
    /// The consumer callback runs under a read lock (multiple consumers in
    /// parallel); rendering takes the write lock. The bitmap passed to the callback
    /// must not be disposed or used outside the callback.
    /// </summary>
    public static class SharedFrameCache
    {
        private sealed class Entry
        {
            public readonly ReaderWriterLockSlim Lock = new(LockRecursionPolicy.NoRecursion);
            public SKBitmap? Bitmap;          // current frame, Rgba8888/Opaque, reused
            public byte[]? PreviousPixels;    // last frame's pixels for change detection
            public long Version;              // bumps when pixels actually changed
            public long RenderedAtMs = long.MinValue;
        }

        private static readonly ConcurrentDictionary<(Guid Guid, int W, int H), Entry> _entries = new();

        /// <summary>
        /// Runs <paramref name="consume"/> with the current shared frame and its
        /// content version, rendering first if the cached frame is older than
        /// <paramref name="maxAgeMs"/>. Pass <paramref name="renderWidth"/> and
        /// <paramref name="renderHeight"/> (same aspect ratio as the profile) to
        /// consume a frame rendered directly at that resolution; 0 renders at the
        /// profile's native size.
        /// </summary>
        public static void WithFrame(Profile profile, int maxAgeMs, Action<SKBitmap, long> consume,
            int renderWidth = 0, int renderHeight = 0)
        {
            var w = renderWidth > 0 ? renderWidth : profile.Width;
            var h = renderHeight > 0 ? renderHeight : profile.Height;
            var entry = _entries.GetOrAdd((profile.Guid, w, h), static _ => new Entry());

            // Fast path: fresh frame, plain read lock (concurrent consumers).
            entry.Lock.EnterReadLock();
            try
            {
                if (IsFresh(entry, w, h, maxAgeMs))
                {
                    consume(entry.Bitmap!, entry.Version);
                    return;
                }
            }
            finally
            {
                entry.Lock.ExitReadLock();
            }

            // Stale: upgradeable read holds our place so no other writer can
            // interleave between our render and our consume (freshness is stamped
            // at render START, so a render slower than maxAgeMs is already stale
            // on completion; re-checking would render forever without delivering,
            // and releasing between render and consume lets another consumer's
            // render steal the lock and stall this one).
            entry.Lock.EnterUpgradeableReadLock();
            try
            {
                if (!IsFresh(entry, w, h, maxAgeMs))
                {
                    entry.Lock.EnterWriteLock();
                    try
                    {
                        Render(entry, profile, w, h);
                    }
                    finally
                    {
                        entry.Lock.ExitWriteLock();
                    }
                }
                consume(entry.Bitmap!, entry.Version);
            }
            finally
            {
                entry.Lock.ExitUpgradeableReadLock();
            }
        }

        /// <summary>
        /// Profiles rendered through this cache within the given window: a direct
        /// signal of which profiles are actively consumed (panels, web viewer),
        /// used by demand-driven sensor polling.
        /// </summary>
        public static List<Guid> RecentlyRendered(TimeSpan window)
        {
            var cutoff = Environment.TickCount64 - (long)window.TotalMilliseconds;
            var result = new List<Guid>();
            foreach (var kvp in _entries)
            {
                if (kvp.Value.RenderedAtMs >= cutoff && !result.Contains(kvp.Key.Guid))
                {
                    result.Add(kvp.Key.Guid);
                }
            }
            return result;
        }

        private static bool IsFresh(Entry entry, int w, int h, int maxAgeMs)
        {
            return entry.Bitmap != null
                && entry.Bitmap.Width == w
                && entry.Bitmap.Height == h
                && Environment.TickCount64 - entry.RenderedAtMs < maxAgeMs;
        }

        private static void Render(Entry entry, Profile profile, int w, int h)
        {
            if (entry.Bitmap == null
                || entry.Bitmap.Width != w
                || entry.Bitmap.Height != h)
            {
                entry.Bitmap?.Dispose();
                entry.Bitmap = new SKBitmap(w, h, SKColorType.Rgba8888, SKAlphaType.Opaque);
                entry.PreviousPixels = null;
            }

            // Stamp freshness at render START, not completion: consumers gate their
            // next render on this timestamp, and stamping at completion made the
            // frame interval = maxAge + render time instead of max(maxAge, render
            // time), silently halving achievable panel frame rates.
            entry.RenderedAtMs = Environment.TickCount64;

            using (var g = SkiaGraphics.FromBitmap(entry.Bitmap, profile.FontScale))
            {
                if (w != profile.Width || h != profile.Height)
                {
                    g.Canvas.Scale((float)w / profile.Width, (float)h / profile.Height);
                }
                PanelDraw.Run(profile, g, preview: false, cacheHint: $"DISPLAY-{profile.Guid}-{w}x{h}");
            }

            var pixels = entry.Bitmap.GetPixelSpan();
            if (entry.PreviousPixels == null || entry.PreviousPixels.Length != pixels.Length)
            {
                entry.PreviousPixels = new byte[pixels.Length];
                pixels.CopyTo(entry.PreviousPixels);
                entry.Version++;
            }
            else if (!pixels.SequenceEqual(entry.PreviousPixels))
            {
                pixels.CopyTo(entry.PreviousPixels);
                entry.Version++;
            }
        }
    }
}
