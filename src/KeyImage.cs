// Composes the images the keys and knobs display.
//
// The host snapshots a plugin's States - image, font size, TitleAlignment - into
// the profile when an action instance is created, and never re-reads the manifest
// for instances that already exist. Anything that depends on those fields is
// frozen at whatever the manifest said that day, so this plugin draws its own
// labels into the image and leaves the host title empty.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Reflection;

namespace StreamDeckAudio
{
    public static class KeyImage
    {
        private const int Size = 144;

        // The knob screens sit at an angle, so their bottom strip is out of view
        // from where the user looks at the deck: on a knob everything is pulled up
        // and that strip left black. Keys are viewed straight on and use the whole
        // tile, so the same artwork has to be composed differently depending on
        // where the action instance landed.
        private const int KnobBand = 20;

        // The caption stops short of where the generated artwork begins (y=48), so
        // the two never touch.
        private const int TopBand = 2;
        private const int TopBandBottom = 40;
        private const int SidePadding = 7;

        // The now-playing tile is laid out differently from the rest: up to two
        // text rows above the transport glyph and two below it, with the glyph
        // taking whatever height is left over. Keys only, so the whole tile is in
        // view and there is no knob band to keep clear.
        private const int TileTop = 4;
        private const int TileFloor = 140;
        private const int RowHeight = 30;
        private const int IconGap = 4;
        private const int IconMargin = 8;
        private const int LineWidth = Size - SidePadding * 2;
        private const float LineFontSize = 20f;

        private static int BottomFor(bool knob)
        {
            return knob ? Size - KnobBand : Size - 4;
        }

        private static readonly Dictionary<string, string> Cache =
            new Dictionary<string, string>(StringComparer.Ordinal);

        private static readonly Color Ink = ColorTranslator.FromHtml("#F2F4F6");
        private static readonly Color Muted = ColorTranslator.FromHtml("#9AA1A7");

        // What a live figure is saying, in this pack's tones rather than in traffic
        // lights: saturated enough to read across a desk, dull enough to sit on a
        // black tile beside brushed chrome without shouting.
        public static readonly Color Good = ColorTranslator.FromHtml("#7ED09B");
        public static readonly Color Warn = ColorTranslator.FromHtml("#E8C46A");
        public static readonly Color Bad = ColorTranslator.FromHtml("#E0736F");
        public static readonly Color Idle = Muted;

        /// <summary>
        /// A generated glyph with an optional caption above it and an optional
        /// value below. Results are cached: the inputs are all short strings.
        /// </summary>
        public static string Glyph(string baseName, string top, string bottom, bool knob)
        {
            return Glyph(baseName, top, bottom, knob, Ink);
        }

