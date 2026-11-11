using InfoPanel.Utils;
using Serilog;
using SkiaSharp;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;

namespace InfoPanel.Video
{
    /// <summary>
    /// Video frame source backed by the system ffmpeg binary: decodes to raw RGBA over
    /// a pipe, paced to the source frame rate, with infinite loop for files, and exposes
    /// the most recent frame. Chosen over LibVLCSharp so no extra native library is
    /// required - v1 already depended on the ffmpeg binary for video conversion.
    /// <para>
    /// ffmpeg starts lazily on the first <see cref="RequestOutputSize"/> and scales file
    /// sources to the size consumers draw (see <see cref="VideoOutputSizePolicy"/>), so
    /// the common case needs no per-frame resize in the app. RGBA matches the Skia
    /// surfaces InfoPanel draws into, which turns the draw into a plain copy.
    /// </para>
    /// </summary>
    public sealed class FfmpegVideoDecoder : IDisposable
    {
        private static readonly ILogger Logger = Log.ForContext<FfmpegVideoDecoder>();

        private static readonly Lazy<bool> _available = new(() =>
            ProbeBinary("ffmpeg") && ProbeBinary("ffprobe"));

        public static bool IsAvailable => _available.Value;

        /// <summary>No complete frame for this long while ffmpeg runs = hung (seen with
        /// a vaapi driver deadlock that ignored SIGTERM and printed nothing).</summary>
        internal static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(5);

        // RTSP connects can legitimately take longer than a file decoder's first frame.
        private static readonly TimeSpan LiveConnectTimeout = TimeSpan.FromSeconds(15);

        private const int RestartBackoffMs = 2000;
        private const int StderrTailLines = 20;

        private static readonly ConcurrentDictionary<FfmpegVideoDecoder, byte> _instances = new();

        // Sources whose hardware decode stalled once; they stay on software decode for
        // the rest of the session, across LockedImage reloads.
        private static readonly ConcurrentDictionary<string, byte> _hwaccelBlocked = new(StringComparer.Ordinal);

        private static volatile bool _hardwareDecode;

        /// <summary>
        /// Adds <c>-hwaccel auto</c> to ffmpeg (Settings.HardwareVideoDecode, pushed by the
        /// host). Off by default: it saves little CPU, wakes a sleeping dGPU on hybrid
        /// laptops, and one driver deadlocked under it. Changing it restarts running
        /// decoders so the toggle applies immediately.
        /// </summary>
        public static bool HardwareDecode
        {
            get => _hardwareDecode;
            set
            {
                if (_hardwareDecode == value) return;
                _hardwareDecode = value;
                foreach (var decoder in _instances.Keys)
                {
                    decoder.RequestRestart("hardware decode setting changed");
                }
            }
        }

        private readonly string _source;
        private readonly bool _isLive;
        private readonly CancellationTokenSource _cts = new();
        private readonly VideoOutputSizePolicy _sizePolicy;
        private readonly TimeSpan _stallTimeout;

        // Guards _front/_frameReady against the render threads; held only for a swap
        // or for the duration of a consumer's TryAccessFrame callback.
        private readonly Lock _frameLock = new();
        private SKBitmap? _front;
        private bool _frameReady;
        private long _frameVersion;

        // Read thread only: ffmpeg writes straight into this bitmap, then it is
        // swapped with _front, so publishing a frame needs no copy.
        private SKBitmap? _back;

        // Guards the process lifecycle shared by the read thread, the render threads
        // (size requests), the watchdog timer and Dispose. A Process referenced by
        // _process is never disposed, so it can be killed under this lock.
        private readonly Lock _stateLock = new();
        private Thread? _readThread;
        private Timer? _watchdog;
        private Process? _process;
        private bool _processHwaccel;
        private StderrTail? _processStderr;
        private long _generation;
        private string? _stopReason;
        private bool _stoppedByWatchdog;
        private volatile bool _disposed;

        private long _lastFrameMs;
        private volatile bool _frameSinceStart;

        /// <summary>Bumps every time a new decoded frame is published. Lets
        /// consumers cache per-frame derived work (e.g. resizes) instead of
        /// redoing it on every draw.</summary>
        public long FrameVersion => Interlocked.Read(ref _frameVersion);

        /// <summary>Probed source size; item layout uses it, so it is valid before the first frame.</summary>
        public int Width { get; }

        /// <summary>Probed source size; item layout uses it, so it is valid before the first frame.</summary>
        public int Height { get; }

        /// <summary>Size ffmpeg currently outputs; 0 until the first <see cref="RequestOutputSize"/>.
        /// Frames from a previous size can still be served for a moment after a change.</summary>
        public int OutputWidth { get; private set; }

