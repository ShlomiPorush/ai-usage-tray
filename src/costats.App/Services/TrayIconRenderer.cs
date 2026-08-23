using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Runtime.InteropServices;
using costats.Core.Tray;

namespace costats.App.Services
{
    /// <summary>
    /// Draws the tray icon: the highest used percentage as big as the canvas
    /// allows, in near-black ink on a filled plate of the band colour.
    /// </summary>
    /// <remarks>
    /// Windows draws notification-area icons at the small-icon size (16 px at
    /// 100% scaling), so anything rendered at a fixed point size vanishes. The
    /// digits are laid out as a path and scaled to fill the plate instead, and
    /// the plate is a rounded square rather than a circle because it leaves
    /// roughly twice the usable area for the number.
    /// </remarks>
    public static class TrayIconRenderer
    {
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyIcon(IntPtr hIcon);

        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int index);

        private const int SmCxSmIcon = 49;

        // Near-black ink clears 4.5:1 on all four vivid bands; white does not,
        // and yellow is one of them.
        private static readonly Color Ink = Color.FromArgb(17, 24, 39); // #111827

        /// <summary>
        /// The size Windows actually draws notification icons at, so the bitmap
        /// is rendered 1:1 instead of being downscaled into mush.
        /// </summary>
        public static int PreferredSize()
        {
            try
            {
                var size = GetSystemMetrics(SmCxSmIcon);
                return size is >= 16 and <= 256 ? size : 16;
            }
            catch
            {
                return 16;
            }
        }

        /// <summary>
        /// Builds the icon handed to the shell. Caller owns the returned icon.
        /// </summary>
        public static Icon CreateIcon(TraySeverity severity, double? highestUsedPercent)
        {
            using var bitmap = Render(severity, highestUsedPercent, PreferredSize());
            var handle = bitmap.GetHicon();
            using var temporary = Icon.FromHandle(handle);
            var cloned = (Icon)temporary.Clone();
            DestroyIcon(handle);
            return cloned;
        }

        /// <summary>
        /// Renders the icon artwork at an arbitrary square size. Caller owns the
        /// returned bitmap.
        /// </summary>
        public static Bitmap Render(TraySeverity severity, double? highestUsedPercent, int size)
        {
            var bitmap = new Bitmap(size, size);
            using var graphics = Graphics.FromImage(bitmap);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.Clear(Color.Transparent);

            // The four locked vivid band colours, identical in both themes.
            var colour = severity switch
            {
                TraySeverity.Green => Color.FromArgb(16, 185, 129),   // #10B981
                TraySeverity.Yellow => Color.FromArgb(234, 179, 8),   // #EAB308
                TraySeverity.Orange => Color.FromArgb(249, 115, 22),  // #F97316
                TraySeverity.Red => Color.FromArgb(239, 68, 68),      // #EF4444
                _ => Color.FromArgb(107, 114, 128)
            };

            // Minimal padding: every pixel spent on the margin is a pixel the
            // number does not get.
            var inset = Math.Max(0.5f, size / 32f);
            var plate = new RectangleF(inset, inset, size - (inset * 2), size - (inset * 2));

            using (var background = new SolidBrush(colour))
            using (var shape = RoundedRectangle(plate, plate.Width * 0.22f))
            {
                graphics.FillPath(background, shape);
            }

            // With no data the plate stays blank, matching the old loading look.
            if (highestUsedPercent.HasValue)
            {
                var percentText = ((long)Math.Round(Math.Clamp(highestUsedPercent.Value, 0, 100)))
                    .ToString(CultureInfo.InvariantCulture);
                DrawFittedNumber(graphics, percentText, plate);
            }

            return bitmap;
        }

        private static void DrawFittedNumber(Graphics graphics, string text, RectangleF plate)
        {
            using var family = new FontFamily("Segoe UI");
            using var format = StringFormat.GenericTypographic;
            using var path = new GraphicsPath();

            // A large nominal em size keeps the outline accurate before scaling.
            path.AddString(text, family, (int)FontStyle.Bold, 96f, PointF.Empty, format);

            var bounds = path.GetBounds();
            if (bounds.Width <= 0f || bounds.Height <= 0f)
            {
                return;
            }

            // Just enough breathing room that the digits do not bleed into the
            // rounded corners. "100" is width-starved either way, so it gets
            // almost the whole plate.
            var padX = plate.Width * (text.Length >= 3 ? 0.02f : 0.07f);
            var target = RectangleF.Inflate(plate, -padX, -plate.Height * 0.11f);
            var scale = Math.Min(target.Width / bounds.Width, target.Height / bounds.Height);

            using var transform = new Matrix();
            transform.Translate(
                target.X + ((target.Width - (bounds.Width * scale)) / 2f),
                target.Y + ((target.Height - (bounds.Height * scale)) / 2f));
            transform.Scale(scale, scale);
            transform.Translate(-bounds.X, -bounds.Y);
            path.Transform(transform);

            using var ink = new SolidBrush(Ink);
            graphics.FillPath(ink, path);
        }

        private static GraphicsPath RoundedRectangle(RectangleF bounds, float radius)
        {
            var diameter = Math.Min(radius, Math.Min(bounds.Width, bounds.Height) / 2f) * 2f;
            var path = new GraphicsPath();
            if (diameter <= 0f)
            {
                path.AddRectangle(bounds);
                return path;
            }

            path.AddArc(bounds.X, bounds.Y, diameter, diameter, 180f, 90f);
            path.AddArc(bounds.Right - diameter, bounds.Y, diameter, diameter, 270f, 90f);
            path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0f, 90f);
            path.AddArc(bounds.X, bounds.Bottom - diameter, diameter, diameter, 90f, 90f);
            path.CloseFigure();
            return path;
        }
    }
}
