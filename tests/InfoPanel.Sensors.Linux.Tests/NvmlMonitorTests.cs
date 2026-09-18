using InfoPanel.Services;
using InfoPanel.Models;
using Xunit;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace InfoPanel.Sensors.Linux.Tests;

public class NvmlMonitorTests : IDisposable
{
    public NvmlMonitorTests() => HwmonMonitor.SENSORHASH.Clear();
    public void Dispose()
    {
        SensorDemand.DemandProvider = null;
        SensorDemand.ForcePollAll = false;
        HwmonMonitor.SENSORHASH.Clear();
    }

    [Fact]
    public void UnchangedGpuInitializesNativeApisOnceAcrossCatalogRescans()
    {
        using var fs = new FakeSysfs();
        long now = 0;
        var native = new NvidiaGpu();
        var api = new CountingNvApi();
        var creations = 0;
        var gpu = new NvmlMonitor(native, () => { creations++; return api; });
        var monitor = new HwmonMonitor(fs, clock: () => now, systemDescriptors: gpu.ScanDescriptors);
        try
        {
            for (var cycle = 0; cycle < 4; cycle++)
            {
                now = cycle * 10_000;
                monitor.RunCycle(poll: false);
                Assert.Contains(monitor.Catalog!.Descriptors, d =>
                    d.StableId == "system/gpu/gpu-uuid+GPU-test~pci+0000-01-00.0/temperature");
            }
            Assert.Equal(4, native.Scans);
            Assert.Equal(1, native.Initializations);
            Assert.Equal(1, creations);
            Assert.Equal(1, api.MaskProbes);
            Assert.Equal(0, api.Disposals);
        }
        finally { gpu.Shutdown(); }
        Assert.Equal(1, api.Disposals);
        Assert.Equal(1, native.Shutdowns);
    }

    [Fact]
    public void BlackwellUnavailableHotspotLogsOnceWhileOtherReadingsContinue()
    {
        using var h = new Harness();
        var sink = new LogSink();
        var oldLog = Log.Logger;
        using var logger = new LoggerConfiguration().WriteTo.Sink(sink).CreateLogger();
        Log.Logger = logger;
        try
        {
            for (var time = 0; time <= 120_000; time += 1000) h.Tick(time);
            Assert.Single(h.Apis);
            Assert.Equal(13, h.Native.Scans);
            Assert.Equal(1, h.Apis[0].MaskProbes);
            Assert.Equal(121, h.Apis[0].Reads);
            Assert.Equal(121, h.Apis[0].VoltageReads);
            // The register read is refused without CAP_SYS_ADMIN and the driver logs
            // every attempt to dmesg (#11): one setup probe, then never again.
            Assert.Equal(1, h.Apis[0].RegisterReads);
            Assert.Single(sink.Messages, m => m.StartsWith("NvApi: GPU 0 matched"));
            Assert.Single(sink.Messages, m => m.Contains("hotspot temperature unavailable"));
            Assert.False(HwmonMonitor.SENSORHASH.ContainsKey(Harness.Prefix + "/temperature_hotspot"));
            Assert.Equal(60, HwmonMonitor.SENSORHASH[Harness.Prefix + "/temperature_vram"].ValueNow);
            Assert.Equal(0.95, HwmonMonitor.SENSORHASH[Harness.Prefix + "/voltage"].ValueNow);
        }
        finally { Log.Logger = oldLog; }
    }

