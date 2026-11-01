using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.Runtime.InteropServices;

namespace FlowDesk
{
    /// <summary>
    /// A transparent, single-line rate icon for a native notification-area slot.
    /// Compact rates are rounded; the NotifyIcon tooltip carries the precise rate.
    /// K/M/G/T/P/E use decimal byte units. Text color marks the direction.
    /// </summary>
    internal static class TraySpeedIcon
    {
        private static readonly string[] Units = { "K", "M", "G", "T", "P", "E" };
        private static readonly Dictionary<string, FontLayout> FontLayouts = new Dictionary<string, FontLayout>();

        private sealed class FontLayout
        {
            internal float PixelSize;
            internal int Width, Top, Bottom;
        }

        internal static string Compact(double bytesPerSecond)
        {
            if (Double.IsNaN(bytesPerSecond)) return "—";
            if (bytesPerSecond <= 0) return "0";
            if (Double.IsPositiveInfinity(bytesPerSecond)) return ">E";
            if (bytesPerSecond < 1000) return "<1K";
            double value = bytesPerSecond / 1000;
            int unit = 0;
            // Keep room for a fixed font size: at most two digits plus
            // the unit. Fractions below one omit their leading zero (.1M).
            while (value >= 99.5 && unit < Units.Length - 1) { value /= 1000; unit++; }
            if (value >= 99.5) return ">E";
            if (value < 1)
            {
                double rounded = Math.Round(value, 1, MidpointRounding.AwayFromZero);
                return (rounded >= 1 ? "1" : rounded.ToString(".0", CultureInfo.InvariantCulture)) + Units[unit];
            }
            return Math.Round(value, 0, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture) + Units[unit];
        }
        internal static Icon Create(double bytesPerSecond, bool download, int size, bool lightTaskbar)
        {
            using (Bitmap bitmap = RenderBitmap(bytesPerSecond, download, size, lightTaskbar))
            {
                IntPtr handle = bitmap.GetHicon();
                try
                {
                    // FromHandle does not own its HICON. Clone it before releasing
                    // the temporary native handle, then let the caller own the clone.
                    using (Icon borrowed = Icon.FromHandle(handle)) return (Icon)borrowed.Clone();
                }
                finally { DestroyIcon(handle); }
            }
        }

        internal static Font CreateTextFont(int size)
        {
            ValidateSize(size);
            using (Font systemFont = SystemFonts.MessageBoxFont)
            {
                return new Font(systemFont.FontFamily, GetFontLayout(size, systemFont).PixelSize, systemFont.Style, GraphicsUnit.Pixel);
            }
        }

        private static FontLayout GetFontLayout(int size, Font systemFont)
        {
            string key = size + ":" + systemFont.Name + ":" + systemFont.Style + ":" + systemFont.SizeInPoints.ToString("R", CultureInfo.InvariantCulture);
            lock (FontLayouts)
            {
                FontLayout layout;
                if (FontLayouts.TryGetValue(key, out layout)) return layout;
                float step = .25f * size / 16;
                float maximum = (float)Math.Floor(systemFont.SizeInPoints * 96 / 72 * size / 16 / step) * step;
                float candidate = maximum;
                layout = MeasureLayout(systemFont, candidate);
                if (!Fits(layout, size))
                {
                    float ratio = Math.Min((size - 2f) / layout.Width, (size - 2f) / (layout.Bottom - layout.Top));
                    candidate = Math.Max(step, (float)Math.Floor(candidate * ratio / step) * step);
                    layout = MeasureLayout(systemFont, candidate);
                }
                while (!Fits(layout, size) && candidate > step)
                {
                    candidate -= step;
                    layout = MeasureLayout(systemFont, candidate);
                }
                // Fit all labels once per system-font/icon-size. The current
                // rate never changes the font size or stretches its glyphs.
                while (candidate + step <= maximum)
                {
                    FontLayout larger = MeasureLayout(systemFont, candidate + step);
                    if (!Fits(larger, size)) break;
                    candidate += step;
                    layout = larger;
                }
                if (FontLayouts.Count >= 64) FontLayouts.Clear();
                FontLayouts.Add(key, layout);
                return layout;
            }
        }

