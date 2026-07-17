using SkiaSharp;

namespace InfoPanel.AudioSpectrum
{
    internal enum SpectrumStyle
    {
        Bars,
        Rounded,
        Wave,
        Dots,
        Mirror,
        VuMeter,
        Lines,
        VFD,
        VFDOrange,
        VFDClassic,
        VFDOrangeRed,
        AnalogVU
    }

    internal enum ColorScheme
    {
        Neon,
        Fire,
        FireInverted,
        Ice,
        Rainbow,
        Ocean,
        Monochrome,
        Classic,
        Custom
    }

    internal enum VfdColorMode { Cyan, Orange, Classic, OrangeRed }

    internal enum SpectrumAlignment
    {
        Left,
        Center,
        Right
    }

    internal class SpectrumRenderer : IDisposable
    {
        private bool _disposed;

        public SpectrumStyle Style { get; set; } = SpectrumStyle.Bars;
        public ColorScheme Scheme { get; set; } = ColorScheme.Neon;
        public SKColor CustomColor1 { get; set; } = new(0, 255, 128);
        public SKColor CustomColor2 { get; set; } = new(0, 128, 255);
        public SKColor BackgroundColor { get; set; } = SKColors.Transparent;
        public float BarSpacing { get; set; } = 0.3f; // 0-1, fraction of bar width used as gap
        public float CornerRadius { get; set; } = 4f;
        public bool ShowPeaks { get; set; } = true;
        public bool ShowReflection { get; set; } = false;
        public bool ShowMirror { get; set; } = false;
        public float Brightness { get; set; } = 1.0f;
        public SpectrumAlignment Alignment { get; set; } = SpectrumAlignment.Left;
        public float ContentWidth { get; set; } = 1.0f; // 0-1, fraction of image width used by spectrum
        public bool CenterOut { get; set; } = false;
        public float EdgeBoost { get; set; } = 5f; // multiplier at edges for CenterOut mode
        public float NoiseFloor { get; set; } = 0f; // 0-1, fraction of average used as minimum band level
        public int TrimBands { get; set; } = 0; // number of bands to cut from each side

        /// <summary>
        /// Renders the spectrum directly onto an externally-provided bitmap (e.g. shared memory).
        /// </summary>
        public void RenderToBitmap(float[] bands, float[] peaks, SKBitmap target)
        {
            if (target == null || bands.Length == 0) return;
            RenderCore(target, bands, peaks, target.Width, target.Height);
        }

        private void RenderCore(SKBitmap bitmap, float[] bands, float[] peaks, int width, int height)
        {
            if (width <= 0 || height <= 0 || bands.Length == 0) return;

            using var canvas = new SKCanvas(bitmap);
            canvas.Clear(BackgroundColor);

            // Calculate content area and alignment offset
            float contentW = width * Math.Clamp(ContentWidth, 0.1f, 1.0f);
            float offsetX = Alignment switch
            {
                SpectrumAlignment.Center => (width - contentW) / 2f,
                SpectrumAlignment.Right => width - contentW,
                _ => 0f
            };

            if (offsetX != 0)
            {
                canvas.Save();
                canvas.Translate(offsetX, 0);
            }

            // Reorder bands for center-out layout (low freq in center, high on edges)
            if (CenterOut && bands.Length > 1)
            {
                bands = ReorderCenterOut(bands);
                peaks = ReorderCenterOut(peaks);

                // Boost edge bars to compensate for high-freq energy falloff
                // Exponential curve so outer bands get much stronger boost
                int center = bands.Length / 2;
                for (int i = 0; i < bands.Length; i++)
                {
                    float distRatio = MathF.Abs(i - center) / (float)center;
                    float edgeBoost = MathF.Pow(EdgeBoost, distRatio * distRatio);
                    bands[i] = MathF.Min(100, bands[i] * edgeBoost);
                    peaks[i] = MathF.Min(100, peaks[i] * edgeBoost);
                }

            }

            // Noise floor: ensure all bands show some activity when there's audio
            if (NoiseFloor > 0)
            {
                float avg = 0;
                for (int i = 0; i < bands.Length; i++) avg += bands[i];
                avg /= bands.Length;

                if (avg > 2f)
                {
                    float floor = avg * NoiseFloor;
                    for (int i = 0; i < bands.Length; i++)
                    {
                        if (bands[i] < floor) bands[i] = floor;
                        if (peaks[i] < floor) peaks[i] = floor;
                    }
                }
            }

            // Trim bands from each side
            if (TrimBands > 0 && bands.Length > TrimBands * 2 + 2)
            {
                bands = bands[TrimBands..^TrimBands];
                peaks = peaks[TrimBands..^TrimBands];
            }

            float drawHeight = (ShowReflection || ShowMirror) ? height * 0.5f : height;

            switch (Style)
            {
                case SpectrumStyle.Bars:
                    DrawBars(canvas, bands, peaks, contentW, drawHeight, false);
                    break;
                case SpectrumStyle.Rounded:
                    DrawBars(canvas, bands, peaks, contentW, drawHeight, true);
                    break;
                case SpectrumStyle.Wave:
                    DrawWave(canvas, bands, contentW, drawHeight);
                    break;
                case SpectrumStyle.Dots:
                    DrawDots(canvas, bands, peaks, contentW, drawHeight);
                    break;
                case SpectrumStyle.Mirror:
                    DrawMirror(canvas, bands, peaks, contentW, height);
                    break;
                case SpectrumStyle.VuMeter:
                    DrawVuMeter(canvas, bands, peaks, contentW, height);
                    break;
                case SpectrumStyle.Lines:
                    DrawLines(canvas, bands, peaks, contentW, drawHeight);
                    break;
                case SpectrumStyle.VFD:
                    DrawVfd(canvas, bands, peaks, contentW, drawHeight, VfdColorMode.Cyan);
                    break;
                case SpectrumStyle.VFDOrange:
                    DrawVfd(canvas, bands, peaks, contentW, drawHeight, VfdColorMode.Orange);
                    break;
                case SpectrumStyle.VFDClassic:
                    DrawVfd(canvas, bands, peaks, contentW, drawHeight, VfdColorMode.Classic);
                    break;
                case SpectrumStyle.VFDOrangeRed:
                    DrawVfd(canvas, bands, peaks, contentW, drawHeight, VfdColorMode.OrangeRed);
                    break;
                case SpectrumStyle.AnalogVU:
                    DrawAnalogVU(canvas, bands, peaks, contentW, height);
                    break;
            }

            if (ShowMirror && Style != SpectrumStyle.Mirror)
            {
                DrawMirrorReflection(canvas, bitmap, contentW, drawHeight, height);
            }
            else if (ShowReflection && Style != SpectrumStyle.Mirror)
            {
                DrawReflection(canvas, bitmap, contentW, drawHeight, height);
            }

            if (offsetX != 0)
            {
                canvas.Restore();
            }
        }

