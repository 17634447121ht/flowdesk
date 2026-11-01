using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using FlowDesk;

internal static class TraySpeedIconTests
{
    private static int checks;
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception("FAIL " + message);
        checks++; Console.WriteLine("PASS " + message);
    }
    private static void Expect(double speed, string text) { Check(TraySpeedIcon.Compact(speed) == text, "compact " + speed.ToString("R", CultureInfo.InvariantCulture) + " = " + text); }
    private static Rectangle InkBounds(Bitmap bitmap)
    {
        int left = bitmap.Width, right = -1, top = bitmap.Height, bottom = -1;
        for (int y = 0; y < bitmap.Height; y++)
        for (int x = 0; x < bitmap.Width; x++)
        {
            if (bitmap.GetPixel(x, y).A == 0) continue;
            left = Math.Min(left, x); right = Math.Max(right, x);
            top = Math.Min(top, y); bottom = Math.Max(bottom, y);
        }
        return right < left ? Rectangle.Empty : Rectangle.FromLTRB(left, top, right + 1, bottom + 1);
    }
    private static void RenderSheet(string directory)
    {
        double[] values = { Double.NaN, 0, 500, 1000, 80000, 100000, 550000, 950000, 12700000, 99500000 };
        int[] sizes = { 16, 20, 24, 32 };
        using (Bitmap sheet = new Bitmap(1100, 1080, PixelFormat.Format32bppArgb))
        using (Graphics graphics = Graphics.FromImage(sheet))
        using (Font label = new Font("Segoe UI", 10, FontStyle.Regular, GraphicsUnit.Pixel))
        {
            for (int theme = 0; theme < 2; theme++)
            {
                bool light = theme == 1;
                Color background = light ? Color.FromArgb(245, 245, 245) : Color.FromArgb(30, 31, 34);
                Color foreground = light ? Color.FromArgb(50, 50, 50) : Color.FromArgb(230, 230, 230);
                using (SolidBrush panel = new SolidBrush(background)) graphics.FillRectangle(panel, 0, theme * 540, 1100, 540);
                using (SolidBrush ink = new SolidBrush(foreground))
                {
                    graphics.DrawString((light ? "Light taskbar" : "Dark taskbar") + " — Windows system font; download teal / upload purple; actual size and 2x zoom", label, ink, 10, theme * 540 + 10);
                    for (int row = 0; row < sizes.Length; row++)
                    {
                        int size = sizes[row], top = theme * 540 + 55 + row * 120;
                        graphics.DrawString(size + "px", label, ink, 6, top + 5);
                        for (int column = 0; column < values.Length; column++)
                        {
                            int left = 56 + column * 104;
                            using (Bitmap down = TraySpeedIcon.RenderBitmap(values[column], true, size, light))
                            using (Bitmap up = TraySpeedIcon.RenderBitmap(values[column], false, size, light))
                            {
                                graphics.DrawImageUnscaled(down, left, top);
                                graphics.DrawImageUnscaled(up, left + size + 3, top);
                                graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
                                graphics.DrawImage(down, new Rectangle(left, top + 36, size * 2, size * 2), new Rectangle(0, 0, size, size), GraphicsUnit.Pixel);
                                graphics.InterpolationMode = InterpolationMode.Bilinear;
                            }
                            graphics.DrawString(TraySpeedIcon.Compact(values[column]), label, ink, left, top + 104);
                        }
                    }
                }
            }
            sheet.Save(Path.Combine(directory, "tray-speed-icons.png"), ImageFormat.Png);
        }
    }
    private static bool SameAlphaShape(Bitmap first, Rectangle firstBounds, Bitmap second, Rectangle secondBounds)
    {
        if (firstBounds.Size != secondBounds.Size) return false;
        for (int y = 0; y < firstBounds.Height; y++)
        for (int x = 0; x < firstBounds.Width; x++)
            if (first.GetPixel(firstBounds.Left + x, firstBounds.Top + y).A !=
                second.GetPixel(secondBounds.Left + x, secondBounds.Top + y).A) return false;
        return true;
    }
    private static IEnumerable<double> RenderValues()
    {
        yield return Double.NaN; yield return 0; yield return 500; yield return Double.PositiveInfinity;
        // Exercise all two-digit labels and every fractional label in each unit,
        // rather than a few conveniently narrow numbers such as 11K.
        double scale = 1000;
        for (int unit = 0; unit < 6; unit++, scale *= 1000)
        {
            for (int number = 1; number <= 99; number++) yield return number * scale;
            if (unit > 0)
                for (int fraction = 1; fraction <= 9; fraction++) yield return fraction * scale / 10;
        }
    }
    private static void CheckRendering()
    {
        foreach (int size in new[] { 16, 20, 24, 32, 48, 64 })
        {
            using (Font font = TraySpeedIcon.CreateTextFont(size))
            using (Font repeated = TraySpeedIcon.CreateTextFont(size))
            using (Font system = SystemFonts.MessageBoxFont)
            {
                Check(font.FontFamily.Name == system.FontFamily.Name && font.Style == system.Style,
                    "uses the actual Windows message font family/style at " + size + "px");
                Check(font.Size == repeated.Size && font.Unit == repeated.Unit && font.Size > 0,
                    "one stable font size is selected for an icon size at " + size + "px");
                int? baselineOffset = null;
                int labels = 0;
                foreach (double value in RenderValues())
                using (Bitmap mask = TraySpeedIcon.RenderTextMask(TraySpeedIcon.Compact(value), font))
                using (Bitmap rendered = TraySpeedIcon.RenderBitmap(value, true, size, false))
                {
                    Rectangle source = InkBounds(mask), result = InkBounds(rendered);
                    string context = TraySpeedIcon.Compact(value) + " / " + size + "px";
                    if (source.IsEmpty || result.IsEmpty) throw new Exception("Invisible system font label: " + context);
                    if (result.Left < 1 || result.Top < 1 || result.Right > size - 1 || result.Bottom > size - 1)
                        throw new Exception("System font label touches the icon boundary: " + context);
                    if (!SameAlphaShape(mask, source, rendered, result))
                        throw new Exception("System font shape was clipped or stretched: " + context);
                    int offset = result.Top - source.Top;
                    if (baselineOffset.HasValue && baselineOffset.Value != offset)
                        throw new Exception("Text baseline moves between labels: " + context);
                    baselineOffset = offset;
                    labels++;
                }
                Check(labels >= 600, "complete labels retain their system glyph shape, size and baseline at " + size + "px (" + labels + " labels)");
            }
        }
        foreach (int size in new[] { 16, 20, 24, 32 })
        foreach (bool light in new[] { false, true })
        {
            using (Bitmap down = TraySpeedIcon.RenderBitmap(80000, true, size, light))
            using (Bitmap up = TraySpeedIcon.RenderBitmap(80000, false, size, light))
            {
                int visible = 0, differences = 0, blendedEdges = 0;
                bool sameShape = true, noColorFringe = true;
                Color expectedDown = light ? Color.FromArgb(20, 85, 67) : Color.FromArgb(169, 232, 210);
                Color expectedUp = light ? Color.FromArgb(88, 45, 123) : Color.FromArgb(222, 199, 255);
                for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    Color color = down.GetPixel(x, y), other = up.GetPixel(x, y);
                    if (color.A != 0)
                    {
                        visible++;
                        if (color.R != expectedDown.R || color.G != expectedDown.G || color.B != expectedDown.B ||
                            other.R != expectedUp.R || other.G != expectedUp.G || other.B != expectedUp.B) noColorFringe = false;
                    }
                    if (color.A != 0 && color.A != 255) blendedEdges++;
                    if (color.A != other.A) sameShape = false;
                    if (color != other) differences++;
                }
                Check(down.Width == size && down.Height == size && visible > size && visible < size * size * .85,
                    "transparent system text fits at " + size + "px / light=" + light);
                Check(differences > 0 && sameShape, "download/upload differ only in color at " + size + "px / light=" + light);
                Check(noColorFringe && blendedEdges > 0, "grayscale alpha edges have no ClearType color fringes at " + size + "px / light=" + light);
            }
        }
        using (Icon icon = TraySpeedIcon.Create(100000, false, 20, true)) Check(icon.Width == 20 && icon.Height == 20, "HICON clone has requested size");
        foreach (bool download in new[] { false, true })
        using (Bitmap dark = TraySpeedIcon.RenderBitmap(80000, download, 16, false))
        using (Bitmap light = TraySpeedIcon.RenderBitmap(80000, download, 16, true))
        {
            int contrasts = 0;
            for (int y = 0; y < 16; y++) for (int x = 0; x < 16; x++)
                if (dark.GetPixel(x, y).A != 0 && dark.GetPixel(x, y).G > light.GetPixel(x, y).G + 100) contrasts++;
            Check(contrasts > 10, "direction text has clear light/dark theme contrast; download=" + download);
        }
    }
    private static void CheckHandles()
    {
        using (Process process = Process.GetCurrentProcess())
        {
            for (int i = 0; i < 64; i++) using (Icon icon = TraySpeedIcon.Create(i * 10000, i % 2 == 0, 20, false)) { }
            uint beforeGdi = GetGuiResources(process.Handle, 0), beforeUser = GetGuiResources(process.Handle, 1);
            for (int i = 0; i < 2000; i++) using (Icon icon = TraySpeedIcon.Create(i * 1500, i % 2 == 0, 16 + (i % 3) * 8, i % 2 != 0)) { }
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            uint afterGdi = GetGuiResources(process.Handle, 0), afterUser = GetGuiResources(process.Handle, 1);
            Console.WriteLine("GDI handles " + beforeGdi + " -> " + afterGdi + "; USER handles " + beforeUser + " -> " + afterUser);
            Check(afterGdi <= beforeGdi + 2 && afterUser <= beforeUser + 2, "repeated icon creation releases native handles");
        }
    }
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            string directory = Path.GetFullPath(args[0]); Directory.CreateDirectory(directory);
            Expect(0, "0"); Expect(-3, "0"); Expect(Double.NaN, "—"); Expect(Double.NegativeInfinity, "0"); Expect(Double.PositiveInfinity, ">E");
            Expect(.001, "<1K"); Expect(1, "<1K"); Expect(800, "<1K"); Expect(999.99, "<1K");
            Expect(1000, "1K"); Expect(1200, "1K"); Expect(80000, "80K"); Expect(99499, "99K"); Expect(99500, ".1M");
            Expect(100000, ".1M"); Expect(550000, ".6M"); Expect(949999, ".9M"); Expect(950000, "1M");
            Expect(1000000, "1M"); Expect(1200000, "1M"); Expect(12700000, "13M"); Expect(99500000, ".1G");
            Expect(1000000000, "1G"); Expect(1000000000000, "1T");
            for (int power = -10; power < 309; power++)
            {
                string value = TraySpeedIcon.Compact(Math.Pow(10, power));
                if (value.Length > 3 || value.IndexOf(',') >= 0) throw new Exception("Unexpected compact format: " + value);
            }
            Check(true, "all finite orders of magnitude keep at most three characters");
            CheckRendering(); CheckHandles(); RenderSheet(directory);
            Console.WriteLine("Tray icon checks passed: " + checks); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
    [DllImport("user32.dll")] private static extern uint GetGuiResources(IntPtr process, uint flags);
}
