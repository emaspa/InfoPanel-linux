using InfoPanel.Platform.Linux;
using Xunit;

namespace InfoPanel.App.Tests;

/// <summary>Issue #14: the startup delay was only written as X-GNOME-Autostart-Delay, which
/// KDE Plasma ignores, so the app started at login with no delay.</summary>
public class AutostartExecTests
{
    [Fact]
    public void DelayIsAShellSleepThatAnyDesktopHonors()
    {
        Assert.Equal("sh -c \"sleep 15; exec '/opt/infopanel/infopanel'\"",
            XdgAutostartService.BuildExec("/opt/infopanel/infopanel", 15));
    }

    [Fact]
    public void NoDelayRunsTheExecutableDirectly()
    {
        Assert.Equal("/opt/infopanel/infopanel", XdgAutostartService.BuildExec("/opt/infopanel/infopanel", 0));
    }

    [Fact]
    public void PathWithSpacesStaysOneArgument()
    {
        Assert.Equal("sh -c \"sleep 5; exec '/home/u/My Apps/infopanel'\"",
            XdgAutostartService.BuildExec("/home/u/My Apps/infopanel", 5));
        Assert.Equal("\"/home/u/My Apps/infopanel\"", XdgAutostartService.BuildExec("/home/u/My Apps/infopanel", 0));
    }

    [Fact]
    public void PathNeedingEscapesFallsBackToNoDelay()
    {
        Assert.Equal("/home/u/it's/infopanel", XdgAutostartService.BuildExec("/home/u/it's/infopanel", 5));
    }
}
