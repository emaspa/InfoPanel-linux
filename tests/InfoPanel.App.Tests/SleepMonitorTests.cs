using InfoPanel.Services;
using Xunit;

namespace InfoPanel.App.Tests;

public class SleepMonitorTests
{
    private sealed class Inhibitor : IDisposable
    {
        public bool Released;
        public void Dispose() => Released = true;
    }

    private sealed class Harness
    {
        public readonly List<string> Events = [];
        public readonly List<Inhibitor> Inhibitors = [];
        public Exception? BeforeSleepError;
        public readonly SleepMonitor Monitor;

        public Harness()
        {
            Monitor = new SleepMonitor(
                beforeSleep: () =>
                {
                    Events.Add("stop");
                    if (BeforeSleepError != null) throw BeforeSleepError;
                    return Task.CompletedTask;
                },
                afterResume: () => { Events.Add("start"); return Task.CompletedTask; },
                takeInhibitor: () =>
                {
                    var inhibitor = new Inhibitor();
                    Inhibitors.Add(inhibitor);
                    Events.Add("inhibit");
                    return Task.FromResult<IDisposable?>(inhibitor);
                },
                resumeDelay: TimeSpan.Zero);
        }
    }

    [Fact]
    public async Task SleepStopsPanelsThenReleasesInhibitorAndResumeRearmsThenStarts()
    {
        var h = new Harness();
        await h.Monitor.ArmAsync();
        Assert.Equal(["inhibit"], h.Events);

        await h.Monitor.HandleAsync(sleeping: true);
        Assert.Equal(["inhibit", "stop"], h.Events);
        Assert.True(h.Inhibitors[0].Released); // only after the panels are stopped
        Assert.True(h.Monitor.IsSleeping);

        await h.Monitor.HandleAsync(sleeping: false);
        Assert.Equal(["inhibit", "stop", "inhibit", "start"], h.Events);
        Assert.False(h.Inhibitors[1].Released);
        Assert.False(h.Monitor.IsSleeping);
    }

    [Fact]
    public async Task RepeatedSignalsForTheSameStateAreIgnored()
    {
        var h = new Harness();
        await h.Monitor.ArmAsync();
        await h.Monitor.HandleAsync(sleeping: false); // resume without a sleep: nothing to do
        await h.Monitor.HandleAsync(sleeping: true);
        await h.Monitor.HandleAsync(sleeping: true);
        Assert.Equal(["inhibit", "stop"], h.Events);
        Assert.Single(h.Inhibitors);
    }

    [Fact]
    public async Task InhibitorIsReleasedEvenWhenStoppingPanelsFails()
    {
        var h = new Harness { BeforeSleepError = new InvalidOperationException("usb hung") };
        await h.Monitor.ArmAsync();
        await h.Monitor.HandleAsync(sleeping: true);
        Assert.True(h.Inhibitors[0].Released);

        h.BeforeSleepError = null;
        await h.Monitor.HandleAsync(sleeping: false);
        Assert.Equal(["inhibit", "stop", "inhibit", "start"], h.Events);
    }

    [Fact]
    public async Task DisposeReleasesInhibitorAndIgnoresLaterSignals()
    {
        var h = new Harness();
        await h.Monitor.ArmAsync();
        h.Monitor.Dispose();
        Assert.True(h.Inhibitors[0].Released);

        await h.Monitor.HandleAsync(sleeping: true);
        await h.Monitor.HandleAsync(sleeping: false);
        Assert.Equal(["inhibit"], h.Events);
    }
}
