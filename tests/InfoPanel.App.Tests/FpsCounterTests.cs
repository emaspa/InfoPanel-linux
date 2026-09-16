using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using InfoPanel.Utils;
using Xunit;

namespace InfoPanel.App.Tests
{
    public class FpsCounterTests
    {
        // Regression: ThermalrightPanelDeviceTask.RunRenderSendLoop shares one
        // FpsCounter between its render task (Update(0) pacing branch) and send
        // task (Update(elapsed)). The unsynchronized Queue<long> made Average()
        // throw "Collection was modified", killing the render task; the panel
        // then fell back to its boot logo until manually disabled/enabled.
        [Fact]
        public void Update_CalledConcurrently_DoesNotThrow()
        {
            var counter = new FpsCounter(60);
            Exception? failure = null;
            bool stop = false;

            var threads = Enumerable.Range(0, 2).Select(_ => new Thread(() =>
            {
                try
                {
                    while (!Volatile.Read(ref stop))
                    {
                        counter.Update(1);
                    }
                }
                catch (Exception e)
                {
                    failure = e;
                    Volatile.Write(ref stop, true);
                }
            })).ToList();

            threads.ForEach(t => t.Start());

            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 2000 && !Volatile.Read(ref stop))
            {
                Thread.Sleep(10);
            }
            Volatile.Write(ref stop, true);
            threads.ForEach(t => t.Join());

            Assert.Null(failure);
        }
    }
}
