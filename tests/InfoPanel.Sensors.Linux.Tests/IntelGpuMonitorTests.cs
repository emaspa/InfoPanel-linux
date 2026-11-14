using InfoPanel.Services;
using Xunit;

namespace InfoPanel.Sensors.Linux.Tests;

public sealed class IntelGpuMonitorTests : IDisposable
{
    private const long Second = 1_000_000_000L;

    public IntelGpuMonitorTests() => HwmonMonitor.SENSORHASH.Clear();
    public void Dispose() => HwmonMonitor.SENSORHASH.Clear();

    // Battlemage (Arc B580) on xe, laid out as xe_tile_sysfs/xe_gt_sysfs/xe_gt_freq/xe_gt_idle
    // create it: render GT gt0 ("gt0-rc") and standalone media GT gt1 ("gt1-mc").
    private static string XeCard(FakeSysfs fs)
    {
        var device = fs.Pci("0000:03:00.0");
        fs.Write(device + "/vendor", "0x8086");
        fs.Write(device + "/device", "0xe20b");
        fs.Write(device + "/uevent", "DRIVER=xe\nPCI_SLOT_NAME=0000:03:00.0");
        fs.Dir("class/drm/card1");
        fs.Link("class/drm/card1/device", device);
        foreach (var (gt, name) in new[] { ("gt0", "gt0-rc"), ("gt1", "gt1-mc") })
        {
            var dir = device + "/tile0/" + gt;
            fs.Write(dir + "/gtidle/name", name);
            fs.Write(dir + "/gtidle/idle_residency_ms", "0");
            fs.Write(dir + "/freq0/act_freq", gt == "gt0" ? "2850" : "1100");
            fs.Write(dir + "/freq0/cur_freq", gt == "gt0" ? "2850" : "1100");
            fs.Write(dir + "/freq0/min_freq", "400");
            fs.Write(dir + "/freq0/max_freq", gt == "gt0" ? "2850" : "2400");
        }
        // The xe PMU is present but must not be needed (or used).
        fs.Write("bus/event_source/devices/xe_0000_03_00.0/type", "32");
        fs.Dir(device + "/drm/card1");
        fs.Dir(device + "/drm/renderD128");
        return device;
    }

    private sealed class FakeMemoryQuery(params XeVram?[] replies) : IXeMemoryQuery
    {
        private int _next;
        public bool Disposed;
        public XeVram? QueryVram() => replies[Math.Min(_next++, replies.Length - 1)];
        public void Dispose() => Disposed = true;
    }

    private const ulong MiB = 1024 * 1024;

    [Fact]
    public void XeCardReportsVramFromTheRenderNodeQuery()
    {
        using var fs = new FakeSysfs();
        XeCard(fs);
        string? opened = null;
        var query = new FakeMemoryQuery(new XeVram(12 * 1024 * MiB, 1536 * MiB), new XeVram(12 * 1024 * MiB, 3072 * MiB));
        var monitor = new IntelGpuMonitor(fs, node => { opened = node; return query; });
        monitor.Initialize();
        Assert.Equal("/dev/dri/renderD128", opened);

        monitor.Poll(1 * Second);
        Assert.Equal(12288, HwmonMonitor.SENSORHASH["system/igpu/vram_total"].ValueNow);
        Assert.Equal(3072, HwmonMonitor.SENSORHASH["system/igpu/vram_used"].ValueNow);
        Assert.Equal(25, HwmonMonitor.SENSORHASH["system/igpu/vram_percent"].ValueNow);
        Assert.Contains(monitor.GetSensorInfoList(), s => s.SensorId == "system/igpu/vram_used" && s.Unit == "MB");

        monitor.Shutdown();
        Assert.True(query.Disposed);
    }