        private void DrawBars(SKCanvas canvas, float[] bands, float[] peaks, float width, float height, bool rounded)
        {
            int count = bands.Length;
            float totalBarWidth = width / count;
            float gap = totalBarWidth * BarSpacing;
            float barWidth = totalBarWidth - gap;

            for (int i = 0; i < count; i++)
            {
                float x = i * totalBarWidth + gap / 2;
                float barHeight = (bands[i] / 100f) * height;
                float y = height - barHeight;

                var color = GetBandColor(i, count, bands[i] / 100f);

                using var paint = new SKPaint
                {
                    IsAntialias = true,
                    Color = ApplyBrightness(color)
                };

                // Gradient fill - vertical gradient within each bar for Classic and Fire
                SKShader shader;
                if (Scheme == ColorScheme.Classic)
                {
                    shader = SKShader.CreateLinearGradient(
                        new SKPoint(x, height),
                        new SKPoint(x, y),
                        [
                            ApplyBrightness(new SKColor(0, 200, 0)),
                            ApplyBrightness(new SKColor(0, 255, 0)),
                            ApplyBrightness(new SKColor(255, 255, 0)),
                            ApplyBrightness(new SKColor(255, 0, 0))
                        ],
                        [0f, 0.3f, 0.7f, 1f],
                        SKShaderTileMode.Clamp);
                }
                else if (Scheme == ColorScheme.Fire)
                {
                    // Flame gradient: dark red at base -> bright orange -> yellow at tip
                    shader = SKShader.CreateLinearGradient(
                        new SKPoint(x, height),
                        new SKPoint(x, y),
                        [
                            ApplyBrightness(new SKColor(120, 0, 0)),
                            ApplyBrightness(new SKColor(255, 50, 0)),
                            ApplyBrightness(new SKColor(255, 160, 0)),
                            ApplyBrightness(new SKColor(255, 240, 60)),
                            ApplyBrightness(new SKColor(255, 255, 180))
                        ],
                        [0f, 0.25f, 0.5f, 0.8f, 1f],
                        SKShaderTileMode.Clamp);
                }
                else if (Scheme == ColorScheme.FireInverted)
                {
                    // Inverted flame: yellow/white at base -> orange -> red at tip
                    shader = SKShader.CreateLinearGradient(
                        new SKPoint(x, height),
                        new SKPoint(x, y),
                        [
                            ApplyBrightness(new SKColor(255, 255, 180)),
                            ApplyBrightness(new SKColor(255, 240, 60)),
                            ApplyBrightness(new SKColor(255, 160, 0)),
                            ApplyBrightness(new SKColor(255, 50, 0)),
                            ApplyBrightness(new SKColor(120, 0, 0))
                        ],
                        [0f, 0.2f, 0.5f, 0.75f, 1f],
                        SKShaderTileMode.Clamp);
                }
                else
                {
                    shader = SKShader.CreateLinearGradient(
                        new SKPoint(x, height),
                        new SKPoint(x, y),
                        [ApplyBrightness(GetBandColor(i, count, 0.2f)), ApplyBrightness(color)],
                        [0f, 1f],
                        SKShaderTileMode.Clamp);
                }
                paint.Shader = shader;

                var rect = new SKRect(x, y, x + barWidth, height);

                if (rounded)
                {
                    canvas.DrawRoundRect(rect, CornerRadius, CornerRadius, paint);
                }
                else
                {
                    canvas.DrawRect(rect, paint);
                }

                shader.Dispose();

                // Peak indicator
                if (ShowPeaks && peaks[i] > 2)
                {
                    float peakY = MathF.Max(5, height - (peaks[i] / 100f) * height);
                    var peakColor = Scheme == ColorScheme.Classic
                        ? ApplyBrightness(new SKColor(255, 0, 0))
                        : ApplyBrightness(color).WithAlpha(200);
                    using var peakPaint = new SKPaint
                    {
                        IsAntialias = true,
                        Color = peakColor,
                        StrokeWidth = 2,
                        Style = SKPaintStyle.Fill
                    };
                    canvas.DrawRect(x, peakY - 2, barWidth, 3, peakPaint);
                }
            }
        }

        private void DrawWave(SKCanvas canvas, float[] bands, float width, float height)
        {
            int count = bands.Length;

            using var path = new SKPath();
            using var fillPath = new SKPath();

            float step = width / (count - 1);

            // Build smooth curve through band points
            fillPath.MoveTo(0, height);

            for (int i = 0; i < count; i++)
            {
                float x = i * step;
                float y = height - (bands[i] / 100f) * height;

                if (i == 0)
                {
                    path.MoveTo(x, y);
                    fillPath.LineTo(x, y);
                }
                else
                {
                    float prevX = (i - 1) * step;
                    float prevY = height - (bands[i - 1] / 100f) * height;
                    float cpX = (prevX + x) / 2;

                    path.CubicTo(cpX, prevY, cpX, y, x, y);
                    fillPath.CubicTo(cpX, prevY, cpX, y, x, y);
                }
            }

            fillPath.LineTo(width, height);
            fillPath.Close();

            // Gradient fill
            using var fillPaint = new SKPaint
            {
                IsAntialias = true,
                Style = SKPaintStyle.Fill
            };

            var colors = GetGradientColors(5);
            using var shader = SKShader.CreateLinearGradient(
                new SKPoint(0, height),
                new SKPoint(0, 0),
                [colors[0].WithAlpha(40), colors[2].WithAlpha(150), colors[4].WithAlpha(200)],
                [0f, 0.5f, 1f],
                SKShaderTileMode.Clamp);
            fillPaint.Shader = shader;
            canvas.DrawPath(fillPath, fillPaint);

            // Stroke
            using var strokePaint = new SKPaint
            {
                IsAntialias = true,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 2.5f
            };

            using var strokeShader = SKShader.CreateLinearGradient(
                new SKPoint(0, 0),
                new SKPoint(width, 0),
                colors,
                null,
                SKShaderTileMode.Clamp);
            strokePaint.Shader = strokeShader;
            canvas.DrawPath(path, strokePaint);

            // Glow effect
            using var glowPaint = new SKPaint
            {
                IsAntialias = true,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 6f,
                MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 4)
            };
            using var glowShader = SKShader.CreateLinearGradient(
                new SKPoint(0, 0),
                new SKPoint(width, 0),
                colors.Select(c => c.WithAlpha(80)).ToArray(),
                null,
                SKShaderTileMode.Clamp);
            glowPaint.Shader = glowShader;
            canvas.DrawPath(path, glowPaint);
        }

