using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FlowDesk;

internal static class MinimalWindowTests
{
    private static int checks;
    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception("FAIL " + name);
        checks++; Console.WriteLine("PASS " + name);
    }
    private static void Expect(double value, string formatted)
    {
        Check(MinimalSpeedWindow.FormatRate(value) == formatted, "rate " + value + " = " + formatted);
    }
    private static FrameworkElement Arrange(MinimalSpeedWindow window)
    {
        FrameworkElement content = (FrameworkElement)window.Content;
        content.Measure(new Size(window.Width, window.Height));
        content.Arrange(new Rect(0, 0, window.Width, window.Height));
        content.UpdateLayout();
        return content;
    }
    private static byte[] Render(MinimalSpeedWindow window, string path)
    {
        FrameworkElement content = Arrange(window);
        RenderTargetBitmap bitmap = new RenderTargetBitmap((int)window.Width, (int)window.Height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(content);
        byte[] pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
        PngBitmapEncoder encoder = new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (FileStream stream = File.Create(path)) encoder.Save(stream);
        return pixels;
    }
    private static int PixelAlpha(byte[] pixels, int x, int y) { return pixels[(y * (int)MinimalSpeedWindow.SurfaceWidth + x) * 4 + 3]; }
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            string output = Path.GetFullPath(args[0]); Directory.CreateDirectory(output);
            Expect(0, "0"); Expect(-1, "0"); Expect(Double.NaN, "—"); Expect(Double.PositiveInfinity, "—");
            Expect(.1, "<1B"); Expect(80, "80B"); Expect(800, "800B"); Expect(1000, "1K");
            Expect(80000, "80K"); Expect(120000, "0.12M"); Expect(550000, "0.55M");
            Expect(1000000, "1M"); Expect(12700000, "12.7M"); Expect(999500000, "1G"); Expect(Double.MaxValue, ">999E");
            double widest = 0;
            for (int exponent = -300; exponent <= 308; exponent++)
            {
                string value = MinimalSpeedWindow.FormatRate(Math.Pow(10, exponent));
                FormattedText text = new FormattedText(value, System.Globalization.CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, new Typeface(SystemFonts.MessageFontFamily, SystemFonts.MessageFontStyle,
                    SystemFonts.MessageFontWeight, FontStretches.Normal), 12, Brushes.White, 1);
                widest = Math.Max(widest, text.WidthIncludingTrailingWhitespace);
                if (value.Length > 6 || text.WidthIncludingTrailingWhitespace > MinimalSpeedWindow.SurfaceWidth - 8)
                    throw new Exception("Speed label exceeds reserved width: " + value);
            }
            Check(widest > 0, "all finite magnitude ranges fit the fixed 12px text area");
            Rect negative = MinimalSpeedWindow.ClampToWorkArea(new Rect(-1800, -80, 112, 40), new Rect(-1920, -1080, 1920, 1080));
            Check(negative.Left == -1800 && negative.Top == -80, "negative monitor coordinates remain valid");
            Rect clamped = MinimalSpeedWindow.ClampToWorkArea(new Rect(9000, -9999, 112, 40), new Rect(-1920, -1080, 1920, 1080));
            Check(clamped.Left == -112 && clamped.Top == -1080, "offscreen position clamps to nearest work area");
            Check((MinimalSpeedWindow.InteractionStyle(0x100, false) & 0x08080080) == 0x08080080,
                "base native style is layered/tool-window/nonactivating");
            Check((MinimalSpeedWindow.InteractionStyle(0x100, true) & 0x120) == 0x120,
                "click-through preserves other native styles");
            Check((MinimalSpeedWindow.InteractionStyle(0x120, false) & 0x20) == 0,
                "click-through can be disabled");
            int saves = 0;
            MinimalSpeedWindow window = new MinimalSpeedWindow(delegate(double l, double t) { saves++; }, delegate { });
            try
            {
                AppSettings settings = new AppSettings { MinimalTextTheme = "light", MinimalTextShadow = false, TopMost = true };
                window.ApplySettings(settings, false);
                Check(!window.IsVisible && !window.ShowInTaskbar && !window.ShowActivated && !window.Focusable,
                    "constructing/applying settings does not show or activate a taskbar window");
                Check(window.AllowsTransparency && ((SolidColorBrush)window.Background).Color.A == 0 && window.WindowStyle == WindowStyle.None,
                    "window background is fully transparent and has no chrome");
                Grid surface = (Grid)window.Content;
                TextBlock down = (TextBlock)surface.Children[0], up = (TextBlock)surface.Children[1];
                Check(down.FontSize == 12 && up.FontSize == 12 && down.FontFamily.Source == SystemFonts.MessageFontFamily.Source,
                    "both lines use fixed 12px Windows system font");
                down.FontFamily = new FontFamily("Arial"); down.FontStyle = FontStyles.Italic; down.FontWeight = FontWeights.Bold;
                up.FontFamily = new FontFamily("Arial"); up.FontStyle = FontStyles.Italic; up.FontWeight = FontWeights.Bold;
                window.ApplySettings(settings, false);
                Check(down.FontFamily.Source == SystemFonts.MessageFontFamily.Source && up.FontFamily.Source == SystemFonts.MessageFontFamily.Source &&
                    down.FontStyle == SystemFonts.MessageFontStyle && up.FontStyle == SystemFonts.MessageFontStyle &&
                    down.FontWeight == SystemFonts.MessageFontWeight && up.FontWeight == SystemFonts.MessageFontWeight,
                    "reapplying appearance refreshes current Windows font preferences");
                Check(Grid.GetRow(down) == 0 && Grid.GetRow(up) == 1 && down.TextAlignment == TextAlignment.Right && up.TextAlignment == TextAlignment.Right,
                    "download is above upload and both are right-aligned");
                window.UpdateRates(120000, 80000, true, "下载 120 KB/s\n上传 80 KB/s");
                Check(down.Text == "0.12M" && up.Text == "80K" && surface.ToolTip.ToString().Contains("上传 80 KB/s"),
                    "basic values and precise tooltip are retained");
                byte[] pixels = Render(window, Path.Combine(output, "minimal-light-text.png"));
                int visible = 0, transparent = 0;
                for (int i = 3; i < pixels.Length; i += 4) { if (pixels[i] == 0) transparent++; else visible++; }
                Check(visible > 100 && transparent > pixels.Length / 4 * .7, "render contains only text on transparent pixels");
                bool clearBorder = true;
                for (int x=0;x<(int)window.Width;x++)
                    if(PixelAlpha(pixels,x,0)!=0 || PixelAlpha(pixels,x,(int)window.Height-1)!=0) clearBorder=false;
                for (int y=0;y<(int)window.Height;y++)
                    if(PixelAlpha(pixels,0,y)!=0 || PixelAlpha(pixels,(int)window.Width-1,y)!=0) clearBorder=false;
                Check(clearBorder, "transparent surface has no background or border");
                double oldDownTop = down.TransformToAncestor(surface).Transform(new Point()).Y;
                double oldUpTop = up.TransformToAncestor(surface).Transform(new Point()).Y;
                window.UpdateRates(Double.MaxValue, 1, true, "precise"); Arrange(window);
                Check(window.Width == 112 && window.Height == 40 && down.FontSize == 12 &&
                    down.TransformToAncestor(surface).Transform(new Point()).Y == oldDownTop &&
                    up.TransformToAncestor(surface).Transform(new Point()).Y == oldUpTop,
                    "widest/narrowest values keep size and baseline");
                foreach (TextBlock text in new[] { down, up })
                {
                    Point at = text.TransformToAncestor(surface).Transform(new Point());
                    Check(at.X >= 0 && at.Y >= 0 && at.X + text.ActualWidth <= window.Width && at.Y + text.ActualHeight <= window.Height,
                        "widest label stays inside the transparent surface");
                }
                settings.MinimalTextTheme = "dark"; settings.MinimalTextShadow = true; settings.Opacity = .2;
                window.ApplySettings(settings, false);
                Check(((SolidColorBrush)down.Foreground).Color == Color.FromRgb(20,85,67) && down.Effect != null && up.Effect != null && window.Opacity == 1,
                    "dark text and optional shadow do not fade the text with detail opacity");
                window.UpdateRates(120000, 80000, true, "precise");
                byte[] shadowPixels = Render(window, Path.Combine(output, "minimal-dark-text-shadow.png"));
                bool shadowBorderClear = true;
                for (int x=0;x<(int)window.Width;x++)
                    if(PixelAlpha(shadowPixels,x,0)!=0 || PixelAlpha(shadowPixels,x,(int)window.Height-1)!=0) shadowBorderClear=false;
                for (int y=0;y<(int)window.Height;y++)
                    if(PixelAlpha(shadowPixels,0,y)!=0 || PixelAlpha(shadowPixels,(int)window.Width-1,y)!=0) shadowBorderClear=false;
                Check(shadowBorderClear, "optional text shadows retain a fully transparent outer border");
                settings.MinimalTextTheme = "auto";settings.MinimalTextShadow=false;
                window.ApplySettings(settings, true);
                Check(((SolidColorBrush)down.Foreground).Color == Color.FromRgb(20,85,67), "auto theme chooses dark text on light background");
                window.ApplySettings(settings, false);
                Check(((SolidColorBrush)down.Foreground).Color == Color.FromRgb(169,232,210), "auto theme chooses light text on dark background");
                window.UpdateRates(120000,80000,false,"网络不可用");
                Check(down.Text == "—" && up.Text == "—", "unavailable monitoring immediately clears old speed");
                IntPtr handle = new WindowInteropHelper(window).EnsureHandle();
                long native = MinimalSpeedWindow.ReadWindowStyle(handle);
                Check((native & 0x08080080) == 0x08080080 && (native & 0x20) == 0,
                    "hidden native window has layered/nonactivating/tool-window styles");
                IntPtr foreground = GetForegroundWindow();
                IntPtr mouseActivation = SendMessage(handle, 0x21, IntPtr.Zero, new IntPtr(0x02010001));
                Check(mouseActivation == new IntPtr(3) && GetForegroundWindow() == foreground,
                    "native mouse activation request is rejected without changing foreground");
                settings.MinimalLocked=true;settings.MinimalClickThrough=true;
                window.ApplySettings(settings,false);
                Check((MinimalSpeedWindow.ReadWindowStyle(handle) & 0x20) != 0 && surface.Cursor == System.Windows.Input.Cursors.Arrow,
                    "click-through is applied to the actual native window");
                Check(SendMessage(handle, 0x84, IntPtr.Zero, IntPtr.Zero) == new IntPtr(-1),
                    "click-through native hit test returns HTTRANSPARENT");
                settings.MinimalLocked=false;settings.MinimalClickThrough=false;
                window.ApplySettings(settings,false);
                Check((MinimalSpeedWindow.ReadWindowStyle(handle) & 0x20) == 0 && surface.Cursor == System.Windows.Input.Cursors.SizeAll,
                    "native click-through can be safely reversed through settings");
                Check(saves == 0, "settings/theme/placement do not pretend the user dragged the window");
            }
            finally { window.Close(); }
            Console.WriteLine("Minimal window checks passed: " + checks);return 0;
        }
        catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", CharSet=CharSet.Auto)] private static extern IntPtr SendMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam);
}
