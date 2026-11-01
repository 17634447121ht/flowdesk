using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Forms = System.Windows.Forms;

namespace FlowDesk
{
    /// <summary>A transparent speed-only surface. Sampling and lifetime belong to Widget.</summary>
    internal sealed class MinimalSpeedWindow : Window
    {
        internal const double SurfaceWidth = 112;
        internal const double SurfaceHeight = 40;
        private const int ExtendedStyleIndex = -20;
        private const long TransparentStyle = 0x20;
        private const long ToolWindowStyle = 0x80;
        private const long LayeredStyle = 0x80000;
        private const long NoActivateStyle = 0x08000000;
        private readonly Action<double, double> positionChanged;
        private readonly Action showMenu;
        private readonly Grid surface;
        private readonly TextBlock download;
        private readonly TextBlock upload;
        private bool positionLocked;
        private bool clickThrough;
        private HwndSource nativeSource;

        internal MinimalSpeedWindow(Action<double, double> positionChanged, Action showMenu)
        {
            this.positionChanged = positionChanged;
            this.showMenu = showMenu;
            Title = "FlowDesk · 极简模式";
            Width = SurfaceWidth; Height = SurfaceHeight;
            MinWidth = MaxWidth = SurfaceWidth;
            MinHeight = MaxHeight = SurfaceHeight;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            ShowActivated = false;
            Focusable = false;
            WindowStartupLocation = WindowStartupLocation.Manual;
            UseLayoutRounding = true;
            SnapsToDevicePixels = true;
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
            TextOptions.SetTextRenderingMode(this, TextRenderingMode.Grayscale);

            surface = new Grid { Background = Brushes.Transparent, Margin = new Thickness(4) };
            surface.RowDefinitions.Add(new RowDefinition { Height = new GridLength(16) });
            surface.RowDefinitions.Add(new RowDefinition { Height = new GridLength(16) });
            download = CreateRateText(); upload = CreateRateText();
            Grid.SetRow(upload, 1);
            surface.Children.Add(download); surface.Children.Add(upload);
            Content = surface;

            surface.MouseLeftButtonDown += delegate(object sender, MouseButtonEventArgs e)
            {
                if (positionLocked || clickThrough || e.ButtonState != MouseButtonState.Pressed) return;
                e.Handled = true;
                DragMove();
                RefreshPlacement();
                if (this.positionChanged != null) this.positionChanged(Left, Top);
            };
            surface.MouseRightButtonUp += delegate(object sender, MouseButtonEventArgs e)
            {
                if (clickThrough) return;
                e.Handled = true;
                if (this.showMenu != null) this.showMenu();
            };
            SourceInitialized += delegate
            {
                nativeSource = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
                if (nativeSource != null) nativeSource.AddHook(WindowMessage);
                ApplyNativeInteraction();
                RefreshPlacement();
            };
            IsVisibleChanged += delegate { if (IsVisible) ApplyNativeInteraction(); };
            Closed += delegate
            {
                if (nativeSource != null && !nativeSource.IsDisposed) nativeSource.RemoveHook(WindowMessage);
                nativeSource = null;
            };
            UpdateRates(0, 0, true, "下载 0 B/s\n上传 0 B/s");
        }

        private static TextBlock CreateRateText()
        {
            TextBlock text = new TextBlock
            {
                FontSize = 12,
                TextAlignment = TextAlignment.Right,
                FlowDirection = FlowDirection.LeftToRight,
                VerticalAlignment = VerticalAlignment.Center,
                Focusable = false,
                TextWrapping = TextWrapping.NoWrap
            };
            RefreshSystemFont(text);
            Typography.SetNumeralAlignment(text, FontNumeralAlignment.Tabular);
            return text;
        }

        private static void RefreshSystemFont(TextBlock text)
        {
            text.FontFamily = SystemFonts.MessageFontFamily;
            text.FontStyle = SystemFonts.MessageFontStyle;
            text.FontWeight = SystemFonts.MessageFontWeight;
        }

        internal void ApplySettings(AppSettings settings, bool lightBackground)
        {
            if (settings == null) throw new ArgumentNullException("settings");
            // Widget reapplies appearance after Windows font preferences change.
            RefreshSystemFont(download); RefreshSystemFont(upload);
            Topmost = settings.TopMost;
            positionLocked = settings.MinimalLocked;
            clickThrough = settings.MinimalClickThrough;
            surface.Cursor = positionLocked || clickThrough ? Cursors.Arrow : Cursors.SizeAll;
            bool darkText = settings.MinimalTextTheme == "dark" ||
                (settings.MinimalTextTheme != "light" && lightBackground);
            download.Foreground = new SolidColorBrush(darkText ? Color.FromRgb(20, 85, 67) : Color.FromRgb(169, 232, 210));
            upload.Foreground = new SolidColorBrush(darkText ? Color.FromRgb(88, 45, 123) : Color.FromRgb(222, 199, 255));
            download.Effect = settings.MinimalTextShadow ? CreateShadow(darkText) : null;
            upload.Effect = settings.MinimalTextShadow ? CreateShadow(darkText) : null;
            // The full-detail window opacity never fades these small numerals.
            Opacity = 1;
            if (settings.HasMinimalPosition && IsFinite(settings.MinimalLeft) && IsFinite(settings.MinimalTop))
            {
                Left = settings.MinimalLeft; Top = settings.MinimalTop;
            }
            else
            {
                Rect work = SystemParameters.WorkArea;
                Left = work.Right - SurfaceWidth - 24;
                Top = work.Top + 40;
            }
            ApplyNativeInteraction();
            RefreshPlacement();
        }

