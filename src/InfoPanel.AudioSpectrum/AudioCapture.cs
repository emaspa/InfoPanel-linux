using Serilog;
using System.Diagnostics;

namespace InfoPanel.AudioSpectrum
{
    /// <summary>
    /// System-audio capture for Linux. The Windows build uses WASAPI loopback; here we
    /// read the default sink's monitor source via `parec` (PulseAudio, or PipeWire's
    /// pipewire-pulse shim) as float32 mono 48 kHz. AudioDevice config selects another
    /// source by partial name match against `pactl list short sources`.
    /// </summary>
    internal class AudioCapture : IDisposable
    {
        private static readonly ILogger Logger = Log.ForContext<AudioCapture>();

        private const int SampleRateHz = 48000;
        private const int RingSize = 16384;   // ~340 ms of mono audio
        private const int WindowSize = 4096;  // samples handed to the FFT

        private readonly float[] _ring = new float[RingSize];
        private readonly object _ringLock = new();
        private int _ringPos;
        private long _totalWritten;

        private Process? _process;
        private Thread? _readThread;
        private volatile bool _running;
        private long _dataCount;
        private volatile float _peak;
        private string _deviceName = "None";
        private string _lastError = "";
        private bool _disposed;

        public int SampleRate => SampleRateHz;
        public bool IsCapturing => _process is { HasExited: false };
        public long DataReceivedCount => Interlocked.Read(ref _dataCount);
        public float PeakLevel => _peak;
        public string DeviceName => _deviceName;
        public string LastError => _lastError;

        public void Start(string? deviceName = null)
        {
            Stop();
            _lastError = "";

            try
            {
                var source = ResolveSource(deviceName);

                var psi = new ProcessStartInfo("parec")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                };
                psi.ArgumentList.Add("--format=float32le");
                psi.ArgumentList.Add($"--rate={SampleRateHz}");
                psi.ArgumentList.Add("--channels=1");
                psi.ArgumentList.Add("--latency-msec=30");
                psi.ArgumentList.Add($"--device={source}");

                _process = Process.Start(psi);
                if (_process == null)
                {
                    _lastError = "Failed to start parec";
                    return;
                }

                _deviceName = source;
                _running = true;
                _readThread = new Thread(() => ReadLoop(_process)) { IsBackground = true, Name = "AudioSpectrum-Capture" };
                _readThread.Start();

                Logger.Information("AudioCapture: parec started on source '{Source}'", source);
            }
            catch (System.ComponentModel.Win32Exception)
            {
                _lastError = "parec not found — install pulseaudio-utils";
                Logger.Warning("AudioCapture: {Error}", _lastError);
                _process = null;
            }
            catch (Exception ex)
            {
                _lastError = $"{ex.GetType().Name}: {ex.Message}";
                Logger.Warning(ex, "AudioCapture: start failed");
                _process?.Dispose();
                _process = null;
            }
        }

        /// <summary>
        /// Empty device → monitor of the default sink (what you hear). Otherwise the
        /// first pactl source whose name or description contains the given text.
        /// </summary>
        private static string ResolveSource(string? deviceName)
        {
            if (!string.IsNullOrWhiteSpace(deviceName))
            {
                try
                {
                    var list = RunPactl("list", "short", "sources");
                    foreach (var line in list.Split('\n'))
                    {
                        var parts = line.Split('\t');
                        if (parts.Length >= 2 && parts[1].Contains(deviceName, StringComparison.OrdinalIgnoreCase))
                        {
                            return parts[1];
                        }
                    }
                }
                catch { /* fall through to literal name */ }

                return deviceName;
            }

            try
            {
                var sink = RunPactl("get-default-sink").Trim();
                if (sink.Length > 0)
                {
                    return $"{sink}.monitor";
                }
            }
            catch { }

            return "@DEFAULT_MONITOR@";
        }

        private static string RunPactl(params string[] args)
        {
            var psi = new ProcessStartInfo("pactl") { RedirectStandardOutput = true, UseShellExecute = false };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(3000);
            return output;
        }

        private void ReadLoop(Process process)
        {
            var buffer = new byte[WindowSize];
            var stream = process.StandardOutput.BaseStream;

            try
            {
                while (_running && !process.HasExited)
                {
                    int read = stream.Read(buffer, 0, buffer.Length);
                    if (read <= 0) break;

                    int sampleCount = read / 4;
                    float chunkPeak = 0;
                    lock (_ringLock)
                    {
                        for (int i = 0; i < sampleCount; i++)
                        {
                            float sample = BitConverter.ToSingle(buffer, i * 4);
                            _ring[_ringPos] = sample;
                            _ringPos = (_ringPos + 1) % RingSize;
                            var abs = MathF.Abs(sample);
                            if (abs > chunkPeak) chunkPeak = abs;
                        }

                        _totalWritten += sampleCount;
                    }

                    _peak = chunkPeak;
                    Interlocked.Increment(ref _dataCount);
                }
            }
            catch (Exception ex)
            {
                if (_running)
                {
                    Logger.Debug(ex, "AudioCapture: read loop ended");
                }
            }

            if (_running)
            {
                try
                {
                    var err = process.StandardError.ReadToEnd().Trim();
                    if (err.Length > 0)
                    {
                        _lastError = err.Split('\n')[^1];
                        Logger.Warning("AudioCapture: parec exited: {Error}", _lastError);
                    }
                }
                catch { }
            }
        }

        public void Stop()
        {
            _running = false;

            try
            {
                if (_process is { HasExited: false })
                {
                    _process.Kill();
                }
            }
            catch { }

            _process?.Dispose();
            _process = null;
            _readThread?.Join(500);
            _readThread = null;

            lock (_ringLock)
            {
                Array.Clear(_ring);
                _ringPos = 0;
                _totalWritten = 0;
            }

            _peak = 0;
            _deviceName = "None";
        }

        /// <summary>Returns the most recent WindowSize samples in chronological order.</summary>
        public float[] GetLatestSamples()
        {
            lock (_ringLock)
            {
                if (_totalWritten == 0) return [];

                int available = (int)Math.Min(_totalWritten, WindowSize);
                var result = new float[available];
                int start = (_ringPos - available + RingSize) % RingSize;
                for (int i = 0; i < available; i++)
                {
                    result[i] = _ring[(start + i) % RingSize];
                }

                return result;
            }
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                Stop();
            }
        }
    }
}
