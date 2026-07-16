using Serilog;

namespace InfoPanel
{
    /// <summary>
    /// Start/stoppable background worker. Successor of v1's BackgroundTask with two
    /// deliberate fixes:
    /// - the start/stop lock is per-instance (v1's was a single process-wide static,
    ///   so one wedged USB device stalled start/stop of every task in the app), and
    /// - StopAsync gives the worker a bounded grace period; a task stuck on a hung
    ///   USB transfer is abandoned rather than blocking shutdown forever.
    /// </summary>
    public abstract class BackgroundTask
    {
        private static readonly ILogger Logger = Log.ForContext<BackgroundTask>();
        private static readonly TimeSpan StopGracePeriod = TimeSpan.FromSeconds(5);

        private readonly SemaphoreSlim _startStopSemaphore = new(1, 1);

        private CancellationTokenSource? _cts;
        private Task? _task;

        protected BackgroundTask() { }

        protected CancellationToken? CancellationToken => _cts?.Token;

        public bool IsRunning => _task is not null && !_task.IsCompleted && _cts is not null && !_cts.IsCancellationRequested;

        protected bool _shutdown = false;

        public async Task StartAsync(CancellationToken? token = null)
        {
            await _startStopSemaphore.WaitAsync();
            _shutdown = false;
            try
            {
                if (IsRunning) return;

                Logger.Debug("{TaskName} starting initialization", GetType().Name);

                _cts = token == null
                    ? new CancellationTokenSource()
                    : CancellationTokenSource.CreateLinkedTokenSource(token.Value);
                _task = Task.Run(() => DoWorkAsync(_cts.Token), _cts.Token);
            }
            finally
            {
                _startStopSemaphore.Release();
            }
        }

        public virtual async Task StopAsync(bool shutdown = false)
        {
            Logger.Debug("{TaskName} stopping", GetType().Name);

            await _startStopSemaphore.WaitAsync();
            _shutdown = shutdown;
            try
            {
                if (_cts is null || _task is null) return;

                _cts.Cancel();

                try
                {
                    var completed = await Task.WhenAny(_task, Task.Delay(StopGracePeriod));
                    if (completed != _task)
                    {
                        Logger.Warning("{TaskName} did not stop within {Grace}; abandoning task", GetType().Name, StopGracePeriod);
                    }
                    else
                    {
                        await _task; // propagate/observe faults
                    }
                }
                catch (OperationCanceledException)
                {
                    // Task was canceled
                }
                catch (Exception ex)
                {
                    Logger.Error(ex, "Exception during task stop for {TaskName}", GetType().Name);
                }
                finally
                {
                    DisposeResources();
                }
            }
            finally
            {
                _startStopSemaphore.Release();
            }

            Logger.Debug("{TaskName} stopped", GetType().Name);
        }

        protected abstract Task DoWorkAsync(CancellationToken token);

        private void DisposeResources()
        {
            Logger.Debug("Disposing resources for {TaskName}", GetType().Name);
            _cts?.Dispose();
            _cts = null;
            _task = null;
        }
    }
}
