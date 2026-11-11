using System;

namespace InfoPanel.Drawing
{
    /// <summary>
    /// Absolute frame schedule for a panel loop: slots fall every 1000/fps ms
    /// from the first call, so render and encode time don't accumulate into the
    /// period the way "sleep after work" or a freshness gate plus polling do.
    /// A loop that falls more than one interval behind restarts the schedule
    /// from now instead of bursting to catch up.
    /// </summary>
    public sealed class FramePacer
    {
        private double _nextMs = double.NaN;

        /// <summary>
        /// Returns 0 when a frame is due (and consumes the slot), otherwise the
        /// milliseconds to wait before asking again.
        /// </summary>
        public int WaitMs(long nowMs, int framesPerSecond)
        {
            var interval = 1000.0 / Math.Max(1, framesPerSecond);
            if (double.IsNaN(_nextMs))
            {
                _nextMs = nowMs;
            }

            if (nowMs < _nextMs)
            {
                return Math.Max(1, (int)Math.Ceiling(_nextMs - nowMs));
            }

            _nextMs += interval;
            if (nowMs - _nextMs >= interval)
            {
                _nextMs = nowMs + interval;
            }
            return 0;
        }
    }
}
