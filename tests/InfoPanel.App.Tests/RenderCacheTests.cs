using System;
using System.Collections.Generic;
using InfoPanel.Drawing;
using InfoPanel.Models;
using SkiaSharp;
using Xunit;

namespace InfoPanel.App.Tests
{
    public class LruResourceCacheTests
    {
        private sealed class Res(string name, List<string> disposed) : IDisposable
        {
            public string Name { get; } = name;
            public void Dispose() => disposed.Add(Name);
        }

        [Fact]
        public void EvictsLeastRecentlyUsedPastEntryLimit()
        {
            var disposed = new List<string>();
            var cache = new LruResourceCache<string, Res>(maxEntries: 2, maxBytes: 1000);
            cache.Add("a", new Res("a", disposed), 1);
            cache.Add("b", new Res("b", disposed), 1);

            Assert.True(cache.TryGet("a", out _)); // a is now most recent
            cache.Add("c", new Res("c", disposed), 1);

            Assert.Equal(["b"], disposed);
            Assert.True(cache.TryGet("a", out _));
            Assert.False(cache.TryGet("b", out _));
            Assert.True(cache.TryGet("c", out _));
            Assert.Equal(2, cache.Count);
        }

        [Fact]
        public void EvictsUntilWithinByteBudget()
        {
            var disposed = new List<string>();
            var cache = new LruResourceCache<string, Res>(maxEntries: 10, maxBytes: 100);
            cache.Add("a", new Res("a", disposed), 40);
            cache.Add("b", new Res("b", disposed), 40);
            cache.Add("c", new Res("c", disposed), 70);

            Assert.Equal(["a", "b"], disposed);
            Assert.Equal(1, cache.Count);
            Assert.Equal(70, cache.Bytes);
        }

        [Fact]
        public void RejectsValueLargerThanBudgetWithoutDisposingIt()
        {
            var disposed = new List<string>();
            var cache = new LruResourceCache<string, Res>(maxEntries: 10, maxBytes: 100);
            cache.Add("a", new Res("a", disposed), 10);

            Assert.False(cache.Add("big", new Res("big", disposed), 101));
            Assert.Empty(disposed);
            Assert.True(cache.TryGet("a", out _));
        }

        [Fact]
        public void ReplacingKeyDisposesOldValueAndClearDisposesAll()
        {
            var disposed = new List<string>();
            var cache = new LruResourceCache<string, Res>(maxEntries: 10, maxBytes: 100);
            cache.Add("k", new Res("old", disposed), 30);
            cache.Add("k", new Res("new", disposed), 20);

            Assert.Equal(["old"], disposed);
            Assert.Equal(20, cache.Bytes);
            Assert.True(cache.TryGet("k", out var v));
            Assert.Equal("new", v.Name);

            cache.Clear();
            Assert.Equal(["old", "new"], disposed);
            Assert.Equal(0, cache.Count);
            Assert.Equal(0, cache.Bytes);
        }
    }

