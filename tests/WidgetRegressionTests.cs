using System;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FlowDesk;

internal static class WidgetRegressionTests
{
    private static readonly BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static int checks;
    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception("FAIL: " + name);
        checks++; Console.WriteLine("PASS " + name);
    }
    private static object Field(object value, string name) { return value.GetType().GetField(name, Private).GetValue(value); }
    private static void Call(object value, string name, params object[] args) { value.GetType().GetMethod(name, Private).Invoke(value, args); }
    private static FrameworkElement Layout(Widget widget)
    {
        FrameworkElement ui = (FrameworkElement)Field(widget, "ui");
        ui.Measure(new Size(widget.Width, widget.Height));
        ui.Arrange(new Rect(0, 0, widget.Width, widget.Height));
        ui.UpdateLayout(); return ui;
    }
    private static void SavePreview(FrameworkElement ui, string path)
    {
        RenderTargetBitmap bitmap = new RenderTargetBitmap((int)Math.Ceiling(ui.ActualWidth), (int)Math.Ceiling(ui.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(ui);
        PngBitmapEncoder encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (FileStream file = File.Create(path)) encoder.Save(file);
    }
    [STAThread]
    private static int Main(string[] args)
    {
        string root = Path.GetFullPath(args[0]); Directory.CreateDirectory(root);
        try
        {
            Check(Startup.HasEntry("\"C:\\old\\FlowDesk.exe\""), "old executable startup entry is enabled");
            Check(!Startup.HasEntry(null) && !Startup.HasEntry(" "), "missing startup entry is disabled");
            Check(Startup.CommandFor(@"C:\Apps\FlowDesk.exe", @"C:\").EndsWith("--data-dir \"C:\\\\\""), "root data directory quoting");
            using (TrafficMonitor monitor = new TrafficMonitor(Path.Combine(root, "data")))
            using (EventWaitHandle activation = new EventWaitHandle(false, EventResetMode.AutoReset))
            {
                AppSettings settings = monitor.Settings.Clone();
                settings.Left = -600; settings.Top = -100; settings.HasSavedPosition = true;
                settings.Width = 286; settings.Height = 260; settings.QuotaGB = 1;
                monitor.ApplySettings(settings);
                Widget widget = new Widget(monitor, activation, Path.Combine(root, "data"), false);
                try
                {
                    Check(widget.Left == -600 && widget.Top == -100, "saved negative coordinates are respected before screen validation");
                    Call(widget, "SetCompact", true);
                    Check(widget.Height == 124 && widget.MinHeight == 124, "compact mode has its own minimum height");
                    Call(widget, "SetCompact", false);
                    Check(widget.Height == 260 && widget.MinHeight == 240, "restore full mode dimensions");
                    Call(widget, "ResizeGripDragDelta", null, new DragDeltaEventArgs(-5000, -5000));
                    Check(widget.Width == 286 && widget.Height == 240, "full-mode resize clamps to visible minimum");
                    FrameworkElement ui = Layout(widget);
                    foreach (string name in new[] { "TodayValue", "PackageButton", "HistoryButton", "ProcessButton" })
                    {
                        FrameworkElement item = (FrameworkElement)ui.FindName(name);
                        Point at = item.TransformToAncestor(ui).Transform(new Point());
                        Check(at.Y >= 0 && at.Y + item.ActualHeight <= widget.Height && at.X >= 0 && at.X + item.ActualWidth <= widget.Width,
                            "minimum window contains " + name);
                    }
                    settings = monitor.Settings.Clone(); settings.AdapterId = "test-missing-adapter"; monitor.ApplySettings(settings);
                    MonitorSnapshot snapshot = monitor.Tick(DateTime.Now);
                    Call(widget, "RenderSnapshot", snapshot, 0);
                    Check(!snapshot.IsMonitoringAvailable, "invalid adapter is unavailable");
                    Check(((System.Windows.Shapes.Ellipse)ui.FindName("StatusDot")).Fill.ToString() == "#FFFFD08A", "invalid adapter gets amber status");
                    Check(((TextBlock)ui.FindName("AdapterText")).Text.Contains("不存在"), "invalid adapter explains failure");
                    DateTime previous = DateTime.Today.AddMonths(-1);
                    monitor.AddInterval(previous, previous.AddSeconds(1), 1000000001L, 0);
                    MonitorSnapshot prior = monitor.Tick(previous.AddSeconds(2));
                    Call(widget, "RenderSnapshot", prior, 100);
                    Check((string)Field(widget, "pendingAlert") != null, "quota alert set in previous cycle");
                    snapshot = monitor.Tick(DateTime.Now);
                    Call(widget, "RenderSnapshot", snapshot, 0);
                    Check(Field(widget, "pendingAlert") == null, "new cycle clears old quota alert");
                    SavePreview(Layout(widget), Path.Combine(root, "widget-minimum.png"));
                    Call(widget, "ResizeGripDragDelta", null, new DragDeltaEventArgs(5000, 5000));
                    Check(widget.Width == 900 && widget.Height == 700, "resize clamps to same maximum as persisted settings");
                    settings = monitor.Settings.Clone(); settings.AdapterId = "auto"; monitor.ApplySettings(settings);
                    widget.Left = -600; widget.Top = -100;
                    Call(widget, "SavePosition");
                    Check(monitor.Settings.HasSavedPosition && monitor.Settings.Left == -600, "saving keeps explicit position flag and negative coordinates");
                }
                finally { Call(widget, "Cleanup"); }
            }
            Console.WriteLine("Widget checks passed: " + checks); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
