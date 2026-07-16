using SkiaSharp;

namespace InfoPanel.Models
{
    /// <summary>
    /// Image cache seam for display items. The rendering layer's image cache registers
    /// itself at startup so EvaluateSize can resolve intrinsic image dimensions and
    /// path changes can invalidate cached decodes. Without a registration, images
    /// report no intrinsic size and invalidation is a no-op.
    /// </summary>
    public static class ImageCacheHook
    {
        /// <summary>Returns the intrinsic pixel size of the item's image, if cached/decodable. Second arg mirrors Cache.GetLocalImage's initialiseIfMissing flag.</summary>
        public static Func<ImageDisplayItem, bool, SKSize?>? GetImageSize;

        /// <summary>Invalidates any cached decode for the given absolute image path.</summary>
        public static Action<string>? InvalidateImage;
    }
}
