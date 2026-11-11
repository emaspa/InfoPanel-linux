using InfoPanel.Utils;
using Serilog;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace InfoPanel.Video
{
    /// <summary>
    /// Keeps a copy of a local video file re-encoded at the size it is drawn, so the
    /// decoder handles fewer pixels and skips the per-frame scale. Decoding a
    /// 2400x1080 H.264 file for a 1600x720 panel costs about 0.21 cores at 24 fps;
    /// a 1600x720 copy costs about 0.10. The copy is made once, in the background at
    /// idle priority, and the decoder switches to it when it is ready.
    /// <para>
    /// Copies live in $XDG_CACHE_HOME/infopanel/video, keyed by the source path, size,
    /// modification time and output size, so editing or replacing the file makes a
    /// new copy. The oldest copies are deleted above <see cref="MaxCacheBytes"/>.
    /// Set INFOPANEL_VIDEO_CACHE=0 to turn it off.
    /// </para>
    /// </summary>
    internal sealed class VideoScaleCache
    {
        private static readonly ILogger Logger = Log.ForContext<VideoScaleCache>();

        /// <summary>Copy only when the source has at least this many times the output's pixels.</summary>
        internal const double MinPixelRatio = 1.5;

        /// <summary>Longer files take minutes to re-encode and a lot of disk.</summary>
        internal static readonly TimeSpan MaxDuration = TimeSpan.FromMinutes(10);

        internal const long MaxCacheBytes = 2L * 1024 * 1024 * 1024;

        // Bump when the encode settings change, so old copies are not reused.
        private const string FormatVersion = "1";

        private static readonly Lazy<VideoScaleCache?> _shared = new(() =>
            Environment.GetEnvironmentVariable("INFOPANEL_VIDEO_CACHE") == "0" ? null : new VideoScaleCache(DefaultDirectory()));

        /// <summary>The app-wide cache, or null when INFOPANEL_VIDEO_CACHE=0.</summary>
        public static VideoScaleCache? Shared => _shared.Value;

        // Fedora's ffmpeg-free, for one, has no libx264.
        private static readonly Lazy<bool> _canEncode = new(ProbeEncoder);

        // One encode at a time: they are rare, and parallel ones would compete for the CPU.
        private static readonly SemaphoreSlim _encodeSlot = new(1, 1);

        private readonly ConcurrentDictionary<string, byte> _pending = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, byte> _failed = new(StringComparer.Ordinal);

        public string Directory { get; }

        public VideoScaleCache(string directory)
        {
            Directory = directory;
        }

        private static string DefaultDirectory()
        {
            var root = Environment.GetEnvironmentVariable("XDG_CACHE_HOME");
            if (string.IsNullOrEmpty(root) || !Path.IsPathFullyQualified(root))
            {
                root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile,
                    Environment.SpecialFolderOption.DoNotVerify), ".cache");
            }
            return Path.Combine(root, "infopanel", "video");
        }

        /// <summary>
        /// True when a re-encoded copy is worth making: a copy saves decode work only
        /// when the source is clearly larger than the output, and libx264's 4:2:0
        /// output needs even sizes.
        /// </summary>
        internal static bool IsWorthCaching(int sourceWidth, int sourceHeight, int width, int height, TimeSpan? duration)
        {
            if (width <= 0 || height <= 0 || width % 2 != 0 || height % 2 != 0)
            {
                return false;
            }
            if (duration is not { } d || d <= TimeSpan.Zero || d > MaxDuration)
            {
                return false;
            }
            return (double)sourceWidth * sourceHeight >= MinPixelRatio * width * height;
        }

        /// <summary>Cache file name for this source state and output size.</summary>
        internal static string Key(string fullPath, long length, DateTime lastWriteUtc, int width, int height)
        {
            var text = $"{FormatVersion}|{fullPath}|{length}|{lastWriteUtc.Ticks}|{width}x{height}";
            return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)).AsSpan(0, 16));
        }

        private static string? KeyFor(string source, int width, int height)
        {
            try
            {
                var info = new FileInfo(source);
                return info.Exists ? Key(info.FullName, info.Length, info.LastWriteTimeUtc, width, height) : null;
            }
            catch (Exception)
            {
                return null;   // URLs and unreadable paths are not cached
            }
        }

        /// <summary>
        /// Returns the path of a ready copy of <paramref name="source"/> at this size.
        /// Otherwise starts making one when it is worth it, calls
        /// <paramref name="onReady"/> when it is done, and returns null.
        /// </summary>
        public string? GetOrStart(string source, int sourceWidth, int sourceHeight, int width, int height,
            TimeSpan? duration, Action onReady, CancellationToken token)
        {
            if (!IsWorthCaching(sourceWidth, sourceHeight, width, height, duration))
            {
                return null;
            }

            var key = KeyFor(source, width, height);
            if (key == null)
            {
                return null;
            }

            var path = Path.Combine(Directory, key + ".mp4");
            if (File.Exists(path))
            {
                // Pruning keeps the most recently used copies.
                try { File.SetLastWriteTimeUtc(path, DateTime.UtcNow); } catch (Exception) { }
                return path;
            }

            if (!_failed.ContainsKey(key) && _canEncode.Value && _pending.TryAdd(key, 0))
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        if (await EncodeAsync(source, path, width, height, token).ConfigureAwait(false))
                        {
                            onReady();
                        }
                        else if (!token.IsCancellationRequested)
                        {
                            _failed.TryAdd(key, 0);
                        }
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested)
                    {
                    }
                    catch (Exception ex)
                    {
                        _failed.TryAdd(key, 0);
                        Logger.Warning(ex, "Could not make a scaled copy of {Source}", source);
                    }
                    finally
                    {
                        _pending.TryRemove(key, out _);
                    }
                }, CancellationToken.None);
            }
            return null;
        }

        internal static List<string> BuildEncodeArguments(string source, string output, int width, int height) =>
        [
            "-hide_banner", "-loglevel", "error", "-nostdin", "-y",
            "-threads", "2",
            "-i", source,
            "-map", "0:v:0", "-an", "-sn", "-dn",
            "-filter_threads", "1",
            "-vf", $"scale={width}:{height}:flags=bicubic",
            // The panel gets a JPEG of this anyway; crf 16 is visually lossless at panel sizes.
            "-c:v", "libx264", "-preset", "veryfast", "-crf", "16", "-pix_fmt", "yuv420p",
            "-threads", "2",
            "-f", "mp4", output,
        ];

        private async Task<bool> EncodeAsync(string source, string path, int width, int height, CancellationToken token)
        {
            await _encodeSlot.WaitAsync(token).ConfigureAwait(false);   // throws before the try when cancelled
            var temp = $"{path}.{Environment.ProcessId}.tmp";
            try
            {
                if (File.Exists(path))
                {
                    return true;   // made while waiting for the slot
                }

                System.IO.Directory.CreateDirectory(Directory);
                var startInfo = new ProcessStartInfo("ffmpeg")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                };
                startInfo.Environment["GLIBC_TUNABLES"] = NativeHeap.ChildTunables(startInfo.Environment.TryGetValue("GLIBC_TUNABLES", out var tunables) ? tunables : null);
                foreach (var arg in BuildEncodeArguments(source, temp, width, height))
                {
                    startInfo.ArgumentList.Add(arg);
                }

                var watch = Stopwatch.StartNew();
                using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("ffmpeg did not start");
                try { process.PriorityClass = ProcessPriorityClass.Idle; } catch (Exception) { }
                var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);
                _ = process.StandardOutput.ReadToEndAsync(CancellationToken.None);

                try
                {
                    await process.WaitForExitAsync(token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    try { process.Kill(entireProcessTree: true); } catch (Exception) { }
                    Logger.Debug("Scaled copy of {Source} cancelled", source);
                    return false;
                }

                if (process.ExitCode != 0)
                {
                    Logger.Warning("Could not make a {Width}x{Height} copy of {Source} (ffmpeg exit code {ExitCode}); decoding the original. {Stderr}",
                        width, height, source, process.ExitCode, (await stderr.ConfigureAwait(false)).Trim());
                    return false;
                }

                File.Move(temp, path, overwrite: true);
                Logger.Information("Made a {Width}x{Height} copy of {Source} in {Seconds:0.0} s",
                    width, height, source, watch.Elapsed.TotalSeconds);
                Prune();
                return true;
            }
            finally
            {
                try { File.Delete(temp); } catch (Exception) { }
                _encodeSlot.Release();
            }
        }

        private static bool ProbeEncoder()
        {
            try
            {
                using var process = Process.Start(new ProcessStartInfo("ffmpeg", "-hide_banner -encoders")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                });
                if (process == null) return false;
                _ = process.StandardError.ReadToEndAsync();
                var output = process.StandardOutput.ReadToEnd();
                process.WaitForExit(5000);
                if (output.Contains(" libx264 ", StringComparison.Ordinal))
                {
                    return true;
                }
                Logger.Information("ffmpeg has no libx264 encoder; videos play from the original files without scaled copies");
                return false;
            }
            catch (Exception ex)
            {
                Logger.Debug(ex, "ffmpeg encoder probe failed");
                return false;
            }
        }

        /// <summary>Deletes the least recently used copies above the size limit, and stale temp files.</summary>
        internal void Prune()
        {
            try
            {
                var dir = new DirectoryInfo(Directory);
                if (!dir.Exists) return;

                foreach (var temp in dir.EnumerateFiles("*.tmp"))
                {
                    if (DateTime.UtcNow - temp.LastWriteTimeUtc > TimeSpan.FromHours(1))
                    {
                        temp.Delete();
                    }
                }

                long total = 0;
                foreach (var file in dir.EnumerateFiles("*.mp4").OrderByDescending(f => f.LastWriteTimeUtc))
                {
                    total += file.Length;
                    if (total > MaxCacheBytes)
                    {
                        file.Delete();
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Debug(ex, "Video cache prune failed");
            }
        }
    }
}
