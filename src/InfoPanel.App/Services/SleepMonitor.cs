using Microsoft.Win32.SafeHandles;
using Serilog;
using System;
using System.Threading;
using System.Threading.Tasks;
using Tmds.DBus.Protocol;

namespace InfoPanel.Services
{
    /// <summary>
    /// Stops the panel devices before the system sleeps and restarts them after it
    /// resumes. The Windows app does this from SystemEvents.PowerModeChanged; on
    /// Linux the signal is logind's PrepareForSleep (see <see cref="LogindSleepMonitor"/>).
    ///
    /// Without this, a panel keeps receiving nothing while the process is frozen
    /// and its firmware can time out mid-stream. The ASRock Steel Legend 360 goes
    /// dark after about a minute and then ignores frames until a full power cycle
    /// (reported on PR #10), so the panels must be put to sleep cleanly before the
    /// suspend, which is what the delay inhibitor buys time for.
    /// </summary>
    public sealed class SleepMonitor : IDisposable
    {
        private static readonly ILogger Logger = Log.ForContext<SleepMonitor>();

        private readonly Func<Task> _beforeSleep;
        private readonly Func<Task> _afterResume;
        private readonly Func<Task<IDisposable?>> _takeInhibitor;
        private readonly TimeSpan _resumeDelay;
        private readonly SemaphoreSlim _gate = new(1, 1);
        private IDisposable? _inhibitor;
        private bool _sleeping;
        private bool _disposed;

        public SleepMonitor(Func<Task> beforeSleep, Func<Task> afterResume,
            Func<Task<IDisposable?>> takeInhibitor, TimeSpan? resumeDelay = null)
        {
            _beforeSleep = beforeSleep;
            _afterResume = afterResume;
            _takeInhibitor = takeInhibitor;
            _resumeDelay = resumeDelay ?? TimeSpan.FromSeconds(1);
        }

        /// <summary>True while a sleep is in progress (between the two signals).</summary>
        public bool IsSleeping => _sleeping;

        /// <summary>Takes the initial delay inhibitor. Safe to call again after a resume.</summary>
        public async Task ArmAsync()
        {
            await _gate.WaitAsync();
            try { await TakeInhibitorLockedAsync(); }
            finally { _gate.Release(); }
        }

        /// <summary>
        /// Handles a PrepareForSleep(sleeping) signal. Signals are serialized; a
        /// repeated signal for the current state is ignored.
        /// </summary>
        public async Task HandleAsync(bool sleeping)
        {
            await _gate.WaitAsync();
            try
            {
                if (_disposed || sleeping == _sleeping) return;
                _sleeping = sleeping;

                if (sleeping)
                {
                    Logger.Information("System is going to sleep: stopping panel devices");
                    try { await _beforeSleep(); }
                    catch (Exception ex) { Logger.Warning(ex, "Stopping panel devices before sleep failed"); }
                    finally
                    {
                        // Releasing the inhibitor lets logind proceed with the suspend.
                        _inhibitor?.Dispose();
                        _inhibitor = null;
                    }
                }
                else
                {
                    Logger.Information("System resumed from sleep: restarting panel devices");
                    await TakeInhibitorLockedAsync();
                    // USB devices re-enumerate for a moment after resume; the device
                    // tasks retry on their own, the delay just avoids the first miss.
                    if (_resumeDelay > TimeSpan.Zero) await Task.Delay(_resumeDelay);
                    try { await _afterResume(); }
                    catch (Exception ex) { Logger.Warning(ex, "Restarting panel devices after resume failed"); }
                }
            }
            finally { _gate.Release(); }
        }

        private async Task TakeInhibitorLockedAsync()
        {
            if (_disposed || _inhibitor != null) return;
            try { _inhibitor = await _takeInhibitor(); }
            catch (Exception ex)
            {
                // Without the inhibitor the sleep still gets handled, just with no
                // guarantee that the panels are stopped before the suspend.
                Logger.Debug(ex, "Could not take the sleep delay inhibitor");
            }
        }

