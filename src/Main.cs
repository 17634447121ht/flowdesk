using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace FlowDesk
{
    internal static class Program
    {
        [STAThread]
        public static void Main(string[] args)
        {
            string dataDir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FlowDesk");
            for (int i = 0; i + 1 < args.Length; i++)
                if (args[i] == "--data-dir") dataDir = System.IO.Path.GetFullPath(args[++i]);
            string suffix;
            using (var hash = SHA256.Create()) suffix = BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(dataDir.ToUpperInvariant()))).Replace("-", "").Substring(0, 20);
            bool owns;
            using (var mutex = new Mutex(true, @"Local\FlowDesk_" + suffix, out owns))
            using (var activate = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\FlowDesk_Show_" + suffix))
            {
                if (!owns) { activate.Set(); return; }
                try
                {
                    var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
                    app.DispatcherUnhandledException += delegate(object sender, DispatcherUnhandledExceptionEventArgs e)
                    {
                        Log(dataDir, e.Exception);
                        MessageBox.Show("操作未完成：" + e.Exception.Message + "\n记录保存在数据目录的 errors.log 中。", "流量浮窗", MessageBoxButton.OK, MessageBoxImage.Warning);
                        e.Handled = true;
                    };
                    using (var monitor = new TrafficMonitor(dataDir))
                    {
                        var widget = new Widget(monitor, activate, dataDir);
                        app.MainWindow = widget;
                        widget.Show();
                        app.Run();
                    }
                }
                catch (Exception ex)
                {
                    Log(dataDir, ex);
                    MessageBox.Show("流量浮窗未能启动：\n" + ex.Message, "流量浮窗", MessageBoxButton.OK, MessageBoxImage.Error);
                }
                finally { mutex.ReleaseMutex(); }
            }
        }

        internal static void Log(string dir, Exception ex)
        {
            try { Directory.CreateDirectory(dir); File.AppendAllText(System.IO.Path.Combine(dir, "errors.log"), DateTime.Now.ToString("s") + " " + ex + Environment.NewLine, Encoding.UTF8); }
            catch { }
        }

    }

    internal static class Startup
    {
        private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "FlowDesk.NetworkWidget";
        private static string Command { get { return "\"" + Process.GetCurrentProcess().MainModule.FileName + "\""; } }
        public static bool Enabled
        {
            get
            {
                try { using (var key = Registry.CurrentUser.OpenSubKey(KeyPath)) return key != null && String.Equals(key.GetValue(ValueName) as string, Command, StringComparison.OrdinalIgnoreCase); }
                catch { return false; }
            }
        }
        public static void Set(bool enabled)
        {
            using (var key = Registry.CurrentUser.CreateSubKey(KeyPath))
            {
                if (key == null) throw new IOException("无法更新开机启动设置。");
                if (enabled) key.SetValue(ValueName, Command);
                else key.DeleteValue(ValueName, false);
            }
        }
    }

    internal static class Units
    {
        public static string Bytes(double bytes)
        {
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            int index = 0;
            bytes = Math.Max(0, bytes);
            while (bytes >= 1000 && index < units.Length - 1) { bytes /= 1000; index++; }
            return bytes.ToString(index == 0 ? "0" : bytes >= 100 ? "0.0" : "0.00", CultureInfo.CurrentCulture) + " " + units[index];
        }
    }

    internal sealed class Widget : Window
    {
        private readonly TrafficMonitor monitor;
        private readonly EventWaitHandle activate;
        private readonly string dataDir;
        private readonly FrameworkElement ui;
        private readonly DispatcherTimer timer;
        private readonly Forms.NotifyIcon tray;
        private readonly Forms.ToolStripMenuItem pinned;
        private readonly Queue<double> downHistory = new Queue<double>();
        private readonly Queue<double> upHistory = new Queue<double>();
        private MonitorSnapshot snapshot;
        private Window currentDialog;
        private bool closing;
        private bool compact;
        private bool hasStorageNotice;
        private string pendingAlert;
        private double normalWidth;
        private double normalHeight;
        private DispatcherTimer resizeSaveTimer;

        private const string Layout = @"<Border xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'
            Margin='4' Padding='13,10,13,9' CornerRadius='14' Background='#121C2D' BorderBrush='#2C3D54' BorderThickness='1'>
          <Border.Resources>
            <Style TargetType='TextBlock'><Setter Property='Foreground' Value='#F3F6FC'/><Setter Property='FontFamily' Value='Microsoft YaHei UI'/><Setter Property='FontSize' Value='12'/></Style>
            <Style TargetType='Button'>
              <Setter Property='Foreground' Value='#A8B9CE'/><Setter Property='Background' Value='Transparent'/><Setter Property='BorderThickness' Value='0'/><Setter Property='Cursor' Value='Hand'/><Setter Property='FontFamily' Value='Microsoft YaHei UI'/><Setter Property='FontSize' Value='12'/><Setter Property='Padding' Value='6,4'/>
              <Setter Property='Template'><Setter.Value><ControlTemplate TargetType='Button'><Border x:Name='b' Background='{TemplateBinding Background}' Padding='{TemplateBinding Padding}' CornerRadius='5'><ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/></Border><ControlTemplate.Triggers><Trigger Property='IsMouseOver' Value='True'><Setter TargetName='b' Property='Background' Value='#2A3A51'/><Setter Property='Foreground' Value='#FFFFFF'/></Trigger><Trigger Property='IsKeyboardFocused' Value='True'><Setter TargetName='b' Property='Background' Value='#344962'/></Trigger></ControlTemplate.Triggers></ControlTemplate></Setter.Value></Setter>
            </Style>
          </Border.Resources>
          <Grid>
            <Grid.RowDefinitions><RowDefinition x:Name='HeaderRow' Height='24'/><RowDefinition x:Name='SpeedRow' Height='55'/><RowDefinition x:Name='ChartRow' Height='28'/><RowDefinition x:Name='DividerRow' Height='10'/><RowDefinition x:Name='TodayRow' Height='22'/><RowDefinition x:Name='CycleRow' Height='22'/><RowDefinition x:Name='QuotaRow' Height='10'/><RowDefinition x:Name='FooterRow' Height='23'/></Grid.RowDefinitions>
            <Grid x:Name='DragHeader' Background='Transparent' Cursor='SizeAll'>
              <StackPanel Orientation='Horizontal' VerticalAlignment='Center'><Ellipse x:Name='StatusDot' Width='5' Height='5' Fill='#72E5CE' Margin='0,0,6,0'/><TextBlock Text='流量浮窗' FontWeight='SemiBold' FontSize='12'/><TextBlock Text='FLOWDESK' FontFamily='Segoe UI' FontSize='8' Foreground='#70849F' Margin='7,2,0,0'/></StackPanel>
              <StackPanel Orientation='Horizontal' HorizontalAlignment='Right'><Button x:Name='SettingsButton' Content='⚙' FontSize='14' Width='24' ToolTip='设置' AutomationProperties.Name='设置'/><Button x:Name='HideButton' Content='−' FontSize='16' Width='23' ToolTip='切换紧凑模式' AutomationProperties.Name='切换紧凑模式'/></StackPanel>
            </Grid>
            <Grid Grid.Row='1' Margin='0,6,0,0'>
              <Grid.ColumnDefinitions><ColumnDefinition/><ColumnDefinition/></Grid.ColumnDefinitions>
              <StackPanel><TextBlock Text='↓  下载' Foreground='#72E5CE' FontSize='10'/><StackPanel Orientation='Horizontal' Margin='0,1,0,0'><TextBlock x:Name='DownValue' Text='0' FontFamily='Segoe UI' FontSize='22' FontWeight='SemiBold'/><TextBlock x:Name='DownUnit' Text='B/s' FontFamily='Segoe UI' FontSize='10' Margin='4,13,0,0' Foreground='#91A3BC'/></StackPanel></StackPanel>
              <StackPanel Grid.Column='1' Margin='8,0,0,0'><TextBlock Text='↑  上传' Foreground='#B3A6FF' FontSize='10'/><StackPanel Orientation='Horizontal' Margin='0,1,0,0'><TextBlock x:Name='UpValue' Text='0' FontFamily='Segoe UI' FontSize='22' FontWeight='SemiBold'/><TextBlock x:Name='UpUnit' Text='B/s' FontFamily='Segoe UI' FontSize='10' Margin='4,13,0,0' Foreground='#91A3BC'/></StackPanel></StackPanel>
            </Grid>
            <Grid Grid.Row='2' Margin='0,2,0,0' ToolTip='最近 60 秒速度 · 绿色下载，紫色上传'><Border BorderThickness='0,0,0,1' BorderBrush='#2A394F' VerticalAlignment='Center'/><Canvas x:Name='Chart' ClipToBounds='True'/></Grid>
            <Border Grid.Row='3' Height='1' Background='#28374B' VerticalAlignment='Center'/>
            <Grid Grid.Row='4'><TextBlock Text='今日用量' Foreground='#97A9C1' VerticalAlignment='Center'/><TextBlock x:Name='TodayValue' Text='0 B' HorizontalAlignment='Right' VerticalAlignment='Center' FontWeight='SemiBold'/></Grid>
            <Grid Grid.Row='5'><TextBlock x:Name='CycleLabel' Text='本月用量' Foreground='#97A9C1' VerticalAlignment='Center'/><TextBlock x:Name='CycleValue' Text='0 B' HorizontalAlignment='Right' VerticalAlignment='Center' FontWeight='SemiBold'/></Grid>
            <Grid Grid.Row='6' VerticalAlignment='Center' Height='4'><Border Background='#28384F' CornerRadius='2'/><Border x:Name='QuotaFill' Background='#72E5CE' Width='0' HorizontalAlignment='Left' CornerRadius='2'/></Grid>
            <Grid Grid.Row='7'><Grid.ColumnDefinitions><ColumnDefinition/><ColumnDefinition Width='Auto'/><ColumnDefinition Width='Auto'/></Grid.ColumnDefinitions><TextBlock x:Name='AdapterText' Text='正在连接网卡…' Foreground='#788CA8' FontSize='9' VerticalAlignment='Center' TextTrimming='CharacterEllipsis' Margin='0,0,4,0'/><Button x:Name='PackageButton' Grid.Column='1' Content='更新套餐' FontSize='9' Padding='3,2' ToolTip='更新套餐额度和结算日' AutomationProperties.Name='更新套餐'/><Button x:Name='HistoryButton' Grid.Column='2' Content='用量记录 ↗' FontSize='9' Padding='3,2' Margin='4,0,0,0' ToolTip='历史记录与导出 CSV' AutomationProperties.Name='用量记录'/></Grid>
            <Thumb x:Name='ResizeGrip' Grid.RowSpan='8' Width='14' Height='14' HorizontalAlignment='Right' VerticalAlignment='Bottom' Background='Transparent' Cursor='SizeNWSE' ToolTip='拖动调整大小'/>
          </Grid>
        </Border>";

        public Widget(TrafficMonitor traffic, EventWaitHandle showEvent, string storageDir)
        {
            monitor = traffic; activate = showEvent; dataDir = storageDir;
            Title = "流量浮窗 · FlowDesk";
            Width = monitor.Settings.Width;
            Height = monitor.Settings.Height;
            normalWidth = Width;
            normalHeight = Height;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.CanResize;
            MinWidth = 230;
            MinHeight = 120;
            AllowsTransparency = true; Background = Brushes.Transparent;
            ShowInTaskbar = false;
            UseLayoutRounding = true;
            ui = (FrameworkElement)XamlReader.Parse(Layout);
            Content = ui;
            ((Button)ui.FindName("SettingsButton")).Click += delegate { OpenSettings(); };
            ((Button)ui.FindName("HideButton")).Click += delegate { SetCompact(!compact); };
            ((Button)ui.FindName("PackageButton")).Click += delegate { OpenPackage(); };
            ((Button)ui.FindName("HistoryButton")).Click += delegate { OpenHistory(); };
            ((Thumb)ui.FindName("ResizeGrip")).DragDelta += ResizeGripDragDelta;
            SizeChanged += WidgetSizeChanged;
            ((Grid)ui.FindName("DragHeader")).MouseLeftButtonDown += delegate(object sender, MouseButtonEventArgs e)
            {
                if (e.ButtonState == MouseButtonState.Pressed) { DragMove(); KeepOnScreen(); SavePosition(); }
            };
            MouseRightButtonUp += delegate { tray.ContextMenuStrip.Show(Forms.Cursor.Position); };
            PreviewKeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.Key == Key.Escape) Hide();
                if (e.Key == Key.F2) OpenSettings();
            };
            var menu = new Forms.ContextMenuStrip();
            menu.Items.Add("显示浮窗", null, delegate { Reveal(); });
            menu.Items.Add("收起到托盘（继续统计）", null, delegate { Hide(); });
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add("更新套餐…", null, delegate { OpenPackage(); });
            menu.Items.Add("用量记录…", null, delegate { OpenHistory(); });
            menu.Items.Add("设置…", null, delegate { OpenSettings(); });
            pinned = new Forms.ToolStripMenuItem("总在最前");
            pinned.Click += delegate { monitor.Settings.TopMost = !Topmost; ApplyAppearance(); monitor.Save(); };
            menu.Items.Add(pinned);
            menu.Items.Add("打开数据文件夹", null, delegate { Process.Start(new ProcessStartInfo(dataDir) { UseShellExecute = true }); });
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add("退出并保存", null, delegate { Close(); });
            tray = new Forms.NotifyIcon { Text = "流量浮窗 · FlowDesk", Visible = true, ContextMenuStrip = menu };
            try { tray.Icon = System.Drawing.Icon.ExtractAssociatedIcon(Process.GetCurrentProcess().MainModule.FileName); }
            catch { tray.Icon = System.Drawing.SystemIcons.Information; }
            tray.DoubleClick += delegate { Reveal(); };
            tray.BalloonTipClicked += delegate { Reveal(); };
            monitor.QuotaReached += delegate(int threshold) { pendingAlert = "本账期流量已达到套餐的 " + threshold + "%"; ShowNotice(pendingAlert, Forms.ToolTipIcon.Warning); };
            ApplyAppearance();
            var bounds = SystemParameters.WorkArea;
            Left = monitor.Settings.Left >= 0 ? monitor.Settings.Left : bounds.Right - Width - 24;
            Top = monitor.Settings.Top >= 0 ? monitor.Settings.Top : bounds.Top + 40;
            KeepOnScreen();
            timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            timer.Tick += delegate { if (activate.WaitOne(0)) Reveal(); UpdateSample(); };
            resizeSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
            resizeSaveTimer.Tick += delegate
            {
                resizeSaveTimer.Stop();
                if (!compact && !closing) { monitor.Settings.Width = Width; monitor.Settings.Height = Height; monitor.Save(); }
            };
            Loaded += delegate { UpdateSample(); timer.Start(); };
            Closing += delegate { Cleanup(); };
            SystemEvents.SessionEnding += SessionEnding;
            SystemEvents.DisplaySettingsChanged += DisplaySettingsChanged;
        }

        private TextBlock Text(string name) { return (TextBlock)ui.FindName(name); }
        private void ShowNotice(string text, Forms.ToolTipIcon kind)
        {
            tray.BalloonTipTitle = "流量浮窗"; tray.BalloonTipText = text; tray.BalloonTipIcon = kind; tray.ShowBalloonTip(8000);
        }
        private void ApplyAppearance()
        {
            Topmost = monitor.Settings.TopMost;
            Opacity = Math.Max(.7, Math.Min(1, monitor.Settings.Opacity));
            pinned.Checked = Topmost;
        }
        private void Reveal()
        {
            Show(); KeepOnScreen(); Activate();
        }
        private void KeepOnScreen()
        {
            Rect window = new Rect(Left, Top, Width, Height);
            var source = PresentationSource.FromVisual(this);
            Matrix transform = source != null && source.CompositionTarget != null ? source.CompositionTarget.TransformFromDevice : Matrix.Identity;
            bool visible = Forms.Screen.AllScreens.Any(delegate(Forms.Screen s)
            {
                var area = s.WorkingArea;
                Point a = transform.Transform(new Point(area.Left, area.Top));
                Point b = transform.Transform(new Point(area.Right, area.Bottom));
                return new Rect(a, b).Contains(new Rect(Left + 16, Top + 16, Width - 32, 32));
            });
            if (!visible)
            {
                var area = SystemParameters.WorkArea;
                Left = Math.Max(area.Left, Math.Min(Left, area.Right - Width));
                Top = Math.Max(area.Top, Math.Min(Top, area.Bottom - Height));
            }
        }
        private RowDefinition LayoutRow(int index)
        {
            return ((Grid)((Border)ui).Child).RowDefinitions[index];
        }

        private void SetCompact(bool value)
        {
            if (compact == value) return;
            Button toggle = (Button)ui.FindName("HideButton");
            Thumb grip = (Thumb)ui.FindName("ResizeGrip");
            if (value)
            {
                normalWidth = Width;
                normalHeight = Height;
                compact = true;
                LayoutRow(2).Height = new GridLength(0);
                LayoutRow(3).Height = new GridLength(0);
                LayoutRow(5).Height = new GridLength(0);
                LayoutRow(6).Height = new GridLength(0);
                LayoutRow(7).Height = new GridLength(0);
                ((FrameworkElement)ui.FindName("Chart")).Visibility = Visibility.Collapsed;
                ((FrameworkElement)ui.FindName("CycleLabel")).Visibility = Visibility.Collapsed;
                ((FrameworkElement)ui.FindName("CycleValue")).Visibility = Visibility.Collapsed;
                ((FrameworkElement)ui.FindName("AdapterText")).Visibility = Visibility.Collapsed;
                ((FrameworkElement)ui.FindName("PackageButton")).Visibility = Visibility.Collapsed;
                ((FrameworkElement)ui.FindName("HistoryButton")).Visibility = Visibility.Collapsed;
                grip.Visibility = Visibility.Hidden;
                ResizeMode = ResizeMode.NoResize;
                toggle.Content = "+";
                toggle.ToolTip = "恢复完整视图";
                Height = 124;
            }
            else
            {
                compact = false;
                LayoutRow(2).Height = new GridLength(28);
                LayoutRow(3).Height = new GridLength(10);
                LayoutRow(5).Height = new GridLength(22);
                LayoutRow(6).Height = new GridLength(10);
                LayoutRow(7).Height = new GridLength(23);
                ((FrameworkElement)ui.FindName("Chart")).Visibility = Visibility.Visible;
                ((FrameworkElement)ui.FindName("CycleLabel")).Visibility = Visibility.Visible;
                ((FrameworkElement)ui.FindName("CycleValue")).Visibility = Visibility.Visible;
                ((FrameworkElement)ui.FindName("AdapterText")).Visibility = Visibility.Visible;
                ((FrameworkElement)ui.FindName("PackageButton")).Visibility = Visibility.Visible;
                ((FrameworkElement)ui.FindName("HistoryButton")).Visibility = Visibility.Visible;
                grip.Visibility = Visibility.Visible;
                ResizeMode = ResizeMode.CanResize;
                toggle.Content = "−";
                toggle.ToolTip = "切换紧凑模式";
                Width = normalWidth;
                Height = normalHeight;
            }
            KeepOnScreen();
        }

        private void ResizeGripDragDelta(object sender, DragDeltaEventArgs e)
        {
            if (compact || closing) return;
            Width = Math.Max(MinWidth, Width + e.HorizontalChange);
            Height = Math.Max(MinHeight, Height + e.VerticalChange);
            KeepOnScreen();
        }

        private void WidgetSizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (compact || resizeSaveTimer == null || closing) return;
            normalWidth = Width;
            normalHeight = Height;
            resizeSaveTimer.Stop();
            resizeSaveTimer.Start();
        }

        private void SavePosition()
        {
            monitor.Settings.Left = Left;
            monitor.Settings.Top = Top;
            if (!compact)
            {
                monitor.Settings.Width = Width;
                monitor.Settings.Height = Height;
            }
            monitor.Save();
        }
        private void SetSpeed(string prefix, double value)
        {
            string[] pieces = Units.Bytes(value).Split(' ');
            Text(prefix + "Value").Text = pieces[0];
            Text(prefix + "Unit").Text = pieces[1] + "/s";
        }
        private void UpdateSample()
        {
            try
            {
                snapshot = monitor.Tick(DateTime.Now);
                SetSpeed("Down", snapshot.DownloadPerSecond); SetSpeed("Up", snapshot.UploadPerSecond);
                Text("TodayValue").Text = Units.Bytes(snapshot.TodayDownload + snapshot.TodayUpload);
                Text("TodayValue").ToolTip = "下载 " + Units.Bytes(snapshot.TodayDownload) + "  /  上传 " + Units.Bytes(snapshot.TodayUpload);
                double used = (double)snapshot.CycleDownload + snapshot.CycleUpload;
                double cap = monitor.Settings.QuotaGB * 1000000000d;
                Text("CycleLabel").Text = monitor.Settings.BillingDay == 1 ? "本月用量" : "本期用量";
                Text("CycleValue").Text = Units.Bytes(used) + (cap > 0 ? " / " + Units.Bytes(cap) : "");
                Text("CycleValue").ToolTip = snapshot.CycleStart.ToString("yyyy.MM.dd") + " — " + snapshot.CycleEnd.AddDays(-1).ToString("yyyy.MM.dd") + "\n下载 " + Units.Bytes(snapshot.CycleDownload) + "  /  上传 " + Units.Bytes(snapshot.CycleUpload) + (cap > 0 ? "\n已用 " + (used / cap * 100).ToString("0.0") + "%" : "\n可在设置中填写套餐额度");
                var fill = (Border)ui.FindName("QuotaFill");
                Grid quotaTrack = fill.Parent as Grid;
                double quotaWidth = quotaTrack == null ? 260 : quotaTrack.ActualWidth;
                fill.Width = cap > 0 ? Math.Min(1, used / cap) * quotaWidth : 0;
                fill.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(cap > 0 && used >= cap ? "#FFA7A7" : cap > 0 && used >= cap * .8 ? "#FFD08A" : "#72E5CE"));
                string label = String.IsNullOrWhiteSpace(snapshot.AdapterLabel) ? "未连接网卡" : snapshot.AdapterLabel;
                string storageError = monitor.LastStorageError;
                Text("AdapterText").Text = !String.IsNullOrEmpty(storageError) ? "记录保存失败 · 点击设置查看" : pendingAlert ?? label;
                Text("AdapterText").ToolTip = label + "\n" + snapshot.Status + "\n从启动后开始统计，包含局域网流量。" + (!String.IsNullOrEmpty(storageError) ? "\n保存失败：" + storageError : "");
                ((System.Windows.Shapes.Ellipse)ui.FindName("StatusDot")).Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(!String.IsNullOrEmpty(storageError) ? "#FFA7A7" : label.Contains("未") || label.Contains("无") ? "#FFD08A" : "#72E5CE"));
                string tip = "↓ " + Units.Bytes(snapshot.DownloadPerSecond) + "/s  ↑ " + Units.Bytes(snapshot.UploadPerSecond) + "/s\n今日 " + Units.Bytes(snapshot.TodayDownload + snapshot.TodayUpload);
                tray.Text = tip.Length <= 63 ? tip : tip.Substring(0, 63);
                if (!String.IsNullOrEmpty(storageError) && !hasStorageNotice) { hasStorageNotice = true; ShowNotice("用量记录保存失败：" + storageError, Forms.ToolTipIcon.Warning); }
                if (String.IsNullOrEmpty(storageError)) hasStorageNotice = false;
                downHistory.Enqueue(snapshot.DownloadPerSecond); upHistory.Enqueue(snapshot.UploadPerSecond);
                while (downHistory.Count > 60) downHistory.Dequeue();
                while (upHistory.Count > 60) upHistory.Dequeue();
                DrawChart();
            }
            catch (Exception ex)
            {
                Text("AdapterText").Text = "暂时无法读取网卡，正在重试";
                Text("AdapterText").ToolTip = ex.Message;
                SetSpeed("Down", 0); SetSpeed("Up", 0);
            }
        }
        private void DrawChart()
        {
            var canvas = (Canvas)ui.FindName("Chart");
            canvas.Children.Clear();
            double width = Math.Max(1, canvas.ActualWidth), height = Math.Max(1, canvas.ActualHeight - 3);
            double max = Math.Max(1024, Math.Max(downHistory.DefaultIfEmpty(0).Max(), upHistory.DefaultIfEmpty(0).Max()));
            DrawLine(canvas, downHistory, width, height, max, "#72E5CE");
            DrawLine(canvas, upHistory, width, height, max, "#B3A6FF");
        }
        private static void DrawLine(Canvas canvas, IEnumerable<double> values, double width, double height, double max, string color)
        {
            var data = values.ToArray();
            var line = new Polyline { Stroke = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)), StrokeThickness = 1.5, StrokeLineJoin = PenLineJoin.Round };
            for (int i = 0; i < data.Length; i++) line.Points.Add(new Point(width * (60 - data.Length + i) / 59, height - data[i] / max * (height - 3)));
            canvas.Children.Add(line);
        }
        private void OpenSettings()
        {
            if (currentDialog != null) { currentDialog.Activate(); return; }
            Reveal();
            var dialog = new SettingsDialog(monitor.Settings, monitor.RefreshAdapters(), Startup.Enabled) { Owner = this, Topmost = true };
            currentDialog = dialog;
            try
            {
                if (dialog.ShowDialog() == true)
                {
                    try { if (dialog.Result.StartWithWindows != Startup.Enabled) Startup.Set(dialog.Result.StartWithWindows); }
                    catch (Exception ex) { dialog.Result.StartWithWindows = Startup.Enabled; MessageBox.Show("开机启动设置未能更新：" + ex.Message + "\n其他设置仍会保存。", "流量浮窗"); }
                    monitor.ApplySettings(dialog.Result); pendingAlert = null; ApplyAppearance(); UpdateSample();
                }
            }
            finally { currentDialog = null; }
        }
        private void OpenPackage()
        {
            if (currentDialog != null) { currentDialog.Activate(); return; }
            Reveal(); UpdateSample();
            if (snapshot == null) return;
            var dialog = new PackageDialog(monitor.Settings, snapshot) { Owner = this, Topmost = true };
            currentDialog = dialog;
            try
            {
                if (dialog.ShowDialog() == true)
                {
                    monitor.ApplySettings(dialog.Result);
                    pendingAlert = null;
                    UpdateSample();
                    ShowNotice("套餐已更新，历史用量已保留。", Forms.ToolTipIcon.Info);
                }
            }
            finally { currentDialog = null; }
        }
        private void OpenHistory()
        {
            if (currentDialog != null) { currentDialog.Activate(); return; }
            Reveal(); UpdateSample();
            if (snapshot == null) return;
            var dialog = new HistoryDialog(snapshot) { Owner = this, Topmost = true };
            currentDialog = dialog;
            try { dialog.ShowDialog(); } finally { currentDialog = null; }
        }
        private void DisplaySettingsChanged(object sender, EventArgs e) { Dispatcher.BeginInvoke(new Action(KeepOnScreen)); }
        private void SessionEnding(object sender, SessionEndingEventArgs e) { monitor.Save(); }
        private void Cleanup()
        {
            if (closing) return;
            closing = true; timer.Stop(); resizeSaveTimer.Stop();
            SystemEvents.SessionEnding -= SessionEnding; SystemEvents.DisplaySettingsChanged -= DisplaySettingsChanged;
            UpdateSample(); SavePosition(); tray.Visible = false; tray.Dispose();
        }
    }
}
