using InfoPanel.Models;
using InfoPanel.Sensors;
using Serilog;
using System.Collections.Concurrent;
using System.Globalization;

namespace InfoPanel.Services;

public class HwmonMonitor
{
    private static readonly Lazy<HwmonMonitor> _instance = new(() => new HwmonMonitor());
    public static HwmonMonitor Instance => _instance.Value;
    public static readonly ConcurrentDictionary<string, SensorReading> SENSORHASH = new(StringComparer.Ordinal);
    private readonly SysfsAccess _sysfs;
    private readonly HwmonCatalogScanner _scanner;
    private readonly Func<IEnumerable<HwmonSensorInfo>> _systemInfo;
    private readonly Action _systemPoll;
    private readonly Func<long> _clock;
    private readonly object _gate = new();
    private readonly object _lifecycle = new();
    private readonly AutoResetEvent _wake = new(false);
    private sealed class Worker
    {
        public readonly CancellationTokenSource Cancellation = new();
        public readonly ManualResetEventSlim Started = new();
        public Thread Thread = null!;
    }
    private Worker? _worker;
    private int _rescanRequested;
    private long _nextScan;
    private HwmonPollPlan _plan = new([]);
    private const int RescanIntervalMs = 10_000;
    public SensorIdResolver Resolver { get; }
    public SensorCatalogSnapshot? Catalog => Resolver.Snapshot;
    public event EventHandler<SensorCatalogSnapshot>? CatalogChanged;

    private HwmonMonitor() : this(new SysfsAccess(), () => LinuxSystemSensors.Instance.GetSensorInfoList(),
        () => LinuxSystemSensors.Instance.Poll(), systemDescriptors: () => LinuxSystemSensors.Instance.ScanDescriptors()) { }

    /// <summary>Injected instances default to no system providers and never initialize host GPU/proc providers.</summary>
    public HwmonMonitor(SysfsAccess sysfs, Func<IEnumerable<HwmonSensorInfo>>? systemSensorInfo = null,
        Action? pollSystemSensors = null, SensorIdResolver? resolver = null, Func<long>? clock = null,
        Func<IEnumerable<SensorDescriptor>>? systemDescriptors = null)
    {
        _sysfs = sysfs;
        _scanner = new(sysfs, systemDescriptors);
        _systemInfo = systemSensorInfo ?? (() => []);
        _systemPoll = pollSystemSensors ?? (() => { });
        _clock = clock ?? (() => Environment.TickCount64);
        Resolver = resolver ?? new();
    }