        public void Dispose()
        {
            _gate.Wait();
            try
            {
                _disposed = true;
                _inhibitor?.Dispose();
                _inhibitor = null;
            }
            finally { _gate.Release(); }
        }
    }

    /// <summary>
    /// Connects <see cref="SleepMonitor"/> to systemd-logind over the system bus:
    /// subscribes to org.freedesktop.login1.Manager.PrepareForSleep and takes a
    /// "sleep" delay inhibitor so the panels can be stopped before the suspend
    /// (logind waits up to InhibitDelayMaxSec, 5 s by default).
    /// </summary>
    public sealed class LogindSleepMonitor : IDisposable
    {
        private static readonly ILogger Logger = Log.ForContext<LogindSleepMonitor>();
        private static readonly TimeSpan BusTimeout = TimeSpan.FromSeconds(5);

        private const string Login1Service = "org.freedesktop.login1";
        private const string Login1Path = "/org/freedesktop/login1";
        private const string Login1Manager = "org.freedesktop.login1.Manager";

        private readonly SleepMonitor _monitor;
        private DBusConnection? _connection;
        private IDisposable? _subscription;

        public LogindSleepMonitor(Func<Task> beforeSleep, Func<Task> afterResume)
        {
            _monitor = new SleepMonitor(beforeSleep, afterResume, TakeInhibitorAsync);
        }

        public async Task StartAsync()
        {
            var address = DBusAddress.System;
            if (string.IsNullOrEmpty(address))
            {
                Logger.Information("No system bus address: panels will not be stopped for system sleep");
                return;
            }

            try
            {
                _connection = new DBusConnection(address);
                await _connection.ConnectAsync().AsTask().WaitAsync(BusTimeout);

                _subscription = await _connection.WatchSignalAsync<bool>(
                    Login1Service, Login1Path, Login1Manager, "PrepareForSleep",
                    (Message message, object? _) => message.GetBodyReader().ReadBool(),
                    (Exception? error, bool sleeping) =>
                    {
                        if (error != null)
                        {
                            Logger.Debug(error, "PrepareForSleep subscription ended");
                            return;
                        }
                        _ = Task.Run(() => _monitor.HandleAsync(sleeping));
                    },
                    null, false, ObserverFlags.None).AsTask().WaitAsync(BusTimeout);

                await _monitor.ArmAsync();
                Logger.Information("Listening for system sleep via logind");
            }
            catch (Exception ex)
            {
                Logger.Information("logind not reachable ({Reason}): panels will not be stopped for system sleep", ex.Message);
                Dispose();
            }
        }

        private async Task<IDisposable?> TakeInhibitorAsync()
        {
            if (_connection == null) return null;

            var handle = await _connection.CallMethodAsync<SafeFileHandle>(BuildInhibitMessage(_connection),
                (Message message, object? _) => message.GetBodyReader().ReadHandle<SafeFileHandle>(), null)
                .WaitAsync(BusTimeout);
            Logger.Debug("Took logind sleep delay inhibitor");
            return handle;
        }

        // MessageWriter is a ref struct, so the message is built outside the async method.
        private static MessageBuffer BuildInhibitMessage(DBusConnection connection)
        {
            using var writer = connection.GetMessageWriter();
            writer.WriteMethodCallHeader(Login1Service, Login1Path, Login1Manager, "Inhibit", "ssss", MessageFlags.None);
            writer.WriteString("sleep");
            writer.WriteString("InfoPanel");
            writer.WriteString("Putting LCD panels to sleep");
            writer.WriteString("delay");
            return writer.CreateMessage();
        }

        public void Dispose()
        {
            _subscription?.Dispose();
            _subscription = null;
            _monitor.Dispose();
            _connection?.Dispose();
            _connection = null;
        }
    }
}
