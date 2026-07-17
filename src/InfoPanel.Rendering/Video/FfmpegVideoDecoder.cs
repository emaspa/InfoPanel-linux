using Serilog;
using SkiaSharp;
using System.Diagnostics;
using System.Text.Json;

namespace InfoPanel.Video
{
    /// <summary>
    /// Video frame source backed by the system ffmpeg binary: decodes to raw BGRA over
    /// a pipe at native speed (-re) with infinite loop for files, and exposes the most
    /// recent frame. Chosen over LibVLCSharp so no extra native library is required -
    /// v1 already depended on the ffmpeg binary for video conversion.
    /// </summary>
    public sealed class FfmpegVideoDecoder : IDisposable
    {
        private static readonly ILogger Logger = Log.ForContext<FfmpegVideoDecoder>();

        private static readonly Lazy<bool> _available = new(() =>
            ProbeBinary("ffmpeg") && ProbeBinary("ffprobe"));

        public static bool IsAvailable => _available.Value;

        private readonly string _source;
        private readonly bool _isLive;
        private readonly CancellationTokenSource _cts = new();
        private readonly Lock _frameLock = new();

        private Process? _process;
        private Thread? _readThread;
        private SKBitmap? _front;
        private SKBitmap? _back;
        private bool _frameReady;
        private bool _disposed;

        public int Width { get; }
        public int Height { get; }
        public double FrameRate { get; }
        public TimeSpan? Duration { get; }

        private FfmpegVideoDecoder(string source, bool isLive, int width, int height, double frameRate, TimeSpan? duration)
        {
            _source = source;
            _isLive = isLive;
            Width = width;
            Height = height;
            FrameRate = frameRate;
            Duration = duration;
        }

        /// <summary>Probes the source and starts decoding. Returns null when the source has no video stream or ffmpeg is missing.</summary>
        public static FfmpegVideoDecoder? Open(string source)
        {
            if (!IsAvailable)
            {
                Logger.Warning("ffmpeg/ffprobe not found on PATH - video playback disabled");
                return null;
            }

            var isLive = source.StartsWith("rtsp://", StringComparison.OrdinalIgnoreCase)
                || source.StartsWith("rtsps://", StringComparison.OrdinalIgnoreCase);

            try
            {
                var probe = RunProbe(source, isLive);
                if (probe == null)
                {
                    return null;
                }

                var (width, height, fps, duration) = probe.Value;
                var decoder = new FfmpegVideoDecoder(source, isLive, width, height, fps, duration);
                decoder.Start();
                return decoder;
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Failed to open video {Source}", source);
                return null;
            }
        }

        private static (int Width, int Height, double Fps, TimeSpan? Duration)? RunProbe(string source, bool isLive)
        {
            var args = $"-v quiet -select_streams v:0 -show_entries stream=width,height,avg_frame_rate:format=duration -of json";
            if (isLive)
            {
                args = "-rtsp_transport tcp " + args;
            }

            using var probe = Process.Start(new ProcessStartInfo("ffprobe", $"{args} \"{source}\"")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            });

            if (probe == null) return null;

            var json = probe.StandardOutput.ReadToEnd();
            if (!probe.WaitForExit(10_000))
            {
                try { probe.Kill(); } catch { }
                Logger.Warning("ffprobe timed out for {Source}", source);
                return null;
            }

            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("streams", out var streams) || streams.GetArrayLength() == 0)
            {
                Logger.Warning("No video stream found in {Source}", source);
                return null;
            }

            var stream = streams[0];
            var width = stream.GetProperty("width").GetInt32();
            var height = stream.GetProperty("height").GetInt32();

            double fps = 30;
            if (stream.TryGetProperty("avg_frame_rate", out var rate) && rate.GetString() is string rateStr)
            {
                var parts = rateStr.Split('/');
                if (parts.Length == 2 && double.TryParse(parts[0], out var num) && double.TryParse(parts[1], out var den) && den > 0)
                {
                    fps = num / den;
                }
            }