        /// <summary>
        /// The same tile with the value in a colour of its own, which is how a live
        /// key says up or down without spending a word on it.
        /// </summary>
        public static string Glyph(string baseName, string top, string bottom, bool knob, Color bottomColor)
        {
            string key = "g\n" + baseName + "\n" + top + "\n" + bottom + "\n" + knob + "\n" + bottomColor.ToArgb();
            string cached;
            if (Cache.TryGetValue(key, out cached)) return cached;

            string result = null;
            try
            {
                using (Bitmap art = LoadBase(baseName))
                {
                    if (art != null)
                    {
                        using (var canvas = new Bitmap(Size, Size, PixelFormat.Format32bppArgb))
                        {
                            using (Graphics g = Prepare(canvas))
                            {
                                using (var background = new SolidBrush(Color.Black))
                                    g.FillRectangle(background, 0, 0, Size, Size);

                                // The artwork itself sits between y=48 and y=96, so
                                // it clears the strip a knob hides without moving;
                                // only the value underneath has to come up.
                                g.DrawImage(art, 0, 0, Size, Size);

                                int floor = BottomFor(knob);
                                if (!string.IsNullOrEmpty(top))
                                    DrawFitted(g, top, Band(TopBand, TopBandBottom), Ink, 26f, 14f);
                                if (!string.IsNullOrEmpty(bottom))
                                    DrawFitted(g, bottom, Band(floor - 32, floor), bottomColor, 30f, 15f);
                            }
                            result = ToDataUrl(canvas);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Write("KeyImage.Glyph(" + baseName + ") failed: " + ex.Message);
            }

            Cache[key] = result;
            return result;
        }

        /// <summary>
        /// A launcher tile: the item's own icon under its name. The icon keeps its
        /// real colours - recognising the app at a glance beats matching the
        /// pack's style here. Falls back to the shortcut glyph when the item has
        /// no icon to give.
        /// </summary>
        public static string Launcher(Bitmap icon, string label, bool knob)
        {
            if (icon == null) return Glyph("shortcut", label, null, knob);

            try
            {
                using (var canvas = new Bitmap(Size, Size, PixelFormat.Format32bppArgb))
                {
                    using (Graphics g = Prepare(canvas))
                    {
                        using (var background = new SolidBrush(Color.Black))
                            g.FillRectangle(background, 0, 0, Size, Size);

                        // With no name to print, the icon owns the whole tile and
                        // is centred on it; reserving the caption band regardless
                        // would push it visibly low.
                        int floor = BottomFor(knob);
                        RectangleF area;
                        if (string.IsNullOrEmpty(label))
                        {
                            area = new RectangleF(8, 8, Size - 16, floor - 16);
                        }
                        else
                        {
                            DrawFitted(g, label, Band(2, 34), Ink, 22f, 12f);
                            area = new RectangleF(14, 36, Size - 28, floor - 40);
                        }

                        g.DrawImage(icon, Fit(icon.Width, icon.Height, area));
                    }
                    return ToDataUrl(canvas);
                }
            }
            catch (Exception ex)
            {
                Log.Write("KeyImage.Launcher failed: " + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// One line of a stats tile: a short label, its value, and how full the
        /// bar under them is. A row with no bar - uptime, which has no ceiling to
        /// draw against - is text only.
        /// </summary>
        public sealed class Stat
        {
            public string Label;
            public string Text;
            public float Fraction;
            public bool Bar;

            /// <summary>
            /// What this figure is saying - Good, Warn, Bad. Left empty it is
            /// drawn in the ordinary ink and chrome, which is what a figure with
            /// no sensible threshold gets.
            /// </summary>
            public Color Accent;
        }

        /// <summary>
        /// Several figures at once: one row each, sized to whatever height is
        /// left after the caption. A tile can hold about four before the rows stop
        /// being readable across a desk, which is what the caller's choice of
        /// metrics is really limited by.
        /// </summary>
        public static string Stats(string caption, Stat[] rows, bool knob)
        {
            if (rows == null || rows.Length == 0) return null;

            try
            {
                using (var canvas = new Bitmap(Size, Size, PixelFormat.Format32bppArgb))
                {
                    using (Graphics g = Prepare(canvas))
                    {
                        using (var background = new SolidBrush(Color.Black))
                            g.FillRectangle(background, 0, 0, Size, Size);

                        float top = 6f;
                        if (!string.IsNullOrEmpty(caption))
                        {
                            DrawFitted(g, caption, Band(2, 28), Muted, 22f, 12f);
                            top = 32f;
                        }

                        float floor = BottomFor(knob) - 2f;
                        float height = (floor - top) / rows.Length;

                        foreach (Stat row in rows)
                        {
                            DrawStat(g, row, top, height);
                            top += height;
                        }
                    }
                    return ToDataUrl(canvas);
                }
            }
            catch (Exception ex)
            {
                Log.Write("KeyImage.Stats failed: " + ex.Message);
                return null;
            }
        }

        // Label on the left in the muted tone, value hard right in the bright one,
        // and the bar under both. The label is what you skim past; the number is
        // what you are actually looking for.
        private static void DrawStat(Graphics g, Stat row, float top, float height)
        {
            bool bar = row.Bar && height >= 22f;

            // The bar and its breathing room come off the bottom of the row; what
            // is left is the text line.
            float textHeight = bar ? height - 10f : height - 2f;
            float width = Size - SidePadding * 2f;

            // Two bands rather than one shared one: sharing works right up until a
            // value is a word rather than a number, and "DOWN" then lands on top of
            // the label it belongs to.
            float labelWidth = width * 0.42f;
            var label = new RectangleF(SidePadding, top, labelWidth, textHeight);
            var value = new RectangleF(SidePadding + labelWidth + 4f, top,
                                       width - labelWidth - 4f, textHeight);

            bool accented = row.Accent != Color.Empty;

            float maxSize = Math.Min(24f, textHeight);
            DrawAligned(g, row.Label, label, Muted, maxSize * 0.8f, 10f, StringAlignment.Near);
            DrawAligned(g, row.Text, value, accented ? row.Accent : Ink, maxSize, 11f, StringAlignment.Far);

            if (!bar) return;

            var track = new RectangleF(SidePadding, top + textHeight + 2f, Size - SidePadding * 2f, 5f);
            using (GraphicsPath rounded = Rounded(track, 2.5f))
            using (var unfilled = new SolidBrush(ColorTranslator.FromHtml("#2A2E33")))
                g.FillPath(unfilled, rounded);

            float filled = track.Width * Math.Min(1f, Math.Max(0f, row.Fraction));
            if (filled < 3f) return;

            // The bar takes the same colour as the figure, darkened at its left end
            // so it keeps the lit-from-one-side look the rest of the pack has.
            Color bright = accented ? row.Accent : ColorTranslator.FromHtml("#F6F9FB");
            Color dark = accented ? Darken(row.Accent, 0.55f) : ColorTranslator.FromHtml("#8B9298");

            using (GraphicsPath rounded = Rounded(new RectangleF(track.X, track.Y, filled, track.Height), 2.5f))
            using (var brush = new LinearGradientBrush(
                       new PointF(track.X - 1f, 0f), new PointF(track.Right + 1f, 0f), dark, bright))
                g.FillPath(brush, rounded);
        }

        /// <summary>
        /// A live metric tile: caption, large value, and a sparkline of recent
        /// samples. Never cached - the whole point is that it changes.
        /// </summary>
        public static string Gauge(string caption, string value, float[] samples, bool knob)
        {
            return Gauge(caption, value, samples, knob, Ink);
        }

        /// <summary>The same tile with the figure in a colour of its own.</summary>
        public static string Gauge(string caption, string value, float[] samples, bool knob, Color valueColor)
        {
            try
            {
                using (var canvas = new Bitmap(Size, Size, PixelFormat.Format32bppArgb))
                {
                    using (Graphics g = Prepare(canvas))
                    {
                        using (var background = new SolidBrush(Color.Black))
                            g.FillRectangle(background, 0, 0, Size, Size);

                        int floor = BottomFor(knob);
                        DrawFitted(g, caption, Band(4, 30), Muted, 20f, 13f);
                        DrawFitted(g, value, Band(30, floor - 34), valueColor, 46f, 20f);
                        if (samples != null && samples.Length > 1) DrawSparkline(g, samples, floor);
                    }
                    return ToDataUrl(canvas);
                }
            }
            catch (Exception ex)
            {
                Log.Write("KeyImage.Gauge failed: " + ex.Message);
                return null;
            }
        }

        /// <summary>One row of the now-playing tile.</summary>
        public sealed class Line
        {
            public string Text;

            /// <summary>
            /// How far left the text is shifted, in pixels, for a row too long to
            /// fit. Zero or negative; ignored when the text fits.
            /// </summary>
            public float Offset;
        }

        /// <summary>
        /// How many pixels a row overflows its band by, or zero if it fits. What
        /// the caller's marquee swings between.
        /// </summary>
        public static float Overflow(string text)
        {
            if (string.IsNullOrEmpty(text)) return 0f;

            using (Font font = LineFont())
            using (StringFormat format = LineFormat())
                return Math.Max(0f, Ruler.MeasureString(text, font, int.MaxValue, format).Width - LineWidth);
        }

        /// <summary>
        /// The transport tile: the rows the caller wants above and below, and a
        /// play or pause glyph filling whatever is left between them. Pass only
        /// rows that have text - an empty one still costs a 30px band and shrinks
        /// the glyph for nothing.
        ///
        /// Never cached - the marquee offsets change on almost every frame.
        /// </summary>
        public static string NowPlaying(Line[] top, Line[] bottom, bool playing, bool live)
        {
            int above = top == null ? 0 : top.Length;
            int below = bottom == null ? 0 : bottom.Length;

            try
            {
                using (var canvas = new Bitmap(Size, Size, PixelFormat.Format32bppArgb))
                {
                    using (Graphics g = Prepare(canvas))
                    {
                        using (var background = new SolidBrush(Color.Black))
                            g.FillRectangle(background, 0, 0, Size, Size);

                        float glyphTop = TileTop + above * RowHeight + IconGap;
                        float glyphBottom = TileFloor - below * RowHeight - IconGap;
                        float side = Math.Min(glyphBottom - glyphTop, Size - IconMargin * 2);

                        if (side > 0f)
                            DrawTransport(g, new RectangleF((Size - side) / 2f,
                                                            glyphTop + (glyphBottom - glyphTop - side) / 2f,
                                                            side, side),
                                          playing, live);

                        for (int i = 0; i < above; i++)
                            DrawLine(g, top[i], TileTop + i * RowHeight);

                        for (int i = 0; i < below; i++)
                            DrawLine(g, bottom[i], TileFloor - (below - i) * RowHeight);
                    }
                    return ToDataUrl(canvas);
                }
            }
            catch (Exception ex)
            {
                Log.Write("KeyImage.NowPlaying failed: " + ex.Message);
                return null;
            }
        }

        #region drawing

        // Measuring needs a Graphics, and building one per row would mean a bitmap
        // per row. Everything here runs on the pump thread, so one shared ruler is
        // enough - but it has to render text the same way the canvas does, or the
        // widths it reports would not be the widths that get drawn.
        private static readonly Graphics Ruler = MakeRuler();

        private static Graphics MakeRuler()
        {
            Graphics g = Graphics.FromImage(new Bitmap(1, 1, PixelFormat.Format32bppArgb));
            g.TextRenderingHint = TextRenderingHint.AntiAlias;
            return g;
        }

        private static Font LineFont()
        {
            return new Font("Segoe UI", LineFontSize, FontStyle.Bold, GraphicsUnit.Pixel);
        }

        // Typographic rather than the default: the default format pads both ends of
        // the string, which would put the measured width and the drawn width a few
        // pixels apart and leave a row scrolling that actually fits.
        private static StringFormat LineFormat()
        {
            return new StringFormat(StringFormat.GenericTypographic)
            {
                FormatFlags = StringFormatFlags.NoWrap
            };
        }

        // A row that fits is centred and still. One that does not is clipped to the
        // band and drawn at whatever offset the caller's marquee has reached; the
        // font size never changes, which is the whole point of scrolling it.
        private static void DrawLine(Graphics g, Line line, float top)
        {
            if (line == null || string.IsNullOrEmpty(line.Text)) return;

            using (Font font = LineFont())
            using (StringFormat format = LineFormat())
            using (var brush = new SolidBrush(Ink))
            {
                float width = g.MeasureString(line.Text, font, int.MaxValue, format).Width;
                float x = width <= LineWidth
                    ? SidePadding + (LineWidth - width) / 2f
                    : SidePadding + line.Offset;
                float y = top + (RowHeight - font.GetHeight(g)) / 2f;

                GraphicsState saved = g.Save();
                g.SetClip(new RectangleF(SidePadding, top, LineWidth, RowHeight));
                g.DrawString(line.Text, font, brush, x, y, format);
                g.Restore(saved);
            }
        }

        // What the key would do if you pressed it, not what the player is doing:
        // pause while it plays, play while it is stopped. Traced rather than loaded
        // from a PNG, because it has to look the same at 68px and at 128.
        private static void DrawTransport(Graphics g, RectangleF box, bool playing, bool live)
        {
            using (Brush brush = live
                       ? (Brush)new LinearGradientBrush(new PointF(box.Left - 1f, 0f),
                                                        new PointF(box.Right + 1f, 0f),
                                                        ColorTranslator.FromHtml("#8B9298"),
                                                        ColorTranslator.FromHtml("#F6F9FB"))
                       : new SolidBrush(Muted))
            {
                if (playing) DrawPauseBars(g, box, brush);
                else DrawPlayTriangle(g, box, brush);
            }
        }

        private static void DrawPauseBars(Graphics g, RectangleF box, Brush brush)
        {
            float bar = box.Width * 0.26f;
            float gap = box.Width * 0.16f;
            float height = box.Height * 0.64f;
            float x = box.X + (box.Width - (bar * 2f + gap)) / 2f;
            float y = box.Y + (box.Height - height) / 2f;

            using (GraphicsPath left = Rounded(new RectangleF(x, y, bar, height), bar * 0.35f))
                g.FillPath(brush, left);
            using (GraphicsPath right = Rounded(new RectangleF(x + bar + gap, y, bar, height), bar * 0.35f))
                g.FillPath(brush, right);
        }

        private static void DrawPlayTriangle(Graphics g, RectangleF box, Brush brush)
        {
            float margin = box.Width * 0.24f;
            var inner = new RectangleF(box.X + margin, box.Y + margin,
                                       box.Width - margin * 2f, box.Height - margin * 2f);

            // A triangle drawn straight is optically off-centre inside a square -
            // its mass sits left of the middle - so it is nudged right by a little
            // of its own width.
            float nudge = inner.Width * 0.10f;
            var corners = new[]
            {
                new PointF(inner.Left + nudge, inner.Top),
                new PointF(inner.Right + nudge, inner.Top + inner.Height / 2f),
                new PointF(inner.Left + nudge, inner.Bottom)
            };

            using (var path = new GraphicsPath())
            {
                path.AddPolygon(corners);

                // Stroking with a round-joined pen before filling rounds the points
                // off and grows the glyph back out to the size the margin took away.
                using (var pen = new Pen(brush, box.Width * 0.16f)
                       { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round })
                    g.DrawPath(pen, path);

                g.FillPath(brush, path);
            }
        }

        private static Bitmap LoadBase(string baseName)
        {
            string path = Path.Combine(
                Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? ".", "images"),
                baseName + ".png");
            if (!File.Exists(path))
            {
                Log.Write("KeyImage: missing base image " + path);
                return null;
            }

            // Copied rather than used directly so the file handle is released and
            // the bitmap is a known pixel format.
            using (var source = new Bitmap(path))
            {
                var canvas = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb);
                using (Graphics g = Graphics.FromImage(canvas))
                    g.DrawImage(source, 0, 0, source.Width, source.Height);
                return canvas;
            }
        }

        private static Graphics Prepare(Bitmap canvas)
        {
            Graphics g = Graphics.FromImage(canvas);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.TextRenderingHint = TextRenderingHint.AntiAlias;
            return g;
        }

        // Largest centred rectangle inside the target that keeps the aspect ratio.
        private static RectangleF Fit(int width, int height, RectangleF target)
        {
            if (width <= 0 || height <= 0) return target;

            float scale = Math.Min(target.Width / width, target.Height / height);
            float w = width * scale, h = height * scale;
            return new RectangleF(target.X + (target.Width - w) / 2f,
                                  target.Y + (target.Height - h) / 2f, w, h);
        }

        private static RectangleF Band(int top, int bottom)
        {
            return new RectangleF(SidePadding, top, Size - SidePadding * 2, bottom - top);
        }

        // Shrinks to fit on one line, and only wraps to two when even the
        // smallest single-line size would overflow.
        private static void DrawFitted(Graphics g, string text, RectangleF band, Color color,
                                       float maxSize, float minSize)
        {
            string[] lines = { text };
            Font font = FitLines(g, lines, band, maxSize, minSize);

            if (font == null)
            {
                lines = Wrap(text);
                font = FitLines(g, lines, band, maxSize * 0.75f, minSize * 0.7f)
                       ?? new Font("Segoe UI", minSize * 0.7f, FontStyle.Bold, GraphicsUnit.Pixel);
            }

            using (font)
            using (var brush = new SolidBrush(color))
            using (var format = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center,
                FormatFlags = StringFormatFlags.NoWrap,
                Trimming = StringTrimming.EllipsisCharacter
            })
            {
                g.DrawString(string.Join("\n", lines), font, brush, band, format);
            }
        }

        // One line, shrunk until it fits, pinned to one end of the band rather
        // than centred in it: a column of labels only reads as a column when they
        // all start at the same x.
        private static void DrawAligned(Graphics g, string text, RectangleF band, Color color,
                                        float maxSize, float minSize, StringAlignment align)
        {
            if (string.IsNullOrEmpty(text)) return;

            Font font = FitLines(g, new[] { text }, band, maxSize, minSize)
                        ?? new Font("Segoe UI", minSize, FontStyle.Bold, GraphicsUnit.Pixel);

            using (font)
            using (var brush = new SolidBrush(color))
            using (var format = new StringFormat
            {
                Alignment = align,
                LineAlignment = StringAlignment.Center,
                FormatFlags = StringFormatFlags.NoWrap,
                Trimming = StringTrimming.EllipsisCharacter
            })
            {
                g.DrawString(text, font, brush, band, format);
            }
        }

        private static Color Darken(Color color, float by)
        {
            return Color.FromArgb(color.A, (int)(color.R * by), (int)(color.G * by), (int)(color.B * by));
        }

        private static GraphicsPath Rounded(RectangleF rect, float radius)
        {
            float d = Math.Min(radius * 2f, Math.Min(rect.Width, rect.Height));
            var path = new GraphicsPath();

            path.AddArc(rect.X, rect.Y, d, d, 180, 90);
            path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
            path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
            path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        private static Font FitLines(Graphics g, string[] lines, RectangleF band, float maxSize, float minSize)
        {
            for (float size = maxSize; size >= minSize; size -= 0.5f)
            {
                var font = new Font("Segoe UI", size, FontStyle.Bold, GraphicsUnit.Pixel);
                float widest = 0f;
                foreach (string line in lines)
                    widest = Math.Max(widest, g.MeasureString(line, font).Width);

                if (widest <= band.Width && font.GetHeight(g) * lines.Length <= band.Height) return font;
                font.Dispose();
            }
            return null;
        }

        // Splits at the space closest to the middle so both lines carry weight.
        private static string[] Wrap(string label)
        {
            string[] words = label.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length < 2) return new[] { label };

            int best = 1;
            int half = label.Length / 2;
            int bestDistance = int.MaxValue;
            int consumed = 0;

            for (int i = 0; i < words.Length - 1; i++)
            {
                consumed += words[i].Length + 1;
                int distance = Math.Abs(consumed - half);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = i + 1;
                }
            }

            return new[]
            {
                string.Join(" ", words, 0, best),
                string.Join(" ", words, best, words.Length - best)
            };
        }

        private static void DrawSparkline(Graphics g, float[] samples, int floor)
        {
            float top = floor - 30f, bottom = floor - 4f;
            const float left = SidePadding, right = Size - SidePadding;
            float step = (right - left) / (samples.Length - 1);

            var points = new PointF[samples.Length];
            for (int i = 0; i < samples.Length; i++)
            {
                float value = Math.Min(1f, Math.Max(0f, samples[i]));
                points[i] = new PointF(left + step * i, bottom - value * (bottom - top));
            }

            // Filled area under the curve, then the curve itself on top.
            var area = new PointF[samples.Length + 2];
            Array.Copy(points, area, samples.Length);
            area[samples.Length] = new PointF(right, bottom);
            area[samples.Length + 1] = new PointF(left, bottom);

            using (var fill = new LinearGradientBrush(new PointF(0, top - 1), new PointF(0, bottom + 1),
                                                      ColorTranslator.FromHtml("#8B9298"),
                                                      ColorTranslator.FromHtml("#2A2E33")))
                g.FillPolygon(fill, area);

            using (var pen = new Pen(ColorTranslator.FromHtml("#DCE2E7"), 3f) { LineJoin = LineJoin.Round })
                g.DrawLines(pen, points);
        }

        private static string ToDataUrl(Bitmap bitmap)
        {
            using (var buffer = new MemoryStream())
            {
                bitmap.Save(buffer, ImageFormat.Png);
                return "data:image/png;base64," + Convert.ToBase64String(buffer.ToArray());
            }
        }

        #endregion
    }
}