        private static DropShadowEffect CreateShadow(bool darkText)
        {
            return new DropShadowEffect
            {
                Color = darkText ? Colors.White : Colors.Black,
                BlurRadius = 2,
                ShadowDepth = 1,
                Direction = 270,
                Opacity = .28,
                RenderingBias = RenderingBias.Quality
            };
        }

        internal void UpdateRates(double down, double up, bool available, string tooltip)
        {
            download.Text = available ? FormatRate(down) : "—";
            upload.Text = available ? FormatRate(up) : "—";
            ToolTipService.SetInitialShowDelay(surface, 250);
            ToolTipService.SetShowDuration(surface, 30000);
            surface.ToolTip = tooltip;
        }

        internal static string FormatRate(double bytesPerSecond)
        {
            if (!IsFinite(bytesPerSecond)) return "—";
            if (bytesPerSecond <= 0) return "0";
            if (bytesPerSecond < 1) return "<1B";
            if (bytesPerSecond < 1000)
                return Math.Round(bytesPerSecond, 0, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture) + "B";
            if (bytesPerSecond < 100000)
                return (bytesPerSecond / 1000).ToString("0.#", CultureInfo.InvariantCulture) + "K";
            string[] units = { "M", "G", "T", "P", "E" };
            double value = bytesPerSecond / 1000000;
            int unit = 0;
            while (value >= 999.5 && unit < units.Length - 1) { value /= 1000; unit++; }
            if (value >= 999.5) return ">999E";
            return value.ToString(value < 10 ? "0.##" : value < 100 ? "0.#" : "0", CultureInfo.InvariantCulture) + units[unit];
        }

        internal void RefreshPlacement()
        {
            double scaleX = 1, scaleY = 1;
            HwndSource source = nativeSource;
            if (source != null && !source.IsDisposed && source.CompositionTarget != null)
            {
                Matrix matrix = source.CompositionTarget.TransformToDevice;
                scaleX = matrix.M11; scaleY = matrix.M22;
            }
            if (!IsFinite(scaleX) || scaleX <= 0) scaleX = 1;
            if (!IsFinite(scaleY) || scaleY <= 0) scaleY = 1;
            double left = IsFinite(Left) ? Left : SystemParameters.WorkArea.Right - Width - 24;
            double top = IsFinite(Top) ? Top : SystemParameters.WorkArea.Top + 40;
            System.Drawing.Rectangle pixels = new System.Drawing.Rectangle(
                ToScreenPixel(left * scaleX), ToScreenPixel(top * scaleY),
                Math.Max(1, ToScreenPixel(Width * scaleX)), Math.Max(1, ToScreenPixel(Height * scaleY)));
            System.Drawing.Rectangle area = Forms.Screen.FromRectangle(pixels).WorkingArea;
            Rect work = new Rect(area.Left / scaleX, area.Top / scaleY, area.Width / scaleX, area.Height / scaleY);
            Rect clamped = ClampToWorkArea(new Rect(left, top, Width, Height), work);
            Left = clamped.Left; Top = clamped.Top;
        }

        private static int ToScreenPixel(double value)
        {
            return (int)Math.Max(Int32.MinValue + 1d, Math.Min(Int32.MaxValue - 1d, Math.Round(value)));
        }

        internal static Rect ClampToWorkArea(Rect window, Rect work)
        {
            return new Rect(
                Math.Max(work.Left, Math.Min(window.Left, work.Right - window.Width)),
                Math.Max(work.Top, Math.Min(window.Top, work.Bottom - window.Height)),
                window.Width, window.Height);
        }

        internal static long InteractionStyle(long current, bool through)
        {
            long result = current | ToolWindowStyle | NoActivateStyle | LayeredStyle;
            return through ? result | TransparentStyle : result & ~TransparentStyle;
        }

        private void ApplyNativeInteraction()
        {
            IntPtr handle = new WindowInteropHelper(this).Handle;
            if (handle == IntPtr.Zero) return;
            long style = ReadWindowStyle(handle);
            long updated = InteractionStyle(style, clickThrough);
            if (updated != style)
            {
                if (IntPtr.Size == 8) SetWindowLongPtr(handle, ExtendedStyleIndex, new IntPtr(updated));
                else SetWindowLong(handle, ExtendedStyleIndex, unchecked((int)updated));
                SetWindowPos(handle, IntPtr.Zero, 0, 0, 0, 0, 0x37); // No move/resize/z-order/activation; refresh style.
            }
        }

        private IntPtr WindowMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (message == 0x84 && clickThrough) { handled = true; return new IntPtr(-1); } // HTTRANSPARENT
            if (message == 0x21) { handled = true; return new IntPtr(3); } // MA_NOACTIVATE
            return IntPtr.Zero;
        }

        internal static long ReadWindowStyle(IntPtr handle)
        {
            return IntPtr.Size == 8 ? GetWindowLongPtr(handle, ExtendedStyleIndex).ToInt64() : (uint)GetWindowLong(handle, ExtendedStyleIndex);
        }

        private static bool IsFinite(double value) { return !Double.IsNaN(value) && !Double.IsInfinity(value); }
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(IntPtr hwnd, int index);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongW")] private static extern int SetWindowLong(IntPtr hwnd, int index, int value);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
    }
}
