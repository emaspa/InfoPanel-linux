using InfoPanel.Extensions;
using InfoPanel.Utils;
using Microsoft.Extensions.Caching.Memory;
using SkiaSharp;
using Svg.Skia;
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using Serilog;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace InfoPanel.Models
{
    public partial class LockedImage : IDisposable
    {
        private static readonly ILogger Logger = Log.ForContext<LockedImage>();
        
        public enum ImageType
        {
            SK, SVG, FFMPEG, PLUGIN
        }

        public readonly string ImagePath;
        private readonly ConcurrentDictionary<Guid, ImageDisplayItem> imageDisplayItems = [];

        private readonly TypedMemoryCache<SKImageFrameSlot[]> SKImageMemoryCache = new(new MemoryCacheOptions()
        {
            ExpirationScanFrequency = TimeSpan.FromSeconds(5)
        });

        private readonly TypedMemoryCache<SKImageFrameSlot[]> SKGLImageMemoryCache = new();

        public int Width { get; private set; } = 0;
        public int Height { get; private set; } = 0;

        public readonly ImageType Type;

        private SKSvg? SKSvg;

        // Video decoded via the system ffmpeg binary (v1 used Windows-only FlyleafLib)
        private Video.FfmpegVideoDecoder? _videoDecoder;

        public TimeSpan? CurrentTime => null;
        public TimeSpan? Duration => _videoDecoder?.Duration;
        public double? FrameRate => _videoDecoder?.FrameRate;

        public bool HasAudio => false;

        public bool IsLive => ImagePath.StartsWith("rtsp", StringComparison.OrdinalIgnoreCase);
        public VideoPlayerStatus? VideoPlayerStatus => _videoDecoder != null ? Models.VideoPlayerStatus.Playing : null;

        public float Volume
        {
            get => 0f;
            set { }
        }

        public long Frames { get; private set; }
        public long TotalFrameTime { get; private set; }

        private SKCodec? _codec;
        private Stream? _stream;
        private SKBitmap? _compositeBitmap;
        private long[]? _cumulativeFrameTimes;
        private int _lastRenderedFrame = -1;

        private readonly string? _pluginId;
        private readonly string? _pluginImageId;

        private readonly object Lock = new();
        private bool IsDisposed = false;

        private readonly Stopwatch Stopwatch = new();

        public bool Loaded { get; private set; } = false;

        /// <summary>
        /// Creates a LockedImage backed by a live plugin image buffer. The writer is
        /// resolved on every access so plugin reloads and resizes are picked up without
        /// cache invalidation.
        /// </summary>
        public LockedImage(string imagePath, string pluginId, string pluginImageId)
        {
            ImagePath = imagePath;
            _pluginId = pluginId;
            _pluginImageId = pluginImageId;
            Type = ImageType.PLUGIN;
            Frames = 1;

            if (PluginImageSource.Resolve(pluginId, pluginImageId) is { } writer)
            {
                Width = writer.Width;
                Height = writer.Height;
            }

            Loaded = true;
        }

        public LockedImage(string imagePath, ImageDisplayItem? sourceImageDisplayItem)
        {
            ImagePath = imagePath;

            try
            {
                // UriBuilder chokes on plain unix paths; only strip query strings off real URLs
                var strippedUrl = imagePath;
                if (imagePath.Contains("://"))
                {
                    try
                    {
                        var uri = new UriBuilder(imagePath) { Query = "" };
                        strippedUrl = uri.Uri.ToString();
                    }
                    catch (UriFormatException)
                    {
                    }
                }
                if (strippedUrl.StartsWith("rtsp://", StringComparison.OrdinalIgnoreCase)
                        || strippedUrl.StartsWith("rtsps://", StringComparison.OrdinalIgnoreCase)
                        || strippedUrl.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)
                        || strippedUrl.EndsWith(".mkv", StringComparison.OrdinalIgnoreCase)
                        || strippedUrl.EndsWith(".webm", StringComparison.OrdinalIgnoreCase)
                        || strippedUrl.EndsWith(".avi", StringComparison.OrdinalIgnoreCase)
                        || strippedUrl.EndsWith(".mov", StringComparison.OrdinalIgnoreCase)
                        || strippedUrl.EndsWith(".m3u", StringComparison.OrdinalIgnoreCase)
                        || strippedUrl.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase))
                {
                    if (!ImagePath.StartsWith("rtsp://", StringComparison.OrdinalIgnoreCase)
                        && !ImagePath.StartsWith("rtsps://", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!ImagePath.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                            && !ImagePath.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                            && !File.Exists(ImagePath))
                        {
                            throw new ArgumentException("Video file does not exist.", nameof(imagePath));
                        }
                    }

                    Type = ImageType.FFMPEG;
                    _videoDecoder = Video.FfmpegVideoDecoder.Open(ImagePath);
                    if (_videoDecoder == null)
                    {
                        throw new InvalidOperationException($"Could not open video (is ffmpeg installed?): {ImagePath}");
                    }

                    Width = _videoDecoder.Width;
                    Height = _videoDecoder.Height;

                    if (sourceImageDisplayItem != null)
                    {
                        AddImageDisplayItem(sourceImageDisplayItem);
                    }

                    Loaded = true;
                    return;
                }
                else if (ImagePath.IsUrl())
                {
                    using HttpClient client = new();
                    client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/58.0.3029.110 Safari/537.3");

                    try
                    {
                        var data = client.GetByteArrayAsync(ImagePath).GetAwaiter().GetResult();
                        _stream = new MemoryStream(data);
                    }
                    catch (Exception e)
                    {
                        Logger.Error(e, "Error loading image from URL");
                    }
                }
                else if (File.Exists(ImagePath))
                {
                    try
                    {
                        var fileStream = new FileStream(ImagePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                        _stream = new MemoryStream();
                        fileStream.CopyTo(_stream);
                        fileStream.Dispose();
                        _stream.Position = 0;

                        Logger.Debug("Image loaded from file: {ImagePath}", ImagePath);
                    }
                    catch (Exception e)
                    {
                        Logger.Error(e, "Error loading image from file");
                    }
                }


                if (_stream == null)
                {
                    throw new ArgumentException("Image path is invalid or file does not exist.", nameof(imagePath));
                }

                if (IsSvgContent(_stream))
                {
                    Type = ImageType.SVG;

                    SKSvg = new SKSvg();
                    SKSvg.Load(_stream);

                    if (SKSvg.Picture is SKPicture picture)
                    {
                        Width = (int)picture.CullRect.Width;
                        Height = (int)picture.CullRect.Height;
                        Frames = 1;
                    }
                }
                else
                {
                    _codec?.Dispose();
                    _codec = SKCodec.Create(_stream);

                    if (_codec == null)
                    {
                        Log.Error("Failed to create SKCodec for {ImagePath}", ImagePath);
                        throw new ArgumentException("Unsupported image format or codec creation failed.", nameof(imagePath));
                    }

                    Width = _codec.Info.Width;
                    Height = _codec.Info.Height;

                    Frames = _codec.FrameCount;

                    //ensure at least 1 frame
                    if (Frames == 0)
                    {
                        Frames = 1;
                    }

                    _cumulativeFrameTimes = new long[Frames];

                    if (Frames > 1)
                    {
                        for (int i = 0; i < Frames; i++)
                        {
                            var frameDelay = _codec.FrameInfo[i].Duration;

                            if (frameDelay == 0)
                            {
                                frameDelay = 100;
                            }

                            TotalFrameTime += frameDelay;
                            _cumulativeFrameTimes[i] = TotalFrameTime;
                        }

                        //start the stopwatch
                        Stopwatch.Start();
                    }
                }

                if (sourceImageDisplayItem != null)
                {
                    AddImageDisplayItem(sourceImageDisplayItem);
                }

                Loaded = true;
            }
            catch (Exception e)
            {
                Log.Error(e, "Error initializing LockedImage for {ImagePath}", ImagePath);
            }
        }

        public void AddImageDisplayItem(ImageDisplayItem item)
        {
            if (imageDisplayItems.TryAdd(item.Guid, item))
            {
                item.Profile.PropertyChanged += Profile_PropertyChanged;
                item.PropertyChanged += ImageDisplayItem_PropertyChanged;

                UpdateVolume();
            }
        }

        public void RemoveImageDisplayItem(ImageDisplayItem item)
        {
            if (imageDisplayItems.TryRemove(item.Guid, out _))
            {
                item.Profile.PropertyChanged -= Profile_PropertyChanged;
                item.PropertyChanged -= ImageDisplayItem_PropertyChanged;

                UpdateVolume();
            }
        }

        private void Profile_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case nameof(Profile.Active):
                    UpdateVolume();
                    break;
            }
        }

        private void ImageDisplayItem_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case nameof(ImageDisplayItem.Volume):
                case nameof(ImageDisplayItem.Hidden):
                    UpdateVolume();
                    break;
            }
        }

        private void UpdateVolume()
        {
            int volume = 0;
            foreach (var item in imageDisplayItems.Values)
            {
                if (item.Profile.Active && !item.Hidden && item.Volume > volume)
                {
                    volume = item.Volume;
                }
            }

            Volume = volume / 100f;
        }

        private static bool IsSvgContent(Stream stream)
        {
            if (stream.Length < 512)
            {
                return false;
            }

            var buffer = new byte[512];
            stream.Read(buffer, 0, buffer.Length);
            stream.Position = 0;

            // Check for SVG markers in the first bytes
            var text = Encoding.UTF8.GetString(buffer);
            return text.Contains("<svg", StringComparison.OrdinalIgnoreCase) ||
                   text.Contains("<?xml", StringComparison.OrdinalIgnoreCase) && text.Contains("svg", StringComparison.OrdinalIgnoreCase);
        }

        private SKBitmap? GetSKBitmapFromSK(int frame)
        {
            if (_stream != null && _codec != null)
            {
                var info = _codec.Info;
                _compositeBitmap ??= new SKBitmap(info);

                SKBitmap? keepCopy = null;

                if (frame != _lastRenderedFrame)
                {
                    if (_lastRenderedFrame >= frame)
                    {
                        ResetCompositeBitmap(_compositeBitmap);
                        _lastRenderedFrame = -1;
                    }

                    for (int i = _lastRenderedFrame + 1; i <= frame; i++)
                    {

                        SKCodecFrameInfo? frameInfo = null;
                        if (_codec.FrameCount > 0)
                        {
                            frameInfo = _codec.FrameInfo[i];
                            if (frameInfo?.DisposalMethod == SKCodecAnimationDisposalMethod.RestoreBackgroundColor)
                            {
                                ResetCompositeBitmap(_compositeBitmap);
                            }
                            else if (frameInfo?.DisposalMethod == SKCodecAnimationDisposalMethod.RestorePrevious)
                            {
                                keepCopy?.Dispose();
                                keepCopy = _compositeBitmap.Copy();
                            }
                        }

                        //var options = new SKCodecOptions(i, i > 0 ? i - 1 : 0);
                        var requiredFrame = frameInfo?.RequiredFrame ?? (i > 0 ? i - 1 : 0);

                        var options = new SKCodecOptions(i, requiredFrame);
                        try
                        {
                            var r = _codec.GetPixels(info, _compositeBitmap.GetPixels(), options);

                            if (r != SKCodecResult.Success)
                            {
                                Log.Error("SKCodec error: {Result} at frame i={FrameIndex}", r, i);
                                return null;
                            }
                        }
                        catch (Exception e)
                        {
                            Log.Error(e, "Error getting pixels from codec at frame {FrameIndex}", i);
                        }
                    }

                    _lastRenderedFrame = frame;
                }

                var result = _compositeBitmap.Copy(SKColorType.Bgra8888);

                if (keepCopy != null)
                {
                    _compositeBitmap?.Dispose();
                    _compositeBitmap = keepCopy;
                }

                return result;
            }

            return null;
        }


        private static void ResetCompositeBitmap(SKBitmap bitmap)
        {
            using var canvas = new SKCanvas(bitmap);
            canvas.Clear(SKColors.Transparent);
        }

        private int GetCurrentFrameCount()
        {
            if (_codec == null || _cumulativeFrameTimes == null || Frames <= 1 || TotalFrameTime == 0)
            {
                return 0;
            }

            var elapsedTime = Stopwatch.ElapsedMilliseconds;

            // Reset stopwatch every day (24 hours).
            if (elapsedTime >= 86400000)
            {
                Stopwatch.Restart();
                elapsedTime = 0;
            }

            var elapsedFrameTime = elapsedTime % TotalFrameTime;

            // Use binary search to find the current frame index
            int index = Array.BinarySearch(_cumulativeFrameTimes, (int)elapsedFrameTime);

            // BinarySearch returns a negative value if the exact value isn't found.
            if (index < 0)
            {
                index = ~index;
            }

            // Handle wrapping around if needed
            if (index >= _cumulativeFrameTimes.Length)
            {
                index = 0; // Wrap to the first frame
            }

            return index;
        }

        // ---- URL refresh (per-item RefreshIntervalSeconds) ----

        private DateTime _lastFetchUtc = DateTime.UtcNow;
        private DateTime _nextFetchAllowedUtc = DateTime.MinValue;
        private int _refreshInFlight;

        /// <summary>
        /// Smallest positive refresh interval across the display items showing this
        /// image, or zero when none requests refreshing.
        /// </summary>
        private TimeSpan GetRefreshInterval()
        {
            int seconds = 0;
            foreach (var item in imageDisplayItems.Values)
            {
                if (item.RefreshIntervalSeconds > 0 && (seconds == 0 || item.RefreshIntervalSeconds < seconds))
                {
                    seconds = item.RefreshIntervalSeconds;
                }
            }

            return TimeSpan.FromSeconds(seconds);
        }

        /// <summary>
        /// Kicks off a background re-download when the refresh interval has elapsed.
        /// The current image keeps rendering until the new one is decoded and swapped
        /// in, so rendering never blocks on the network.
        /// </summary>
        private void MaybeStartRefresh()
        {
            if (Type is not (ImageType.SK or ImageType.SVG) || !Loaded || !ImagePath.IsUrl())
            {
                return;
            }

            var interval = GetRefreshInterval();
            if (interval <= TimeSpan.Zero)
            {
                return;
            }

            var now = DateTime.UtcNow;
            if (now - _lastFetchUtc < interval || now < _nextFetchAllowedUtc)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref _refreshInFlight, 1, 0) != 0)
            {
                return;
            }

            _ = Task.Run(RefreshFromUrlAsync);
        }

        private async Task RefreshFromUrlAsync()
        {
            try
            {
                using HttpClient client = new();
                client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/58.0.3029.110 Safari/537.3");
                client.DefaultRequestHeaders.CacheControl = new CacheControlHeaderValue { NoCache = true };

                var data = await client.GetByteArrayAsync(ImagePath);
                var newStream = new MemoryStream(data);

                if (Type == ImageType.SVG)
                {
                    var newSvg = new SKSvg();
                    newSvg.Load(newStream);
                    if (newSvg.Picture is not SKPicture picture)
                    {
                        newSvg.Dispose();
                        newStream.Dispose();
                        throw new InvalidOperationException("Refreshed content is not a valid SVG.");
                    }

                    lock (Lock)
                    {
                        if (IsDisposed)
                        {
                            newSvg.Dispose();
                            newStream.Dispose();
                            return;
                        }

                        SKSvg?.Dispose();
                        _stream?.Dispose();
                        SKSvg = newSvg;
                        _stream = newStream;
                        Width = (int)picture.CullRect.Width;
                        Height = (int)picture.CullRect.Height;

                        SKImageMemoryCache.Clear();
                        SKGLImageMemoryCache.Clear();
                    }
                }
                else
                {
                    var newCodec = SKCodec.Create(newStream);
                    if (newCodec == null)
                    {
                        newStream.Dispose();
                        throw new InvalidOperationException("Refreshed content could not be decoded.");
                    }

                    lock (Lock)
                    {
                        if (IsDisposed)
                        {
                            newCodec.Dispose();
                            newStream.Dispose();
                            return;
                        }

                        _codec?.Dispose();
                        _stream?.Dispose();
                        _compositeBitmap?.Dispose();
                        _compositeBitmap = null;
                        _codec = newCodec;
                        _stream = newStream;
                        _lastRenderedFrame = -1;

                        Width = newCodec.Info.Width;
                        Height = newCodec.Info.Height;
                        Frames = Math.Max(newCodec.FrameCount, 1);
                        TotalFrameTime = 0;
                        _cumulativeFrameTimes = new long[Frames];

                        if (Frames > 1)
                        {
                            for (int i = 0; i < Frames; i++)
                            {
                                var frameDelay = newCodec.FrameInfo[i].Duration;
                                if (frameDelay == 0)
                                {
                                    frameDelay = 100;
                                }

                                TotalFrameTime += frameDelay;
                                _cumulativeFrameTimes[i] = TotalFrameTime;
                            }

                            Stopwatch.Restart();
                        }

                        // Frame caches hold images decoded from the previous download
                        SKImageMemoryCache.Clear();
                        SKGLImageMemoryCache.Clear();
                    }
                }

                _lastFetchUtc = DateTime.UtcNow;
            }
            catch (Exception e)
            {
                Logger.Debug(e, "Refresh failed for {ImagePath}", ImagePath);
                // Keep showing the last good image; retry no sooner than 10s from now
                _lastFetchUtc = DateTime.UtcNow;
                _nextFetchAllowedUtc = DateTime.UtcNow.AddSeconds(10);
            }
            finally
            {
                Interlocked.Exchange(ref _refreshInFlight, 0);
            }
        }

        public void AccessSVG(Action<SKPicture> access)
        {
            if (IsDisposed)
            {
                throw new ObjectDisposedException("LockedImage");
            }

            if (!Loaded)
            {
                return;
            }

            MaybeStartRefresh();

            lock (Lock)
            {
                if (SKSvg?.Picture is SKPicture picture)
                {
                    access(picture);
                }
            }
        }

        private SKImageFrameSlot[] GetSKBitmapFrameCache(string cacheHint)
        {
            lock (Lock)
            {
                SKImageMemoryCache.TryGetValue(cacheHint, out var cacheValue);
                if (cacheValue == null)
                {
                    cacheValue = new SKImageFrameSlot[Frames];
                    for (int i = 0; i < Frames; i++)
                    {
                        cacheValue[i] = new SKImageFrameSlot();
                    }

                    SKImageMemoryCache.Set(cacheHint, cacheValue, new MemoryCacheEntryOptions
                    {
                        SlidingExpiration = TimeSpan.FromSeconds(5),
                        PostEvictionCallbacks = {
                            new PostEvictionCallbackRegistration
                            {
                                EvictionCallback = (key, value, reason, state) =>
                                {
                                    Log.Debug("Cache entry '{Key}' evicted due to {Reason}.", key, reason);
                                    if (value is SKImageFrameSlot[] slots)
                                    {
                                        foreach (var slot in slots)
                                        {
                                            slot.Dispose();
                                        }
                                    }
                                }
                            }
                        }
                    });
                }

                return cacheValue;
            }
        }

        public SKImageFrameSlot[] GetD2DBitmapFrameCache(string cacheHint)
        {
            lock (Lock)
            {
                SKGLImageMemoryCache.TryGetValue(cacheHint, out var cacheValue);
                if (cacheValue == null)
                {
                    cacheValue = new SKImageFrameSlot[Frames];
                    for (int i = 0; i < Frames; i++)
                    {
                        cacheValue[i] = new SKImageFrameSlot();
                    }

                    SKGLImageMemoryCache.Set(cacheHint, cacheValue);
                }

                return cacheValue;
            }
        }

        // ConvertToSKImage(System.Drawing.Bitmap) removed for Linux port
        // System.Drawing.Bitmap is not available on Linux
        // TODO: Reimplement with LibVLCSharp frame capture in Phase 5.5

        public void AccessSK(int targetWidth, int targetHeight, Action<SKImage> access, bool cache = true, string cacheHint = "default", GRContext? grContext = null)
        {
            ObjectDisposedException.ThrowIf(IsDisposed, this);

            if (!Loaded)
            {
                return;
            }

            if (targetWidth <= 0 || targetHeight <= 0)
                return;

            MaybeStartRefresh();

            lock (Lock)
            {
                if (Type == ImageType.PLUGIN)
                {
                    var writer = PluginImageSource.Resolve(_pluginId!, _pluginImageId!);
                    writer?.TryAccessFrame(frameBitmap =>
                    {
                        Width = writer.Width;
                        Height = writer.Height;

                        using var resized = frameBitmap.Resize(
                            new SKImageInfo(targetWidth, targetHeight),
                            new SKSamplingOptions(SKCubicResampler.Mitchell));
                        if (resized != null)
                        {
                            using var image = SKImage.FromBitmap(resized);
                            access(image);
                        }
                    });
                    return;
                }

                if (Type == ImageType.FFMPEG)
                {
                    // Video frames change every access: resize the latest decoded frame
                    // directly, bypassing the per-frame caches.
                    _videoDecoder?.TryAccessFrame(frameBitmap =>
                    {
                        using var resized = frameBitmap.Resize(
                            new SKImageInfo(targetWidth, targetHeight),
                            new SKSamplingOptions(SKCubicResampler.Mitchell));
                        if (resized != null)
                        {
                            using var image = SKImage.FromBitmap(resized);
                            access(image);
                        }
                    });
                    return;
                }

                // We are on the GL render thread when grContext is set (leased canvas):
                // the only safe place to free texture images queued by other threads.
                if (grContext != null)
                {
                    SKImageFrameSlot.DrainGlDisposalQueue();
                }

                var SKBitmapCache = grContext != null ? GetD2DBitmapFrameCache(cacheHint) : GetSKBitmapFrameCache(cacheHint);

                var frame = GetCurrentFrameCount();

                var bitmapFrame = SKBitmapCache[frame];

                if (cache && (bitmapFrame.Width != targetWidth || bitmapFrame.Height != targetHeight))
                {
                    bitmapFrame.Invalidate();
                }

                if (bitmapFrame.Image != null && grContext != null && !bitmapFrame.Image.IsValid(grContext))
                {
                    bitmapFrame.Invalidate();
                }

                var shouldDispose = false;

                if (bitmapFrame.Image == null)
                {
                    using var bitmap = GetSKBitmapFromSK(frame);
                    //using var resizedBitmap = bitmap?.Resize(new SKImageInfo(targetWidth, targetHeight), SKSamplingOptions.Default);
                    using var resizedBitmap = bitmap?.Resize(new SKImageInfo(targetWidth, targetHeight), new SKSamplingOptions(SKCubicResampler.Mitchell));

                    if (grContext != null && cache && resizedBitmap != null)
                    {
                        using var image = SKImage.FromBitmap(resizedBitmap);
                        bitmapFrame.Image = image.ToTextureImage(grContext);

                        // Evict other GPU texture frames for animated images to prevent VRAM accumulation
                        if (Frames > 1 && bitmapFrame.Image != null)
                        {
                            for (int i = 0; i < SKBitmapCache.Length; i++)
                            {
                                if (i != frame)
                                {
                                    SKBitmapCache[i].Invalidate();
                                }
                            }
                        }
                    }
                    else
                    {
                        bitmapFrame.Image = SKImage.FromBitmap(resizedBitmap);

                        // Long animations: keep only the current frame resident, like the
                        // GPU branch above. Caching every frame of a big GIF at target
                        // size costs Frames x W x H x 4 bytes (hundreds of MB for long
                        // animations); re-resizing one frame per tick is ~1 ms.
                        if (Frames > 8 && bitmapFrame.Image != null)
                        {
                            for (int i = 0; i < SKBitmapCache.Length; i++)
                            {
                                if (i != frame)
                                {
                                    SKBitmapCache[i].Invalidate();
                                }
                            }
                        }
                    }

                    if (!cache)
                    {
                        shouldDispose = true;
                    }
                }

                if (bitmapFrame.Image != null)
                {
                    access(bitmapFrame.Image);

                    if (shouldDispose)
                    {
                        bitmapFrame.Invalidate();
                    }
                }
            }
        }

        public void DisposeSKAssets()
        {
            lock (Lock)
            {
                foreach (var key in SKImageMemoryCache.Keys)
                {
                    Log.Debug("Clearing SKImageMemoryCache[{Key}]", key);
                }
                SKImageMemoryCache.Clear();
            }
        }

        public void DisposeGLAssets()
        {
            lock (Lock)
            {
                foreach (var key in SKGLImageMemoryCache.Keys)
                {
                    Log.Debug("Clearing SKGLImageMemoryCache[{Key}]", key);
                }
                SKGLImageMemoryCache.Clear();
            }
        }

        public void Dispose()
        {
            if (IsDisposed)
                return;

            lock (Lock)
            {
                if (!IsDisposed)
                {
                    SKImageMemoryCache.Dispose();
                    SKGLImageMemoryCache.Dispose();

                    SKSvg?.Dispose();

                    _videoDecoder?.Dispose();
                    _videoDecoder = null;

                    _codec?.Dispose();
                    _stream?.Dispose();
                    _compositeBitmap?.Dispose();

                    Stopwatch.Stop();

                    foreach (var item in imageDisplayItems.Values)
                    {
                        item.Profile.PropertyChanged -= Profile_PropertyChanged;
                        item.PropertyChanged -= ImageDisplayItem_PropertyChanged;
                    }

                    imageDisplayItems.Clear();

                    IsDisposed = true;
                    Log.Debug("LockedImage {ImagePath} disposed.", ImagePath);
                }
            }

            GC.SuppressFinalize(this);
        }

        public class SKImageFrameSlot : IDisposable
        {
            private volatile SKImage? _bitmap;
            private int _disposed = 0;

            public SKImage? Image
            {
                get => _bitmap;
                set
                {
                    if (Interlocked.CompareExchange(ref _disposed, 0, 0) == 1)
                        return; // Already disposed

                    var oldBitmap = Interlocked.Exchange(ref _bitmap, value);
                    DisposeImage(oldBitmap);
                }
            }

            public int Width => _bitmap?.Width ?? 0;
            public int Height => _bitmap?.Height ?? 0;

            /// <summary>
            /// GPU-texture-backed images must be disposed with their GL context current.
            /// That context belongs to the Avalonia RENDER thread (the leased-canvas
            /// draws), not the UI dispatcher - posting Dispose to the UI thread freed
            /// GL textures from the wrong thread, a native-crash source while editing
            /// profiles with images. Foreign-thread disposals are queued here instead
            /// and drained at the start of the next GL-thread access.
            /// </summary>
            private static readonly System.Collections.Concurrent.ConcurrentQueue<SKImage> _glDisposalQueue = new();

            /// <summary>Call only from the GL render thread (inside a canvas lease).</summary>
            public static void DrainGlDisposalQueue()
            {
                while (_glDisposalQueue.TryDequeue(out var image))
                {
                    try { image.Dispose(); }
                    catch (Exception e) { Log.Error(e, "Error disposing queued GL SKImage"); }
                }
            }

            private static void DisposeImage(SKImage? image)
            {
                if (image == null) return;

                try
                {
                    if (image.IsTextureBacked)
                    {
                        _glDisposalQueue.Enqueue(image);
                    }
                    else
                    {
                        image.Dispose();
                    }
                }
                catch (Exception e)
                {
                    Log.Error(e, "Error disposing SKImage");
                }
            }

            public void Invalidate()
            {
                Image = null; // Uses the setter logic
            }

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 1)
                    return; // Already disposed

                var oldBitmap = Interlocked.Exchange(ref _bitmap, null);
                DisposeImage(oldBitmap);
                GC.SuppressFinalize(this);
            }
        }

    }
}
