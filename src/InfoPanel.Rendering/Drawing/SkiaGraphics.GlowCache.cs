using SkiaSharp;
using System;

namespace InfoPanel.Drawing
{
    public partial class SkiaGraphics
    {
        /// <summary>
        /// Reuse rendered glow text (blurred glow plus sharp text) across frames.
        /// The blur dominated raster render time for glowing profiles while the
        /// text itself changes at sensor rate. Set false to blur on every draw.
        /// </summary>
        public static bool GlowCacheEnabled { get; set; } = true;

        internal const int GlowCacheMaxEntries = 256;
        internal const long GlowCacheMaxBytes = 64L * 1024 * 1024;

        // A single render larger than this (huge font, mostly off-canvas text) is
        // drawn uncached: the layer path only allocates its clipped part.
        private const long GlowCacheMaxEntryBytes = 8L * 1024 * 1024;

        // Device-space origins are bucketed to this step so text moving by
        // fractional device pixels (marquee on a scaled canvas) reuses a bounded
        // set of renders; a reused render is off by at most half a step. Skia
        // rounds glyph baselines to whole pixels, so a coarse step (1/4 px) can
        // put reused text one pixel row off from where it would render.
        internal const float GlowOriginStep = 1f / 64;

        /// <summary>Guarded by <see cref="_textLock"/>.</summary>
        private static readonly LruResourceCache<GlowCacheKey, GlowRender> _glowCache = new(GlowCacheMaxEntries, GlowCacheMaxBytes);

        private readonly record struct GlowCacheKey(
            TextBlockKey Text, string GlowColor, int GlowRadius,
            float ScaleX, float ScaleY, float OriginFractionX, float OriginFractionY);

        /// <summary>Rendered glow text plus its pixel offset from the quantised device origin.</summary>
        private sealed class GlowRender(SKImage image, int left, int top) : IDisposable
        {
            public SKImage Image { get; } = image;
            public int Left { get; } = left;
            public int Top { get; } = top;
            public long Bytes => (long)Image.Width * Image.Height * 4;
            public void Dispose() => Image.Dispose();
        }

        internal static int GlowCacheCount
        {
            get { lock (_textLock) return _glowCache.Count; }
        }

        internal static void ClearGlowCache()
        {
            lock (_textLock) _glowCache.Clear();
        }

        /// <summary>Splits a device coordinate into whole pixels and a fraction rounded to <see cref="GlowOriginStep"/>.</summary>
        internal static (int Whole, float Fraction) QuantiseDeviceOrigin(float value)
        {
            var whole = MathF.Floor(value);
            var fraction = MathF.Round((value - whole) / GlowOriginStep) * GlowOriginStep;
            if (fraction >= 1f)
            {
                whole += 1f;
                fraction = 0f;
            }
            return ((int)whole, fraction);
        }

        /// <summary>
        /// GPU canvases blur cheaply on the GPU, and caching would read their
        /// results back for nothing.
        /// </summary>
        private bool UseGlowCache => GlowCacheEnabled && !OpenGL;