        private void DrawDots(SKCanvas canvas, float[] bands, float[] peaks, float width, float height)
        {
            int count = bands.Length;
            float totalBarWidth = width / count;
            int dotsPerColumn = 16;
            float dotSpacing = height / dotsPerColumn;
            float dotRadius = MathF.Min(totalBarWidth * 0.3f, dotSpacing * 0.35f);

            for (int i = 0; i < count; i++)
            {
                float x = i * totalBarWidth + totalBarWidth / 2;
                int activeDots = Math.Min((int)(bands[i] / 100f * dotsPerColumn), dotsPerColumn - 2);
                int peakDot = Math.Min((int)(peaks[i] / 100f * dotsPerColumn), dotsPerColumn - 2);

                for (int d = 0; d < dotsPerColumn; d++)
                {
                    float y = height - (d + 0.5f) * dotSpacing;
                    bool active = d < activeDots;
                    bool isPeak = ShowPeaks && d == peakDot && peakDot > 0;

                    var color = GetBandColor(i, count, (float)d / dotsPerColumn);

                    using var paint = new SKPaint
                    {
                        IsAntialias = true,
                        Style = SKPaintStyle.Fill
                    };

                    if (active)
                    {
                        paint.Color = ApplyBrightness(color);
                        canvas.DrawCircle(x, y, dotRadius, paint);

                        // Glow on active dots
                        using var glowPaint = new SKPaint
                        {
                            IsAntialias = true,
                            Color = ApplyBrightness(color).WithAlpha(60),
                            MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 3)
                        };
                        canvas.DrawCircle(x, y, dotRadius * 1.5f, glowPaint);
                    }
                    else if (isPeak)
                    {
                        paint.Color = ApplyBrightness(color).WithAlpha(200);
                        canvas.DrawCircle(x, y, dotRadius, paint);
                    }
                    else
                    {
                        paint.Color = new SKColor(60, 60, 60, 40);
                        canvas.DrawCircle(x, y, dotRadius * 0.6f, paint);
                    }
                }
            }
        }

        private void DrawMirror(SKCanvas canvas, float[] bands, float[] peaks, float width, float height)
        {
            int count = bands.Length;
            float totalBarWidth = width / count;
            float gap = totalBarWidth * BarSpacing;
            float barWidth = totalBarWidth - gap;
            float midY = height / 2;

            for (int i = 0; i < count; i++)
            {
                float x = i * totalBarWidth + gap / 2;
                float halfBarHeight = (bands[i] / 100f) * midY;

                var color = GetBandColor(i, count, bands[i] / 100f);

                // Top half (going up from center)
                using var topPaint = new SKPaint { IsAntialias = true };
                using var topShader = SKShader.CreateLinearGradient(
                    new SKPoint(x, midY),
                    new SKPoint(x, midY - halfBarHeight),
                    [ApplyBrightness(color).WithAlpha(80), ApplyBrightness(color)],
                    [0f, 1f],
                    SKShaderTileMode.Clamp);
                topPaint.Shader = topShader;
                canvas.DrawRoundRect(new SKRect(x, midY - halfBarHeight, x + barWidth, midY), CornerRadius, CornerRadius, topPaint);

                // Bottom half (going down from center)
                using var botPaint = new SKPaint { IsAntialias = true };
                using var botShader = SKShader.CreateLinearGradient(
                    new SKPoint(x, midY),
                    new SKPoint(x, midY + halfBarHeight),
                    [ApplyBrightness(color).WithAlpha(80), ApplyBrightness(color)],
                    [0f, 1f],
                    SKShaderTileMode.Clamp);
                botPaint.Shader = botShader;
                canvas.DrawRoundRect(new SKRect(x, midY, x + barWidth, midY + halfBarHeight), CornerRadius, CornerRadius, botPaint);

                // Peak indicators
                if (ShowPeaks && peaks[i] > 2)
                {
                    float peakOffset = MathF.Min(peaks[i] / 100f, 1f) * (midY - 5);
                    using var peakPaint = new SKPaint
                    {
                        IsAntialias = true,
                        Color = ApplyBrightness(color).WithAlpha(200),
                        StrokeWidth = 2
                    };
                    canvas.DrawRect(x, midY - peakOffset - 2, barWidth, 3, peakPaint);
                    canvas.DrawRect(x, midY + peakOffset - 1, barWidth, 3, peakPaint);
                }
            }
        }

        private void DrawVuMeter(SKCanvas canvas, float[] bands, float[] peaks, float width, float height)
        {
            int count = bands.Length;
            float totalBarWidth = width / count;
            float gap = totalBarWidth * BarSpacing;
            float barWidth = totalBarWidth - gap;
            int segmentsPerBar = 24;
            // Segment height is ~40% of the pitch so the gap between segments is clearly visible
            float pitch = height / segmentsPerBar;
            float segHeight = pitch * 0.55f;
            float segGap = pitch - segHeight;

            for (int i = 0; i < count; i++)
            {
                float x = i * totalBarWidth + gap / 2;
                int activeSegments = Math.Min((int)(bands[i] / 100f * segmentsPerBar), segmentsPerBar - 2);
                int peakSegment = Math.Min((int)(peaks[i] / 100f * segmentsPerBar), segmentsPerBar - 2);

                for (int s = 0; s < segmentsPerBar; s++)
                {
                    float y = height - (s + 1) * pitch + segGap / 2;
                    float level = (float)s / segmentsPerBar;

                    SKColor segColor;
                    if (Scheme == ColorScheme.Fire)
                    {
                        // Flame gradient: dark red at base -> orange -> yellow at tip
                        if (level < 0.3f)
                            segColor = InterpolateColor(new SKColor(120, 0, 0), new SKColor(255, 50, 0), level / 0.3f);
                        else if (level < 0.6f)
                            segColor = InterpolateColor(new SKColor(255, 50, 0), new SKColor(255, 160, 0), (level - 0.3f) / 0.3f);
                        else if (level < 0.85f)
                            segColor = InterpolateColor(new SKColor(255, 160, 0), new SKColor(255, 240, 60), (level - 0.6f) / 0.25f);
                        else
                            segColor = InterpolateColor(new SKColor(255, 240, 60), new SKColor(255, 255, 180), (level - 0.85f) / 0.15f);
                    }
                    else if (Scheme == ColorScheme.FireInverted)
                    {
                        // Inverted: yellow/white at base -> orange -> red at tip
                        float inv = 1f - level;
                        if (inv < 0.3f)
                            segColor = InterpolateColor(new SKColor(120, 0, 0), new SKColor(255, 50, 0), inv / 0.3f);
                        else if (inv < 0.6f)
                            segColor = InterpolateColor(new SKColor(255, 50, 0), new SKColor(255, 160, 0), (inv - 0.3f) / 0.3f);
                        else if (inv < 0.85f)
                            segColor = InterpolateColor(new SKColor(255, 160, 0), new SKColor(255, 240, 60), (inv - 0.6f) / 0.25f);
                        else
                            segColor = InterpolateColor(new SKColor(255, 240, 60), new SKColor(255, 255, 180), (inv - 0.85f) / 0.15f);
                    }
                    else
                    {
                        // Classic LED VU: green -> yellow -> red
                        if (level < 0.6f)
                            segColor = new SKColor(0, 210, 0);
                        else if (level < 0.8f)
                            segColor = new SKColor(240, 200, 0);
                        else
                            segColor = new SKColor(255, 20, 0);
                    }

                    bool active = s < activeSegments;
                    bool isPeak = ShowPeaks && s == peakSegment && peakSegment > 0;

                    if (active)
                    {
                        var lit = ApplyBrightness(segColor);
                        using var paint = new SKPaint { IsAntialias = true, Color = lit };
                        canvas.DrawRoundRect(new SKRect(x, y, x + barWidth, y + segHeight), 1, 1, paint);

                        // LED glow
                        using var glowPaint = new SKPaint
                        {
                            IsAntialias = true,
                            Color = lit.WithAlpha(50),
                            MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 2.5f)
                        };
                        canvas.DrawRoundRect(new SKRect(x - 1, y - 1, x + barWidth + 1, y + segHeight + 1), 1, 1, glowPaint);
                    }
                    else if (isPeak)
                    {
                        var lit = ApplyBrightness(segColor);
                        using var paint = new SKPaint { IsAntialias = true, Color = lit };
                        canvas.DrawRoundRect(new SKRect(x, y, x + barWidth, y + segHeight), 1, 1, paint);
                    }
                    else
                    {
                        // Dim unlit LED
                        using var paint = new SKPaint { IsAntialias = true, Color = new SKColor(30, 30, 30, 60) };
                        canvas.DrawRoundRect(new SKRect(x, y, x + barWidth, y + segHeight), 1, 1, paint);
                    }
                }
            }
        }

        private void DrawLines(SKCanvas canvas, float[] bands, float[] peaks, float width, float height)
        {
            int count = bands.Length;
            float totalBarWidth = width / count;
            float gap = totalBarWidth * BarSpacing;
            float barWidth = totalBarWidth - gap;

            int linesPerBar = Math.Max(16, (int)(height / 4.5f));
            float pitch = height / linesPerBar;
            float lineThickness = MathF.Max(1.2f, pitch * 0.4f);

            for (int i = 0; i < count; i++)
            {
                float x = i * totalBarWidth + gap / 2;
                int activeLines = Math.Min((int)(bands[i] / 100f * linesPerBar), linesPerBar - 2);
                int peakLine = Math.Min((int)(peaks[i] / 100f * linesPerBar), linesPerBar - 2);
                var bandColor = GetBandColor(i, count, bands[i] / 100f);

                for (int s = 0; s < linesPerBar; s++)
                {
                    float y = height - (s + 0.5f) * pitch;
                    bool active = s < activeLines;
                    bool isPeak = ShowPeaks && s == peakLine && peakLine > 0;

                    if (active)
                    {
                        float intensity = (float)s / linesPerBar;
                        byte alpha = (byte)(180 + intensity * 75);

                        // Use color scheme gradient within the bar
                        var lineColor = LerpColor(
                            GetBandColor(i, count, 0.2f),
                            bandColor,
                            intensity);
                        var color = ApplyBrightness(lineColor).WithAlpha(alpha);

                        using var paint = new SKPaint
                        {
                            IsAntialias = true,
                            Color = color,
                            StrokeWidth = lineThickness,
                            StrokeCap = SKStrokeCap.Butt
                        };
                        canvas.DrawLine(x, y, x + barWidth, y, paint);

                        using var bloomPaint = new SKPaint
                        {
                            IsAntialias = true,
                            Color = color.WithAlpha((byte)(20 + intensity * 25)),
                            StrokeWidth = lineThickness + 3,
                            StrokeCap = SKStrokeCap.Butt,
                            MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 2)
                        };
                        canvas.DrawLine(x - 1, y, x + barWidth + 1, y, bloomPaint);
                    }
                    else if (isPeak)
                    {
                        var color = ApplyBrightness(bandColor);
                        using var paint = new SKPaint
                        {
                            IsAntialias = true,
                            Color = color,
                            StrokeWidth = lineThickness,
                            StrokeCap = SKStrokeCap.Butt
                        };
                        canvas.DrawLine(x, y, x + barWidth, y, paint);
                    }
                    else
                    {
                        // Dim ghost line
                        using var paint = new SKPaint
                        {
                            IsAntialias = true,
                            Color = ApplyBrightness(bandColor).WithAlpha(12),
                            StrokeWidth = lineThickness * 0.6f,
                            StrokeCap = SKStrokeCap.Butt
                        };
                        canvas.DrawLine(x, y, x + barWidth, y, paint);
                    }
                }
            }
        }

        private static SKColor LerpColor(SKColor a, SKColor b, float t)
        {
            return new SKColor(
                (byte)(a.Red + (b.Red - a.Red) * t),
                (byte)(a.Green + (b.Green - a.Green) * t),
                (byte)(a.Blue + (b.Blue - a.Blue) * t),
                (byte)(a.Alpha + (b.Alpha - a.Alpha) * t));
        }

        private void DrawVfd(SKCanvas canvas, float[] bands, float[] peaks, float width, float height, VfdColorMode colorMode = VfdColorMode.Cyan)
        {
            // VFD phosphor colors per mode
            SKColor vfdBright, vfdDim, vfdGlow, vfdRed;
            switch (colorMode)
            {
                case VfdColorMode.Orange:
                    vfdBright = new SKColor(255, 160, 20);
                    vfdDim = new SKColor(255, 160, 20, 12);
                    vfdGlow = new SKColor(200, 120, 10, 8);
                    vfdRed = default; // unused
                    break;
                case VfdColorMode.Classic:
                    vfdBright = new SKColor(0, 230, 190);
                    vfdDim = new SKColor(0, 230, 190, 12);
                    vfdGlow = new SKColor(0, 180, 150, 8);
                    vfdRed = new SKColor(255, 40, 30);
                    break;
                case VfdColorMode.OrangeRed:
                    vfdBright = new SKColor(255, 160, 20);
                    vfdDim = new SKColor(255, 160, 20, 12);
                    vfdGlow = new SKColor(200, 120, 10, 8);
                    vfdRed = new SKColor(255, 40, 30);
                    break;
                default: // Cyan
                    vfdBright = new SKColor(0, 230, 190);
                    vfdDim = new SKColor(0, 230, 190, 12);
                    vfdGlow = new SKColor(0, 180, 150, 8);
                    vfdRed = default; // unused
                    break;
            }

            int count = bands.Length;
            float totalBarWidth = width / count;
            float gap = totalBarWidth * BarSpacing;
            float barWidth = totalBarWidth - gap;

            // Many thin horizontal lines like a real VFD - line thickness ~1.5px with ~60% gap
            int linesPerBar = Math.Max(16, (int)(height / 4.5f));
            float pitch = height / linesPerBar;
            float lineThickness = MathF.Max(1.2f, pitch * 0.4f);

            // Overall phosphor glow across the whole display (subtle background bloom)
            using var bgGlow = new SKPaint
            {
                IsAntialias = true,
                Color = vfdGlow,
                MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 6)
            };

            // Threshold for red zone in Classic mode (top 1/3)
            float redThreshold = (colorMode == VfdColorMode.Classic || colorMode == VfdColorMode.OrangeRed) ? 1f / 3f : float.MaxValue;

            for (int i = 0; i < count; i++)
            {
                float x = i * totalBarWidth + gap / 2;
                int activeLines = Math.Min((int)(bands[i] / 100f * linesPerBar), linesPerBar - 2);
                int peakLine = Math.Min((int)(peaks[i] / 100f * linesPerBar), linesPerBar - 2);

                for (int s = 0; s < linesPerBar; s++)
                {
                    float y = height - (s + 0.5f) * pitch;
                    bool active = s < activeLines;
                    bool isPeak = ShowPeaks && s == peakLine && peakLine > 0;

                    if (active)
                    {
                        // Brighter toward the top of the active area
                        float intensity = (float)s / linesPerBar;
                        byte alpha = (byte)(180 + intensity * 75);
                        bool inRedZone = intensity >= redThreshold;
                        var baseColor = inRedZone ? vfdRed : vfdBright;
                        var color = ApplyBrightness(baseColor).WithAlpha(alpha);

                        // Thin horizontal phosphor line
                        using var paint = new SKPaint
                        {
                            IsAntialias = true,
                            Color = color,
                            StrokeWidth = lineThickness,
                            StrokeCap = SKStrokeCap.Butt
                        };
                        canvas.DrawLine(x, y, x + barWidth, y, paint);

                        // Phosphor bloom (soft glow around each lit line)
                        using var bloomPaint = new SKPaint
                        {
                            IsAntialias = true,
                            Color = color.WithAlpha((byte)(25 + intensity * 30)),
                            StrokeWidth = lineThickness + 3,
                            StrokeCap = SKStrokeCap.Butt,
                            MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 2)
                        };
                        canvas.DrawLine(x - 1, y, x + barWidth + 1, y, bloomPaint);
                    }
                    else if (isPeak)
                    {
                        // Peak: bright single line
                        float peakIntensity = (float)s / linesPerBar;
                        bool peakInRedZone = peakIntensity >= redThreshold;
                        var color = ApplyBrightness(peakInRedZone ? vfdRed : vfdBright);
                        using var paint = new SKPaint
                        {
                            IsAntialias = true,
                            Color = color,
                            StrokeWidth = lineThickness,
                            StrokeCap = SKStrokeCap.Butt
                        };
                        canvas.DrawLine(x, y, x + barWidth, y, paint);

                        using var bloomPaint = new SKPaint
                        {
                            IsAntialias = true,
                            Color = color.WithAlpha(35),
                            StrokeWidth = lineThickness + 4,
                            StrokeCap = SKStrokeCap.Butt,
                            MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 3)
                        };
                        canvas.DrawLine(x - 1, y, x + barWidth + 1, y, bloomPaint);
                    }
                    else
                    {
                        // Ghost grid line (faint unlit phosphor, characteristic VFD look)
                        using var paint = new SKPaint
                        {
                            IsAntialias = true,
                            Color = vfdDim,
                            StrokeWidth = lineThickness * 0.6f,
                            StrokeCap = SKStrokeCap.Butt
                        };
                        canvas.DrawLine(x, y, x + barWidth, y, paint);
                    }
                }

                // Column-level glow for active bars (ambient phosphor scatter)
                if (activeLines > 2)
                {
                    float barHeight = activeLines * pitch;
                    canvas.DrawRect(x - 1, height - barHeight, barWidth + 2, barHeight, bgGlow);
                }
            }
        }

        private void DrawAnalogVU(SKCanvas canvas, float[] bands, float[] peaks, float width, float height)
        {
            // Compute peak and average from band data
            float peak = 0, sum = 0, peakHold = 0, avgPeakHold = 0;
            for (int i = 0; i < bands.Length; i++)
            {
                if (bands[i] > peak) peak = bands[i];
                sum += bands[i];
            }
            float avg = sum / bands.Length;
            for (int i = 0; i < peaks.Length; i++)
            {
                if (peaks[i] > peakHold) peakHold = peaks[i];
                avgPeakHold += peaks[i];
            }
            avgPeakHold /= peaks.Length;

            // Two meters side by side
            float meterW = width / 2f;
            DrawSingleAnalogMeter(canvas, 0, 0, meterW, height, peak / 100f, peakHold / 100f, "PEAK");
            DrawSingleAnalogMeter(canvas, meterW, 0, meterW, height, avg / 100f, avgPeakHold / 100f, "AVG");
        }

        private void DrawSingleAnalogMeter(SKCanvas canvas, float ox, float oy, float w, float h,
            float level, float peakLevel, string label)
        {
            level = Math.Clamp(level, 0f, 1f);
            peakLevel = Math.Clamp(peakLevel, 0f, 1f);
            float s = MathF.Min(w, h); // scale unit

            // === BEZEL ===
            float bz = Math.Max(3, s * 0.04f);
            var outerRect = new SKRect(ox + 1, oy + 1, ox + w - 1, oy + h - 1);
            using var framePaint = new SKPaint { IsAntialias = true, Color = new SKColor(30, 28, 24) };
            canvas.DrawRoundRect(outerRect, 5, 5, framePaint);

            var midRect = new SKRect(ox + bz * 0.5f, oy + bz * 0.5f, ox + w - bz * 0.5f, oy + h - bz * 0.5f);
            using var midPaint = new SKPaint { IsAntialias = true, Color = new SKColor(60, 55, 48) };
            canvas.DrawRoundRect(midRect, 4, 4, midPaint);

            // === FACE ===
            var face = new SKRect(ox + bz, oy + bz, ox + w - bz, oy + h - bz);
            float fW = face.Width, fH = face.Height;

            using var faceShader = SKShader.CreateRadialGradient(
                new SKPoint(face.MidX, face.Top + fH * 0.35f), fW * 0.8f,
                [new SKColor(242, 235, 208), new SKColor(220, 208, 175)],
                [0f, 1f], SKShaderTileMode.Clamp);
            using var facePaint = new SKPaint { IsAntialias = true, Shader = faceShader };
            canvas.DrawRoundRect(face, 2, 2, facePaint);

            // Vignette
            using var vig = new SKPaint
            {
                IsAntialias = true, Style = SKPaintStyle.Stroke,
                StrokeWidth = Math.Max(4, s * 0.03f),
                Color = new SKColor(0, 0, 0, 20)
            };
            canvas.DrawRoundRect(face, 2, 2, vig);

            // === ARC GEOMETRY ===
            // Pivot at bottom center, large radius so arc fills the face
            float cx = face.MidX;
            float cy = face.Bottom + fH * 0.05f; // pivot slightly below face bottom
            float R = fH * 0.85f; // large radius
            if (R > fW * 0.52f) R = fW * 0.52f; // don't exceed width

            float aDeg0 = -145f; // start (left)
            float aDeg1 = -35f;  // end (right)
            float sweep = aDeg1 - aDeg0;

            // dB positions along the arc (0..1)
            float[] dbVal = [-20, -10, -7, -5, -3, -2, -1, 0, 1, 2, 3];
            float[] dbPos = [0f, 0.23f, 0.33f, 0.42f, 0.51f, 0.57f, 0.635f, 0.71f, 0.79f, 0.89f, 1f];

            // === dB ARC LINE ===
            float zeroA = aDeg0 + 0.71f * sweep;
            var arcBox = new SKRect(cx - R, cy - R, cx + R, cy + R);
            using var blackLine = new SKPaint
            {
                IsAntialias = true, Style = SKPaintStyle.Stroke,
                Color = new SKColor(40, 35, 30), StrokeWidth = Math.Max(1, s * 0.006f)
            };
            using var redLine = new SKPaint
            {
                IsAntialias = true, Style = SKPaintStyle.Stroke,
                Color = new SKColor(190, 25, 15), StrokeWidth = Math.Max(1.2f, s * 0.007f)
            };
            canvas.DrawArc(arcBox, aDeg0, zeroA - aDeg0, false, blackLine);
            canvas.DrawArc(arcBox, zeroA, aDeg1 - zeroA, false, redLine);

            // === TICKS & dB LABELS ===
            float tOut = R;
            float tIn = R * 0.90f;
            float tMid = R * 0.94f;
            float lR = R * 0.80f; // label distance from pivot
            float fs = Math.Max(6f, s * 0.055f);

            using var fnt = new SKFont(SKTypeface.FromFamilyName("Arial", SKFontStyle.Normal), fs);
            using var lblP = new SKPaint { IsAntialias = true };
            using var tkP = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke };

            for (int i = 0; i < dbVal.Length; i++)
            {
                float a = aDeg0 + dbPos[i] * sweep;
                float rad = a * MathF.PI / 180f;
                float co = MathF.Cos(rad), si = MathF.Sin(rad);
                bool red = dbVal[i] >= 0;

                tkP.Color = red ? new SKColor(190, 25, 15) : new SKColor(40, 35, 30);
                tkP.StrokeWidth = Math.Max(1, s * 0.006f);

                canvas.DrawLine(cx + co * tIn, cy + si * tIn, cx + co * tOut, cy + si * tOut, tkP);

                string lbl = dbVal[i] switch { < 0 => dbVal[i].ToString("F0"), 0 => "0", _ => "+" + dbVal[i].ToString("F0") };
                lblP.Color = red ? new SKColor(190, 25, 15) : new SKColor(40, 35, 30);
                canvas.DrawText(lbl, cx + co * lR, cy + si * lR + fs * 0.35f, SKTextAlign.Center, fnt, lblP);

                // Minor tick
                if (i < dbVal.Length - 1)
                {
                    float ma = aDeg0 + ((dbPos[i] + dbPos[i + 1]) / 2f) * sweep;
                    float mr = ma * MathF.PI / 180f;
                    tkP.StrokeWidth = Math.Max(0.5f, s * 0.003f);
                    tkP.Color = (dbVal[i] >= 0 || dbVal[i + 1] >= 0) ? new SKColor(190, 25, 15) : new SKColor(40, 35, 30);
                    canvas.DrawLine(cx + MathF.Cos(mr) * tMid, cy + MathF.Sin(mr) * tMid,
                        cx + MathF.Cos(mr) * tOut, cy + MathF.Sin(mr) * tOut, tkP);
                }
            }

            // === PERCENTAGE SCALE (outer arc, below dB) ===
            float pR = R * 1.08f;  // outer radius for pct ticks
            float pIn = R * 1.02f; // inner radius for pct ticks
            float pLbl = R * 1.16f;
            float pFs = Math.Max(5f, fs * 0.6f);
            using var pFnt = new SKFont(SKTypeface.FromFamilyName("Arial", SKFontStyle.Normal), pFs);
            using var pP = new SKPaint { IsAntialias = true, Color = new SKColor(50, 45, 40, 180) };
            using var pTk = new SKPaint
            {
                IsAntialias = true, Style = SKPaintStyle.Stroke,
                Color = new SKColor(50, 45, 40, 140), StrokeWidth = Math.Max(0.5f, s * 0.003f)
            };

            int[] pcts = [0, 20, 40, 60, 80, 100];
            float[] pPos = [0f, 0.23f, 0.42f, 0.57f, 0.71f, 1f];
            for (int i = 0; i < pcts.Length; i++)
            {
                float a = aDeg0 + pPos[i] * sweep;
                float rad = a * MathF.PI / 180f;
                float co = MathF.Cos(rad), si = MathF.Sin(rad);
                canvas.DrawLine(cx + co * pIn, cy + si * pIn, cx + co * pR, cy + si * pR, pTk);
                canvas.DrawText(pcts[i].ToString(), cx + co * pLbl, cy + si * pLbl + pFs * 0.35f,
                    SKTextAlign.Center, pFnt, pP);
            }

            // === "VU" LABEL ===
            float vuFs = Math.Max(8f, s * 0.08f);
            using var vuFnt = new SKFont(
                SKTypeface.FromFamilyName("Arial", SKFontStyleWeight.Bold, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright), vuFs);
            using var vuP = new SKPaint { IsAntialias = true, Color = new SKColor(40, 35, 30) };
            // Place between top of face and top of arc
            float arcTopY = cy - R;
            float vuY = face.Top + (arcTopY - face.Top) * 0.65f;
            canvas.DrawText("VU", cx, vuY, SKTextAlign.Center, vuFnt, vuP);

            // Small marker dot at top of arc
            float topRad = -90f * MathF.PI / 180f;
            using var dotP = new SKPaint { IsAntialias = true, Color = new SKColor(40, 35, 30) };
            canvas.DrawCircle(cx + MathF.Cos(topRad) * R, cy + MathF.Sin(topRad) * R,
                Math.Max(1.5f, s * 0.007f), dotP);

            // Sub-label
            float subFs = Math.Max(5f, s * 0.04f);
            using var subFnt = new SKFont(SKTypeface.FromFamilyName("Arial", SKFontStyle.Normal), subFs);
            using var subP = new SKPaint { IsAntialias = true, Color = new SKColor(110, 100, 85) };
            canvas.DrawText(label, cx, vuY + vuFs * 0.7f, SKTextAlign.Center, subFnt, subP);

            // === PEAK HOLD NEEDLE (thin, red, behind main needle) ===
            if (ShowPeaks && peakLevel > 0.01f)
            {
                float pkA = aDeg0 + peakLevel * sweep;
                float pkR = pkA * MathF.PI / 180f;
                float pkLen = R * 1.04f;
                using var pkP = new SKPaint
                {
                    IsAntialias = true, Style = SKPaintStyle.Stroke,
                    Color = new SKColor(200, 30, 15, 140),
                    StrokeWidth = Math.Max(0.8f, s * 0.005f),
                    StrokeCap = SKStrokeCap.Round
                };
                canvas.DrawLine(cx, cy, cx + MathF.Cos(pkR) * pkLen, cy + MathF.Sin(pkR) * pkLen, pkP);
            }

            // === MAIN NEEDLE ===
            float nA = aDeg0 + level * sweep;
            float nR = nA * MathF.PI / 180f;
            float nLen = R * 1.06f;
            float nW = Math.Max(1.2f, s * 0.008f);

            // Shadow
            using var shP = new SKPaint
            {
                IsAntialias = true, Style = SKPaintStyle.Stroke,
                Color = new SKColor(0, 0, 0, 25), StrokeWidth = nW + 2
            };
            canvas.DrawLine(cx + 1, cy + 1,
                cx + 1 + MathF.Cos(nR) * nLen, cy + 1 + MathF.Sin(nR) * nLen, shP);

            // Needle
            using var nP = new SKPaint
            {
                IsAntialias = true, Style = SKPaintStyle.Stroke,
                Color = new SKColor(15, 12, 8), StrokeWidth = nW, StrokeCap = SKStrokeCap.Round
            };
            canvas.DrawLine(cx, cy, cx + MathF.Cos(nR) * nLen, cy + MathF.Sin(nR) * nLen, nP);

            // Pivot
            float pvR = Math.Max(2.5f, s * 0.02f);
            using var pvO = new SKPaint { IsAntialias = true, Color = new SKColor(45, 40, 35) };
            using var pvI = new SKPaint { IsAntialias = true, Color = new SKColor(25, 22, 18) };
            canvas.DrawCircle(cx, cy, pvR, pvO);
            canvas.DrawCircle(cx, cy, pvR * 0.55f, pvI);

            // === CLIP LED (top-right corner, red when level >= 0 dB i.e. 71%) ===
            float ledR = Math.Max(3, s * 0.025f);
            float ledX = face.Right - ledR * 2.5f;
            float ledY = face.Top + ledR * 2.5f;
            bool clipping = level >= 0.71f;

            // LED housing (dark circle)
            using var ledHousing = new SKPaint { IsAntialias = true, Color = new SKColor(40, 35, 30) };
            canvas.DrawCircle(ledX, ledY, ledR * 1.3f, ledHousing);

            if (clipping)
            {
                // Red LED on with glow
                using var ledGlow = new SKPaint
                {
                    IsAntialias = true,
                    Color = new SKColor(255, 20, 10, 60),
                    MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, ledR * 1.5f)
                };
                canvas.DrawCircle(ledX, ledY, ledR * 2f, ledGlow);

                using var ledOn = new SKPaint { IsAntialias = true, Color = new SKColor(255, 25, 10) };
                canvas.DrawCircle(ledX, ledY, ledR, ledOn);

                // Bright highlight
                using var ledHi = new SKPaint { IsAntialias = true, Color = new SKColor(255, 150, 130, 180) };
                canvas.DrawCircle(ledX - ledR * 0.25f, ledY - ledR * 0.25f, ledR * 0.35f, ledHi);
            }
            else
            {
                // LED off (dark red)
                using var ledOff = new SKPaint { IsAntialias = true, Color = new SKColor(80, 15, 10) };
                canvas.DrawCircle(ledX, ledY, ledR, ledOff);
            }
        }

        private void DrawReflection(SKCanvas canvas, SKBitmap bitmap, float width, float mainHeight, float totalHeight)
        {
            float reflectionHeight = totalHeight - mainHeight;

            // Create a faded copy of the main area, flipped
            using var snapshot = new SKBitmap((int)width, (int)mainHeight);
            using var snapshotCanvas = new SKCanvas(snapshot);

            // Read pixels from the main area
            var info = new SKImageInfo((int)width, (int)mainHeight);
            using var mainPixels = new SKBitmap(info);
            bitmap.ExtractSubset(mainPixels, new SKRectI(0, 0, (int)width, (int)mainHeight));

            // Draw flipped
            snapshotCanvas.Save();
            snapshotCanvas.Scale(1, -1, 0, mainHeight / 2);
            snapshotCanvas.DrawBitmap(mainPixels, 0, 0);
            snapshotCanvas.Restore();

            // Draw reflection with fade
            using var paint = new SKPaint
            {
                IsAntialias = true
            };
            using var shader = SKShader.CreateLinearGradient(
                new SKPoint(0, mainHeight),
                new SKPoint(0, totalHeight),
                [SKColors.White.WithAlpha(60), SKColors.White.WithAlpha(0)],
                [0f, 1f],
                SKShaderTileMode.Clamp);
            paint.Shader = shader;
            paint.BlendMode = SKBlendMode.DstIn;

            // Draw the flipped image
            canvas.DrawBitmap(snapshot, 0, mainHeight);

            // Apply fade mask
            using var fadeBitmap = new SKBitmap((int)width, (int)reflectionHeight);
            using var fadeCanvas = new SKCanvas(fadeBitmap);
            fadeCanvas.DrawBitmap(snapshot, new SKRect(0, 0, width, mainHeight),
                new SKRect(0, 0, width, reflectionHeight));

            // Simple alpha fade on the reflection area
            using var fadePaint = new SKPaint
            {
                Color = SKColors.Black.WithAlpha(180)
            };
            canvas.DrawRect(0, mainHeight, width, reflectionHeight, fadePaint);
        }

        private void DrawMirrorReflection(SKCanvas canvas, SKBitmap bitmap, float width, float mainHeight, float totalHeight)
        {
            float mirrorHeight = totalHeight - mainHeight;

            // Extract the main area pixels
            var info = new SKImageInfo((int)width, (int)mainHeight);
            using var mainPixels = new SKBitmap(info);
            bitmap.ExtractSubset(mainPixels, new SKRectI(0, 0, (int)width, (int)mainHeight));

            // Draw flipped 1:1 copy below
            canvas.Save();
            canvas.Translate(0, totalHeight);
            canvas.Scale(1, -1);
            canvas.DrawBitmap(mainPixels, new SKRect(0, 0, width, mainHeight),
                new SKRect(0, 0, width, mirrorHeight));
            canvas.Restore();
        }

        private SKColor GetBandColor(int bandIndex, int bandCount, float intensity)
        {
            float position = (float)bandIndex / bandCount;

            return Scheme switch
            {
                ColorScheme.Neon => InterpolateColors(position, intensity,
                    new SKColor(0, 255, 128), new SKColor(0, 200, 255), new SKColor(180, 0, 255)),
                ColorScheme.Fire => InterpolateColors(position, intensity,
                    new SKColor(255, 60, 0), new SKColor(255, 180, 0), new SKColor(255, 255, 100)),
                ColorScheme.FireInverted => InterpolateColors(position, intensity,
                    new SKColor(255, 255, 100), new SKColor(255, 180, 0), new SKColor(255, 60, 0)),
                ColorScheme.Ice => InterpolateColors(position, intensity,
                    new SKColor(100, 200, 255), new SKColor(150, 230, 255), new SKColor(220, 240, 255)),
                ColorScheme.Rainbow => HSLtoRGB(position * 360f, 1f, 0.3f + intensity * 0.3f),
                ColorScheme.Ocean => InterpolateColors(position, intensity,
                    new SKColor(0, 50, 120), new SKColor(0, 150, 200), new SKColor(0, 220, 180)),
                ColorScheme.Monochrome => InterpolateColors(position, intensity,
                    new SKColor(80, 80, 80), new SKColor(180, 180, 180), new SKColor(255, 255, 255)),
                ColorScheme.Classic => GetClassicColor(intensity),
                ColorScheme.Custom => InterpolateColor(CustomColor1, CustomColor2, position),
                _ => new SKColor(0, 255, 128)
            };
        }

        private static SKColor GetClassicColor(float intensity)
        {
            // Classic EQ: green at low levels, yellow at mid, red at peak
            if (intensity < 0.6f)
            {
                // Green to yellow
                float t = intensity / 0.6f;
                return new SKColor(
                    (byte)(0 + 255 * t),
                    (byte)(200 + 55 * t),
                    0);
            }
            else
            {
                // Yellow to red
                float t = (intensity - 0.6f) / 0.4f;
                return new SKColor(
                    255,
                    (byte)(255 * (1f - t)),
                    0);
            }
        }

        private SKColor[] GetGradientColors(int count)
        {
            var colors = new SKColor[count];
            for (int i = 0; i < count; i++)
            {
                colors[i] = ApplyBrightness(GetBandColor(i, count, 0.8f));
            }
            return colors;
        }

        private static SKColor InterpolateColors(float position, float intensity, SKColor low, SKColor mid, SKColor high)
        {
            // Blend based on intensity (low=quiet, high=loud)
            SKColor baseColor;
            if (intensity < 0.5f)
            {
                baseColor = InterpolateColor(low, mid, intensity * 2);
            }
            else
            {
                baseColor = InterpolateColor(mid, high, (intensity - 0.5f) * 2);
            }

            // Shift hue slightly based on position
            return InterpolateColor(baseColor, ShiftHue(baseColor, position * 60), position);
        }

        private static SKColor InterpolateColor(SKColor a, SKColor b, float t)
        {
            t = MathF.Max(0, MathF.Min(1, t));
            return new SKColor(
                (byte)(a.Red + (b.Red - a.Red) * t),
                (byte)(a.Green + (b.Green - a.Green) * t),
                (byte)(a.Blue + (b.Blue - a.Blue) * t),
                (byte)(a.Alpha + (b.Alpha - a.Alpha) * t)
            );
        }

        private static SKColor ShiftHue(SKColor color, float degrees)
        {
            color.ToHsl(out float h, out float s, out float l);
            h = (h + degrees) % 360;
            if (h < 0) h += 360;
            return SKColor.FromHsl(h, s, l);
        }

        private static SKColor HSLtoRGB(float h, float s, float l)
        {
            return SKColor.FromHsl(h % 360, s * 100, l * 100);
        }

        private SKColor ApplyBrightness(SKColor color)
        {
            return new SKColor(
                (byte)MathF.Min(255, color.Red * Brightness),
                (byte)MathF.Min(255, color.Green * Brightness),
                (byte)MathF.Min(255, color.Blue * Brightness),
                color.Alpha
            );
        }

        private static float[] ReorderCenterOut(float[] input)
        {
            int n = input.Length;
            var result = new float[n];
            int mid = (n - 1) / 2;

            // Place band 0 (lowest freq) at center, fan outward symmetrically
            for (int i = 0; i < n; i++)
            {
                int target;
                if (i % 2 == 0)
                    target = mid - i / 2;
                else
                    target = mid + 1 + i / 2;

                if (target >= 0 && target < n)
                    result[target] = input[i];
            }

            return result;
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
            }
        }
    }
}
