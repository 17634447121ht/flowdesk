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
using System.Windows.Interop;
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
            for (int i = 0; i + 1 < args.Length; i++)
            {
                int previousId;
                if (args[i] == "--wait-for-exit" && Int32.TryParse(args[++i], out previousId))
                {
                    try
                    {
                        using (Process previous = Process.GetProcessById(previousId))
                        {
                            if (!previous.WaitForExit(120000))
                            {
                                MessageBox.Show("旧进程仍在退出并保存数据，暂时无法重新启动。请等待其退出后，再打开 FlowDesk。", "FlowDesk");
                                return;
                            }
                        }
                    }
                    catch (ArgumentException) { }
                }
            }
            Startup.DataDirectory = dataDir;
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
                        try { widget.Start(); app.Run(); }
                        finally { widget.WaitForShutdown(); }
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
        internal static string DataDirectory;
        internal static string QuotePath(string value)
        {
            int trailing = value.Length - value.TrimEnd('\\').Length;
            return "\"" + value + new string('\\', trailing) + "\"";
        }
        internal static string CommandFor(string executable, string directory)
        {
            return QuotePath(executable) + (String.IsNullOrEmpty(directory) ? "" : " --data-dir " + QuotePath(directory));
        }
        private static string Command { get { return CommandFor(Process.GetCurrentProcess().MainModule.FileName, DataDirectory); } }
        internal static bool HasEntry(object value) { return value is string && !String.IsNullOrWhiteSpace((string)value); }
        public static bool Enabled
        {
            get
            {
                try { using (var key = Registry.CurrentUser.OpenSubKey(KeyPath)) return key != null && HasEntry(key.GetValue(ValueName)); }
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
        private readonly ProcessTrafficMonitor processTraffic;
        private readonly EventWaitHandle activate;
        private readonly string dataDir;
        private readonly FrameworkElement ui;
        private readonly DispatcherTimer timer;
        private readonly Forms.NotifyIcon tray;
        private readonly Forms.NotifyIcon uploadTray;
        private readonly Forms.ToolStripMenuItem pinned;
        private readonly Forms.ToolStripMenuItem modeUltra;
        private readonly Forms.ToolStripMenuItem modeMinimal;
        private readonly Forms.ToolStripMenuItem modeDetailed;
        private readonly Forms.ToolStripMenuItem minimalOptions;
        private readonly Forms.ToolStripMenuItem lockedMinimal;
        private readonly Forms.ToolStripMenuItem clickThroughMinimal;
        private readonly Forms.ToolStripMenuItem shadowMinimal;
        private readonly Forms.ToolStripMenuItem textAuto;
        private readonly Forms.ToolStripMenuItem textLight;
        private readonly Forms.ToolStripMenuItem textDark;
        private readonly System.Drawing.Icon applicationIcon;
        private readonly MinimalSpeedWindow minimalWindow;
        private readonly bool showDesktopUi;
        private System.Drawing.Icon downloadIcon;
        private System.Drawing.Icon uploadIcon;
        private string downloadIconKey;
        private string uploadIconKey;
        private readonly TrayRateDisplay trayRates = new TrayRateDisplay();
        private bool lightTaskbar;
        private bool started;
        private bool trayPopup;
        private readonly Queue<double> downHistory = new Queue<double>();
        private readonly Queue<double> upHistory = new Queue<double>();
        private MonitorSnapshot snapshot;
        private string samplingError;
        private Window currentDialog;
        private volatile bool closing;
        private bool compact;
        private bool hasStorageNotice;
        private string pendingAlert;
        private DateTime alertCycleStart;
        private DateTime alertCycleEnd;
        private int sampleInFlight;
        private int newQuotaThreshold;
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
            <Grid.RowDefinitions><RowDefinition x:Name='HeaderRow' Height='24'/><RowDefinition x:Name='SpeedRow' Height='55'/><RowDefinition x:Name='ChartRow' Height='*' MinHeight='28'/><RowDefinition x:Name='DividerRow' Height='10'/><RowDefinition x:Name='TodayRow' Height='22'/><RowDefinition x:Name='CycleRow' Height='22'/><RowDefinition x:Name='QuotaRow' Height='10'/><RowDefinition x:Name='FooterRow' Height='36'/></Grid.RowDefinitions>
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
            <Grid Grid.Row='7'><Grid.RowDefinitions><RowDefinition Height='14'/><RowDefinition Height='22'/></Grid.RowDefinitions><TextBlock x:Name='AdapterText' Text='正在连接网卡…' Foreground='#91A3BC' FontSize='9' VerticalAlignment='Center' TextTrimming='CharacterEllipsis' Margin='0,0,4,0'/><StackPanel Grid.Row='1' Orientation='Horizontal' HorizontalAlignment='Right' Margin='0,0,14,0'><Button x:Name='PackageButton' Content='更新套餐' FontSize='10' Padding='5,2' ToolTip='更新套餐额度和结算日' AutomationProperties.Name='更新套餐'/><Button x:Name='HistoryButton' Content='用量记录' FontSize='10' Padding='5,2' Margin='3,0,0,0' ToolTip='历史记录、刷新与导出 CSV' AutomationProperties.Name='用量记录'/><Button x:Name='ProcessButton' Content='谁在用网 ↗' FontSize='10' Padding='5,2' Margin='3,0,0,0' ToolTip='按程序查看实时流量和累计用量' AutomationProperties.Name='谁在用网'/></StackPanel></Grid>
            <Thumb x:Name='ResizeGrip' Grid.RowSpan='8' Width='14' Height='14' HorizontalAlignment='Right' VerticalAlignment='Bottom' Background='Transparent' Cursor='SizeNWSE' ToolTip='拖动调整大小'><Thumb.Template><ControlTemplate TargetType='Thumb'><Grid Background='Transparent'><Path Data='M 4,12 L 12,4 M 8,12 L 12,8' Stroke='#70849F' StrokeThickness='1'/></Grid></ControlTemplate></Thumb.Template></Thumb>
          </Grid>
        </Border>";

        public Widget(TrafficMonitor traffic, EventWaitHandle showEvent, string storageDir)
            : this(traffic, showEvent, storageDir, true) { }

        internal Widget(TrafficMonitor traffic, EventWaitHandle showEvent, string storageDir, bool startProcessCollection)
        {
            monitor = traffic; activate = showEvent; dataDir = storageDir;
            showDesktopUi = startProcessCollection;
            processTraffic = new ProcessTrafficMonitor(storageDir);
            if (startProcessCollection) processTraffic.Start();
            Title = "FlowDesk 1.3.1 · 网络用量";
            Width = monitor.Settings.Width;
            Height = monitor.Settings.Height;
            normalWidth = Width;
            normalHeight = Height;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.CanResize;
            MinWidth = 286;
            MinHeight = 240;
            MaxWidth = 900;
            MaxHeight = 700;
            AllowsTransparency = true; Background = Brushes.Transparent;
            ShowInTaskbar = false;
            UseLayoutRounding = true;
            ui = (FrameworkElement)XamlReader.Parse(Layout);
            Content = ui;
            ((Button)ui.FindName("SettingsButton")).Click += delegate { OpenSettings(); };
            ((Button)ui.FindName("HideButton")).Click += delegate { SetCompact(!compact); };
            ((Button)ui.FindName("PackageButton")).Click += delegate { OpenPackage(); };
            ((Button)ui.FindName("HistoryButton")).Click += delegate { OpenHistory(); };
            ((Button)ui.FindName("ProcessButton")).Click += delegate { OpenNetworkDetails(); };
            ((Thumb)ui.FindName("ResizeGrip")).DragDelta += ResizeGripDragDelta;
            SizeChanged += WidgetSizeChanged;
            ((Grid)ui.FindName("DragHeader")).MouseLeftButtonDown += delegate(object sender, MouseButtonEventArgs e)
            {
                if (e.ButtonState == MouseButtonState.Pressed) { DragMove(); KeepOnScreen(); SavePosition(); }
            };
            MouseRightButtonUp += delegate { tray.ContextMenuStrip.Show(Forms.Cursor.Position); };
            PreviewKeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.Key == Key.Escape) HideDetails();
                if (e.Key == Key.F2) OpenSettings();
            };
            var menu = new Forms.ContextMenuStrip();
            menu.Items.Add("查看实时详情", null, delegate { RevealTrayPopup(); });
            var modes = new Forms.ToolStripMenuItem("显示模式");
            modeUltra = new Forms.ToolStripMenuItem("超简模式 · 任务栏数字");
            modeMinimal = new Forms.ToolStripMenuItem("极简模式 · 透明网速窗");
            modeDetailed = new Forms.ToolStripMenuItem("详细模式 · 完整用量面板");
            modeUltra.Click += delegate { SetDisplayMode(DisplayMode.UltraMinimal); };
            modeMinimal.Click += delegate { SetDisplayMode(DisplayMode.Minimal); };
            modeDetailed.Click += delegate { SetDisplayMode(DisplayMode.Detailed); };
            modes.DropDownItems.AddRange(new Forms.ToolStripItem[] { modeUltra, modeMinimal, modeDetailed });
            menu.Items.Add(modes);
            minimalOptions = new Forms.ToolStripMenuItem("极简浮窗选项");
            lockedMinimal = new Forms.ToolStripMenuItem("锁定位置");
            clickThroughMinimal = new Forms.ToolStripMenuItem("鼠标穿透");
            shadowMinimal = new Forms.ToolStripMenuItem("文字淡阴影");
            lockedMinimal.Click += delegate { ToggleMinimalLock(); };
            clickThroughMinimal.Click += delegate { ToggleMinimalClickThrough(); };
            shadowMinimal.Click += delegate { SetMinimalPreference("shadow", !monitor.Settings.MinimalTextShadow); };
            var textTheme = new Forms.ToolStripMenuItem("文字颜色");
            textAuto = new Forms.ToolStripMenuItem("跟随系统");
            textLight = new Forms.ToolStripMenuItem("浅色文字");
            textDark = new Forms.ToolStripMenuItem("深色文字");
            textAuto.Click += delegate { SetMinimalTextTheme("auto"); };
            textLight.Click += delegate { SetMinimalTextTheme("light"); };
            textDark.Click += delegate { SetMinimalTextTheme("dark"); };
            textTheme.DropDownItems.AddRange(new Forms.ToolStripItem[] { textAuto, textLight, textDark });
            minimalOptions.DropDownItems.AddRange(new Forms.ToolStripItem[] { lockedMinimal, clickThroughMinimal, textTheme, shadowMinimal });
            minimalOptions.DropDownItems.Add("重置浮窗位置", null, delegate { ResetMinimalPosition(); });
            menu.Items.Add(minimalOptions);
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add("更新套餐…", null, delegate { OpenPackage(); });
            menu.Items.Add("谁在用网（程序明细）…", null, delegate { OpenNetworkDetails(); });
            menu.Items.Add("用量记录…", null, delegate { OpenHistory(); });
            menu.Items.Add("设置…", null, delegate { OpenSettings(); });
            pinned = new Forms.ToolStripMenuItem("总在最前");
            pinned.Click += delegate { AppSettings value = monitor.Settings.Clone(); value.TopMost = !value.TopMost; monitor.ApplySettings(value); ApplyAppearance(); };
            menu.Items.Add(pinned);
            menu.Items.Add("打开数据文件夹", null, delegate { Process.Start(new ProcessStartInfo(dataDir) { UseShellExecute = true }); });
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add("退出并保存", null, delegate { Close(); });
            tray = new Forms.NotifyIcon { Text = "FlowDesk · 下载", ContextMenuStrip = menu };
            uploadTray = new Forms.NotifyIcon { Text = "FlowDesk · 上传", ContextMenuStrip = menu };
            applicationIcon = LoadApplicationIcon();
            minimalWindow = new MinimalSpeedWindow(SaveMinimalPosition, delegate { if (!closing) menu.Show(Forms.Cursor.Position); });
            lightTaskbar = ReadLightTaskbar();
            UpdateTrayIcons(0, 0, true);
            tray.Visible = showDesktopUi;
            uploadTray.Visible = showDesktopUi && monitor.Settings.Mode == DisplayMode.UltraMinimal;
            tray.MouseClick += TrayMouseClick;
            uploadTray.MouseClick += TrayMouseClick;
            tray.BalloonTipClicked += delegate { RevealTrayPopup(); };
            monitor.QuotaReached += delegate(int threshold) { Interlocked.Exchange(ref newQuotaThreshold, threshold); };
            ApplyAppearance();
            var bounds = SystemParameters.WorkArea;
            Left = monitor.Settings.HasSavedPosition ? monitor.Settings.Left : bounds.Right - Width - 24;
            Top = monitor.Settings.HasSavedPosition ? monitor.Settings.Top : bounds.Top + 40;
            timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            timer.Tick += delegate { if (activate.WaitOne(0)) RevealTrayPopup(); UpdateSample(); };
            resizeSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
            resizeSaveTimer.Tick += delegate
            {
                resizeSaveTimer.Stop();
                if (!compact && !closing) SavePosition();
            };
            Loaded += delegate { KeepOnScreen(); };
            Deactivated += delegate { HandleDeactivated(); };
            Closing += delegate { Cleanup(); };
            SystemEvents.SessionEnding += SessionEnding;
            SystemEvents.DisplaySettingsChanged += DisplaySettingsChanged;
            SystemEvents.UserPreferenceChanged += UserPreferenceChanged;
        }

        internal void Start()
        {
            if (started || closing) return;
            started = true;
            // Create the owner handle without ever displaying a desktop window.
            new WindowInteropHelper(this).EnsureHandle();
            UpdateSample(); timer.Start();
            ApplyDisplayMode();
            if (!String.IsNullOrEmpty(monitor.RecoveryNotice)) ShowNotice(monitor.RecoveryNotice, Forms.ToolTipIcon.Warning);
        }

        private void TrayMouseClick(object sender, Forms.MouseEventArgs e)
        {
            if (e.Button != Forms.MouseButtons.Left) return;
            if (currentDialog != null) { currentDialog.Activate(); return; }
            if (IsVisible && trayPopup) HideDetails();
            else RevealTrayPopup();
        }

        private static bool ReadLightTaskbar()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                    return key != null && Convert.ToInt32(key.GetValue("SystemUsesLightTheme", 0), CultureInfo.InvariantCulture) != 0;
            }
            catch { return false; }
        }

        private static System.Drawing.Icon LoadApplicationIcon()
        {
            try
            {
                System.Drawing.Icon icon = System.Drawing.Icon.ExtractAssociatedIcon(Process.GetCurrentProcess().MainModule.FileName);
                if (icon != null) return icon;
            }
            catch { }
            return (System.Drawing.Icon)System.Drawing.SystemIcons.Application.Clone();
        }

        private void UserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
        {
            if (closing || Dispatcher.HasShutdownStarted) return;
            Dispatcher.BeginInvoke(new Action(delegate
            {
                if (closing) return;
                lightTaskbar = ReadLightTaskbar();
                downloadIconKey = null; uploadIconKey = null;
                ApplyAppearance();
                UpdateTrayIcons(trayRates.Download, trayRates.Upload, snapshot == null || trayRates.Available);
                UpdateMinimalRates();
            }));
        }

        private void UpdateTrayIcons(double download, double upload, bool available)
        {
            if (monitor.Settings.Mode != DisplayMode.UltraMinimal)
            {
                tray.Icon = applicationIcon;
                downloadIconKey = null; uploadIconKey = null;
                return;
            }
            int size = Math.Max(16, Math.Min(64, Forms.SystemInformation.SmallIconSize.Width));
            string state = "/" + size + "/" + lightTaskbar;
            double downValue = available ? download : Double.NaN;
            double upValue = available ? upload : Double.NaN;
            string downKey = TraySpeedIcon.Compact(downValue) + state;
            string upKey = TraySpeedIcon.Compact(upValue) + state;
            if (downloadIconKey != downKey)
            {
                System.Drawing.Icon next = TraySpeedIcon.Create(downValue, true, size, lightTaskbar);
                tray.Icon = next;
                if (downloadIcon != null) downloadIcon.Dispose();
                downloadIcon = next; downloadIconKey = downKey;
            }
            if (uploadIconKey != upKey)
            {
                System.Drawing.Icon next = TraySpeedIcon.Create(upValue, false, size, lightTaskbar);
                uploadTray.Icon = next;
                if (uploadIcon != null) uploadIcon.Dispose();
                uploadIcon = next; uploadIconKey = upKey;
            }
        }

        private void PublishTrayRates(double download, double upload, bool available)
        {
            double now = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
            if (trayRates.Observe(now, download, upload, available))
                UpdateTrayIcons(trayRates.Download, trayRates.Upload, trayRates.Available);
            UpdateMinimalRates();
        }

        private void UpdateMinimalRates()
        {
            string tooltip;
            if (snapshot == null) tooltip = "下载在上，上传在下\n正在读取网速…";
            else if (!trayRates.Available) tooltip = samplingError ?? snapshot.Status ?? "暂时无法读取网卡，正在重试";
            else tooltip = "下载 " + Units.Bytes(snapshot.DownloadPerSecond) + "/s\n上传 " + Units.Bytes(snapshot.UploadPerSecond) + "/s\n显示约 3 秒平均速度；右键打开菜单";
            minimalWindow.UpdateRates(trayRates.Download, trayRates.Upload, snapshot == null || trayRates.Available, tooltip);
        }

        private TextBlock Text(string name) { return (TextBlock)ui.FindName(name); }
        private void ShowNotice(string text, Forms.ToolTipIcon kind)
        {
            tray.BalloonTipTitle = "流量浮窗"; tray.BalloonTipText = text; tray.BalloonTipIcon = kind; tray.ShowBalloonTip(8000);
        }
        private void ApplyAppearance()
        {
            Topmost = trayPopup || monitor.Settings.TopMost;
            Opacity = Math.Max(.7, Math.Min(1, monitor.Settings.Opacity));
            pinned.Checked = monitor.Settings.TopMost;
            modeUltra.Checked = monitor.Settings.Mode == DisplayMode.UltraMinimal;
            modeMinimal.Checked = monitor.Settings.Mode == DisplayMode.Minimal;
            modeDetailed.Checked = monitor.Settings.Mode == DisplayMode.Detailed;
            minimalOptions.Enabled = modeMinimal.Checked;
            lockedMinimal.Checked = monitor.Settings.MinimalLocked;
            clickThroughMinimal.Checked = monitor.Settings.MinimalClickThrough;
            shadowMinimal.Checked = monitor.Settings.MinimalTextShadow;
            textAuto.Checked = monitor.Settings.MinimalTextTheme == "auto";
            textLight.Checked = monitor.Settings.MinimalTextTheme == "light";
            textDark.Checked = monitor.Settings.MinimalTextTheme == "dark";
            minimalWindow.ApplySettings(monitor.Settings, lightTaskbar);
        }
        private void Reveal()
        {
            if (monitor.Settings.Mode != DisplayMode.Detailed) { RevealTrayPopup(); return; }
            trayPopup = false;
            ApplyAppearance();
            if (showDesktopUi) { Show(); KeepOnScreen(); Activate(); }
        }
        private void RevealTrayPopup()
        {
            if (closing) return;
            if (currentDialog != null) { currentDialog.Activate(); return; }
            if (monitor.Settings.Mode == DisplayMode.Detailed) { Reveal(); return; }
            trayPopup = true;
            if (compact) SetCompact(false);
            new WindowInteropHelper(this).EnsureHandle();
            PlaceTrayPopup();
            ApplyAppearance();
            if (showDesktopUi) { Show(); Activate(); }
        }
        private void HideDetails()
        {
            Hide(); trayPopup = false;
            Topmost = monitor.Settings.TopMost;
        }
        private void HandleDeactivated()
        {
            if (trayPopup && currentDialog == null && !closing) HideDetails();
        }
        private void SetDesktopWidgetEnabled(bool enabled)
        {
            SetDisplayMode(enabled ? DisplayMode.Detailed : DisplayMode.UltraMinimal);
        }
        private void SetDisplayMode(DisplayMode mode)
        {
            if (closing) return;
            if (monitor.Settings.Mode == DisplayMode.Detailed && !trayPopup && IsVisible) SavePosition();
            AppSettings value = monitor.Settings.Clone();
            value.Mode = mode;
            monitor.ApplySettings(value);
            ApplyDisplayMode();
        }
        private void ApplyDisplayMode()
        {
            if (closing) return;
            ApplyAppearance();
            UpdateTrayIcons(trayRates.Download, trayRates.Upload, snapshot == null || trayRates.Available);
            UpdateMinimalRates();
            tray.Visible = showDesktopUi;
            uploadTray.Visible = showDesktopUi && monitor.Settings.Mode == DisplayMode.UltraMinimal;
            if (monitor.Settings.Mode != DisplayMode.Minimal) minimalWindow.Hide();
            // A modal dialog keeps its visible owner until it closes. Its saved
            // mode can change now; the new persistent surface is applied later.
            if (currentDialog != null) return;
            if (monitor.Settings.Mode == DisplayMode.Detailed)
            {
                trayPopup = false;
                var area = SystemParameters.WorkArea;
                AppSettings value = monitor.Settings;
                Left = value.HasSavedPosition ? value.Left : area.Right - Width - 24;
                Top = value.HasSavedPosition ? value.Top : area.Top + 40;
                Reveal();
            }
            else
            {
                HideDetails();
                if (monitor.Settings.Mode == DisplayMode.Minimal && showDesktopUi)
                {
                    minimalWindow.Show();
                    minimalWindow.RefreshPlacement();
                }
            }
        }
        private void SaveMinimalPosition(double left, double top)
        {
            if (closing) return;
            AppSettings value = monitor.Settings.Clone();
            value.MinimalLeft = left; value.MinimalTop = top; value.HasMinimalPosition = true;
            monitor.ApplySettings(value);
        }
        private void ToggleMinimalLock() { SetMinimalPreference("locked", !monitor.Settings.MinimalLocked); }
        private void ToggleMinimalClickThrough() { SetMinimalPreference("through", !monitor.Settings.MinimalClickThrough); }
        private void SetMinimalPreference(string name, bool enabled)
        {
            AppSettings value = monitor.Settings.Clone();
            if (name == "locked") value.MinimalLocked = enabled;
            else if (name == "through") value.MinimalClickThrough = enabled;
            else if (name == "shadow") value.MinimalTextShadow = enabled;
            monitor.ApplySettings(value); ApplyAppearance();
        }
        private void SetMinimalTextTheme(string theme)
        {
            AppSettings value = monitor.Settings.Clone(); value.MinimalTextTheme = theme;
            monitor.ApplySettings(value); ApplyAppearance();
        }
        private void ResetMinimalPosition()
        {
            AppSettings value = monitor.Settings.Clone(); value.HasMinimalPosition = false;
            monitor.ApplySettings(value); ApplyAppearance();
            if (showDesktopUi && monitor.Settings.Mode == DisplayMode.Minimal) minimalWindow.Show();
            minimalWindow.RefreshPlacement();
        }
        private void PlaceTrayPopup()
        {
            var cursor = Forms.Cursor.Position;
            var screen = Forms.Screen.FromPoint(cursor);
            var source = PresentationSource.FromVisual(this);
            Matrix transform = source != null && source.CompositionTarget != null ? source.CompositionTarget.TransformFromDevice : Matrix.Identity;
            Point origin = transform.Transform(new Point(screen.WorkingArea.Left, screen.WorkingArea.Top));
            Point end = transform.Transform(new Point(screen.WorkingArea.Right, screen.WorkingArea.Bottom));
            Point anchor = transform.Transform(new Point(cursor.X, cursor.Y));
            const double gap = 8;
            double x = anchor.X - Width / 2, y = end.Y - Height - gap;
            if (screen.WorkingArea.Top > screen.Bounds.Top) y = origin.Y + gap;
            else if (screen.WorkingArea.Left > screen.Bounds.Left) { x = origin.X + gap; y = anchor.Y - Height / 2; }
            else if (screen.WorkingArea.Right < screen.Bounds.Right) { x = end.X - Width - gap; y = anchor.Y - Height / 2; }
            Left = Math.Max(origin.X + gap, Math.Min(x, end.X - Width - gap));
            Top = Math.Max(origin.Y + gap, Math.Min(y, end.Y - Height - gap));
        }
        private void DialogFinished()
        {
            currentDialog = null;
            if (!closing) ApplyDisplayMode();
        }
        private void KeepOnScreen()
        {
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
                MinHeight = 124;
                LayoutRow(2).MinHeight = 0;
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
                ((FrameworkElement)ui.FindName("ProcessButton")).Visibility = Visibility.Collapsed;
                grip.Visibility = Visibility.Hidden;
                ResizeMode = ResizeMode.NoResize;
                toggle.Content = "+";
                toggle.ToolTip = "恢复完整视图";
                Height = 124;
            }
            else
            {
                // Keep resize callbacks suppressed until both dimensions are restored.
                MinHeight = 240;
                Width = normalWidth;
                Height = normalHeight;
                compact = false;
                LayoutRow(2).MinHeight = 28;
                LayoutRow(2).Height = new GridLength(1, GridUnitType.Star);
                LayoutRow(3).Height = new GridLength(10);
                LayoutRow(5).Height = new GridLength(22);
                LayoutRow(6).Height = new GridLength(10);
                LayoutRow(7).Height = new GridLength(36);
                ((FrameworkElement)ui.FindName("Chart")).Visibility = Visibility.Visible;
                ((FrameworkElement)ui.FindName("CycleLabel")).Visibility = Visibility.Visible;
                ((FrameworkElement)ui.FindName("CycleValue")).Visibility = Visibility.Visible;
                ((FrameworkElement)ui.FindName("AdapterText")).Visibility = Visibility.Visible;
                ((FrameworkElement)ui.FindName("PackageButton")).Visibility = Visibility.Visible;
                ((FrameworkElement)ui.FindName("HistoryButton")).Visibility = Visibility.Visible;
                ((FrameworkElement)ui.FindName("ProcessButton")).Visibility = Visibility.Visible;
                grip.Visibility = Visibility.Visible;
                ResizeMode = ResizeMode.CanResize;
                toggle.Content = "−";
                toggle.ToolTip = "切换紧凑模式";
            }
            KeepOnScreen();
        }

        private void ResizeGripDragDelta(object sender, DragDeltaEventArgs e)
        {
            if (compact || closing) return;
            Width = Math.Max(MinWidth, Math.Min(MaxWidth, Width + e.HorizontalChange));
            Height = Math.Max(MinHeight, Math.Min(MaxHeight, Height + e.VerticalChange));
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
            AppSettings value = monitor.Settings.Clone();
            if (!trayPopup && IsVisible)
            {
                value.Left = Left;
                value.Top = Top;
                value.HasSavedPosition = true;
            }
            if (!compact)
            {
                value.Width = Width;
                value.Height = Height;
            }
            monitor.ApplySettings(value);
        }
        private void SetSpeed(string prefix, double value)
        {
            string[] pieces = Units.Bytes(value).Split(' ');
            Text(prefix + "Value").Text = pieces[0];
            Text(prefix + "Unit").Text = pieces[1] + "/s";
        }
        private void UpdateSample()
        {
            if (closing || Interlocked.CompareExchange(ref sampleInFlight, 1, 0) != 0) return;
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    MonitorSnapshot value = monitor.Tick(DateTime.Now);
                    int threshold = Interlocked.Exchange(ref newQuotaThreshold, 0);
                    if (!closing && !Dispatcher.HasShutdownStarted)
                        Dispatcher.BeginInvoke(new Action(delegate { if (!closing) RenderSnapshot(value, threshold); }));
                }
                catch (Exception ex)
                {
                    Program.Log(dataDir, ex);
                    if (!closing && !Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(new Action(delegate
                    {
                        if (closing) return;
                        samplingError = "暂时无法读取网卡，正在重试";
                        Text("AdapterText").Text = "暂时无法读取网卡，正在重试";
                        Text("AdapterText").ToolTip = ex.Message;
                        ((Ellipse)ui.FindName("StatusDot")).Fill = new SolidColorBrush(Color.FromRgb(255, 208, 138));
                        SetSpeed("Down", 0); SetSpeed("Up", 0);
                        PublishTrayRates(0, 0, false);
                        tray.Text = uploadTray.Text = "FlowDesk · 暂时无法读取网卡，正在重试";
                    }));
                }
                finally { Interlocked.Exchange(ref sampleInFlight, 0); }
            });
        }
        private void RenderSnapshot(MonitorSnapshot value, int threshold)
        {
                samplingError = null;
                snapshot = value;
                if (alertCycleStart != snapshot.CycleStart || alertCycleEnd != snapshot.CycleEnd) pendingAlert = null;
                alertCycleStart = snapshot.CycleStart;
                alertCycleEnd = snapshot.CycleEnd;
                double used = (double)snapshot.CycleDownload + snapshot.CycleUpload;
                double cap = monitor.Settings.QuotaGB * 1000000000d;
                int currentLevel = cap > 0 ? (used >= cap ? 100 : used >= cap * .8 ? 80 : 0) : 0;
                pendingAlert = currentLevel > 0 ? "本账期流量已达到套餐的 " + currentLevel + "%" : null;
                if (threshold > 0 && currentLevel >= threshold)
                {
                    ShowNotice(pendingAlert, Forms.ToolTipIcon.Warning);
                }
                SetSpeed("Down", snapshot.DownloadPerSecond); SetSpeed("Up", snapshot.UploadPerSecond);
                Text("TodayValue").Text = Units.Bytes((double)snapshot.TodayDownload + snapshot.TodayUpload);
                Text("TodayValue").ToolTip = "下载 " + Units.Bytes(snapshot.TodayDownload) + "  /  上传 " + Units.Bytes(snapshot.TodayUpload);
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
                Text("AdapterText").Text = !String.IsNullOrEmpty(storageError) ? "记录保存失败 · 悬停查看原因" : !snapshot.IsMonitoringAvailable ? snapshot.Status : pendingAlert ?? label;
                Text("AdapterText").ToolTip = label + "\n" + snapshot.Status + "\n从启动后开始统计，包含局域网流量。" + (!String.IsNullOrEmpty(storageError) ? "\n" + storageError : "");
                ((System.Windows.Shapes.Ellipse)ui.FindName("StatusDot")).Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(!String.IsNullOrEmpty(storageError) ? "#FFA7A7" : !snapshot.IsMonitoringAvailable ? "#FFD08A" : "#72E5CE"));
                string tip = "↓ " + Units.Bytes(snapshot.DownloadPerSecond) + "/s  ↑ " + Units.Bytes(snapshot.UploadPerSecond) + "/s\n今日 " + Units.Bytes((double)snapshot.TodayDownload + snapshot.TodayUpload);
                if (!snapshot.IsMonitoringAvailable) tip = "FlowDesk · " + snapshot.Status;
                tray.Text = tip.Length <= 63 ? tip : tip.Substring(0, 63);
                string upTip = snapshot.IsMonitoringAvailable ? "↑ " + Units.Bytes(snapshot.UploadPerSecond) + "/s  ↓ " + Units.Bytes(snapshot.DownloadPerSecond) + "/s\n今日 " + Units.Bytes((double)snapshot.TodayDownload + snapshot.TodayUpload) : tip;
                uploadTray.Text = upTip.Length <= 63 ? upTip : upTip.Substring(0, 63);
                PublishTrayRates(snapshot.DownloadPerSecond, snapshot.UploadPerSecond, snapshot.IsMonitoringAvailable);
                if (!String.IsNullOrEmpty(storageError) && !hasStorageNotice) { hasStorageNotice = true; ShowNotice("用量记录" + storageError, Forms.ToolTipIcon.Warning); }
                if (String.IsNullOrEmpty(storageError)) hasStorageNotice = false;
                downHistory.Enqueue(snapshot.DownloadPerSecond); upHistory.Enqueue(snapshot.UploadPerSecond);
                while (downHistory.Count > 60) downHistory.Dequeue();
                while (upHistory.Count > 60) upHistory.Dequeue();
                DrawChart();
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
                    try { Startup.Set(dialog.Result.StartWithWindows); }
                    catch (Exception ex) { dialog.Result.StartWithWindows = Startup.Enabled; MessageBox.Show("开机启动设置未能更新：" + ex.Message + "\n其他设置仍会保存。", "流量浮窗"); }
                    monitor.ApplySettings(dialog.Result); pendingAlert = null; ApplyDisplayMode(); UpdateSample();
                }
            }
            finally { DialogFinished(); }
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
            finally { DialogFinished(); }
        }
        private void OpenNetworkDetails()
        {
            if (currentDialog != null) { currentDialog.Activate(); return; }
            Reveal();
            var dialog = new ProcessUsageDialog(delegate { return processTraffic.GetSnapshot(DateTime.Now, monitor.Settings.BillingDay); }, EnableProcessCollection) { Owner = this, Topmost = true };
            currentDialog = dialog;
            try { dialog.ShowDialog(); } finally { DialogFinished(); }
        }
        private void EnableProcessCollection()
        {
            try
            {
                if (ProcessTrafficMonitor.IsAdministrator) { processTraffic.Start(); return; }
                var start = new ProcessStartInfo(Process.GetCurrentProcess().MainModule.FileName,
                    "--data-dir " + Startup.QuotePath(dataDir) + " --wait-for-exit " + Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture))
                    { UseShellExecute = true, Verb = "runas" };
                using (Process replacement = Process.Start(start))
                {
                    if (replacement == null) return;
                }
                if (currentDialog != null) currentDialog.Close();
                Close();
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                if (ex.NativeErrorCode != 1223) MessageBox.Show(this, "无法启用程序统计：" + ex.Message, "FlowDesk");
            }
            catch (Exception ex) { MessageBox.Show(this, "无法启用程序统计：" + ex.Message, "FlowDesk"); }
        }
        private void OpenHistory()
        {
            if (currentDialog != null) { currentDialog.Activate(); return; }
            Reveal(); UpdateSample();
            if (snapshot == null) return;
            var dialog = new HistoryDialog(delegate { UpdateSample(); return snapshot; }) { Owner = this, Topmost = true };
            currentDialog = dialog;
            try { dialog.ShowDialog(); } finally { DialogFinished(); }
        }
        private void DisplaySettingsChanged(object sender, EventArgs e)
        {
            if (closing || Dispatcher.HasShutdownStarted) return;
            Dispatcher.BeginInvoke(new Action(delegate
            {
                if (closing) return;
                KeepOnScreen(); minimalWindow.RefreshPlacement();
            }));
        }
        private void SessionEnding(object sender, SessionEndingEventArgs e) { monitor.Save(); }
        internal void WaitForShutdown()
        {
            processTraffic.Dispose();
            // Keep the single-instance ownership until deferred native cleanup
            // has also flushed its final snapshot to this data directory.
            while (!processTraffic.IsShutdownComplete) Thread.Sleep(50);
        }
        private void Cleanup()
        {
            if (closing) return;
            closing = true; timer.Stop(); resizeSaveTimer.Stop();
            SystemEvents.SessionEnding -= SessionEnding; SystemEvents.DisplaySettingsChanged -= DisplaySettingsChanged;
            SystemEvents.UserPreferenceChanged -= UserPreferenceChanged;
            try { monitor.Tick(DateTime.Now); SavePosition(); }
            finally
            {
                tray.Visible = false; uploadTray.Visible = false;
                tray.Dispose(); uploadTray.Dispose();
                minimalWindow.Close();
                if (downloadIcon != null) downloadIcon.Dispose();
                if (uploadIcon != null) uploadIcon.Dispose();
                applicationIcon.Dispose();
                processTraffic.Dispose();
            }
        }
    }
}
