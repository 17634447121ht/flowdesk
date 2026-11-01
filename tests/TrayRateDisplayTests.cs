using System;
using FlowDesk;

internal static class TrayRateDisplayTests
{
    private static int checks;
    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception("FAIL " + name);
        checks++; Console.WriteLine("PASS " + name);
    }
    private static void Near(double value, double expected, string name) { Check(Math.Abs(value - expected) < .001, name); }
    private static int Main()
    {
        try
        {
            var display = new TrayRateDisplay();
            Check(display.Observe(0, 0, 0, true), "initial value publishes immediately");
            Check(!display.Observe(1, 100, 40, true), "one-second changes do not repaint");
            Near(display.Download, 0, "published speed holds between refreshes");
            Check(!display.Observe(2, 400, 80, true), "two-second changes do not repaint");
            Check(display.Observe(3, 1000, 180, true), "three-second interval publishes");
            Near(display.Download, 500, "bursty download shows window average");
            Near(display.Upload, 100, "upload is smoothed independently");
            Check(!display.Observe(4, 0, 0, true), "next window holds previous value");
            Check(display.Observe(6, 900, 300, true), "delayed sample publishes after interval");
            Near(display.Download, 600, "irregular intervals are weighted by time");
            Check(display.Observe(6.1, 0, 0, false), "disconnection is shown immediately");
            Check(!display.Available && display.Download == 0, "old speed is cleared on disconnect");
            Check(!display.Observe(7, 999, 999, false), "unavailable state does not repaint repeatedly");
            Check(display.Observe(7.2, 200, 50, true), "reconnection is shown immediately");
            Near(display.Download, 200, "reconnection does not blend stale samples");
            Check(display.Observe(100, 0, 0, true), "resuming after a long gap resets smoothing");
            Near(display.Download, 0, "sleep gap does not retain old speed");
            Check(!display.Observe(101, Double.NaN, Double.PositiveInfinity, true), "invalid input does not cause extra repaint");
            display.Observe(103, -4, -7, true);
            Near(display.Download + display.Upload, 0, "invalid rates are sanitized");
            Console.WriteLine("Tray rate checks passed: " + checks); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