            TimeSpan? duration = null;
            if (doc.RootElement.TryGetProperty("format", out var format)
                && format.TryGetProperty("duration", out var dur)
                && double.TryParse(dur.GetString(), out var seconds))
            {
                duration = TimeSpan.FromSeconds(seconds);
            }

            return (width, height, fps, duration);
        }

        private void Start()
        {
            _readThread = new Thread(ReadLoop)
            {
                IsBackground = true,
                Name = $"ffmpeg-video-{Path.GetFileName(_source)}"
            };
            _readThread.Start();
        }

        private void ReadLoop()
        {
            var frameBytes = Width * Height * 4;
            var buffer = new byte[frameBytes];
            var token = _cts.Token;

            while (!token.IsCancellationRequested)
            {
                try
                {
                    var input = _isLive
                        ? $"-rtsp_transport tcp -i \"{_source}\""
                        : $"-stream_loop -1 -re -i \"{_source}\"";

                    _process = Process.Start(new ProcessStartInfo("ffmpeg",
                        $"-hide_banner -loglevel error -nostdin {input} -an -sn -f rawvideo -pix_fmt bgra pipe:1")
                    {
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                    });

                    if (_process == null)
                    {
                        return;
                    }

                    var stdout = _process.StandardOutput.BaseStream;

                    while (!token.IsCancellationRequested)
                    {
                        var read = 0;
                        while (read < frameBytes)
                        {
                            var n = stdout.Read(buffer, read, frameBytes - read);
                            if (n == 0)
                            {
                                throw new EndOfStreamException();
                            }

                            read += n;
                        }

                        PublishFrame(buffer);
                    }
                }
                catch (EndOfStreamException)
                {
                    Logger.Debug("ffmpeg stream ended for {Source}; restarting", _source);
                }
                catch (Exception ex) when (!token.IsCancellationRequested)
                {
                    Logger.Warning(ex, "Video decode error for {Source}; restarting", _source);
                }
                finally
                {
                    KillProcess();
                }

                if (!token.IsCancellationRequested)
                {
                    Thread.Sleep(2000); // restart backoff
                }
            }
        }

        private unsafe void PublishFrame(byte[] bgra)
        {
            lock (_frameLock)
            {
                if (_disposed) return;

                _back ??= new SKBitmap(Width, Height, SKColorType.Bgra8888, SKAlphaType.Opaque);

                fixed (byte* src = bgra)
                {
                    Buffer.MemoryCopy(src, (void*)_back.GetPixels(), bgra.Length, bgra.Length);
                }

                (_front, _back) = (_back, _front);
                _frameReady = true;
            }
        }

        /// <summary>Runs <paramref name="access"/> with the most recent frame, if one has been decoded.</summary>
        public bool TryAccessFrame(Action<SKBitmap> access)
        {
            lock (_frameLock)
            {
                if (!_frameReady || _front == null || _disposed)
                {
                    return false;
                }

                access(_front);
                return true;
            }
        }

        private void KillProcess()
        {
            try
            {
                if (_process is { HasExited: false })
                {
                    _process.Kill(entireProcessTree: true);
                }

                _process?.Dispose();
            }
            catch
            {
            }
            finally
            {
                _process = null;
            }
        }

        public void Dispose()
        {
            lock (_frameLock)
            {
                if (_disposed) return;
                _disposed = true;
            }

            _cts.Cancel();
            KillProcess();
            _readThread?.Join(2000);
            _cts.Dispose();

            lock (_frameLock)
            {
                _front?.Dispose();
                _back?.Dispose();
                _front = null;
                _back = null;
            }
        }

        private static bool ProbeBinary(string name)
        {
            try
            {
                using var process = Process.Start(new ProcessStartInfo(name, "-version")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                });
                process?.WaitForExit(5000);
                return process?.ExitCode == 0;
            }
            catch
            {
                return false;
            }
        }
    }
}