    [Fact]
    public void XeVramUsedIsLeftOutWhenTheKernelDoesNotReportIt()
    {
        using var fs = new FakeSysfs();
        XeCard(fs);
        var monitor = new IntelGpuMonitor(fs, _ => new FakeMemoryQuery(new XeVram(12 * 1024 * MiB, null)));
        monitor.Initialize();
        monitor.Poll(1 * Second);
        Assert.Equal(12288, HwmonMonitor.SENSORHASH["system/igpu/vram_total"].ValueNow);
        Assert.False(HwmonMonitor.SENSORHASH.ContainsKey("system/igpu/vram_used"));
        Assert.False(HwmonMonitor.SENSORHASH.ContainsKey("system/igpu/vram_percent"));
    }

    [Fact]
    public void XeIntegratedGpuWithoutVramClosesTheQuery()
    {
        using var fs = new FakeSysfs();
        XeCard(fs);
        var query = new FakeMemoryQuery((XeVram?)null);
        var monitor = new IntelGpuMonitor(fs, _ => query);
        monitor.Initialize();
        monitor.Poll(1 * Second);
        Assert.True(query.Disposed);
        Assert.False(HwmonMonitor.SENSORHASH.ContainsKey("system/igpu/vram_total"));
        Assert.True(HwmonMonitor.SENSORHASH.ContainsKey("system/igpu/frequency"));
    }

    [Fact]
    public void XeMemoryRegionReplyIsParsedPerUapiLayout()
    {
        // struct drm_xe_query_mem_regions: sysmem region, then one VRAM region (88 bytes each).
        var reply = new byte[8 + 2 * 88];
        BitConverter.TryWriteBytes(reply.AsSpan(0), 2u);
        BitConverter.TryWriteBytes(reply.AsSpan(8 + 0), (ushort)0);
        BitConverter.TryWriteBytes(reply.AsSpan(8 + 8), 64UL * 1024 * MiB);
        BitConverter.TryWriteBytes(reply.AsSpan(8 + 16), 900 * MiB);
        var vram = 8 + 88;
        BitConverter.TryWriteBytes(reply.AsSpan(vram + 0), (ushort)1);
        BitConverter.TryWriteBytes(reply.AsSpan(vram + 2), (ushort)1);
        BitConverter.TryWriteBytes(reply.AsSpan(vram + 8), 12UL * 1024 * MiB);
        BitConverter.TryWriteBytes(reply.AsSpan(vram + 16), 512 * MiB);
        var handle = System.Runtime.InteropServices.GCHandle.Alloc(reply, System.Runtime.InteropServices.GCHandleType.Pinned);
        try
        {
            Assert.Equal(new XeVram(12UL * 1024 * MiB, 512 * MiB), XeMemoryQuery.Parse(handle.AddrOfPinnedObject(), reply.Length));
            BitConverter.TryWriteBytes(reply.AsSpan(vram + 16), 0UL);
            Assert.Equal(new XeVram(12UL * 1024 * MiB, null), XeMemoryQuery.Parse(handle.AddrOfPinnedObject(), reply.Length));
            BitConverter.TryWriteBytes(reply.AsSpan(0), 3u); // claims more regions than the reply holds
            Assert.Null(XeMemoryQuery.Parse(handle.AddrOfPinnedObject(), reply.Length));
        }
        finally { handle.Free(); }
    }

