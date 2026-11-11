using System;
using System.Linq;
using System.Threading.Tasks;
using InfoPanel.Imaging;
using SkiaSharp;
using Xunit;

namespace InfoPanel.App.Tests
{
    public class JpegEncoderTests
    {
        private static void RequireTurbo()
        {
            // Packages depend on libturbojpeg; a dev box without it would silently test only the fallback
            Assert.True(JpegEncoder.IsTurboAvailable,
                "libturbojpeg.so.0 not loadable (install libjpeg-turbo / libturbojpeg0 / turbojpeg)");
        }

        // Gradient, hard-edged shapes and text: smooth areas plus the high-contrast
        // edges panel dashboards have, and a red/blue split that shows channel swaps.
        private static SKBitmap CreateTestFrame(int width, int height, SKColorType colorType,
            SKAlphaType alphaType = SKAlphaType.Opaque)
        {
            var bitmap = new SKBitmap(new SKImageInfo(width, height, colorType, alphaType));
            using var canvas = new SKCanvas(bitmap);
            using (var gradient = new SKPaint())
            {
                gradient.Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(width, height),
                    [SKColors.Navy, SKColors.Teal, SKColors.Orange], SKShaderTileMode.Clamp);
                canvas.DrawRect(0, 0, width, height, gradient);
            }
            using (var paint = new SKPaint { IsAntialias = true })
            {
                paint.Color = SKColors.Red;
                canvas.DrawRect(0, 0, width / 4f, height / 4f, paint);
                paint.Color = SKColors.Blue;
                canvas.DrawRect(width * 3 / 4f, 0, width / 4f, height / 4f, paint);
                paint.Color = SKColors.White;
                canvas.DrawCircle(width / 2f, height / 2f, height / 5f, paint);

                using var font = new SKFont(SKTypeface.Default, height / 8f);
                paint.Color = SKColors.Black;
                canvas.DrawText("CPU 63°C  GPU 71°C", width / 10f, height * 0.85f, SKTextAlign.Left, font, paint);
            }
            return bitmap;
        }

        private static byte[] ToRgba(SKBitmap source)
        {
            var info = new SKImageInfo(source.Width, source.Height, SKColorType.Rgba8888, source.AlphaType);
            using var converted = new SKBitmap(info);
            Assert.True(source.PeekPixels().ReadPixels(info, converted.GetPixels(), converted.RowBytes));
            return converted.GetPixelSpan().ToArray();
        }

        private static SKBitmap Decode(byte[] jpeg)
        {
            Assert.True(jpeg.Length > 4);
            Assert.Equal(0xFF, jpeg[0]);
            Assert.Equal(0xD8, jpeg[1]);
            var decoded = SKBitmap.Decode(jpeg);
            Assert.NotNull(decoded);
            return decoded;
        }

        // PSNR over the colour channels (alpha ignored, as JPEG has none)
        private static double Psnr(byte[] a, byte[] b)
        {
            Assert.Equal(a.Length, b.Length);
            double sum = 0;
            long count = 0;
            for (int i = 0; i < a.Length; i += 4)
            {
                for (int c = 0; c < 3; c++)
                {
                    double d = a[i + c] - b[i + c];
                    sum += d * d;
                    count++;
                }
            }
            double mse = sum / count;
            return mse == 0 ? double.PositiveInfinity : 10 * Math.Log10(255.0 * 255.0 / mse);
        }

        [Fact]
        public void Turbo_OutputDecodesCloseToSource()
        {
            RequireTurbo();
            using var source = CreateTestFrame(1600, 720, SKColorType.Rgba8888);

            var jpeg = JpegEncoder.TryEncodeTurbo(source, 90);

            Assert.NotNull(jpeg);
            using var decoded = Decode(jpeg);
            Assert.Equal(source.Width, decoded.Width);
            Assert.Equal(source.Height, decoded.Height);
            Assert.True(Psnr(ToRgba(source), ToRgba(decoded)) > 35);
        }

        [Fact]
        public void Turbo_MatchesSkiaQuality()
        {
            RequireTurbo();
            using var source = CreateTestFrame(800, 480, SKColorType.Rgba8888);
            var reference = ToRgba(source);

            using var turbo = Decode(JpegEncoder.TryEncodeTurbo(source, 80)!);
            using var skia = Decode(JpegEncoder.EncodeSkia(source, 80)!);

            // Same libjpeg quantisation tables for the same quality number
            Assert.True(Math.Abs(Psnr(reference, ToRgba(turbo)) - Psnr(reference, ToRgba(skia))) < 1.0);
        }