        private static bool Fits(FontLayout layout, int size)
        {
            return layout.Width <= size - 2 && layout.Bottom - layout.Top <= size - 2;
        }

        private static FontLayout MeasureLayout(Font systemFont, float pixelSize)
        {
            FontLayout layout = new FontLayout { PixelSize = pixelSize, Top = Int32.MaxValue };
            using (Font font = new Font(systemFont.FontFamily, pixelSize, systemFont.Style, GraphicsUnit.Pixel))
            {
                foreach (string unit in Units)
                {
                    for (int number = 1; number <= 99; number++)
                        MeasureLabel(number.ToString(CultureInfo.InvariantCulture) + unit, font, layout);
                    for (int fraction = 1; fraction <= 9; fraction++)
                        MeasureLabel("." + fraction + unit, font, layout);
                }
                foreach (string text in new[] { "0", "<1K", ">E", "—" }) MeasureLabel(text, font, layout);
            }
            return layout;
        }

        private static void MeasureLabel(string text, Font font, FontLayout layout)
        {
            using (Bitmap mask = RenderTextMask(text, font))
            {
                Rectangle bounds = InkBounds(mask);
                layout.Width = Math.Max(layout.Width, bounds.Width);
                layout.Top = Math.Min(layout.Top, bounds.Top);
                layout.Bottom = Math.Max(layout.Bottom, bounds.Bottom);
            }
        }

        // Draw Windows font glyphs at their final pixel size. Grayscale coverage
        // preserves transparency without ClearType color fringes.
        internal static Bitmap RenderTextMask(string text, Font font)
        {
            using (Bitmap measure = new Bitmap(1, 1))
            using (Graphics graphics = Graphics.FromImage(measure))
            using (StringFormat format = (StringFormat)StringFormat.GenericTypographic.Clone())
            {
                format.FormatFlags |= StringFormatFlags.NoClip | StringFormatFlags.NoWrap;
                int width = (int)Math.Ceiling(graphics.MeasureString(text, font, Int32.MaxValue, format).Width) + 12;
                int height = (int)Math.Ceiling(font.GetHeight(graphics)) + 12;
                Bitmap mask = new Bitmap(width, height, PixelFormat.Format32bppArgb);
                try
                {
                    using (Graphics drawing = Graphics.FromImage(mask))
                    {
                        drawing.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                        drawing.DrawString(text, font, Brushes.White, new PointF(4, 4), format);
                    }
                    return mask;
                }
                catch { mask.Dispose(); throw; }
            }
        }

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

        private static void ValidateSize(int size)
        {
            if (size < 16 || size > 256) throw new ArgumentOutOfRangeException("size", "Tray icons must be between 16 and 256 pixels.");
        }

        internal static Bitmap RenderBitmap(double bytesPerSecond, bool download, int size, bool lightTaskbar)
        {
            ValidateSize(size);
            Bitmap bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            try
            {
                Color foreground = lightTaskbar
                    ? (download ? Color.FromArgb(20, 85, 67) : Color.FromArgb(88, 45, 123))
                    : (download ? Color.FromArgb(169, 232, 210) : Color.FromArgb(222, 199, 255));
                string text = Compact(bytesPerSecond);
                using (Font systemFont = SystemFonts.MessageBoxFont)
                {
                    FontLayout layout = GetFontLayout(size, systemFont);
                    using (Font font = new Font(systemFont.FontFamily, layout.PixelSize, systemFont.Style, GraphicsUnit.Pixel))
                    using (Bitmap mask = RenderTextMask(text, font))
                    {
                        Rectangle bounds = InkBounds(mask);
                        int left = (size - bounds.Width) / 2 - bounds.Left;
                        int top = (size - (layout.Bottom - layout.Top)) / 2 - layout.Top;
                        for (int y = bounds.Top; y < bounds.Bottom; y++)
                        for (int x = bounds.Left; x < bounds.Right; x++)
                        {
                            int alpha = mask.GetPixel(x, y).A;
                            if (alpha != 0) bitmap.SetPixel(left + x, top + y, Color.FromArgb(alpha, foreground));
                        }
                    }
                }
                return bitmap;
            }
            catch { bitmap.Dispose(); throw; }
        }
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DestroyIcon(IntPtr icon);
    }
}