    [Fact]
    public void BlackwellHotspotRegisterIsPolledWhenTheProbeSucceeds()
    {
        using var h = new Harness();
        h.Create = () =>
        {
            var api = new CountingNvApi { HotspotRegisterValue = 71 };
            h.Apis.Add(api);
            return api;
        };
        for (var time = 0; time <= 10_000; time += 1000) h.Tick(time);
        Assert.Single(h.Apis);
        Assert.Equal(11, h.Apis[0].Reads);
        Assert.Equal(12, h.Apis[0].RegisterReads); // one setup probe and 11 polls
        Assert.Equal(71, HwmonMonitor.SENSORHASH[Harness.Prefix + "/temperature_hotspot"].ValueNow);
        Assert.Equal(60, HwmonMonitor.SENSORHASH[Harness.Prefix + "/temperature_vram"].ValueNow);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingDriverOrFailedNvmlInitBacksOffThenRecovers(bool missingLibrary)
    {
        using var h = new Harness();
        h.Native.MissingLibrary = missingLibrary;
        h.Native.InitResult = NvmlReturn.DriverNotLoaded;
        for (var time = 0; time < 60_000; time += 10_000) h.Tick(time);
        Assert.Equal(1, h.Native.Initializations);
        Assert.Equal(0, h.Native.Scans);
        Assert.Empty(h.Apis);
        Assert.Empty(h.Monitor.Catalog!.Descriptors);
        h.Native.MissingLibrary = false;
        h.Native.InitResult = NvmlReturn.Success;
        h.Tick(60_000);
        Assert.Equal(2, h.Native.Initializations);
        Assert.Single(h.Apis);
        h.Gpu.Shutdown();
        h.Gpu.Shutdown();
        Assert.Equal(1, h.Native.Shutdowns); // failed init owns no NVML reference
        Assert.Equal(1, h.Apis[0].Disposals);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("factory throws")]
    [InlineData("setup throws")]
    public void NvApiFailureBacksOffWithoutInterruptingNvmlReadings(string failure)
    {
        using var h = new Harness();
        var attempts = 0;
        var partialApis = new List<CountingNvApi>();
        h.Create = () =>
        {
            attempts++;
            if (failure == "factory throws") throw new InvalidOperationException("setup failed");
            if (failure == "missing") return null;
            var api = new CountingNvApi { FailMask = true };
            partialApis.Add(api);
            return api;
        };
        for (var time = 0; time < 60_000; time += 10_000) h.Tick(time);
        Assert.Equal(1, attempts);
        Assert.Equal(1, h.Native.Initializations);
        Assert.Equal(50, HwmonMonitor.SENSORHASH[Harness.Prefix + "/temperature"].ValueNow);
        h.Tick(60_000);
        Assert.Equal(2, attempts);
        Assert.All(partialApis, a => Assert.Equal(1, a.Disposals));
    }

    [Fact]
    public void EmptyGpuListKeepsNvmlSessionAndDetectsLaterAppearance()
    {
        using var h = new Harness();
        h.Native.Devices = [];
        h.Tick(0);
        h.Tick(10_000);
        Assert.Empty(h.Apis);
        h.Native.Devices = [new(1)];
        h.Tick(20_000);
        Assert.Single(h.Apis);
        Assert.Equal(1, h.Native.Initializations);
    }

    [Fact]
    public void DisappearanceRemovesReadingsAndReappearanceCreatesFreshNvApi()
    {
        using var h = new Harness();
        h.Tick(0);
        h.Native.Devices = [];
        h.Tick(10_000);
        Assert.Equal(1, h.Apis[0].Disposals);
        Assert.Empty(h.Monitor.Catalog!.Descriptors);
        Assert.Empty(HwmonMonitor.SENSORHASH);
        var reads = h.Apis[0].Reads;
        h.Tick(11_000);
        Assert.Equal(reads, h.Apis[0].Reads);
        h.Native.Devices = [new(1)];
        h.Tick(20_000);
        Assert.Equal(2, h.Apis.Count);
        Assert.Equal(1, h.Native.Initializations);
        Assert.Contains(Harness.Prefix + "/temperature", HwmonMonitor.SENSORHASH.Keys);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HandleOrIdentityReplacementRefreshesNvApi(bool changeIdentity)
    {
        using var h = new Harness();
        h.Tick(0);
        if (changeIdentity) h.Native.UuidSuffix = "replacement";
        else
        {
            h.Native.Devices = [new(2)];
            h.Native.IdentityOverrides[new(2)] = 1;
        }
        h.Tick(10_000);
        Assert.Equal(2, h.Apis.Count);
        Assert.Equal(1, h.Apis[0].Disposals);
        Assert.Equal(1, h.Native.Initializations);
        if (changeIdentity) Assert.DoesNotContain(Harness.Prefix + "/temperature", HwmonMonitor.SENSORHASH.Keys);
        else Assert.Contains(Harness.Prefix + "/temperature", HwmonMonitor.SENSORHASH.Keys);
    }

    [Fact]
    public void GpuIndexReorderPreservesSessionAndBindings()
    {
        using var h = new Harness();
        h.Native.Devices = [new(1), new(2)];
        h.Tick(0);
        h.Native.Devices = [new(2), new(1)];
        h.Tick(10_000);
        Assert.Single(h.Apis);
        Assert.Equal(2, h.Apis[0].MaskProbes);
        Assert.Equal(0, h.Apis[0].Disposals);
        Assert.Equal(new long[] { 41, 42, 42, 41 }, h.Apis[0].ReadHandles);
        Assert.Equal(new long[] { 41, 42 }, h.Apis[0].RegisterHandles); // probed once per GPU at setup
    }

    [Fact]
    public void DiscoveryFailureDiscardsNativeSessionAndRetriesAfterCooldown()
    {
        using var h = new Harness();
        h.Tick(0);
        h.Native.CountResult = NvmlReturn.GpuIsLost;
        h.Tick(10_000);
        Assert.Empty(h.Monitor.Catalog!.Descriptors);
        Assert.Empty(HwmonMonitor.SENSORHASH);
        Assert.Equal(1, h.Apis[0].Disposals);
        Assert.Equal(1, h.Native.Shutdowns);
        h.Native.CountResult = NvmlReturn.Success;
        for (var time = 20_000; time < 70_000; time += 10_000) h.Tick(time);
        Assert.Equal(1, h.Native.Initializations);
        h.Tick(70_000);
        Assert.Equal(2, h.Native.Initializations);
        Assert.Equal(2, h.Apis.Count);
    }

    [Theory]
    [InlineData(1)] // uninitialized
    [InlineData(9)] // driver not loaded
    [InlineData(15)] // GPU lost, not 9
    [InlineData(16)] // reset required
    [InlineData(18)] // driver/library version mismatch
    public void PollLossStopsBeforeNvApiAndClearsEvenPartiallyUpdatedReadings(int status)
    {
        using var h = new Harness();
        h.Tick(0);
        var reads = h.Apis[0].Reads;
        h.Native.PowerResult = (NvmlReturn)status; // after a successful temperature read
        h.Tick(1000);
        Assert.Equal(reads, h.Apis[0].Reads);
        Assert.Equal(1, h.Apis[0].Disposals);
        Assert.Equal(1, h.Native.Shutdowns);
        Assert.Empty(HwmonMonitor.SENSORHASH);
        h.Native.PowerResult = NvmlReturn.NotSupported;
        h.Tick(60_000);
        Assert.Equal(1, h.Native.Initializations);
        h.Tick(70_000);
        Assert.Equal(2, h.Native.Initializations);
        Assert.Equal(2, h.Apis.Count);
    }

    [Fact]
    public void NvApiInvalidationStopsBeforeVoltageAndRecoversWithoutRestartingNvml()
    {
        using var h = new Harness();
        h.Tick(0);
        h.Apis[0].Lost = true;
        h.Tick(1000);
        Assert.Equal(1, h.Apis[0].VoltageReads);
        Assert.Equal(1, h.Apis[0].Disposals);
        Assert.Equal(0, h.Native.Shutdowns);
        Assert.False(HwmonMonitor.SENSORHASH.ContainsKey(Harness.Prefix + "/temperature_vram"));
        Assert.True(HwmonMonitor.SENSORHASH.ContainsKey(Harness.Prefix + "/temperature"));
        h.Tick(60_000);
        Assert.Single(h.Apis);
        h.Tick(70_000);
        Assert.Equal(2, h.Apis.Count);
        Assert.Equal(1, h.Native.Initializations);
    }

    [Theory]
    [InlineData(-4)]
    [InlineData(-6)]
    [InlineData(-8)]
    [InlineData(-10)]
    [InlineData(-220)]
    [InlineData(-221)]
    public void NvApiDetectsNativeSessionLoss(int status) =>
        Assert.Throws<NvApiUnavailableException>(() => NvApi.CheckStatus(status));

    [Theory]
    [InlineData(0)]
    [InlineData(-1)] // generic error, including unsupported register reads
    [InlineData(-3)] // no implementation
    [InlineData(-5)] // invalid argument while probing thermal masks
    [InlineData(-104)] // not supported
    [InlineData(-137)] // insufficient permissions
    public void UnsupportedNvApiSensorsDoNotInvalidateTheSession(int status) =>
        Assert.Equal(status, NvApi.CheckStatus(status));

    [Fact]
    public void InvalidFanIndexSkipsOnlyThatReadingAcrossPollsAndRescans()
    {
        using var h = new Harness();
        h.Native.EnableMetrics();
        h.Native.FanResults[1] = NvmlReturn.InvalidArgument; // reports two fans, but fan 1 fails
        for (var time = 0; time <= 120_000; time += 1000)
        {
            h.Native.Sample++;
            h.Tick(time);
            AssertReadings(h, Harness.Prefix, "fan1_rpm");
            AssertNativeSessionUnchanged(h);
        }
        Assert.Equal(13, h.Native.Scans);
        Assert.Equal(121, h.Native.FanQueries.Count(fan => fan == 0));
        Assert.Equal(121, h.Native.FanQueries.Count(fan => fan == 1));
    }

    [Theory]
    [InlineData(2)] // invalid argument
    [InlineData(3)] // not supported
    [InlineData(4)] // no permission
    [InlineData(6)] // not found
    [InlineData(7)] // insufficient size
    [InlineData(999)] // unknown per-query error
    public void VideoClockErrorSkipsOnlyThatReadingAcrossPollsAndRescans(int status)
    {
        using var h = new Harness();
        h.Native.EnableMetrics();
        h.Tick(0);
        AssertReadings(h, Harness.Prefix);
        h.Native.VideoClockResult = (NvmlReturn)status;
        for (var time = 1000; time <= 120_000; time += 1000)
        {
            h.Native.Sample++;
            h.Tick(time);
            AssertReadings(h, Harness.Prefix, "clock_video");
            AssertNativeSessionUnchanged(h);
        }
        Assert.Equal(13, h.Native.Scans);
        h.Native.VideoClockResult = NvmlReturn.Success;
        h.Tick(121_000);
        AssertReadings(h, Harness.Prefix);
        AssertNativeSessionUnchanged(h);
    }

    [Fact]
    public void UnsupportedFanCountSkipsRpmQueriesAcrossPollsAndRescans()
    {
        using var h = new Harness();
        h.Native.EnableMetrics();
        h.Native.NumFansResult = NvmlReturn.NotSupported;
        for (var time = 0; time <= 120_000; time += 1000)
        {
            h.Native.Sample++;
            h.Tick(time);
            AssertReadings(h, Harness.Prefix, "fan0_rpm", "fan1_rpm");
            Assert.Empty(h.Native.FanQueries);
            AssertNativeSessionUnchanged(h);
        }
        Assert.Equal(13, h.Native.Scans);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(6)]
    [InlineData(999)]
    public void HandleQueryErrorSkipsOnlyThatIndexAcrossRescans(int status)
    {
        using var h = new Harness();
        h.Native.EnableMetrics();
        h.Native.Devices = [new(2), new(1)];
        h.Native.HandleResults[0] = (NvmlReturn)status;
        for (var time = 0; time <= 120_000; time += 1000)
        {
            h.Native.Sample++;
            h.Tick(time);
            AssertReadings(h, Harness.Prefix);
            Assert.DoesNotContain(h.Monitor.Catalog!.Descriptors, d => d.StableId.StartsWith(Harness.SecondPrefix + "/"));
            AssertNativeSessionUnchanged(h);
        }
        Assert.Equal(13, h.Native.Scans);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnexpectedMetricExceptionKeepsCompletedReadingsAndPollsOtherGpus(bool failingGpuFirst)
    {
        using var h = new Harness();
        h.Native.EnableMetrics();
        h.Native.Devices = failingGpuFirst ? [new(1), new(2)] : [new(2), new(1)];
        h.Tick(0);
        AssertReadings(h, Harness.Prefix);
        AssertReadings(h, Harness.SecondPrefix);
        h.Native.ThrowVideoClockFor = new(1);
        var sink = new LogSink();
        var oldLog = Log.Logger;
        using var logger = new LoggerConfiguration().MinimumLevel.Debug().WriteTo.Sink(sink).CreateLogger();
        Log.Logger = logger;
        try
        {
            for (var time = 1000; time <= 30_000; time += 1000)
            {
                h.Native.Sample++;
                h.Tick(time);
                // Successful reads before the exception remain fresh; later GPUs still poll.
                Assert.Equal(50 + h.Native.Sample, HwmonMonitor.SENSORHASH[Harness.Prefix + "/temperature"].ValueNow);
                Assert.Equal(100, HwmonMonitor.SENSORHASH[Harness.Prefix + "/power"].ValueNow);
                Assert.Equal(1500 + h.Native.Sample, HwmonMonitor.SENSORHASH[Harness.Prefix + "/clock_sm"].ValueNow);
                AssertReadings(h, Harness.SecondPrefix);
                AssertNativeSessionUnchanged(h);
            }
            Assert.Equal(30, sink.Events.Count(e => e.Level == LogEventLevel.Debug &&
                e.Exception is InvalidOperationException && e.RenderMessage().StartsWith("NVML poll error for GPU")));
            h.Native.ThrowVideoClockFor = null;
            h.Tick(31_000); // retries next poll, with no cooldown
            AssertReadings(h, Harness.Prefix);
            AssertReadings(h, Harness.SecondPrefix);
            AssertNativeSessionUnchanged(h);
        }
        finally { Log.Logger = oldLog; }
    }

    private static void AssertNativeSessionUnchanged(Harness h)
    {
        Assert.Equal(1, h.Native.Initializations);
        Assert.Equal(0, h.Native.Shutdowns); // before the harness's explicit shutdown
        Assert.Single(h.Apis);
        Assert.Equal(0, h.Apis[0].Disposals);
    }

    private static void AssertReadings(Harness h, string prefix, params string[] missing)
    {
        var expected = new Dictionary<string, double>
        {
            ["temperature"] = 50 + h.Native.Sample, ["temperature_vram"] = 60, ["voltage"] = 0.95,
            ["power"] = 100, ["power_limit"] = 200, ["power_percent"] = 50,
            ["utilization"] = 25, ["memory_utilization"] = 30,
            ["clock_graphics"] = 1500 + h.Native.Sample, ["clock_sm"] = 1500 + h.Native.Sample,
            ["clock_memory"] = 1500 + h.Native.Sample, ["clock_video"] = 1500 + h.Native.Sample,
            ["pstate"] = 0, ["throttle_thermal"] = 0, ["throttle_power"] = 0,
            ["memory_used"] = 4096, ["memory_total"] = 16384, ["memory_percent"] = 25,
            ["fan_speed"] = 40, ["fan0_rpm"] = 1200 + h.Native.Sample, ["fan1_rpm"] = 1200 + h.Native.Sample,
        };
        foreach (var metric in missing) expected.Remove(metric);
        var actual = HwmonMonitor.SENSORHASH.Where(pair => pair.Key.StartsWith(prefix + "/"))
            .ToDictionary(pair => pair.Key[(prefix.Length + 1)..], pair => pair.Value.ValueNow);
        Assert.Equal(expected.Keys.Order(), actual.Keys.Order());
        foreach (var (metric, value) in expected) Assert.Equal(value, actual[metric]);
    }

    private sealed class Harness : IDisposable
    {
        public const string Prefix = "system/gpu/gpu-uuid+GPU-test~pci+0000-01-00.0";
        public const string SecondPrefix = "system/gpu/gpu-uuid+GPU-test2~pci+0000-02-00.0";
        private readonly FakeSysfs _fs = new();
        private long _now;
        public readonly NvidiaGpu Native = new();
        public readonly List<CountingNvApi> Apis = [];
        public Func<INvApi?>? Create;
        public readonly NvmlMonitor Gpu;
        public readonly HwmonMonitor Monitor;
        public Harness()
        {
            SensorDemand.ForcePollAll = true;
            Gpu = new(Native, () =>
            {
                if (Create != null) return Create();
                var api = new CountingNvApi();
                Apis.Add(api);
                return api;
            }, () => _now);
            Monitor = new(_fs, clock: () => _now, systemDescriptors: Gpu.ScanDescriptors, pollSystemSensors: Gpu.Poll);
        }
        public void Tick(long now) { _now = now; Monitor.RunCycle(); }
        public void Dispose() { Gpu.Shutdown(); _fs.Dispose(); }
    }

    private sealed class LogSink : ILogEventSink
    {
        public readonly List<string> Messages = [];
        public readonly List<LogEvent> Events = [];
        public void Emit(LogEvent logEvent) { Events.Add(logEvent); Messages.Add(logEvent.RenderMessage()); }
    }

    private sealed class NvidiaGpu : FakeNvmlApi
    {
        public int Initializations, Shutdowns, Scans;
        public bool MissingLibrary;
        public NvmlReturn InitResult = NvmlReturn.Success, CountResult = NvmlReturn.Success, PowerResult = NvmlReturn.NotSupported;
        public NvmlReturn MetricResult = NvmlReturn.NotSupported, VideoClockResult = NvmlReturn.Success,
            NumFansResult = NvmlReturn.NotSupported;
        public readonly Dictionary<uint, NvmlReturn> FanResults = [], HandleResults = [];
        public readonly List<uint> FanQueries = [];
        public IntPtr? ThrowVideoClockFor;
        public int Sample;
        public void EnableMetrics() => MetricResult = PowerResult = NumFansResult = NvmlReturn.Success;
        public IntPtr[] Devices = [new(1)];
        public readonly Dictionary<IntPtr, uint> IdentityOverrides = [];
        private uint Identity(IntPtr device) => IdentityOverrides.GetValueOrDefault(device, (uint)device);
        public string UuidSuffix = "";
        public override NvmlReturn nvmlInit_v2()
        {
            Initializations++;
            if (MissingLibrary) throw new DllNotFoundException();
            return InitResult;
        }
        public override NvmlReturn nvmlShutdown() { Shutdowns++; return NvmlReturn.Success; }
        public override NvmlReturn nvmlDeviceGetCount_v2(out uint count)
        { Scans++; count = (uint)Devices.Length; return CountResult; }
        public override NvmlReturn nvmlDeviceGetHandleByIndex_v2(uint index, out IntPtr device)
        { device = Devices[index]; return HandleResults.GetValueOrDefault(index, NvmlReturn.Success); }
        public override NvmlReturn nvmlDeviceGetName(IntPtr device, out string name)
        { name = "RTX 5080"; return NvmlReturn.Success; }
        public override NvmlReturn nvmlDeviceGetUUID(IntPtr device, out string uuid)
        { uuid = "GPU-test" + (Identity(device) == 1 ? "" : Identity(device).ToString()) + UuidSuffix; return NvmlReturn.Success; }
        public override NvmlReturn nvmlDeviceGetPciInfo_v3(IntPtr device, out NvmlPciInfo pci)
        { pci = new() { bus = Identity(device), busId = System.Text.Encoding.ASCII.GetBytes($"00000000:{Identity(device):x2}:00.0\0") }; return NvmlReturn.Success; }
        public override NvmlReturn nvmlDeviceGetTemperature(IntPtr device, NvmlTemperatureSensor sensorType, out uint temp)
        { temp = (uint)(50 + Sample); return NvmlReturn.Success; }
        public override NvmlReturn nvmlDeviceGetPowerUsage(IntPtr device, out uint power)
        { power = 100_000; return PowerResult; }
        public override NvmlReturn nvmlDeviceGetUtilizationRates(IntPtr device, out NvmlUtilization utilization)
        { utilization = new() { gpu = 25, memory = 30 }; return MetricResult; }
        public override NvmlReturn nvmlDeviceGetPowerManagementLimit(IntPtr device, out uint limit)
        { limit = 200_000; return MetricResult; }
        public override NvmlReturn nvmlDeviceGetClockInfo(IntPtr device, NvmlClockType type, out uint clock)
        {
            clock = (uint)(1500 + Sample);
            if (type == NvmlClockType.Video && device == ThrowVideoClockFor)
                throw new InvalidOperationException("unexpected video clock query failure");
            return type == NvmlClockType.Video && MetricResult == NvmlReturn.Success ? VideoClockResult : MetricResult;
        }
        public override NvmlReturn nvmlDeviceGetPerformanceState(IntPtr device, out uint pstate)
        { pstate = 0; return MetricResult; }
        public override NvmlReturn nvmlDeviceGetCurrentClocksThrottleReasons(IntPtr device, out ulong reasons)
        { reasons = 0; return MetricResult; }
        public override NvmlReturn nvmlDeviceGetMemoryInfo(IntPtr device, out NvmlMemory memory)
        { memory = new() { total = 16UL * 1024 * 1024 * 1024, used = 4UL * 1024 * 1024 * 1024 }; return MetricResult; }
        public override NvmlReturn nvmlDeviceGetFanSpeed(IntPtr device, out uint speed)
        { speed = 40; return MetricResult; }
        public override NvmlReturn nvmlDeviceGetNumFans(IntPtr device, ref uint numFans)
        { if (NumFansResult == NvmlReturn.Success) numFans = 2; return NumFansResult; }
        public override NvmlReturn nvmlDeviceGetFanSpeedRPM(IntPtr device, ref NvmlFanSpeedInfo fanSpeed)
        {
            FanQueries.Add(fanSpeed.fan);
            fanSpeed.speed = (uint)(1200 + Sample);
            return FanResults.GetValueOrDefault(fanSpeed.fan, MetricResult);
        }
        public override NvmlReturn nvmlDeviceGetArchitecture(IntPtr device, out uint arch)
        { arch = 10; return NvmlReturn.Success; }
    }

    private sealed class CountingNvApi : INvApi
    {
        public int Disposals, MaskProbes, Reads, RegisterReads, VoltageReads;
        public bool FailMask, Lost;
        public int? HotspotRegisterValue;
        public readonly List<long> ReadHandles = [], RegisterHandles = [];
        public IntPtr SingleGpuHandle => new(42);
        public IntPtr FindGpuByBusId(uint busId) => new(40 + busId);
        public int CalculateThermalsMask(IntPtr handle) { MaskProbes++; if (FailMask) throw new InvalidOperationException("mask setup failed"); return 0x7FFFF; }
        public (int? Hotspot, int? Vram) ReadTemperatures(IntPtr handle, int mask, bool isBlackwell)
        {
            Assert.Equal(0, Disposals);
            Reads++;
            ReadHandles.Add(handle.ToInt64());
            if (Lost) throw new NvApiUnavailableException(-10);
            return (null, 60);
        }
        public int? ReadHotspotRegister(IntPtr handle) { Assert.Equal(0, Disposals); RegisterReads++; RegisterHandles.Add(handle.ToInt64()); return HotspotRegisterValue; }
        public int? ReadVoltageMv(IntPtr handle) { Assert.Equal(0, Disposals); VoltageReads++; return 950; }
        public void Dispose() => Disposals++;
    }
}
