using Serilog;
using SkiaSharp;
using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading;

namespace InfoPanel.Imaging
{
    /// <summary>
    /// Baseline 4:2:0 JPEG encoder for panel frames. Uses the system libturbojpeg
    /// (about 7x faster than Skia's encoder at panel sizes) and falls back to Skia
    /// when the library is missing, the pixel format is unsupported or a call fails.
    /// Thread-safe: each call rents its own TurboJPEG compressor from a small pool.
    /// </summary>
    /// <remarks>
    /// Alpha is ignored by both paths, as Skia's encoder does by default
    /// (SkJpegEncoder AlphaOption::kIgnore): the stored colour bytes are encoded
    /// as-is, so a premultiplied translucent pixel comes out as if composited on
    /// black and an unpremultiplied one keeps its full colour. Panel frames are
    /// opaque, so this only matters for parity.
    /// </remarks>
    public static unsafe class JpegEncoder
    {
        private static readonly ILogger Logger = Log.ForContext(typeof(JpegEncoder));

        // turbojpeg.h enum values; stable since TurboJPEG 1.2
        private const int TJPF_RGBX = 2;
        private const int TJPF_BGRX = 3;
        private const int TJPF_RGBA = 7;
        private const int TJPF_BGRA = 8;
        private const int TJSAMP_420 = 2;
        private const int TJFLAG_NOREALLOC = 1024;

        // Idle compressors kept for reuse; each holds a worst-case output buffer
        // (about 3 bytes per pixel), so extras beyond this are destroyed.
        private const int MaxIdleCompressors = 4;

        /// <summary>Set to "skia" to bypass libturbojpeg (e.g. if a panel rejects its output).</summary>
        public const string EncoderEnvironmentVariable = "INFOPANEL_JPEG_ENCODER";

        private static readonly string[] LibraryNames =
        [
            "libturbojpeg.so.0",
            "libturbojpeg.so",
        ];

        // Only the TurboJPEG 2.x API (also exported by 3.x) so older distros work.
        private static delegate* unmanaged<nint> _tjInitCompress;
        private static delegate* unmanaged<nint, byte*, int, int, int, int, byte**, CULong*, int, int, int, int> _tjCompress2;
        private static delegate* unmanaged<int, int, int, CULong> _tjBufSize;
        private static delegate* unmanaged<nint, int> _tjDestroy;
        private static delegate* unmanaged<nint, byte*> _tjGetErrorStr2;

        private static readonly Lazy<bool> _turboAvailable = new(ProbeTurbo, LazyThreadSafetyMode.ExecutionAndPublication);
        private static readonly ConcurrentBag<Compressor> _idle = new();
        private static int _idleCount;
        private static int _failureLogged;
        private static int _skiaFailureLogged;

        /// <summary>True when libturbojpeg was loaded and is used for supported pixel formats.</summary>
        public static bool IsTurboAvailable => _turboAvailable.Value;

        /// <summary>
        /// Encodes <paramref name="bitmap"/> at <paramref name="quality"/> (0-100, same
        /// scale as Skia). Throws when neither encoder can handle the bitmap.
        /// </summary>
        public static byte[] Encode(SKBitmap bitmap, int quality)
        {
            return TryEncode(bitmap, quality)
                ?? throw new InvalidOperationException($"JPEG encode failed for {bitmap.Width}x{bitmap.Height} {bitmap.ColorType} bitmap");
        }

        /// <summary>As <see cref="Encode"/>, but returns null when encoding fails.</summary>
        public static byte[]? TryEncode(SKBitmap bitmap, int quality)
        {
            return TryEncodeTurbo(bitmap, quality) ?? EncodeSkia(bitmap, quality);
        }

        /// <summary>Skia encoder straight from the bitmap's pixels (no SKImage copy).</summary>
        public static byte[]? EncodeSkia(SKBitmap bitmap, int quality)
        {
            using var pixmap = bitmap.PeekPixels();
            if (pixmap == null)
            {
                return null;
            }
            using var data = pixmap.Encode(SKEncodedImageFormat.Jpeg, quality);
            if (data == null || data.IsEmpty)
            {
                if (Interlocked.Exchange(ref _skiaFailureLogged, 1) == 0)
                {
                    Logger.Warning("Skia JPEG encode failed for {Width}x{Height} {ColorType} bitmap",
                        bitmap.Width, bitmap.Height, bitmap.ColorType);
                }
                return null;
            }
            return data.ToArray();
        }