        /// <summary>
        /// Draws glow text from the cache, rendering it on a miss. Returns false
        /// when the current transform rotates, skews or mirrors (the cached pixels
        /// only translate) or the render would be too large; the caller then takes
        /// the layer path.
        /// </summary>
        private bool TryDrawCachedGlow(string text, string fontName, string fontStyle, int fontSize, string color, string glowColor, int glowRadius, SKBlendMode blendMode, int x, int y, bool rightAlign, bool centerAlign, bool bold, bool italic, bool underline, bool strikeout, bool wrap, bool ellipsis, int width)
        {
            var m = Canvas.TotalMatrix;
            if (!IsScaleTranslate(m) || m.ScaleX <= 0 || m.ScaleY <= 0)
            {
                return false;
            }

            var (originX, fractionX) = QuantiseDeviceOrigin(m.ScaleX * x + m.TransX);
            var (originY, fractionY) = QuantiseDeviceOrigin(m.ScaleY * y + m.TransY);

            lock (_textLock)
            {
                var textKey = CreateTextBlockKey(text, fontName, fontStyle, fontSize, color, rightAlign, centerAlign, bold, italic, underline, strikeout, wrap, ellipsis, width);
                var key = new GlowCacheKey(textKey, glowColor, glowRadius, m.ScaleX, m.ScaleY, fractionX, fractionY);

                GlowRender? uncached = null;
                if (!_glowCache.TryGet(key, out var render))
                {
                    var glowKey = CreateTextBlockKey(text, fontName, fontStyle, fontSize, glowColor, rightAlign, centerAlign, bold, italic, underline, strikeout, wrap, ellipsis, width);
                    var rendered = RenderGlow(textKey, glowKey, glowRadius, fontSize, x, y, rightAlign, width, m, originX, originY);
                    if (rendered == null)
                        return false;

                    render = rendered;
                    if (!_glowCache.Add(key, render, render.Bytes))
                        uncached = render;
                }
                using var notCached = uncached; // only when the cache refused it

                // Same composite the outer SaveLayer's restore performs: the
                // combined glow+text layer, pixel aligned, with the glow blend mode.
                using var paint = new SKPaint { BlendMode = blendMode };
                Canvas.Save();
                Canvas.ResetMatrix();
                Canvas.DrawImage(render.Image, originX + render.Left, originY + render.Top, paint);
                Canvas.Restore();
            }

            return true;
        }

        /// <summary>
        /// Renders glow and text exactly as the uncached path does: same matrix,
        /// same local coordinates, into a surface covering the device pixels the
        /// outer layer would have (before clipping). The offset is stored
        /// relative to the quantised origin so later draws at other whole-pixel
        /// positions reuse it. Caller holds <see cref="_textLock"/>.
        /// </summary>
        private GlowRender? RenderGlow(in TextBlockKey textKey, in TextBlockKey glowKey, int glowRadius, int fontSize, int x, int y, bool rightAlign, int width, in SKMatrix matrix, int originX, int originY)
        {
            var textBlock = GetOrCreateTextBlock(textKey);
            var glowBlock = GetOrCreateTextBlock(glowKey);

            // Same geometry as GetTextBounds and DrawStringCoreLocked
            var paintAt = new SKPoint(width == 0 && rightAlign ? x - textBlock.MeasuredWidth : x, y);
            var bounds = new SKRect(paintAt.X, y, paintAt.X + Math.Max(width, textBlock.MeasuredWidth), y + textBlock.MeasuredHeight);
            var pad = glowRadius * 3f + fontSize * FontScale * 0.5f + 2f;
            bounds.Inflate(pad, pad);

            var device = matrix.MapRect(bounds);
            var left = (int)MathF.Floor(device.Left);
            var top = (int)MathF.Floor(device.Top);
            var w = (int)MathF.Ceiling(device.Right) - left;
            var h = (int)MathF.Ceiling(device.Bottom) - top;
            if (w <= 0 || h <= 0 || (long)w * h * 4 > GlowCacheMaxEntryBytes)
                return null;

            using var surface = SKSurface.Create(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul));
            if (surface == null)
                return null;

            var canvas = surface.Canvas;
            canvas.Clear(SKColors.Transparent);
            canvas.SetMatrix(matrix.PostConcat(SKMatrix.CreateTranslation(-left, -top)));

            using (var blurFilter = SKImageFilter.CreateBlur(glowRadius, glowRadius))
            using (var glowPaint = new SKPaint { ImageFilter = blurFilter })
            {
                canvas.SaveLayer(bounds, glowPaint);
                glowBlock.Paint(canvas, paintAt);
                canvas.Restore();
            }
            textBlock.Paint(canvas, paintAt);

            return new GlowRender(surface.Snapshot(), left - originX, top - originY);
        }
    }
}
