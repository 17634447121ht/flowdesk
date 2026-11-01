using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using FlowDesk;
using Forms = System.Windows.Forms;

internal static class TaskbarRegressionTests
{
    private static readonly BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static int checks;

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception("FAIL: " + name);
        checks++;
        Console.WriteLine("PASS " + name);
    }

    private static object Field(object value, string name) { return value.GetType().GetField(name, Private).GetValue(value); }
    private static void SetField(object value, string name, object replacement) { value.GetType().GetField(name, Private).SetValue(value, replacement); }
    private static void Call(object value, string name, params object[] args) { value.GetType().GetMethod(name, Private).Invoke(value, args); }

    private static void CheckSelectedMode(Widget widget, TrafficMonitor monitor, DisplayMode expected)
    {
        Forms.ToolStripMenuItem ultra = (Forms.ToolStripMenuItem)Field(widget, "modeUltra");
        Forms.ToolStripMenuItem minimal = (Forms.ToolStripMenuItem)Field(widget, "modeMinimal");
        Forms.ToolStripMenuItem detailed = (Forms.ToolStripMenuItem)Field(widget, "modeDetailed");
        Check(monitor.Settings.Mode == expected &&
            ultra.Checked == (expected == DisplayMode.UltraMinimal) &&
            minimal.Checked == (expected == DisplayMode.Minimal) &&
            detailed.Checked == (expected == DisplayMode.Detailed),
            "display mode and exclusive menu selection agree: " + expected);
        Forms.NotifyIcon tray = (Forms.NotifyIcon)Field(widget, "tray");
        object appIcon = Field(widget, "applicationIcon");
        Check(tray.Icon != null && Object.ReferenceEquals(tray.Icon, appIcon) == (expected != DisplayMode.UltraMinimal),
            "selected mode uses the appropriate application or rate tray icon: " + expected);
        Check(!widget.IsVisible && !((Window)Field(widget, "minimalWindow")).IsVisible &&
            !tray.Visible && !((Forms.NotifyIcon)Field(widget, "uploadTray")).Visible,
            "isolated fixture does not display desktop surfaces: " + expected);
    }

    private static void CheckModeSwitching(Widget widget, TrafficMonitor monitor)
    {
        AppSettings positions = monitor.Settings.Clone();
        positions.Left = 45; positions.Top = 65; positions.HasSavedPosition = true;
        positions.MinimalLeft = 125; positions.MinimalTop = 165; positions.HasMinimalPosition = true;
        monitor.ApplySettings(positions);
        foreach (DisplayMode mode in new[] { DisplayMode.Detailed, DisplayMode.UltraMinimal, DisplayMode.Minimal, DisplayMode.Detailed, DisplayMode.Minimal })
        {
            string field = mode == DisplayMode.Detailed ? "modeDetailed" : mode == DisplayMode.Minimal ? "modeMinimal" : "modeUltra";
            ((Forms.ToolStripMenuItem)Field(widget, field)).PerformClick();
            CheckSelectedMode(widget, monitor, mode);
            Check(monitor.Settings.Left == 45 && monitor.Settings.Top == 65 &&
                monitor.Settings.MinimalLeft == 125 && monitor.Settings.MinimalTop == 165 &&
                monitor.Settings.HasSavedPosition && monitor.Settings.HasMinimalPosition,
                "mode switching preserves independent saved locations: " + mode);
        }

        Forms.ToolStripMenuItem locked = (Forms.ToolStripMenuItem)Field(widget, "lockedMinimal");
        Forms.ToolStripMenuItem through = (Forms.ToolStripMenuItem)Field(widget, "clickThroughMinimal");
        locked.PerformClick();
        Check(monitor.Settings.MinimalLocked && locked.Checked, "tray menu locks the minimal window");
        through.PerformClick();
        Check(monitor.Settings.MinimalClickThrough && through.Checked, "tray menu enables minimal click-through");
        locked.PerformClick();
        Check(!monitor.Settings.MinimalLocked && monitor.Settings.MinimalClickThrough && !locked.Checked,
            "locking and click-through remain independent choices");
        through.PerformClick();
        Check(!monitor.Settings.MinimalClickThrough && !through.Checked,
            "tray menu restores interaction after click-through was enabled");

        Window dialogGuard = new Window();
        SetField(widget, "currentDialog", dialogGuard);
        ((Forms.ToolStripMenuItem)Field(widget, "modeUltra")).PerformClick();
        Check(Field(widget, "currentDialog") == dialogGuard && monitor.Settings.Mode == DisplayMode.UltraMinimal,
            "mode selection keeps the active dialog and records the next display mode");
        Call(widget, "DialogFinished");
        CheckSelectedMode(widget, monitor, DisplayMode.UltraMinimal);
        dialogGuard.Close();
        ((Forms.ToolStripMenuItem)Field(widget, "modeMinimal")).PerformClick();
        CheckSelectedMode(widget, monitor, DisplayMode.Minimal);
    }

    private static void PumpUntil(Func<bool> condition, int timeoutMilliseconds)
    {
        Stopwatch elapsed = Stopwatch.StartNew();
        while (!condition() && elapsed.ElapsedMilliseconds < timeoutMilliseconds)
        {
            DispatcherFrame frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(delegate { frame.Continue = false; }));
            Dispatcher.PushFrame(frame);
            Thread.Sleep(5);
        }
        if (!condition()) throw new TimeoutException("Timed out waiting for background sampling.");
    }

    private static AppSettings SaveDialogWithoutShowing(AppSettings current)
    {
        SettingsDialog dialog = new SettingsDialog(current, new List<AdapterChoice>(), false);
        try
        {
            try { Call(dialog, "Save", null, new RoutedEventArgs()); }
            catch (TargetInvocationException error)
            {
                // Save populates Result before assigning DialogResult. WPF rejects
                // DialogResult on a never-shown window; no real dialog is needed.
                if (!(error.InnerException is InvalidOperationException) || dialog.Result == null) throw;
            }
            return dialog.Result;
        }
        finally { dialog.Close(); }
    }

    [STAThread]
    private static int Main(string[] args)
    {
        string root = Path.GetFullPath(args[0]);
        Directory.CreateDirectory(root);
        Widget widget = null;
        try
        {
            Check(new AppSettings().Mode == DisplayMode.Minimal, "fresh settings default to the transparent minimal window");
            string preferencesPath = Path.Combine(root, "preferences");
            using (TrafficMonitor preferences = new TrafficMonitor(preferencesPath))
            {
                AppSettings value = preferences.Settings.Clone();
                value.DesktopWidgetEnabled = true;
                AppSettings saved = SaveDialogWithoutShowing(value);
                Check(saved != null && saved.DesktopWidgetEnabled, "settings dialog preserves desktop mode enabled");
                preferences.ApplySettings(saved);
            }
            using (TrafficMonitor preferences = new TrafficMonitor(preferencesPath))
            {
                Check(preferences.Settings.DesktopWidgetEnabled, "desktop mode survives saving and reloading");
                AppSettings value = preferences.Settings.Clone();
                value.DesktopWidgetEnabled = false;
                Check(!SaveDialogWithoutShowing(value).DesktopWidgetEnabled, "settings dialog preserves taskbar mode");
                value.Mode = DisplayMode.Minimal;
                Check(SaveDialogWithoutShowing(value).Mode == DisplayMode.Minimal,
                    "settings dialog preserves the transparent minimal mode");
            }

            using (TrafficMonitor monitor = new TrafficMonitor(Path.Combine(root, "lifecycle")))
            using (EventWaitHandle activation = new EventWaitHandle(false, EventResetMode.AutoReset))
            {
                Application app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
                widget = new Widget(monitor, activation, Path.Combine(root, "lifecycle"), false);
                app.MainWindow = widget;
                Check(!widget.IsVisible && !widget.ShowInTaskbar, "constructed widget has no desktop or taskbar window");
                Check(!((Forms.NotifyIcon)Field(widget, "tray")).Visible && !((Forms.NotifyIcon)Field(widget, "uploadTray")).Visible,
                    "fixture keeps both notification icons out of user desktop");
                widget.Start();
                Check(new WindowInteropHelper(widget).Handle != IntPtr.Zero, "hidden start initializes owner handle");
                Check(!widget.IsVisible, "start does not display widget");
                CheckSelectedMode(widget, monitor, DisplayMode.Minimal);
                Check(app.MainWindow == widget && !Object.ReferenceEquals(app.MainWindow, Field(widget, "minimalWindow")),
                    "the hidden owner remains the application main window in minimal mode");
                Check(((DispatcherTimer)Field(widget, "timer")).IsEnabled, "hidden start enables sampling timer");
                PumpUntil(delegate { return Field(widget, "snapshot") != null; }, 10000);
                DateTime first = ((MonitorSnapshot)Field(widget, "snapshot")).SampledAt;
                PumpUntil(delegate { return ((MonitorSnapshot)Field(widget, "snapshot")).SampledAt > first; }, 10000);
                Check(!widget.IsVisible, "sampling continues while window remains hidden");
                widget.Start();
                Check((bool)Field(widget, "started") && ((DispatcherTimer)Field(widget, "timer")).IsEnabled,
                    "starting twice keeps one active lifecycle");

                // Inject popup state without showing or activating any test window.
                SetField(widget, "trayPopup", true);
                Call(widget, "HandleDeactivated");
                Check(!(bool)Field(widget, "trayPopup") && !widget.IsVisible, "temporary popup state clears on deactivation");
                Window dialogGuard = new Window();
                SetField(widget, "trayPopup", true);
                SetField(widget, "currentDialog", dialogGuard);
                Call(widget, "HandleDeactivated");
                Check((bool)Field(widget, "trayPopup"), "deactivation leaves active child dialog owner intact");
                Call(widget, "TrayMouseClick", null, new Forms.MouseEventArgs(Forms.MouseButtons.Left, 1, 0, 0, 0));
                Check((bool)Field(widget, "trayPopup") && Field(widget, "currentDialog") == dialogGuard,
                    "left tray click preserves active child dialog owner");
                Call(widget, "TrayMouseClick", null, new Forms.MouseEventArgs(Forms.MouseButtons.Right, 1, 0, 0, 0));
                Check((bool)Field(widget, "trayPopup") && Field(widget, "currentDialog") == dialogGuard,
                    "right tray click preserves active child dialog owner");
                Call(widget, "DialogFinished");
                Check(Field(widget, "currentDialog") == null && !(bool)Field(widget, "trayPopup"),
                    "finishing child dialog returns to the selected persistent mode");
                dialogGuard.Close();

                AppSettings desktop = monitor.Settings.Clone();
                desktop.DesktopWidgetEnabled = true;
                monitor.ApplySettings(desktop);
                SetField(widget, "trayPopup", false);
                Call(widget, "HandleDeactivated");
                Check(monitor.Settings.DesktopWidgetEnabled, "deactivation preserves desktop preference");
                Call(widget, "SetDesktopWidgetEnabled", false);
                Check(!monitor.Settings.DesktopWidgetEnabled && !widget.IsVisible, "switching to taskbar mode hides details and updates preference");

                CheckModeSwitching(widget, monitor);
                Window minimalWindow = (Window)Field(widget, "minimalWindow");
                Check(app.Windows.Cast<Window>().Contains(minimalWindow), "minimal window participates in application cleanup");

                bool closeTick = false;
                DispatcherTimer closeWindow = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                closeWindow.Tick += delegate
                {
                    closeWindow.Stop();
                    closeTick = true;
                    widget.Close();
                };
                closeWindow.Start();
                int exitCode = app.Run();
                Check(closeTick && exitCode == 0, "closing hidden main window exits Application.Run message loop");
                Check((bool)Field(widget, "closing"), "closing an unshown main window runs cleanup");
                Check(!((DispatcherTimer)Field(widget, "timer")).IsEnabled && !((DispatcherTimer)Field(widget, "resizeSaveTimer")).IsEnabled,
                    "hidden close stops both UI timers");
                Check(!((Forms.NotifyIcon)Field(widget, "tray")).Visible && !((Forms.NotifyIcon)Field(widget, "uploadTray")).Visible,
                    "hidden close removes both notification icons");
                Check(((ProcessTrafficMonitor)Field(widget, "processTraffic")).IsShutdownComplete, "hidden close disposes process traffic worker");
                Check(!app.Windows.Cast<Window>().Contains(minimalWindow), "exiting closes the minimal window as well as its hidden owner");
                Call(widget, "Cleanup");
                Check((bool)Field(widget, "closing"), "cleanup is idempotent");
                widget.WaitForShutdown();
                widget = null;
            }
            Console.WriteLine("Taskbar checks passed: " + checks);
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
        finally { if (widget != null) { Call(widget, "Cleanup"); widget.WaitForShutdown(); } }
    }
}