        /// <inheritdoc cref="OutputWidth"/>
        public int OutputHeight { get; private set; }

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
            _sizePolicy = new VideoOutputSizePolicy(width, height);

            // Very low frame rate sources (slideshows) legitimately go quiet longer.
            var interval = TimeSpan.FromSeconds(1.0 / (frameRate > 0.01 ? frameRate : 30));
            _stallTimeout = StallTimeout > interval * 3 ? StallTimeout : interval * 3;
        }

        /// <summary>
        /// Probes the source. Returns null when the source has no video stream or ffmpeg
        /// is missing. ffmpeg itself starts on the first <see cref="RequestOutputSize"/>.
        /// </summary>
        public static FfmpegVideoDecoder? Open(string source)
        {
            if (!IsAvailable)
            {
                Logger.Warning("ffmpeg/ffprobe not found on PATH - video playback disabled");
                return null;
            }

            var isLive = IsLiveSource(source);

            try
            {
                var probe = RunProbe(source, isLive);
                if (probe == null)
                {
                    return null;
                }

                var (width, height, fps, duration) = probe.Value;
                var decoder = new FfmpegVideoDecoder(source, isLive, width, height, fps, duration);
                _instances.TryAdd(decoder, 0);
                return decoder;
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Failed to open video {Source}", source);
                return null;
            }
        }