        /// <summary>
        /// libturbojpeg encode. Returns null when the library is unavailable, the
        /// colour type has no TurboJPEG equivalent, or the call fails.
        /// </summary>
        internal static byte[]? TryEncodeTurbo(SKBitmap bitmap, int quality)
        {
            if (!_turboAvailable.Value)
            {
                return null;
            }

            int pixelFormat = bitmap.ColorType switch
            {
                SKColorType.Rgba8888 => TJPF_RGBA,
                SKColorType.Bgra8888 => TJPF_BGRA,
                SKColorType.Rgb888x => TJPF_RGBX,
                _ => -1
            };
            nint pixels = bitmap.GetPixels();
            int width = bitmap.Width;
            int height = bitmap.Height;
            if (pixelFormat < 0 || pixels == 0 || width <= 0 || height <= 0)
            {
                return null;
            }

            ulong bufSize = _tjBufSize(width, height, TJSAMP_420).Value;
            // tjBufSize returns (unsigned long)-1 on error
            if (bufSize == 0 || bufSize > int.MaxValue)
            {
                return null;
            }

            var compressor = Rent();
            if (compressor == null)
            {
                return null;
            }

            bool healthy = false;
            try
            {
                if (compressor.Buffer.Length < (int)bufSize)
                {
                    compressor.Buffer = GC.AllocateUninitializedArray<byte>((int)bufSize);
                }

                fixed (byte* output = compressor.Buffer)
                {
                    // NOREALLOC with a tjBufSize-sized buffer: TurboJPEG never
                    // reallocates or frees it, so a managed array is safe here.
                    byte* jpegBuf = output;
                    var jpegSize = new CULong((nuint)compressor.Buffer.Length);
                    int rc = _tjCompress2(compressor.Handle, (byte*)pixels, width, bitmap.RowBytes, height,
                        pixelFormat, &jpegBuf, &jpegSize, TJSAMP_420, Math.Clamp(quality, 1, 100), TJFLAG_NOREALLOC);

                    if (rc != 0 || jpegBuf != output || jpegSize.Value == 0 || jpegSize.Value > (ulong)compressor.Buffer.Length)
                    {
                        LogFailureOnce(Marshal.PtrToStringUTF8((nint)_tjGetErrorStr2(compressor.Handle)) ?? $"tjCompress2 returned {rc}");
                        return null;
                    }

                    healthy = true;
                    return new ReadOnlySpan<byte>(output, (int)jpegSize.Value).ToArray();
                }
            }
            finally
            {
                if (healthy)
                {
                    Return(compressor);
                }
                else
                {
                    // Don't reuse a handle that just reported an error
                    compressor.Destroy();
                }
            }
        }

        private static bool ProbeTurbo()
        {
            if (string.Equals(Environment.GetEnvironmentVariable(EncoderEnvironmentVariable), "skia", StringComparison.OrdinalIgnoreCase))
            {
                Logger.Information("JPEG encoder: Skia ({Variable}=skia)", EncoderEnvironmentVariable);
                return false;
            }

            foreach (var name in LibraryNames)
            {
                if (!NativeLibrary.TryLoad(name, out var library))
                {
                    continue;
                }

                try
                {
                    _tjInitCompress = (delegate* unmanaged<nint>)NativeLibrary.GetExport(library, "tjInitCompress");
                    _tjCompress2 = (delegate* unmanaged<nint, byte*, int, int, int, int, byte**, CULong*, int, int, int, int>)NativeLibrary.GetExport(library, "tjCompress2");
                    _tjBufSize = (delegate* unmanaged<int, int, int, CULong>)NativeLibrary.GetExport(library, "tjBufSize");
                    _tjDestroy = (delegate* unmanaged<nint, int>)NativeLibrary.GetExport(library, "tjDestroy");
                    _tjGetErrorStr2 = (delegate* unmanaged<nint, byte*>)NativeLibrary.GetExport(library, "tjGetErrorStr2");

                    // Prove a compressor can actually be created before committing to it
                    nint handle = _tjInitCompress();
                    if (handle == 0)
                    {
                        Logger.Warning("JPEG encoder: Skia ({Library} loaded but tjInitCompress failed)", name);
                        return false;
                    }
                    Return(new Compressor(handle));

                    Logger.Information("JPEG encoder: libjpeg-turbo ({Library})", name);
                    return true;
                }
                catch (Exception e)
                {
                    // Typically EntryPointNotFoundException from a pre-2.0 library.
                    // Lazy<T> would cache a thrown exception, so never let one escape.
                    Logger.Warning("JPEG encoder: Skia ({Library} unusable: {Message})", name, e.Message);
                    return false;
                }
            }

            Logger.Information("JPEG encoder: Skia (libturbojpeg.so.0 not found)");
            return false;
        }

        private static Compressor? Rent()
        {
            if (_idle.TryTake(out var compressor))
            {
                Interlocked.Decrement(ref _idleCount);
                return compressor;
            }

            nint handle = _tjInitCompress();
            if (handle == 0)
            {
                LogFailureOnce("tjInitCompress returned NULL");
                return null;
            }
            return new Compressor(handle);
        }

        private static void Return(Compressor compressor)
        {
            if (Interlocked.Increment(ref _idleCount) <= MaxIdleCompressors)
            {
                _idle.Add(compressor);
                return;
            }
            Interlocked.Decrement(ref _idleCount);
            compressor.Destroy();
        }

        private static void LogFailureOnce(string error)
        {
            if (Interlocked.Exchange(ref _failureLogged, 1) == 0)
            {
                Logger.Warning("libjpeg-turbo encode failed, using Skia for this frame: {Error}", error);
            }
        }

        private sealed class Compressor(nint handle)
        {
            public nint Handle { get; private set; } = handle;
            public byte[] Buffer = [];

            public void Destroy()
            {
                if (Handle != 0)
                {
                    _tjDestroy(Handle);
                    Handle = 0;
                }
            }
        }
    }
}
