using SkiaSharp;

namespace InfoPanel.Drawing
{
    /// <summary>
    /// Text measurement seam for display items. The rendering layer registers the real
    /// RichTextKit-based measurer at startup; the built-in fallback gives a rough
    /// single-line estimate so Core stays usable (e.g. in tests) without the renderer.
    /// Signature mirrors SkiaGraphics.MeasureString.
    /// </summary>
    public delegate (float Width, float Height) MeasureStringFunc(
        string text, float fontScale, string fontName, string fontStyle, int fontSize,
        bool bold, bool italic, bool underline, bool strikeout,
        bool wrap, bool ellipsis, int width, int height);

    public static class TextMeasure
    {
        private static MeasureStringFunc _measure = FallbackMeasure;

        public static void Configure(MeasureStringFunc measure)
        {
            _measure = measure;
        }

        public static (float Width, float Height) MeasureString(
            string text, float fontScale, string fontName, string fontStyle, int fontSize,
            bool bold = false, bool italic = false, bool underline = false, bool strikeout = false,
            bool wrap = false, bool ellipsis = true, int width = 0, int height = 0)
        {
            return _measure(text, fontScale, fontName, fontStyle, fontSize,
                bold, italic, underline, strikeout, wrap, ellipsis, width, height);
        }

        private static (float Width, float Height) FallbackMeasure(
            string text, float fontScale, string fontName, string fontStyle, int fontSize,
            bool bold, bool italic, bool underline, bool strikeout,
            bool wrap, bool ellipsis, int width, int height)
        {
            var scaledSize = fontSize * fontScale;
            using var typeface = SKTypeface.FromFamilyName(fontName,
                bold ? SKFontStyleWeight.Bold : SKFontStyleWeight.Normal,
                SKFontStyleWidth.Normal,
                italic ? SKFontStyleSlant.Italic : SKFontStyleSlant.Upright);
            using var font = new SKFont(typeface, scaledSize);
            var measuredWidth = font.MeasureText(text);
            var lineHeight = font.Spacing;
            return (width > 0 ? width : measuredWidth, height > 0 ? height : lineHeight);
        }
    }
}