        internal static bool IsLiveSource(string source) =>
            source.StartsWith("rtsp://", StringComparison.OrdinalIgnoreCase)
            || source.StartsWith("rtsps://", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// ffmpeg command line. <paramref name="scale"/> null keeps the source size;
        /// live sources ignore it (a restart there is a multi-second reconnect).
        /// </summary>
        internal static List<string> BuildArguments(string source, bool isLive, (int Width, int Height)? scale, bool hwaccel)
        {
            List<string> args = ["-hide_banner", "-loglevel", "error", "-nostdin"];

            // Before -i: an input option. Falls back to software decode on its own when
            // the stream is unsupported ("Failed setup for format cuda").
            if (hwaccel)
            {
                args.AddRange(["-hwaccel", "auto"]);
            }

            // No -re for file sources: combined with -stream_loop it stalls at every
            // loop boundary (input timestamps reset, the -re wallclock does not) and
            // then bursts to catch up. Pacing happens in the read loop instead; the
            // pipe's backpressure throttles ffmpeg.
            if (isLive)
            {
                args.AddRange(["-rtsp_transport", "tcp"]);
            }
            else
            {
                args.AddRange(["-stream_loop", "-1"]);
            }

            // ffmpeg defaults to one decoder thread and one scaler thread per core.
            // Paced to 24-30 fps that buys nothing but contention: on a 32-thread CPU,
            // a 2400x1080 file at 1600x720 used 0.28 cores and 444 MB by default, and
            // 0.21 cores and 284 MB with these limits, which still decode a 40 Mbit/s
            // 4K60 file at 2.7x real time.
            args.AddRange(["-threads", "4", "-i", source, "-an", "-sn", "-filter_threads", "1"]);

            // Software scale also after hardware decode: scale_cuda needs even sizes and
            // cannot output RGBA on every build. Odd sizes are fine for swscale RGB output.
            if (!isLive && scale is { } size)
            {
                args.AddRange(["-vf", $"scale={size.Width}:{size.Height}:flags=bilinear"]);
            }

            args.AddRange(["-f", "rawvideo", "-pix_fmt", "rgba", "pipe:1"]);
            return args;
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

        /// <summary>
        /// Tells the decoder a consumer is about to draw at this size; call it on every
        /// draw. Starts ffmpeg on the first call. For files, a larger size that keeps
        /// being requested restarts ffmpeg at that size (never smaller, capped at the
        /// source); live sources always run at native size.
        /// </summary>
        public void RequestOutputSize(int width, int height)
        {
            if (width <= 0 || height <= 0) return;

            lock (_stateLock)
            {
                if (_disposed) return;

                bool changed;
                if (_isLive)
                {
                    changed = _readThread == null;
                    if (changed)
                    {
                        (OutputWidth, OutputHeight) = (Width, Height);
                    }
                }
                else
                {
                    changed = _sizePolicy.Request(width, height, TimeSpan.FromMilliseconds(Environment.TickCount64));
                    if (changed)
                    {
                        (OutputWidth, OutputHeight) = _sizePolicy.Output!.Value;
                    }
                }

                if (!changed) return;

                if (_readThread == null)
                {
                    StartLocked();
                }
                else
                {
                    StopProcessLocked($"output size {OutputWidth}x{OutputHeight}");
                }
            }
        }

        private void RequestRestart(string reason)
        {
            lock (_stateLock)
            {
                if (_disposed || _readThread == null) return;
                StopProcessLocked(reason);
            }
        }

        // Kills the running ffmpeg on purpose; the read loop restarts it without the
        // backoff and picks up the current OutputWidth/Height and hwaccel setting.
        private void StopProcessLocked(string reason)
        {
            _generation++;
            _stopReason ??= reason;
            if (_process != null)
            {
                KillQuietly(_process);
            }
        }

        private void StartLocked()
        {
            _readThread = new Thread(ReadLoop)
            {
                IsBackground = true,
                Name = $"ffmpeg-video-{Path.GetFileName(_source)}"
            };
            _readThread.Start();
            _watchdog = new Timer(_ => CheckStall(), null, 1000, 1000);
        }

        private void ReadLoop()
        {
            var token = _cts.Token;

            try
            {
                while (!token.IsCancellationRequested)
                {
                    int width, height;
                    bool hwaccel;
                    long generation;
                    lock (_stateLock)
                    {
                        width = OutputWidth;
                        height = OutputHeight;
                        hwaccel = _hardwareDecode && !_hwaccelBlocked.ContainsKey(_source);
                        generation = _generation;
                        _stopReason = null;
                        _stoppedByWatchdog = false;
                    }

                    var input = _source;
                    var scale = width == Width && height == Height ? ((int, int)?)null : (width, height);
                    if (scale != null && !_isLive && VideoScaleCache.Shared is { } cache
                        && cache.GetOrStart(_source, Width, Height, width, height, Duration,
                            () => RequestRestart("scaled copy ready"), token) is { } copy)
                    {
                        // Already at the output size: no scale filter needed.
                        input = copy;
                        scale = null;
                    }

                    var startInfo = new ProcessStartInfo("ffmpeg")
                    {
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                    };
                    // Same arena cap as the app: 299 -> 238 MB for a 2400x1080 file.
                    startInfo.Environment["GLIBC_TUNABLES"] = NativeHeap.ChildTunables(startInfo.Environment.TryGetValue("GLIBC_TUNABLES", out var tunables) ? tunables : null);
                    foreach (var arg in BuildArguments(input, _isLive, scale, hwaccel))
                    {
                        startInfo.ArgumentList.Add(arg);
                    }

                    var stderr = new StderrTail(_source);
                    Process? process = null;
                    Exception? error = null;
                    string? stopReason = null;
                    var byWatchdog = false;

                    try
                    {
                        process = new Process { StartInfo = startInfo };
                        // Drained continuously: an unread stderr pipe fills at 64 KB and
                        // then blocks ffmpeg mid-frame.
                        process.ErrorDataReceived += (_, e) => stderr.Add(e.Data);
                        process.Start();
                        process.BeginErrorReadLine();

                        Logger.Debug("ffmpeg started for {Source}: {Width}x{Height}, hwaccel {Hwaccel}, input {Input}",
                            _source, width, height, hwaccel, input);

                        lock (_stateLock)
                        {
                            _process = process;
                            _processHwaccel = hwaccel;
                            _processStderr = stderr;
                            Volatile.Write(ref _lastFrameMs, Environment.TickCount64);
                            _frameSinceStart = false;

                            // A size change or setting toggle landed while we were starting.
                            if (generation != _generation || token.IsCancellationRequested)
                            {
                                KillQuietly(process);
                            }
                        }

                        ReadFrames(process.StandardOutput.BaseStream, width, height, token);
                    }
                    catch (Exception ex)
                    {
                        error = ex;
                    }
                    finally
                    {
                        lock (_stateLock)
                        {
                            stopReason = _stopReason;
                            byWatchdog = _stoppedByWatchdog;
                            _process = null;
                            _processStderr = null;
                        }

                        if (process != null)
                        {
                            KillQuietly(process);
                            try { process.WaitForExit(1000); } catch { }
                        }
                    }

                    if (token.IsCancellationRequested)
                    {
                        process?.Dispose();
                        break;
                    }

                    if (stopReason != null)
                    {
                        // Intentional stop (size change, setting change) or watchdog; the
                        // watchdog already logged its own warning.
                        Logger.Debug("ffmpeg for {Source} stopped: {Reason}", _source, stopReason);
                    }
                    else
                    {
                        int? exitCode = null;
                        try { if (process is { HasExited: true }) exitCode = process.ExitCode; } catch { }
                        Logger.Warning(error, "ffmpeg exited unexpectedly for {Source} (exit code {ExitCode}); restarting. Last stderr:{Stderr}",
                            _source, exitCode, stderr.Format());
                    }

                    process?.Dispose();

                    if (stopReason == null || byWatchdog)
                    {
                        token.WaitHandle.WaitOne(RestartBackoffMs);
                    }
                }
            }
            finally
            {
                _back?.Dispose();
                _back = null;
            }
        }

        private unsafe void ReadFrames(Stream stdout, int width, int height, CancellationToken token)
        {
            var frameBytes = width * height * 4;
            var frameIntervalMs = FrameRate > 1 ? 1000.0 / FrameRate : 1000.0 / 30;
            var pace = Stopwatch.StartNew();
            long framesRead = 0;

            while (!token.IsCancellationRequested)
            {
                if (_back == null || _back.Width != width || _back.Height != height)
                {
                    _back?.Dispose();
                    _back = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque));
                }

                // Tightly packed (rowBytes == width * 4), matching ffmpeg's rawvideo layout.
                var target = new Span<byte>((void*)_back.GetPixels(), frameBytes);
                if (stdout.ReadAtLeast(target, frameBytes, throwOnEndOfStream: false) < frameBytes)
                {
                    return; // EOF: ffmpeg exited or was killed
                }

                Volatile.Write(ref _lastFrameMs, Environment.TickCount64);
                _frameSinceStart = true;

                // Pace to the source frame rate (replaces ffmpeg's -re).
                // Reset the reference on drift > 2 frames (slow reads,
                // suspend/resume) instead of bursting to catch up.
                framesRead++;
                var dueMs = framesRead * frameIntervalMs;
                var aheadMs = dueMs - pace.Elapsed.TotalMilliseconds;
                if (aheadMs > 2)
                {
                    Thread.Sleep((int)aheadMs);
                }
                else if (aheadMs < -2 * frameIntervalMs)
                {
                    pace.Restart();
                    framesRead = 0;
                }

                lock (_frameLock)
                {
                    if (_disposed) return;

                    (_front, _back) = (_back, _front);
                    _frameReady = true;
                    Interlocked.Increment(ref _frameVersion);
                }
            }
        }

