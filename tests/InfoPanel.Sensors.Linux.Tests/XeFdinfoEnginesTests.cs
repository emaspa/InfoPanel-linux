using InfoPanel.Services;
using Xunit;

namespace InfoPanel.Sensors.Linux.Tests;

public sealed class XeFdinfoEnginesTests : IDisposable
{
    private readonly string _proc = Path.Combine(Path.GetTempPath(), "infopanel-proc-" + Guid.NewGuid().ToString("N"));
    private static readonly HashSet<string> Nodes = ["/dev/dri/card1", "/dev/dri/renderD128"];
    private const string Pdev = "0000:03:00.0";

    public XeFdinfoEnginesTests() => HwmonMonitor.SENSORHASH.Clear();

    public void Dispose()
    {
        HwmonMonitor.SENSORHASH.Clear();
        IntelGpuMonitor.FdinfoEngineUtilization = false;
        Directory.Delete(_proc, recursive: true);
    }

    // An fd of process pid on target with the fdinfo xe prints (xe_drm_client.c show_run_ticks).
    private void Fd(int pid, int fd, string target, string? fdinfo)
    {
        var dir = Path.Combine(_proc, pid.ToString());
        Directory.CreateDirectory(Path.Combine(dir, "fd"));
        Directory.CreateDirectory(Path.Combine(dir, "fdinfo"));
        var link = Path.Combine(dir, "fd", fd.ToString());
        if (!File.Exists(link)) File.CreateSymbolicLink(link, target);
        if (fdinfo != null) File.WriteAllText(Path.Combine(dir, "fdinfo", fd.ToString()), fdinfo);
    }

    private static string Xe(int client, ulong total, ulong rcs, ulong vcs = 0, ulong ccs = 0, string pdev = Pdev) => $"""
        pos:	0
        flags:	02100002
        drm-driver:	xe
        drm-client-id:	{client}
        drm-pdev:	{pdev}
        drm-total-vram0:	23992 KiB
        drm-cycles-rcs:	{rcs}
        drm-total-cycles-rcs:	{total}
        drm-cycles-bcs:	0
        drm-total-cycles-bcs:	{total}
        drm-cycles-vcs:	{vcs}
        drm-total-cycles-vcs:	{total}
        drm-engine-capacity-vcs:	2
        drm-cycles-ccs:	{ccs}
        drm-total-cycles-ccs:	{total}
        drm-engine-capacity-ccs:	4

        """;

    [Fact]
    public void BusyIsSummedOverClientsAndDividedByCapacity()
    {
        Fd(100, 5, "/dev/dri/renderD128", Xe(3, 1_000_000, 0));                 // game
        Fd(200, 9, "/dev/dri/card1", Xe(7, 1_000_500, 0));                      // compositor
        Fd(300, 4, "/dev/dri/renderD129", Xe(9, 1_000_000, 0, pdev: "0000:7d:00.0")); // another GPU's node
        Fd(400, 3, "/dev/null", null);
        Fd(500, 6, "/dev/dri/renderD128", Xe(11, 1_000_000, 0));                // our own process
        var engines = new XeFdinfoEngines(_proc, Pdev, Nodes, selfPid: 500);

        Assert.Empty(engines.Sample(0));

        // Over the next 1,000,000 ticks: game rcs 600,000 and vcs 1,000,000 (one of two video
        // engines full), compositor rcs 150,000; our own fd claims full load and must be ignored.
        Fd(100, 5, "/dev/dri/renderD128", Xe(3, 2_000_000, 600_000, vcs: 1_000_000));
        Fd(200, 9, "/dev/dri/card1", Xe(7, 2_000_500, 150_000));
        Fd(500, 6, "/dev/dri/renderD128", Xe(11, 2_000_000, 1_000_000));
        var busy = engines.Sample(1000);
        Assert.Equal(75, busy["rcs"]);
        Assert.Equal(50, busy["vcs"]);
        Assert.Equal(0, busy["bcs"]);
        Assert.Equal(0, busy["ccs"]);
    }