    public void Start(int intervalMs = 1000)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(intervalMs);
        Worker worker;
        lock (_lifecycle)
        {
            if (_worker != null) return;
            worker = new Worker();
            Interlocked.Exchange(ref _rescanRequested, 1);
            worker.Thread = new Thread(() => Work(intervalMs, worker)) { IsBackground = true, Name = "Hwmon catalog/poll worker" };
            _worker = worker;
            worker.Thread.Start();
        }
        // Preserve synchronous startup sampling; no subscriber runs under the lifecycle lock.
        worker.Started.Wait();
        Log.Information("HwmonMonitor started with {Interval}ms interval", intervalMs);
    }

    public void Stop()
    {
        Worker? worker;
        lock (_lifecycle)
        {
            worker = _worker;
            worker?.Cancellation.Cancel();
            _wake.Set();
        }
        // Never join under a lock that a catalog subscriber can enter.
        if (worker?.Thread != Thread.CurrentThread) worker?.Thread.Join();
        lock (_lifecycle)
        {
            if (_worker != null && _worker != worker) return; // A new run already owns the readings.
            lock (_gate)
            {
                // Only retire this catalog's hardware keys. Other providers own system/... readings.
                foreach (var entry in _plan.Entries) SENSORHASH.TryRemove(entry.StableId, out _);
            }
        }
    }

    public void RequestRescan()
    {
        Interlocked.Exchange(ref _rescanRequested, 1);
        _wake.Set();
    }

    private void Work(int intervalMs, Worker worker)
    {
        try
        {
            RunCycle(forcePollAll: true);
            worker.Started.Set();
            var nextPoll = _clock() + intervalMs;
            while (!worker.Cancellation.IsCancellationRequested)
            {
                var delay = (int)Math.Clamp(Math.Min(nextPoll, _nextScan) - _clock(), 0, int.MaxValue);
                _wake.WaitOne(delay);
                if (worker.Cancellation.IsCancellationRequested) break;
                var poll = _clock() >= nextPoll;
                RunCycle(poll: poll);
                if (poll) nextPoll = _clock() + intervalMs;
            }
        }
        catch (Exception ex) { Log.Error(ex, "Hwmon worker failed"); }
        finally
        {
            worker.Started.Set();
            lock (_lifecycle) { if (_worker == worker) _worker = null; }
        }
    }

    // Deterministic tick seam: tests advance an injected clock without sleeping or starting host providers.
    internal void RunCycle(bool forcePollAll = false, bool poll = true)
    {
        SensorCatalogSnapshot? changed = null;
        lock (_gate)
        {
            if (Interlocked.Exchange(ref _rescanRequested, 0) != 0 || Catalog == null || _clock() >= _nextScan)
            {
                try
                {
                    var scan = _scanner.Scan(Catalog);
                    if (!scan.IsIdentical)
                    {
                        foreach (var old in Catalog?.Descriptors ?? [])
                        {
                            if (!scan.Snapshot.StableIdIndex.TryGetValue(old.StableId, out var replacement)
                                || old.RealPath != replacement.RealPath || old.ValuePath != replacement.ValuePath
                                || old.Unit != replacement.Unit || old.Divisor != replacement.Divisor)
                                SENSORHASH.TryRemove(old.StableId, out _);
                        }
                        _plan = scan.PollPlan;
                        Resolver.Publish(scan.Snapshot);
                        changed = scan.Snapshot;
                        foreach (var ambiguity in changed.Ambiguities)
                            Log.Warning("Ambiguous sensor identity {Id}: {Reason}; paths: {Paths}", ambiguity.StableId,
                                ambiguity.Reason, string.Join(", ", ambiguity.Candidates.Select(d => d.ValuePath)));
                    }
                }
                catch (Exception ex) { Log.Warning(ex, "Hwmon discovery failed; retaining previous catalog"); }
                _nextScan = _clock() + RescanIntervalMs;
            }
            if (poll) Poll(forcePollAll);
        }
        // Subscribers see a complete publication and are never called under the poll/scan lock.
        if (changed != null)
        {
            try { CatalogChanged?.Invoke(this, changed); }
            catch (Exception ex) { Log.Warning(ex, "Hwmon catalog subscriber failed"); }
        }
    }

    private void Poll(bool forcePollAll)
    {
        SensorDemand.RebuildIfDue();
        foreach (var entry in _plan.Entries)
        {
            if (!forcePollAll && !SensorDemand.IsHwmonUsed(entry.StableId)) continue;
            string? raw;
            try { raw = _sysfs.ReadValue(entry.ValuePath); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Debug(ex, "Hwmon value read failed for {Id}", entry.StableId);
                raw = null;
            }
            if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) || !double.IsFinite(number))
            {
                SENSORHASH.TryRemove(entry.StableId, out _);
                continue;
            }
            var value = number / entry.Divisor;
            if (SENSORHASH.TryGetValue(entry.StableId, out var existing))
            {
                var min = Math.Min(existing.ValueMin, value);
                var max = Math.Max(existing.ValueMax, value);
                SENSORHASH[entry.StableId] = new SensorReading(min, max, (min + max) / 2, value, entry.Unit);
            }
            else SENSORHASH[entry.StableId] = new SensorReading(value, value, value, value, entry.Unit);
        }
        var forced = SensorDemand.ForcePollAll;
        if (forcePollAll) SensorDemand.ForcePollAll = true;
        try { _systemPoll(); }
        catch (Exception ex) { Log.Debug(ex, "LinuxSystemSensors poll error"); }
        finally { if (forcePollAll) SensorDemand.ForcePollAll = forced; }
    }

    public static List<HwmonSensorInfo> GetOrderedList() => GetOrderedList(Instance);
    public static List<HwmonSensorInfo> GetOrderedList(HwmonMonitor monitor)
    {
        var result = (monitor.Catalog?.Descriptors ?? []).Select(d => new HwmonSensorInfo
        {
            SensorId = d.StableId, DeviceName = d.ChipDisplayName, ChipKey = d.ChipKey,
            Label = d.Label, Category = d.Category, Unit = d.Unit, IdentityStrength = d.IdentityStrength,
            IsAmbiguous = d.IsAmbiguous, LegacyAlias = d.LegacyAlias
        }).ToList();
        result.AddRange(monitor._systemInfo().Where(s => !SensorId.IsMigratedSystemFamily(s.SensorId)));
        return result;
    }
}

public class HwmonSensorInfo
{
    public string SensorId { get; set; } = "";
    public string DeviceName { get; set; } = "";
    public string ChipKey { get; set; } = "";
    public string Category { get; set; } = "";
    public string Label { get; set; } = "";
    public string Unit { get; set; } = "";
    public SensorIdentityStrength IdentityStrength { get; set; }
    public bool IsAmbiguous { get; set; }
    public string? LegacyAlias { get; set; }
}