        private void CheckStall()
        {
            string? stderr;
            bool disabledHwaccel;
            long silentMs;

            lock (_stateLock)
            {
                if (_disposed || _process == null || _stopReason != null)
                {
                    return;
                }

                var timeout = _isLive && !_frameSinceStart ? LiveConnectTimeout : _stallTimeout;
                silentMs = Environment.TickCount64 - Volatile.Read(ref _lastFrameMs);
                if (silentMs < timeout.TotalMilliseconds)
                {
                    return;
                }

                // SIGKILL (Process.Kill): the hung driver case ignored SIGTERM.
                _stopReason = $"no frame for {silentMs / 1000.0:0.0} s";
                _stoppedByWatchdog = true;
                disabledHwaccel = _processHwaccel && _hwaccelBlocked.TryAdd(_source, 0);
                stderr = _processStderr?.Format();
                KillQuietly(_process);
            }

            Logger.Warning("ffmpeg for {Source} produced no frame for {Seconds} s; killed and restarting{Fallback}. Last stderr:{Stderr}",
                _source, Math.Round(silentMs / 1000.0, 1),
                disabledHwaccel ? " with software decode for this source" : "", stderr);
        }

        /// <summary>Runs <paramref name="access"/> with the most recent frame, if one has been decoded.
        /// The frame is <see cref="SKColorType.Rgba8888"/>/opaque and may be any size up to
        /// the source size; it is reused after the callback returns, so copy what you keep.</summary>
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

        private static void KillQuietly(Process process)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
            }
        }

        public void Dispose()
        {
            Thread? readThread;
            lock (_stateLock)
            {
                if (_disposed) return;
                _disposed = true;
                _instances.TryRemove(this, out _);

                _watchdog?.Dispose();
                _watchdog = null;
                _cts.Cancel();
                _stopReason ??= "disposed";
                if (_process != null)
                {
                    KillQuietly(_process);
                }

                readThread = _readThread;
            }

            // The read thread owns and frees _back on exit.
            readThread?.Join(2000);

            lock (_frameLock)
            {
                _front?.Dispose();
                _front = null;
                _frameReady = false;
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

        /// <summary>Last lines ffmpeg wrote to stderr, for the warning on an unexpected exit.</summary>
        private sealed class StderrTail(string source)
        {
            private readonly Queue<string> _lines = new();

            public void Add(string? line)
            {
                if (string.IsNullOrEmpty(line)) return;

                Logger.Debug("ffmpeg [{Source}]: {Line}", source, line);
                lock (_lines)
                {
                    if (_lines.Count == StderrTailLines) _lines.Dequeue();
                    _lines.Enqueue(line);
                }
            }

            public string Format()
            {
                lock (_lines)
                {
                    return _lines.Count == 0 ? " (none)" : "\n  " + string.Join("\n  ", _lines);
                }
            }
        }
    }
}