    [Fact]
    public void XeCardReportsFrequencyAndUtilizationFromSysfs()
    {
        using var fs = new FakeSysfs { ReverseListings = true };
        var device = XeCard(fs);
        var monitor = new IntelGpuMonitor(fs, _ => null);
        monitor.Initialize();

        monitor.Poll(10 * Second);
        Assert.Equal(2850, HwmonMonitor.SENSORHASH["system/igpu/frequency"].ValueNow);
        Assert.Equal(2850, HwmonMonitor.SENSORHASH["system/igpu/req_frequency"].ValueNow);
        Assert.Equal(400, HwmonMonitor.SENSORHASH["system/igpu/min_frequency"].ValueNow);
        Assert.Equal(2850, HwmonMonitor.SENSORHASH["system/igpu/max_frequency"].ValueNow);
        Assert.False(HwmonMonitor.SENSORHASH.ContainsKey("system/igpu/utilization")); // needs two samples

        // Over the next second gt0 idled 250 ms (75% busy) and the media GT 900 ms (10% busy).
        fs.Write(device + "/tile0/gt0/gtidle/idle_residency_ms", "250");
        fs.Write(device + "/tile0/gt1/gtidle/idle_residency_ms", "900");
        monitor.Poll(11 * Second);
        Assert.Equal(75, HwmonMonitor.SENSORHASH["system/igpu/utilization"].ValueNow);
        Assert.Equal(25, HwmonMonitor.SENSORHASH["system/igpu/rc6_percent"].ValueNow);
        Assert.Equal(10, HwmonMonitor.SENSORHASH["system/igpu/media_utilization"].ValueNow);

        var listed = monitor.GetSensorInfoList();
        Assert.Contains(listed, s => s.SensorId == "system/igpu/utilization" && s.DeviceName.Contains("(xe)"));
        Assert.Contains(listed, s => s.SensorId == "system/igpu/media_utilization");
        Assert.Contains(listed, s => s.SensorId == "system/igpu/frequency");
    }

    [Fact]
    public void XeIdleCounterResetSkipsOneSampleInsteadOfReportingGarbage()
    {
        using var fs = new FakeSysfs();
        var device = XeCard(fs);
        var monitor = new IntelGpuMonitor(fs, _ => null);
        monitor.Initialize();
        fs.Write(device + "/tile0/gt0/gtidle/idle_residency_ms", "5000");
        monitor.Poll(1 * Second);
        fs.Write(device + "/tile0/gt0/gtidle/idle_residency_ms", "100"); // e.g. after resume
        monitor.Poll(2 * Second);
        Assert.False(HwmonMonitor.SENSORHASH.ContainsKey("system/igpu/utilization"));
        fs.Write(device + "/tile0/gt0/gtidle/idle_residency_ms", "1100");
        monitor.Poll(3 * Second);
        Assert.Equal(0, HwmonMonitor.SENSORHASH["system/igpu/utilization"].ValueNow);
    }

    [Fact]
    public void XeIntegratedGpuWithoutMediaGtHasNoMediaSensor()
    {
        using var fs = new FakeSysfs();
        var device = XeCard(fs);
        Directory.Delete(fs.PathUnderRoot(device + "/tile0/gt1"), recursive: true);
        var monitor = new IntelGpuMonitor(fs, _ => null);
        monitor.Initialize();
        monitor.Poll(1 * Second);
        monitor.Poll(2 * Second);
        Assert.True(HwmonMonitor.SENSORHASH.ContainsKey("system/igpu/utilization"));
        Assert.False(HwmonMonitor.SENSORHASH.ContainsKey("system/igpu/media_utilization"));
    }

    [Fact]
    public void I915SysfsFrequencyStillWorksWithoutPmu()
    {
        using var fs = new FakeSysfs();
        var device = fs.Pci("0000:00:02.0");
        fs.Write(device + "/vendor", "0x8086");
        fs.Write(device + "/device", "0x4680");
        fs.Write(device + "/uevent", "DRIVER=i915");
        fs.Dir("class/drm/card0");
        fs.Link("class/drm/card0/device", device);
        fs.Write("class/drm/card0/gt_act_freq_mhz", "1450");
        fs.Write("class/drm/card0/gt_max_freq_mhz", "1450");
        var monitor = new IntelGpuMonitor(fs);
        monitor.Initialize();
        monitor.Poll(1 * Second);
        Assert.Equal(1450, HwmonMonitor.SENSORHASH["system/igpu/frequency"].ValueNow);
        Assert.False(HwmonMonitor.SENSORHASH.ContainsKey("system/igpu/media_utilization"));
    }
}
