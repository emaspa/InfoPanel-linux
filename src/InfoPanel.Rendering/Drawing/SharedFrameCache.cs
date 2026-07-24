using InfoPanel.Drawing;
using InfoPanel.Models;
using SkiaSharp;
using System;
using System.Collections.Concurrent;
using System.Threading;

namespace InfoPanel
{
    /// <summary>
    /// One rendered frame per profile, shared by every panel consumer (issue #9).
    ///
    /// Instead of each device task rendering the profile independently every frame,
    /// consumers call <see cref="WithFrame"/>: the profile is rendered at most once
    /// per <c>maxAgeMs</c> into a reused Rgba8888 bitmap (no per-frame allocation),
    /// and a version counter bumps only when the rendered pixels actually changed.
    /// Consumers that see an unchanged version can skip their resize/encode/send
    /// work entirely, which is the common case: profile content typically changes
    /// at sensor rate (~1 Hz), far below panel frame rates.
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

        private static readonly ConcurrentDictionary<Guid, Entry> _entries = new();

        /// <summary>
        /// Runs <paramref name="consume"/> with the current shared frame and its
        /// content version, rendering first if the cached frame is older than
        /// <paramref name="maxAgeMs"/>.
        /// </summary>
        public static void WithFrame(Profile profile, int maxAgeMs, Action<SKBitmap, long> consume)
        {
            var entry = _entries.GetOrAdd(profile.Guid, static _ => new Entry());

            while (true)
            {
                entry.Lock.EnterReadLock();
                try
                {
                    if (IsFresh(entry, profile, maxAgeMs))
                    {
                        consume(entry.Bitmap!, entry.Version);
                        return;
                    }
                }
                finally
                {
                    entry.Lock.ExitReadLock();
                }

                entry.Lock.EnterWriteLock();
                try
                {
                    if (!IsFresh(entry, profile, maxAgeMs))
                    {
                        Render(entry, profile);
                    }
                }
                finally
                {
                    entry.Lock.ExitWriteLock();
                }
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
                if (kvp.Value.RenderedAtMs >= cutoff)
                {
                    result.Add(kvp.Key);
                }
            }
            return result;
        }

        private static bool IsFresh(Entry entry, Profile profile, int maxAgeMs)
        {
            return entry.Bitmap != null
                && entry.Bitmap.Width == profile.Width
                && entry.Bitmap.Height == profile.Height
                && Environment.TickCount64 - entry.RenderedAtMs < maxAgeMs;
        }

        private static void Render(Entry entry, Profile profile)
        {
            if (entry.Bitmap == null
                || entry.Bitmap.Width != profile.Width
                || entry.Bitmap.Height != profile.Height)
            {
                entry.Bitmap?.Dispose();
                entry.Bitmap = new SKBitmap(profile.Width, profile.Height, SKColorType.Rgba8888, SKAlphaType.Opaque);
                entry.PreviousPixels = null;
            }

            using (var g = SkiaGraphics.FromBitmap(entry.Bitmap, profile.FontScale))
            {
                PanelDraw.Run(profile, g, preview: false, cacheHint: $"DISPLAY-{profile.Guid}");
            }

            entry.RenderedAtMs = Environment.TickCount64;

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
