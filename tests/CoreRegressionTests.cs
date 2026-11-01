using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Xml;
using System.Xml.Serialization;
using FlowDesk;

internal static class CoreRegressionTests
{
    private static string root;
    private static int checks;

    private static void Check(bool condition, string description)
    {
        if (!condition) throw new Exception(description);
        checks++;
    }

    private static string NewCase(string name)
    {
        string dir = Path.Combine(root, name);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string StoreXml(double left, double top, bool saved, bool legacy)
    {
        UsageStore store = new UsageStore();
        store.Settings.Left = left;
        store.Settings.Top = top;
        store.Settings.HasSavedPosition = saved;
        store.Settings.AdapterId = "test-no-adapter";
        store.Settings.Width = 230;
        store.Settings.Height = 120;
        store.Days.Add(new UsageDay { Date = "2026-11-01", Download = 123456789, Upload = 123 });
        StringWriter text = new StringWriter();
        new XmlSerializer(typeof(UsageStore)).Serialize(text, store);
        string xml = text.ToString();
        if (legacy)
        {
            XmlDocument document = new XmlDocument();
            document.LoadXml(xml);
            XmlNode node = document.SelectSingleNode("UsageStore/Settings/HasSavedPosition");
            node.ParentNode.RemoveChild(node);
            xml = document.OuterXml;
        }
        return xml;
    }

    private static void Write(string path, string text)
    {
        // StringWriter advertises UTF-16, so persist it with that encoding too.
        File.WriteAllText(path, text, Encoding.Unicode);
    }

    private static void Recovery()
    {
        foreach (string missing in new[] { "all", "Version", "StartedOn", "Settings", "Days" })
        {
            string dir = NewCase("missing-" + missing);
            string good = StoreXml(42, 64, true, true);
            XmlDocument document = new XmlDocument();
            document.LoadXml(good);
            if (missing == "all") document.DocumentElement.RemoveAll();
            else
            {
                XmlNode node = document.SelectSingleNode("UsageStore/" + missing);
                node.ParentNode.RemoveChild(node);
            }
            string invalid = document.OuterXml;
            Write(Path.Combine(dir, "usage.xml"), invalid);
            Write(Path.Combine(dir, "usage.xml.bak"), good);
            using (TrafficMonitor monitor = new TrafficMonitor(dir))
            {
                MonitorSnapshot snap = monitor.Tick(new DateTime(2026, 11, 1));
                Check(snap.TodayDownload == 123456789, "Restore backup for missing " + missing);
                Check(!String.IsNullOrEmpty(monitor.RecoveryNotice), "Recovery notice persists");
                Check(String.IsNullOrEmpty(monitor.LastStorageError), "Recovery is separate from write errors");
                monitor.Save(); monitor.Flush();
                monitor.Save(); monitor.Flush();
                Check(String.IsNullOrEmpty(monitor.LastStorageError), "Recovered data saves successfully");
            }
            string damaged = Directory.GetFiles(dir, "usage.xml.damaged-*").Single();
            Check(File.ReadAllText(damaged) == invalid, "Original invalid primary retained");
            string restored = Directory.GetFiles(dir, "usage.xml.bak.recovered-*").Single();
            Check(File.ReadAllText(restored) == good, "Original recovery backup retained");
        }

        string both = NewCase("both-invalid");
        Write(Path.Combine(both, "usage.xml"), "<UsageStore />");
        Write(Path.Combine(both, "usage.xml.bak"), "<broken>");
        using (TrafficMonitor monitor = new TrafficMonitor(both))
        {
            Check(!String.IsNullOrEmpty(monitor.RecoveryNotice), "Both invalid inputs produce notice");
            monitor.Save(); monitor.Flush(); monitor.Save(); monitor.Flush();
        }
        Check(File.ReadAllText(Directory.GetFiles(both, "usage.xml.damaged-*").Single()) == "<UsageStore />", "Bad primary preserved");
        Check(File.ReadAllText(Directory.GetFiles(both, "usage.xml.bak.damaged-*").Single()) == "<broken>", "Bad backup preserved");

        foreach (string missing in new[] { "Date", "Download", "Upload" })
        {
            string dir = NewCase("missing-day-" + missing);
            string good = StoreXml(0, 0, true, true);
            XmlDocument document = new XmlDocument();
            document.LoadXml(good);
            XmlNode node = document.SelectSingleNode("UsageStore/Days/UsageDay/" + missing);
            node.ParentNode.RemoveChild(node);
            Write(Path.Combine(dir, "usage.xml"), document.OuterXml);
            Write(Path.Combine(dir, "usage.xml.bak"), good);
            using (TrafficMonitor monitor = new TrafficMonitor(dir))
            {
                Check(!String.IsNullOrEmpty(monitor.RecoveryNotice), "Missing day field triggers recovery: " + missing);
                Check(monitor.Tick(new DateTime(2026, 11, 1)).TodayDownload == 123456789, "Missing day field restores original total");
            }
        }

        string unknown = NewCase("unknown-daily-element");
        string original = StoreXml(0, 0, true, true);
        Write(Path.Combine(unknown, "usage.xml"), original.Replace("UsageDay", "UnknownDailyRow"));
        Write(Path.Combine(unknown, "usage.xml.bak"), original);
        using (TrafficMonitor monitor = new TrafficMonitor(unknown))
        {
            Check(!String.IsNullOrEmpty(monitor.RecoveryNotice), "Unknown day element triggers recovery");
            Check(monitor.Tick(new DateTime(2026, 11, 1)).TodayDownload == 123456789, "Unknown day element restores backup total");
        }
    }

    private static void Modes()
    {
        string fresh = NewCase("mode-fresh");
        using (TrafficMonitor monitor = new TrafficMonitor(fresh))
        {
            Check(monitor.Settings.Mode == DisplayMode.Minimal, "Fresh install defaults to minimal transparent window");
            Check(!monitor.Settings.DesktopWidgetEnabled && monitor.Settings.MinimalTextShadow, "Minimal defaults keep legacy detailed flag false and text shadow enabled");
            monitor.Save(); monitor.Flush();
        }
        using (TrafficMonitor monitor = new TrafficMonitor(fresh))
            Check(monitor.Settings.Mode == DisplayMode.Minimal, "New minimal default survives XML round trip");

        foreach (bool desktop in new[] { false, true })
        {
            string dir = NewCase("mode-legacy-" + desktop);
            XmlDocument document = new XmlDocument(); document.LoadXml(StoreXml(42, 64, true, false));
            foreach (string field in new[] { "Mode", "MinimalLeft", "MinimalTop", "HasMinimalPosition", "MinimalLocked", "MinimalClickThrough", "MinimalTextTheme", "MinimalTextShadow" })
            {
                XmlNode node = document.SelectSingleNode("UsageStore/Settings/" + field);
                node.ParentNode.RemoveChild(node);
            }
            document.SelectSingleNode("UsageStore/Settings/DesktopWidgetEnabled").InnerText = desktop ? "true" : "false";
            Write(Path.Combine(dir, "usage.xml"), document.OuterXml);
            using (TrafficMonitor monitor = new TrafficMonitor(dir))
            {
                Check(monitor.Settings.Mode == (desktop ? DisplayMode.Detailed : DisplayMode.UltraMinimal), "Legacy display choice migrates without changing user selection");
                Check(monitor.Settings.MinimalTextShadow && monitor.Settings.MinimalTextTheme == "auto", "Absent minimal settings receive defaults");
                Check(monitor.Tick(new DateTime(2026, 11, 1)).TodayDownload == 123456789, "Display migration preserves traffic history");
                monitor.Save(); monitor.Flush();
            }
            using (TrafficMonitor monitor = new TrafficMonitor(dir))
                Check(monitor.Settings.Mode == (desktop ? DisplayMode.Detailed : DisplayMode.UltraMinimal), "Migrated display mode round trip");
        }

        foreach (DisplayMode mode in Enum.GetValues(typeof(DisplayMode)))
        {
            string dir = NewCase("mode-roundtrip-" + mode);
            using (TrafficMonitor monitor = new TrafficMonitor(dir))
            {
                AppSettings settings = monitor.Settings.Clone();
                settings.Mode = mode; settings.MinimalLeft = -1920; settings.MinimalTop = -1080;
                settings.HasMinimalPosition = true; settings.MinimalLocked = true; settings.MinimalClickThrough = true;
                settings.MinimalTextTheme = "dark"; settings.MinimalTextShadow = false;
                settings.Left = 123; settings.Top = 456; settings.HasSavedPosition = true;
                monitor.ApplySettings(settings); monitor.Flush();
            }
            using (TrafficMonitor monitor = new TrafficMonitor(dir))
            {
                AppSettings settings = monitor.Settings;
                Check(settings.Mode == mode && settings.DesktopWidgetEnabled == (mode == DisplayMode.Detailed), "Each display mode serializes with a compatible legacy flag");
                Check(settings.MinimalLeft == -1920 && settings.MinimalTop == -1080 && settings.HasMinimalPosition, "Minimal negative monitor coordinates round trip");
                Check(settings.MinimalLocked && settings.MinimalClickThrough && settings.MinimalTextTheme == "dark" && !settings.MinimalTextShadow, "Minimal appearance and interaction preferences round trip");
                Check(settings.Left == 123 && settings.Top == 456 && settings.HasSavedPosition, "Minimal position remains independent of detailed window position");
            }
        }

        string reordered = NewCase("mode-reordered");
        XmlDocument reorderedDocument = new XmlDocument(); reorderedDocument.LoadXml(StoreXml(0, 0, false, false));
        XmlNode modeElement = reorderedDocument.SelectSingleNode("UsageStore/Settings/Mode");
        modeElement.ParentNode.PrependChild(modeElement);
        Write(Path.Combine(reordered, "usage.xml"), reorderedDocument.OuterXml);
        using (TrafficMonitor monitor = new TrafficMonitor(reordered))
            Check(monitor.Settings.Mode == DisplayMode.Minimal, "Mode wins over legacy flag regardless of XML element order");

        string numeric = NewCase("mode-numeric");
        Write(Path.Combine(numeric, "usage.xml"), StoreXml(0, 0, false, false).Replace("<Mode>Minimal</Mode>", "<Mode>1</Mode>"));
        using (TrafficMonitor monitor = new TrafficMonitor(numeric))
        {
            Check(monitor.Settings.Mode == DisplayMode.Minimal && String.IsNullOrEmpty(monitor.RecoveryNotice), "Numeric enum preference normalizes before XML enum deserialization");
            Check(monitor.Tick(new DateTime(2026, 11, 1)).TodayDownload == 123456789, "Numeric mode input preserves traffic history");
        }

        string invalid = NewCase("mode-invalid");
        Write(Path.Combine(invalid, "usage.xml"), StoreXml(0, 0, false, false).Replace("<Mode>Minimal</Mode>", "<Mode>FutureMode</Mode>"));
        using (TrafficMonitor monitor = new TrafficMonitor(invalid))
        {
            Check(monitor.Settings.Mode == DisplayMode.Minimal && String.IsNullOrEmpty(monitor.RecoveryNotice), "Unknown optional mode resets without discarding valid history");
            Check(monitor.Tick(new DateTime(2026, 11, 1)).TodayDownload == 123456789, "Invalid display preference preserves traffic totals");
            AppSettings settings = monitor.Settings.Clone();
            settings.Mode = (DisplayMode)999; settings.MinimalLeft = Double.NaN; settings.MinimalTop = Double.PositiveInfinity;
            settings.HasMinimalPosition = true; settings.MinimalTextTheme = "unknown";
            monitor.ApplySettings(settings);
            Check(monitor.Settings.Mode == DisplayMode.Minimal && !monitor.Settings.HasMinimalPosition && monitor.Settings.MinimalLeft == -1 && monitor.Settings.MinimalTop == -1, "Invalid display enum and minimal position normalize safely");
            Check(monitor.Settings.MinimalTextTheme == "auto", "Unknown text theme returns to automatic");
            settings = monitor.Settings.Clone(); settings.MinimalTextTheme = " LIGHT "; monitor.ApplySettings(settings);
            Check(monitor.Settings.MinimalTextTheme == "light", "Text theme normalizes whitespace and case");
        }
    }

    private static void Positions()
    {
        int index = 0;
        foreach (double[] item in new[] { new[] { -1920d, 25d, 1d }, new[] { 20d, -1080d, 1d }, new[] { -1d, -1d, 0d }, new[] { -1d, 0d, 1d } })
        {
            string dir = NewCase("legacy-position-" + index++);
            Write(Path.Combine(dir, "usage.xml"), StoreXml(item[0], item[1], false, true));
            using (TrafficMonitor monitor = new TrafficMonitor(dir))
            {
                Check(monitor.Settings.HasSavedPosition == (item[2] == 1), "Legacy saved-position migration");
                Check(monitor.Settings.Left == item[0] && monitor.Settings.Top == item[1], "Negative coordinates retained");
                Check(monitor.Settings.Width == 286 && monitor.Settings.Height == 240, "Full-window minimum dimensions");
                monitor.Save(); monitor.Flush();
            }
            using (TrafficMonitor reopened = new TrafficMonitor(dir))
                Check(reopened.Settings.HasSavedPosition == (item[2] == 1), "Position migration round trip");
        }
        string fresh = NewCase("explicit-unsaved-position");
        Write(Path.Combine(fresh, "usage.xml"), StoreXml(100, 100, false, false));
        using (TrafficMonitor monitor = new TrafficMonitor(fresh))
            Check(!monitor.Settings.HasSavedPosition, "Explicit new false is not migrated");
    }

    private static void BackgroundSave()
    {
        string dir = NewCase("background-save");
        string path = Path.Combine(dir, "usage.xml");
        Write(path, StoreXml(0, 0, true, false));
        using (TrafficMonitor monitor = new TrafficMonitor(dir))
        {
            // Lock the existing destination: a write must report failure without
            // holding the monitor lock, and the latest snapshot must survive retry.
            using (FileStream locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                Stopwatch stopwatch = Stopwatch.StartNew();
                monitor.Save();
                Check(stopwatch.ElapsedMilliseconds < 1000, "Save returns without disk work");
                monitor.Flush();
                Check(!String.IsNullOrEmpty(monitor.LastStorageError), "Write failure is exposed");
                DateTime now = new DateTime(2026, 11, 1);
                monitor.AddInterval(now, now.AddSeconds(1), 991, 9);
                monitor.Save();
                MonitorSnapshot snap = monitor.Tick(now.AddSeconds(1));
                Check(!snap.IsMonitoringAvailable && snap.SampledAt == now.AddSeconds(1), "Structured unavailable sample status");
            }
            monitor.Flush();
            Check(String.IsNullOrEmpty(monitor.LastStorageError), "Flush retries newest snapshot successfully");
            // Leave a final update for Dispose to flush.
            monitor.AddInterval(new DateTime(2026, 11, 1), new DateTime(2026, 11, 1, 0, 0, 2), 10, 20);
        }
        using (TrafficMonitor reopened = new TrafficMonitor(dir))
        {
            MonitorSnapshot snap = reopened.Tick(new DateTime(2026, 11, 1));
            Check(snap.TodayDownload == 123457790 && snap.TodayUpload == 152, "Latest retry and final Dispose data persist");
            Check(String.IsNullOrEmpty(reopened.RecoveryNotice), "Normal data round trip needs no recovery");
            // Exercise the native MIB_IF_ROW2 marshaling on the host.
            Check(reopened.RefreshAdapters().Count >= 1, "Native adapter metadata enumeration");
            Type native = typeof(TrafficMonitor).GetNestedType("NativeInterfaceRow", BindingFlags.NonPublic);
            Check(Marshal.SizeOf(native) == 1352, "MIB_IF_ROW2 native size");
            Check(Marshal.OffsetOf(native, "Flags").ToInt32() == 1152, "MIB_IF_ROW2 flags offset");
            Check(Marshal.OffsetOf(native, "Counters").ToInt32() == 1192, "MIB_IF_ROW2 counters offset");
            MethodInfo query = typeof(TrafficMonitor).GetMethod("TryGetHardwareAdapter", BindingFlags.Instance | BindingFlags.NonPublic);
            int queried = 0, hardware = 0;
            foreach (NetworkInterface adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                object[] arguments = { adapter, false };
                if ((bool)query.Invoke(reopened, arguments)) { queried++; if ((bool)arguments[1]) hardware++; }
            }
            Check(queried > 0, "Native metadata successfully read on host interfaces");
            Console.WriteLine("Native adapter metadata: " + queried + " queried, " + hardware + " hardware interfaces.");
        }
    }

    private static void Accounting()
    {
        foreach (int year in new[] { 2024, 2025, 2026 })
        for (int month = 1; month <= 12; month++)
        for (int billing = 1; billing <= 31; billing++)
        foreach (int day in new[] { 1, 15, DateTime.DaysInMonth(year, month) })
        {
            DateTime now = new DateTime(year, month, day, 12, 0, 0);
            var period = TrafficMonitor.BillingPeriod(now, billing);
            Check(period.Item1 <= now && now < period.Item2, "Billing boundaries contain current date");
            Check(TrafficMonitor.BillingPeriod(period.Item2, billing).Item1 == period.Item2, "Billing periods are contiguous");
        }
        using (TrafficMonitor monitor = new TrafficMonitor(NewCase("midnight")))
        {
            monitor.Settings.AdapterId = "test-no-adapter";
            monitor.AddInterval(new DateTime(2026, 10, 31, 23, 59, 58), new DateTime(2026, 11, 1, 0, 0, 2), 101, 33);
            MonitorSnapshot snap = monitor.Tick(new DateTime(2026, 11, 1));
            Check(snap.History.Sum(d => d.Download) == 101 && snap.History.Sum(d => d.Upload) == 33, "Cross-midnight byte conservation");
        }
    }

    public static int Main(string[] args)
    {
        root = args.Length == 0 ? Path.Combine(Path.GetTempPath(), "FlowDesk-tests-" + Guid.NewGuid().ToString("N")) : args[0];
        Directory.CreateDirectory(root);
        try
        {
            Recovery(); Positions(); Modes(); BackgroundSave(); Accounting();
            Console.WriteLine("PASS: " + checks + " checks; isolated data in " + root);
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }
}