        [Fact]
        public void Turbo_RgbaAndBgraInputGiveSameColours()
        {
            RequireTurbo();
            using var rgba = CreateTestFrame(640, 480, SKColorType.Rgba8888);
            using var bgra = CreateTestFrame(640, 480, SKColorType.Bgra8888);

            using var fromRgba = Decode(JpegEncoder.TryEncodeTurbo(rgba, 90)!);
            using var fromBgra = Decode(JpegEncoder.TryEncodeTurbo(bgra, 90)!);

            Assert.True(Psnr(ToRgba(fromRgba), ToRgba(fromBgra)) > 45);

            // A symmetric swap would pass the comparison above; check absolute colours too
            foreach (var decoded in new[] { fromRgba, fromBgra })
            {
                var red = decoded.GetPixel(40, 40);
                Assert.True(red.Red > 200 && red.Blue < 60, $"red patch decoded as {red}");
                var blue = decoded.GetPixel(600, 40);
                Assert.True(blue.Blue > 200 && blue.Red < 60, $"blue patch decoded as {blue}");
            }
        }

        [Theory]
        [InlineData(481, 263)]
        [InlineData(1, 1)]
        [InlineData(1920, 462)]
        public void Encode_PreservesDimensions(int width, int height)
        {
            using var source = CreateTestFrame(width, height, SKColorType.Rgba8888);

            using var viaEncode = Decode(JpegEncoder.Encode(source, 90));
            using var viaSkia = Decode(JpegEncoder.EncodeSkia(source, 90)!);

            Assert.Equal((width, height), (viaEncode.Width, viaEncode.Height));
            Assert.Equal((width, height), (viaSkia.Width, viaSkia.Height));
        }

        [Fact]
        public void Fallback_SkiaEncoderProducesValidJpeg()
        {
            using var source = CreateTestFrame(1600, 720, SKColorType.Rgba8888);

            var jpeg = JpegEncoder.EncodeSkia(source, 90);

            Assert.NotNull(jpeg);
            using var decoded = Decode(jpeg);
            Assert.True(Psnr(ToRgba(source), ToRgba(decoded)) > 35);
        }

        [Fact]
        public void Encode_UnsupportedColorType_FallsBackToSkia()
        {
            // RGB565 has no TurboJPEG pixel format, so Encode must take the Skia path
            using var source = CreateTestFrame(320, 240, SKColorType.Rgb565);

            Assert.Null(JpegEncoder.TryEncodeTurbo(source, 90));
            using var decoded = Decode(JpegEncoder.Encode(source, 90));
            Assert.Equal((320, 240), (decoded.Width, decoded.Height));
            var red = decoded.GetPixel(20, 20);
            Assert.True(red.Red > 200 && red.Blue < 60, $"red patch decoded as {red}");
        }

        [Fact]
        public void Turbo_PremulTranslucentInputMatchesSkia()
        {
            // Both encoders ignore alpha and encode the stored (premultiplied) bytes
            RequireTurbo();
            using var source = new SKBitmap(new SKImageInfo(256, 256, SKColorType.Rgba8888, SKAlphaType.Premul));
            source.Erase(new SKColor(255, 0, 0, 128));
            using (var canvas = new SKCanvas(source))
            using (var paint = new SKPaint { Color = new SKColor(0, 255, 0, 64) })
            {
                canvas.DrawRect(64, 64, 128, 128, paint);
            }

            using var turbo = Decode(JpegEncoder.TryEncodeTurbo(source, 90)!);
            using var skia = Decode(JpegEncoder.EncodeSkia(source, 90)!);

            Assert.True(Psnr(ToRgba(turbo), ToRgba(skia)) > 40);
            Assert.InRange(turbo.GetPixel(10, 10).Red, 118, 138);
        }

        [Fact]
        public void Encode_ConcurrentCallsDoNotMixFrames()
        {
            RequireTurbo();
            var colors = new[] { SKColors.Red, SKColors.Lime, SKColors.Blue, SKColors.Yellow, SKColors.Magenta, SKColors.Cyan, SKColors.White, SKColors.Gray };

            var results = new SKColor[colors.Length][];
            Parallel.For(0, colors.Length, new ParallelOptions { MaxDegreeOfParallelism = colors.Length }, i =>
            {
                using var bitmap = new SKBitmap(new SKImageInfo(400 + i * 16, 300, SKColorType.Rgba8888, SKAlphaType.Opaque));
                bitmap.Erase(colors[i]);
                results[i] = Enumerable.Range(0, 20).Select(_ =>
                {
                    using var decoded = Decode(JpegEncoder.Encode(bitmap, 90));
                    Assert.Equal(400 + i * 16, decoded.Width);
                    return decoded.GetPixel(decoded.Width / 2, 150);
                }).ToArray();
            });

            for (int i = 0; i < colors.Length; i++)
            {
                foreach (var c in results[i])
                {
                    Assert.InRange(c.Red, Math.Max(0, colors[i].Red - 8), Math.Min(255, colors[i].Red + 8));
                    Assert.InRange(c.Green, Math.Max(0, colors[i].Green - 8), Math.Min(255, colors[i].Green + 8));
                    Assert.InRange(c.Blue, Math.Max(0, colors[i].Blue - 8), Math.Min(255, colors[i].Blue + 8));
                }
            }
        }
    }
}