    [Fact]
    public void SharedClientIsCountedOnceAndExitedClientsDropOut()
    {
        // A dup'd fd (or a forked child) shares the drm client.
        Fd(100, 5, "/dev/dri/renderD128", Xe(3, 1_000_000, 0));
        Fd(100, 8, "/dev/dri/renderD128", Xe(3, 1_000_000, 0));
        var engines = new XeFdinfoEngines(_proc, Pdev, Nodes, selfPid: 1);
        engines.Sample(0);
        Fd(100, 5, "/dev/dri/renderD128", Xe(3, 2_000_000, 400_000));
        Fd(100, 8, "/dev/dri/renderD128", Xe(3, 2_000_000, 400_000));
        Assert.Equal(40, engines.Sample(1000)["rcs"]);

        // The game exits: nothing left on the GPU reads 0, not the last value.
        Directory.Delete(Path.Combine(_proc, "100"), recursive: true);
        Assert.Equal(0, engines.Sample(2000)["rcs"]);
    }

    [Fact]
    public void NewProcessesAreFoundOnTheNextRescan()
    {
        var engines = new XeFdinfoEngines(_proc, Pdev, Nodes, selfPid: 1);
        Directory.CreateDirectory(_proc);
        engines.Sample(0);
        Fd(100, 5, "/dev/dri/renderD128", Xe(3, 1_000_000, 0));
        Assert.Empty(engines.Sample(1000));   // no class seen yet, and no rescan yet
        engines.Sample(XeFdinfoEngines.RescanMs);
        Fd(100, 5, "/dev/dri/renderD128", Xe(3, 2_000_000, 250_000));
        Assert.Equal(25, engines.Sample(XeFdinfoEngines.RescanMs + 1000)["rcs"]);
    }

    [Fact]
    public void KnownProcessThatOpensTheGpuLaterIsFoundOnTheFullWalk()
    {
        Fd(100, 3, "/dev/null", null);   // e.g. a launcher that starts rendering later
        Fd(200, 5, "/dev/dri/renderD128", Xe(3, 1_000_000, 0));
        var engines = new XeFdinfoEngines(_proc, Pdev, Nodes, selfPid: 1);
        engines.Sample(0);
        Fd(100, 7, "/dev/dri/renderD128", Xe(4, 1_000_000, 0));
        var t = XeFdinfoEngines.RescanMs;
        engines.Sample(t);
        Fd(100, 7, "/dev/dri/renderD128", Xe(4, 2_000_000, 500_000));
        Assert.Equal(0, engines.Sample(t + 1000)["rcs"]);   // pid 100 was walked already: not re-walked yet

        t = XeFdinfoEngines.FullWalkMs;
        engines.Sample(t);
        Fd(100, 7, "/dev/dri/renderD128", Xe(4, 3_000_000, 1_000_000));
        Assert.Equal(50, engines.Sample(t + 1000)["rcs"]);
    }

    [Fact]
    public void IntelMonitorReportsEnginesOnlyWhenEnabled()
    {
        using var fs = new FakeSysfs();
        var device = fs.Pci("0000:03:00.0");
        fs.Write(device + "/vendor", "0x8086");
        fs.Write(device + "/device", "0xe20b");
        fs.Write(device + "/uevent", "DRIVER=xe");
        fs.Dir("class/drm/card1");
        fs.Link("class/drm/card1/device", device);
        fs.Write(device + "/tile0/gt0/gtidle/name", "gt0-rc");
        fs.Write(device + "/tile0/gt0/gtidle/idle_residency_ms", "0");
        fs.Dir(device + "/drm/card1");
        fs.Dir(device + "/drm/renderD128");
        Fd(100, 5, "/dev/dri/renderD128", Xe(3, 1_000_000, 0));

        var monitor = new IntelGpuMonitor(fs, _ => null, _proc);
        monitor.Initialize();
        monitor.Poll(1_000_000_000);
        monitor.Poll(2_000_000_000);
        Assert.DoesNotContain(HwmonMonitor.SENSORHASH.Keys, k => k.StartsWith("system/igpu/engine/"));

        IntelGpuMonitor.FdinfoEngineUtilization = true;
        monitor.Poll(3_000_000_000);
        Fd(100, 5, "/dev/dri/renderD128", Xe(3, 2_000_000, 900_000));
        monitor.Poll(4_000_000_000);
        Assert.Equal(90, HwmonMonitor.SENSORHASH["system/igpu/engine/render"].ValueNow);
        Assert.Equal(0, HwmonMonitor.SENSORHASH["system/igpu/engine/compute"].ValueNow);
        Assert.Contains(monitor.GetSensorInfoList(), s => s.SensorId == "system/igpu/engine/render" && s.Label == "render Busy");

        IntelGpuMonitor.FdinfoEngineUtilization = false;
        monitor.Poll(5_000_000_000);
        Assert.DoesNotContain(HwmonMonitor.SENSORHASH.Keys, k => k.StartsWith("system/igpu/engine/"));
    }
}
