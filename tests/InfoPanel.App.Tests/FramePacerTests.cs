using InfoPanel.Drawing;
using Xunit;

namespace InfoPanel.App.Tests
{
    public class FramePacerTests
    {
        // Simulates a loop that asks the pacer, sleeps what it says, and spends
        // workMs per frame; returns frames delivered in one simulated second.
        private static int FramesInOneSecond(int fps, int workMs)
        {
            var pacer = new FramePacer();
            long now = 0;
            int frames = 0;
            while (now < 1000)
            {
                var wait = pacer.WaitMs(now, fps);
                if (wait > 0)
                {
                    now += wait;
                    continue;
                }
                frames++;
                now += workMs;
            }
            return frames;
        }

        [Theory]
        [InlineData(23, 1)]
        [InlineData(23, 20)]
        [InlineData(30, 25)]
        [InlineData(60, 3)]
        public void HoldsTargetRateWhenWorkFitsTheInterval(int fps, int workMs)
        {
            Assert.InRange(FramesInOneSecond(fps, workMs), fps - 1, fps + 1);
        }

        [Fact]
        public void SlowFramesRunBackToBackWithoutBursting()
        {
            // 60 ms frames at a 23 fps target: as fast as the work allows, no more.
            Assert.InRange(FramesInOneSecond(23, 60), 16, 17);
        }

        [Fact]
        public void AStallDoesNotCauseACatchUpBurst()
        {
            var pacer = new FramePacer();
            Assert.Equal(0, pacer.WaitMs(0, 20));
            // 500 ms stall: the next frame goes at once, then the schedule restarts
            Assert.Equal(0, pacer.WaitMs(500, 20));
            Assert.Equal(50, pacer.WaitMs(500, 20));
        }

        [Fact]
        public void WaitsUntilTheNextSlot()
        {
            var pacer = new FramePacer();
            Assert.Equal(0, pacer.WaitMs(1000, 10));
            Assert.Equal(60, pacer.WaitMs(1040, 10));
            Assert.Equal(0, pacer.WaitMs(1100, 10));
        }
    }
}

namespace InfoPanel.App.Tests
{
    public class SharedFrameCacheBackoffTests
    {
        [Fact]
        public void KeepsRequestedAgeWhileContentChanges()
        {
            for (int unchanged = 0; unchanged < 5; unchanged++)
            {
                Assert.Equal(38, SharedFrameCache.EffectiveMaxAgeMs(38, unchanged));
            }
        }

        [Fact]
        public void BacksOffToTheCapWhenContentStopsChanging()
        {
            Assert.Equal(76, SharedFrameCache.EffectiveMaxAgeMs(38, 5));
            Assert.Equal(SharedFrameCache.IdleBackoffCapMs, SharedFrameCache.EffectiveMaxAgeMs(38, 6));
            Assert.Equal(SharedFrameCache.IdleBackoffCapMs, SharedFrameCache.EffectiveMaxAgeMs(38, 1000));
        }

        [Fact]
        public void NeverShortensALongerRequestedAge()
        {
            Assert.Equal(1000, SharedFrameCache.EffectiveMaxAgeMs(1000, 50));
        }
    }
}
