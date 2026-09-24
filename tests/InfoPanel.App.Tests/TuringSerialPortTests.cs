using InfoPanel.TuringPanel;
using Xunit;

namespace InfoPanel.App.Tests;

/// <summary>Issue #1: Turzx 5" sleeps as 1A86:CA21 (CT21INCH) and wakes as 1D6B:0106,
/// and ttyACM numbers swap between enumerations.</summary>
public class TuringSerialPortTests
{
    private const int RevCVid = 0x1d6b, Rev5Pid = 0x0106;

    [Fact]
    public void SelectsPortByVidPidWhenAcmNumbersSwapped()
    {
        // Saved at scan time as ttyACM0; after a replug the identifier port took ttyACM0
        var ports = new List<(string, int, int)> { ("/dev/ttyACM0", 0x1a86, 0xca21), ("/dev/ttyACM1", RevCVid, Rev5Pid) };

        Assert.Equal("/dev/ttyACM1", TuringPanelHelper.SelectSerialPort(ports, RevCVid, Rev5Pid, "/dev/ttyACM0"));
    }

    [Fact]
    public void KeepsSavedPortWhileItStillMatches()
    {
        var ports = new List<(string, int, int)> { ("/dev/ttyACM0", RevCVid, Rev5Pid), ("/dev/ttyACM2", RevCVid, Rev5Pid) };

        Assert.Equal("/dev/ttyACM2", TuringPanelHelper.SelectSerialPort(ports, RevCVid, Rev5Pid, "/dev/ttyACM2"));
    }

    [Fact]
    public void ReturnsNullWhenPanelPortIsMissing()
    {
        var ports = new List<(string, int, int)> { ("/dev/ttyACM0", 0x1a86, 0xca21) };

        Assert.Null(TuringPanelHelper.SelectSerialPort(ports, RevCVid, Rev5Pid, "/dev/ttyACM0"));
    }

    [Fact]
    public void WakesCt21InchWhenPanelIsAsleep()
    {
        var ports = new List<(string, int, int)> { ("/dev/ttyACM0", 0x1a86, 0xca21) };

        Assert.Equal(["/dev/ttyACM0"], TuringPanelHelper.GetPortsToWake(ports).Select(p => p.portPath));
    }

    [Fact]
    public void LeavesCt21InchAloneWhenDataPortIsAwake()
    {
        // The layout from the issue #1 log: both ports present at once
        var ports = new List<(string, int, int)> { ("/dev/ttyACM0", RevCVid, Rev5Pid), ("/dev/ttyACM1", 0x1a86, 0xca21) };

        Assert.Empty(TuringPanelHelper.GetPortsToWake(ports));
    }

    [Fact]
    public void StillWakesUsb7Inch()
    {
        var ports = new List<(string, int, int)> { ("/dev/ttyUSB0", 0x1a86, 0x5722), ("/dev/ttyACM0", RevCVid, Rev5Pid) };

        Assert.Equal(["/dev/ttyUSB0"], TuringPanelHelper.GetPortsToWake(ports).Select(p => p.portPath));
    }
}