    public class FrameImageTests
    {
        private static SKBitmap Filled(int w, int h, SKColor color)
        {
            var bitmap = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul));
            bitmap.Erase(color);
            return bitmap;
        }

        [Theory]
        [InlineData(1, 1)]
        [InlineData(37, 3)]   // row length not a multiple of the vector width
        [InlineData(256, 64)]
        public void OpaqueDetection(int w, int h)
        {
            using var bitmap = Filled(w, h, new SKColor(10, 20, 30, 255));
            using (var pixmap = bitmap.PeekPixels())
                Assert.True(LockedImage.IsFullyOpaque(pixmap));

            // Last pixel (the scalar tail) and a middle pixel each break opacity
            foreach (var (x, y) in new[] { (w - 1, h - 1), (w / 2, h / 2) })
            {
                using var copy = bitmap.Copy();
                copy.SetPixel(x, y, new SKColor(10, 20, 30, 254));
                using var pixmap = copy.PeekPixels();
                Assert.False(LockedImage.IsFullyOpaque(pixmap));
            }
        }

        [Fact]
        public void FrameImageIsRgbaAndTaggedOpaqueOnlyWithoutAlpha()
        {
            using var opaqueSource = Filled(64, 32, new SKColor(200, 100, 50, 255));
            using var opaque = LockedImage.CreateFrameImage(opaqueSource, 30, 15);
            Assert.NotNull(opaque);
            Assert.Equal(SKColorType.Rgba8888, opaque!.ColorType);
            Assert.Equal(SKAlphaType.Opaque, opaque.AlphaType);
            Assert.Equal(30, opaque.Width);

            using var translucentSource = Filled(64, 32, new SKColor(200, 100, 50, 255));
            translucentSource.SetPixel(0, 0, SKColors.Transparent);
            using var translucent = LockedImage.CreateFrameImage(translucentSource, 30, 15);
            Assert.Equal(SKColorType.Rgba8888, translucent!.ColorType);
            Assert.Equal(SKAlphaType.Premul, translucent.AlphaType);
        }
    }

    [Collection("AppState")] // toggles the process-wide glow cache
    public class GlowCacheTests
    {
        [Theory]
        [InlineData(10.0f, 10, 0f)]
        [InlineData(10.99999f, 11, 0f)]
        [InlineData(10.5f, 10, 0.5f)]
        [InlineData(-6.6666667f, -7, 21f / 64)]
        [InlineData(4.3333333f, 4, 21f / 64)]
        public void DeviceOriginSplitsIntoWholeAndQuantisedFraction(float value, int whole, float fraction)
        {
            var (w, f) = SkiaGraphics.QuantiseDeviceOrigin(value);
            Assert.Equal(whole, w);
            Assert.Equal(fraction, f, 5);
        }

        private static byte[] Render(float scale, bool cacheEnabled, Action<SkiaGraphics> draw)
        {
            var previous = SkiaGraphics.GlowCacheEnabled;
            SkiaGraphics.GlowCacheEnabled = cacheEnabled;
            try
            {
                using var bitmap = new SKBitmap(new SKImageInfo(300, 120, SKColorType.Rgba8888, SKAlphaType.Opaque));
                using (var g = SkiaGraphics.FromBitmap(bitmap, 1f))
                {
                    g.Clear(new SKColor(20, 10, 5));
                    g.Canvas.Scale(scale);
                    draw(g);
                }
                return bitmap.GetPixelSpan().ToArray();
            }
            finally
            {
                SkiaGraphics.GlowCacheEnabled = previous;
            }
        }

        private static void Glow(SkiaGraphics g, string text, int x, int y, int rotation = 0) =>
            g.DrawString(text, "DejaVu Sans", "Regular", 28, "#FFFFA040", x, y,
                glowRadius: 6, glowColor: "#C0FF6000", glowBlendMode: "Plus", rotation: rotation);

        [Theory]
        [InlineData(1f)]
        [InlineData(2f / 3)]
        public void CachedGlowMatchesLayerPath(float scale)
        {
            // A miss replays the layer path's geometry, and a hit at the same
            // spot draws that render back, so both match exactly.
            for (int x = 31; x < 35; x++)
            {
                for (int y = 17; y < 20; y++)
                {
                    var text = $"glow match {scale} {x} {y}";
                    var uncached = Render(scale, false, g => Glow(g, text, x, y));
                    var miss = Render(scale, true, g => Glow(g, text, x, y));
                    var hit = Render(scale, true, g => Glow(g, text, x, y));

                    Assert.Equal(0, MaxChannelDifference(uncached, miss));
                    Assert.Equal(0, MaxChannelDifference(uncached, hit));
                }
            }
        }

        private static int MaxChannelDifference(byte[] a, byte[] b)
        {
            Assert.Equal(a.Length, b.Length);
            var max = 0;
            for (int i = 0; i < a.Length; i++)
                max = Math.Max(max, Math.Abs(a[i] - b[i]));
            return max;
        }

        [Fact]
        public void ReusesRenderAcrossWholePixelMovesButNotRotations()
        {
            var text = "glow reuse " + Guid.NewGuid().ToString("N")[..6];
            var before = SkiaGraphics.GlowCacheCount;

            Render(1f, true, g => Glow(g, text, 10, 10));
            Assert.Equal(before + 1, SkiaGraphics.GlowCacheCount);

            Render(1f, true, g => { Glow(g, text, 10, 10); Glow(g, text, 57, 33); });
            Assert.Equal(before + 1, SkiaGraphics.GlowCacheCount);

            // Rotated text takes the layer path and caches nothing
            Render(1f, true, g => Glow(g, text + "r", 10, 10, rotation: 30));
            Assert.Equal(before + 1, SkiaGraphics.GlowCacheCount);

            // Different value: a new entry
            Render(1f, true, g => Glow(g, text + "x", 10, 10));
            Assert.Equal(before + 2, SkiaGraphics.GlowCacheCount);
        }
    }
}
