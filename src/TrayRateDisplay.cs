using System;

namespace FlowDesk
{
    // Smooth only the taskbar presentation. The accounting and detail views retain
    // their original sampling interval and do not lose any traffic.
    internal sealed class TrayRateDisplay
    {
        internal const double RefreshSeconds = 3;
        private bool initialized;
        private double lastObserved;
        private double lastPublished;
        private double elapsed;
        private double downloaded;
        private double uploaded;

        internal double Download { get; private set; }
        internal double Upload { get; private set; }
        internal bool Available { get; private set; }

        internal bool Observe(double now, double down, double up, bool available)
        {
            if (Double.IsNaN(now) || Double.IsInfinity(now)) throw new ArgumentOutOfRangeException("now");
            down = SafeRate(down); up = SafeRate(up);
            double duration = now - lastObserved;
            if (!initialized || Available != available || duration < 0 || duration > 30)
            {
                initialized = true; Available = available;
                lastObserved = lastPublished = now;
                elapsed = downloaded = uploaded = 0;
                Download = available ? down : 0;
                Upload = available ? up : 0;
                return true;
            }
            lastObserved = now;
            if (!available) return false;
            elapsed += duration;
            downloaded += down * duration;
            uploaded += up * duration;
            if (now - lastPublished < RefreshSeconds) return false;
            Download = elapsed > 0 ? downloaded / elapsed : down;
            Upload = elapsed > 0 ? uploaded / elapsed : up;
            elapsed = downloaded = uploaded = 0;
            lastPublished = now;
            return true;
        }

        private static double SafeRate(double value)
        {
            return Double.IsNaN(value) || Double.IsInfinity(value) || value < 0 ? 0 : value;
        }
    }
}
