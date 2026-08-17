// Draws every image this project ships, so the repository carries no binary
// assets beyond the source art in this folder. Two outputs:
//
//   1. the plugin's key artwork, laid out with a band kept clear at the top for
//      the caption and another at the bottom for the value
//   2. an .sdIconPack for the software's icon library, where the same glyphs are
//      centred and full-tile so they can be dropped on any key by hand
//
// The look follows the Blade pack that ships with the software: black tile,
// chunky filled silhouette, one brushed-chrome gradient down the whole glyph.
// Shapes that would be hopeless to draw by hand are taken from source art in
// this folder and restyled: an SVG outline is filled with the same gradient, and
// a raster image is reduced to its silhouette and filled the same way.
//
//   GenIcons.exe <plugin images dir> [<icon pack dir> [<assets dir>]]

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

internal static class GenIcons
{
    private const float Design = 144f;

    // Artwork is centred on the tile. The knob screens are the ones that sit at an
    // angle and need their bottom strip left clear, and that is handled at
    // composition time in src/KeyImage.cs, which knows whether an action instance
    // landed on a knob or on a key - baking it into the artwork would raise it on
    // keys too, where nothing is hidden.
    private const float GlyphAxis = 84f;
    private const float ValueGlyphTop = 48f;
    private const float ValueGlyphBottom = 96f;

    // Every icon in the pack is scaled to this box, centred within the safe area.
    private const float PackBox = 106f;

    private static string _assets;

    // Where a caption goes when the key carries one. Icons flagged TitleRoom are
    // fitted below this instead of being centred on the tile, so a hostname or
    // label printed above them never lands on the artwork.
    private const float TitleBandBottom = 40f;
    // Leaves a margin at the bottom: fitted flush to the edge, a title-room icon
    // looks like it is falling off the key.
    private const float TitleRoomBottom = Design - 14f;

    // Title-room icons may run wider than the standard box: they have less height
    // to work with, so the width is what is left to grow into.
    private const float TitleRoomWidth = 118f;

    private sealed class Glyph
    {
        public string File;
        public string Name;
        public string Tag;
        public RectangleF Bounds;
        public bool TitleRoom;
        public Action<Graphics> Draw;
    }

    private static int Main(string[] args)
    {
        if (args.Length < 1)
        {
            Console.WriteLine("usage: GenIcons.exe <plugin images dir> [<icon pack dir> [<assets dir>]]");
            return 1;
        }

        _assets = args.Length > 2 ? args[2] : AppDomain.CurrentDomain.BaseDirectory;

        WriteKeyImages(args[0]);
        if (args.Length > 1) WritePack(args[1]);
        return 0;
    }

    #region plugin key artwork

    private static void WriteKeyImages(string dir)
    {
        Directory.CreateDirectory(dir);

        // The two audio-output states carry the minus/plus pair, so the tile says
        // both which output is live and that rotating changes the volume.
        Write(Path.Combine(dir, "headphones.png"), 144,
              delegate(Graphics g) { DrawHeadphones(g); DrawVolumeSigns(g); });
        Write(Path.Combine(dir, "speakers.png"), 144,
              delegate(Graphics g) { DrawSpeakers(g); DrawVolumeSigns(g); });

        Write(Path.Combine(dir, "action.png"), 144, DrawDial);
        Write(Path.Combine(dir, "category.png"), 48, DrawDial);

        // Actions whose tiles print a value underneath the glyph.
        Write(Path.Combine(dir, "mixer.png"), 144, DrawMixer);
        Write(Path.Combine(dir, "mixer-muted.png"), 144,
              delegate(Graphics g) { DrawMixer(g); DrawSlash(g); });
        Write(Path.Combine(dir, "sun.png"), 144, DrawSun);
        Write(Path.Combine(dir, "screens.png"), 144, DrawScreens);
        Write(Path.Combine(dir, "screens-off.png"), 144,
              delegate(Graphics g) { DrawScreens(g); DrawSlash(g); });
        Write(Path.Combine(dir, "gauge.png"), 144, DrawGauge);
        Write(Path.Combine(dir, "shortcut.png"), 144, DrawShortcut);
        Write(Path.Combine(dir, "wake.png"), 144, DrawWake);

        Write(Path.Combine(dir, "remotestats.png"), 144, DrawRemoteGauge);
        Write(Path.Combine(dir, "media.png"), 144, DrawMedia);

        // Drawn at full size rather than fitted to a band: a separator carries no
        // caption and no value, so nothing has to be left clear for them.
        Write(Path.Combine(dir, "separator.png"), 144, DrawSeparator);
        Write(Path.Combine(dir, "separator-h.png"), 144, DrawSeparatorFlat);

        // Two sizes of the same mark: one that leaves room for a caption above it,
        // and one for a key showing nothing but the icon. The caption version is
        // fitted to the band the hand-drawn glyphs occupy, so it sits at the size
        // the rest of the set is drawn at rather than looking shrunken beside them.
        var rustdesk = new RectangleF(28, 28, 88, 88);
        WriteFitted(Path.Combine(dir, "rustdesk.png"), rustdesk, CaptionGlyphTop, CaptionGlyphBottom, DrawRustDesk);
        WriteFitted(Path.Combine(dir, "rustdesk-big.png"), rustdesk, FullGlyphTop, FullGlyphBottom, DrawRustDesk);

        // The padlock is drawn tall enough to sit under a caption on its own, so
        // for a key that also prints a value it is scaled back into the strip
        // between the two bands rather than redrawn at another size.
        WriteFitted(Path.Combine(dir, "ssh.png"), new RectangleF(34, 37, 76, 79),
                    ValueGlyphTop, ValueGlyphBottom, DrawSsh);

        Console.WriteLine("key images written to " + dir);
    }

    // A key with a caption but no value underneath: the artwork can run from just
    // below the caption band down to where a key's own bottom margin starts, which
    // is where the hand-drawn glyphs sit.
    private const float CaptionGlyphTop = 40f;
    private const float CaptionGlyphBottom = 104f;

    // Nothing but the icon: the same box the icon pack centres its glyphs in.
    private const float FullGlyphTop = (Design - PackBox) / 2f;
    private const float FullGlyphBottom = FullGlyphTop + PackBox;

    // Key artwork for a glyph whose natural bounds are not the band it has to sit
    // in - scaled to fit, and centred on the tile.
    private static void WriteFitted(string path, RectangleF bounds, float top, float bottom,
                                    Action<Graphics> draw)
    {
        Write(path, 144, delegate(Graphics g)
        {
            float box = bottom - top;
            float scale = Math.Min(box / bounds.Width, box / bounds.Height);

            g.TranslateTransform(Design / 2f, (top + bottom) / 2f);
            g.ScaleTransform(scale, scale);
            g.TranslateTransform(-(bounds.Left + bounds.Width / 2f), -(bounds.Top + bounds.Height / 2f));
            draw(g);
        });
    }

    #endregion

    #region icon pack

    private static void WritePack(string dir)
    {
        string icons = Path.Combine(dir, "icons");
        Directory.CreateDirectory(icons);

        var glyphs = new List<Glyph>
        {
            Def("Headphones.png", "Headphones", "audio", 39, 40, 105, 96, DrawHeadphones),
            Def("Headphones_Muted.png", "Headphones muted", "audio", 34, 34, 110, 102,
                // Thinner than the default slash: a headset is mostly outline, and
                // a fat one swallows an ear cup whole.
                delegate(Graphics g) { DrawHeadphones(g); DrawSlash(g, 36, 100, 108, 36, 11f, 7f); }),
            Def("Speakers.png", "Speakers", "audio", 46, 47, 101, 101, DrawSpeakers),
            Def("App_Volume.png", "App volume", "audio", 44, 48, 104, 96, DrawMixer),
            Def("App_Muted.png", "App muted", "audio", 34, 44, 110, 100,
                delegate(Graphics g) { DrawMixer(g); DrawSlash(g); }),
            Def("Brightness.png", "Brightness", "display", 44, 44, 100, 100, DrawSun),
            Def("Screens.png", "Screens", "display", 40, 48, 108, 96, DrawScreens),
            Def("Screens_Blackout.png", "Blackout screens", "display", 34, 44, 110, 100,
                delegate(Graphics g) { DrawScreens(g); DrawSlash(g); }),
            Def("Share_Screen.png", "Share screen", "display", 34, 30, 110, 96, DrawShareScreen),
            Def("Stop_Sharing.png", "Stop sharing", "display", 30, 26, 114, 100,
                delegate(Graphics g) { DrawShareScreen(g); DrawSlash(g, 32, 98, 112, 30); }),
            Def("Stats.png", "System stats", "system", 38, 58, 110, 96, DrawGauge),
            Def("Shortcut.png", "Shortcut", "function", 40, 46, 104, 98, DrawShortcut),
            Def("Wake_Host.png", "Wake host", "network", 30, 36, 118, 104, DrawWake),
            Def("Knob.png", "Knob", "function", 18, 18, 126, 126, DrawDial),
            // Meant for keys labelled with a hostname, so the glyph stays clear of
            // the caption.
            WithTitleRoom(Def("SSH.png", "SSH", "network", 34, 37, 110, 116, DrawSsh)),
            Def("Terminal.png", "Terminal", "network", 34, 44, 110, 100, DrawTerminal),
            Def("Remote_Desktop.png", "Remote desktop", "apps", 34, 28, 110, 96, DrawRemote),
            Def("Remote_Stats.png", "Remote stats", "network", 34, 38, 110, 106, DrawRemoteGauge),
            Def("Separator.png", "Separator", "function", 64, 14, 80, 130, DrawSeparator),
            Def("Separator_Flat.png", "Separator flat", "function", 14, 64, 130, 80, DrawSeparatorFlat),
            Def("RustDesk.png", "RustDesk", "apps", 30, 30, 114, 114, DrawRustDesk),
            Def("UniversalPlus.png", "Universal+", "media", 22, 34, 126, 110, DrawUniversalPlus)
        };

        var entries = new List<string>();
        foreach (Glyph glyph in glyphs)
        {
            WriteCentered(Path.Combine(icons, glyph.File), glyph);
            entries.Add(Entry(glyph.File, glyph.Name, glyph.Tag));
        }

        // Source art restyled rather than redrawn from memory.
        if (WriteFromSvg(Path.Combine(icons, "Claude.png"), "claude.svg"))
            entries.Add(Entry("Claude.png", "Claude", "apps"));

        // Every outline dropped into tools/brands lands in the pack: brand marks
        // are wordmarks and curves that would be hopeless to reproduce by hand.
        foreach (string svg in Assets("brands", "*.svg"))
        {
            string slug = Path.GetFileNameWithoutExtension(svg);
            string file = BrandFile(slug) + ".png";

            if (WriteFromSvg(Path.Combine(icons, file), Path.Combine("brands", slug + ".svg")))
                entries.Add(Entry(file, BrandName(slug), "media"));
        }

        // Same idea for raster logos: drop a PNG in tools/brands-art and it is
        // traced and restyled. Transparent art is traced by its alpha channel.
        foreach (string art in Assets("brands-art", "*.png"))
        {
            string slug = Path.GetFileNameWithoutExtension(art);
            string file = BrandFile(slug) + ".png";

            if (WriteFromImage(Path.Combine(icons, file), Path.Combine("brands-art", slug + ".png")))
                entries.Add(Entry(file, BrandName(slug), "media"));
        }

        if (WriteFromImage(Path.Combine(icons, "Monogram.png"), "monogram.png"))
            entries.Add(Entry("Monogram.png", "Monogram", "custom"));

        // The pack thumbnail and cover reuse the dial.
        Write(Path.Combine(dir, "icon.png"), 144, DrawDial);
        Write(Path.Combine(dir, "cover.png"), 144, DrawDial);

        File.WriteAllText(Path.Combine(dir, "icons.json"),
            "[\n" + string.Join(",\n", entries.ToArray()) + "\n]\n", new UTF8Encoding(false));

        File.WriteAllText(Path.Combine(dir, "manifest.json"),
            "{\n" +
            "    \"Name\": \"Deck Tools\",\n" +
            "    \"Version\": \"1.1.0\",\n" +
            "    \"Description\": \"Brushed-chrome icons for audio routing, per-app volume, brightness, screen sharing and system monitoring.\",\n" +
            "    \"Author\": \"KnuPwns\",\n" +
            "    \"URL\": \"\",\n" +
            "    \"Icon\": \"icon.png\",\n" +
            "    \"License\": \"license.txt\"\n" +
            "}\n", new UTF8Encoding(false));

        File.WriteAllText(Path.Combine(dir, "license.txt"),
            "Free to use and modify.\n\n" +
            "Claude.png is rendered from the Claude mark's outline (path via Simple\n" +
            "Icons, CC0); the mark is Anthropic's trademark. RustDesk.png echoes the\n" +
            "shape of the RustDesk mark but is drawn here, not their artwork.\n" +
            "Monogram.png is the silhouette of artwork supplied by the pack's author.\n",
            new UTF8Encoding(false));

        Console.WriteLine("icon pack written to " + dir);
    }

    private static Glyph Def(string file, string name, string tag,
                             float left, float top, float right, float bottom, Action<Graphics> draw)
    {
        return new Glyph
        {
            File = file,
            Name = name,
            Tag = tag,
            Bounds = new RectangleF(left, top, right - left, bottom - top),
            Draw = draw
        };
    }

    private static readonly Dictionary<string, string> BrandNames =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "youtube", "YouTube" },
            { "youtubemusic", "YouTube Music" },
            { "primevideo", "Prime Video" },
            { "paramountplus", "Paramount+" },
            { "hbomax", "HBO Max" },
            { "disneyplus", "Disney+" },
            { "universalplus", "Universal+" },
            { "mercadoplay", "MercadoPlay" },
            { "appletv", "Apple TV+" }
        };

    private static IEnumerable<string> Assets(string subfolder, string pattern)
    {
        string folder = Path.Combine(_assets, subfolder);
        if (!Directory.Exists(folder)) return new string[0];

        string[] found = Directory.GetFiles(folder, pattern);
        Array.Sort(found, StringComparer.OrdinalIgnoreCase);
        return found;
    }

    private static string BrandName(string slug)
    {
        string known;
        if (BrandNames.TryGetValue(slug, out known)) return known;

        return slug.Length == 0 ? slug : char.ToUpperInvariant(slug[0]) + slug.Substring(1);
    }

    private static string BrandFile(string slug)
    {
        return BrandName(slug).Replace(" ", "_").Replace("+", "Plus");
    }

    private static Glyph WithTitleRoom(Glyph glyph)
    {
        glyph.TitleRoom = true;
        return glyph;
    }

    private static string Entry(string path, string name, string tag)
    {
        return "    {\n" +
               "        \"path\": \"" + path + "\",\n" +
               "        \"name\": \"" + name + "\",\n" +
               "        \"tags\": [\n            \"" + tag + "\"\n        ]\n" +
               "    }";
    }

    // Same drawing code as the key artwork, remapped so the glyph fills the tile
    // instead of leaving room for text.
    private static void WriteCentered(string path, Glyph glyph)
    {
        Write(path, 144, delegate(Graphics g)
        {
            float scale, centreY;
            if (glyph.TitleRoom)
            {
                // Fit below the caption band rather than on the whole tile.
                float height = TitleRoomBottom - TitleBandBottom;
                scale = Math.Min(TitleRoomWidth / glyph.Bounds.Width, height / glyph.Bounds.Height);
                centreY = (TitleBandBottom + TitleRoomBottom) / 2f;
            }
            else
            {
                scale = PackBox / Math.Max(glyph.Bounds.Width, glyph.Bounds.Height);
                centreY = Design / 2f;
            }

            g.TranslateTransform(Design / 2f, centreY);
            g.ScaleTransform(scale, scale);
            g.TranslateTransform(-(glyph.Bounds.Left + glyph.Bounds.Width / 2f),
                                 -(glyph.Bounds.Top + glyph.Bounds.Height / 2f));
            glyph.Draw(g);
        });
    }

    #endregion

    #region source art

    /// <summary>
    /// Fills an SVG outline with the pack's gradient. Returns false when the
    /// asset is missing, so the pack simply omits that icon.
    /// </summary>
    private sealed class SvgShape
    {
        public string Data;
        public bool Light;   // filled with a pale colour in the source
    }

    private static bool WriteFromSvg(string output, string assetName)
    {
        string asset = Path.Combine(_assets, assetName);
        if (!File.Exists(asset))
        {
            Console.WriteLine("  skipped " + Path.GetFileName(output) + ": " + assetName + " not found");
            return false;
        }

        List<SvgShape> shapes = ExtractShapes(File.ReadAllText(asset));
        if (shapes.Count == 0)
        {
            Console.WriteLine("  skipped " + Path.GetFileName(output) + ": no path data in " + assetName);
            return false;
        }

        // A flat-colour logo carries its own figure and ground: a pale field with
        // the mark punched out of it in a dark colour. Flattening that to one tone
        // turns it into a grey smear, so it is kept as two - the field becomes
        // chrome, the mark becomes the tile's black. Marks that are a single
        // colour on transparency have no ground and are simply filled.
        bool twoTone = shapes.Exists(s => s.Light) && shapes.Exists(s => !s.Light);

        float left = (Design - PackBox) / 2f;
        var target = new RectangleF(left, left, PackBox, PackBox);

        Write(output, 144, delegate(Graphics g)
        {
            using (Brush chrome = Chrome(left, left + PackBox))
            using (var black = new SolidBrush(Color.Black))
            {
                if (!twoTone)
                {
                    List<string> data = WithoutBackdrop(shapes.ConvertAll(s => s.Data));
                    using (GraphicsPath path = SvgPath(data, target))
                        g.FillPath(chrome, path);
                    return;
                }

                // One shared transform, then each shape in document order so the
                // mark lands on top of its field.
                using (GraphicsPath all = BuildPath(shapes.ConvertAll(s => s.Data)))
                using (Matrix matrix = FitTo(all.GetBounds(), target))
                {
                    foreach (SvgShape shape in shapes)
                    {
                        using (GraphicsPath path = BuildPath(new[] { shape.Data }))
                        {
                            path.Transform(matrix);
                            g.FillPath(shape.Light ? chrome : black, path);
                        }
                    }
                }
            }
        });

        Console.WriteLine("  rendered " + Path.GetFileName(output) + " from " + assetName +
                          (twoTone ? "  (two-tone)" : string.Empty));
        return true;
    }

    /// <summary>
    /// Reduces a raster image to its silhouette and fills that with the pack's
    /// gradient, which is what turns supplied art into this style.
    /// </summary>
    private static bool WriteFromImage(string output, string assetName)
    {
        string asset = Path.Combine(_assets, assetName);
        if (!File.Exists(asset))
        {
            Console.WriteLine("  skipped " + Path.GetFileName(output) + ": " + assetName + " not found");
            return false;
        }

        using (var source = new Bitmap(asset))
        using (Bitmap mask = Silhouette(source))
        {
            float left = (Design - PackBox) / 2f;
            RectangleF box = Fit(mask.Width, mask.Height, new RectangleF(left, left, PackBox, PackBox));

            Write(output, 144, delegate(Graphics g)
            {
                using (Bitmap plated = Plate(mask, box))
                    g.DrawImage(plated, 0, 0, Design, Design);
            });
        }

        Console.WriteLine("  traced " + Path.GetFileName(output) + " from " + assetName);
        return true;
    }

    // Separating subject from background needs a different test depending on the
    // art: a bright mark on a light plate is found by how far it is from white,
    // while artwork on a dark background is found by warmth and brightness. A
    // plain luminance cut is what erased the descender of the monogram's letter -
    // it is there, just darker than the rest of the stroke.
    private static Bitmap Silhouette(Bitmap source)
    {
        int w = source.Width, h = source.Height;
        var coverage = new int[w, h];
        var ink = new bool[w, h];

        int opaque = 0;
        long luminanceSum = 0;
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                Color probe = source.GetPixel(x, y);
                if (probe.A < 32) continue;

                opaque++;
                luminanceSum += (probe.R * 299 + probe.G * 587 + probe.B * 114) / 1000;
            }
        }

        // A logo cut out on transparency already carries its own mask; anything
        // else has to be separated from a background.
        bool cutOut = opaque < w * h * 0.88;
        bool lightPlate = !cutOut && opaque > 0 && luminanceSum / opaque > 150;

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                Color c = source.GetPixel(x, y);
                if (c.A < 32) continue;

                int lum = (c.R * 299 + c.G * 587 + c.B * 114) / 1000;
                int strength;

                if (cutOut)
                {
                    strength = c.A;
                }
                else if (lightPlate)
                {
                    // Distance from white: the mark, not the plate it sits on.
                    int darkest = Math.Min((int)c.R, Math.Min((int)c.G, (int)c.B));
                    strength = 255 - darkest;
                }
                else
                {
                    // Warm ink, or anything bright enough to be a highlight.
                    int warmth = c.R - c.B;
                    strength = Math.Max(warmth > 20 ? lum : 0, lum > 140 ? lum : 0);
                }

                coverage[x, y] = strength;
                ink[x, y] = strength > 45;
            }
        }

        bool[,] kept = SignificantBlobs(ink, w, h);

        var mask = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                // Feathered so the downscale does not produce a jagged edge.
                int alpha = 0;
                if (kept[x, y])
                {
                    alpha = (int)((coverage[x, y] - 40) * 255f / 60f);
                    alpha = Math.Min(255, Math.Max(0, alpha));
                }
                mask.SetPixel(x, y, Color.FromArgb(alpha, 255, 255, 255));
            }
        }
        return mask;
    }

    // Keeps every blob within an order of magnitude of the biggest one: that
    // drops a starfield's speckles while preserving a mark made of two arcs, or
    // a letter whose stem is separated by an ornament.
    private static bool[,] SignificantBlobs(bool[,] ink, int w, int h)
    {
        var seen = new bool[w, h];
        var blobs = new List<List<int>>();
        var queue = new Queue<int>();

        for (int sy = 0; sy < h; sy++)
        {
            for (int sx = 0; sx < w; sx++)
            {
                if (!ink[sx, sy] || seen[sx, sy]) continue;

                var member = new List<int>();
                queue.Enqueue(sy * w + sx);
                seen[sx, sy] = true;

                while (queue.Count > 0)
                {
                    int index = queue.Dequeue();
                    int x = index % w, y = index / w;
                    member.Add(index);

                    for (int dy = -1; dy <= 1; dy++)
                    {
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int nx = x + dx, ny = y + dy;
                            if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                            if (!ink[nx, ny] || seen[nx, ny]) continue;

                            seen[nx, ny] = true;
                            queue.Enqueue(ny * w + nx);
                        }
                    }
                }
                blobs.Add(member);
            }
        }

        int largest = 0;
        foreach (List<int> blob in blobs) largest = Math.Max(largest, blob.Count);
        int floor = Math.Max(150, largest / 12);

        var kept = new bool[w, h];
        foreach (List<int> blob in blobs)
        {
            if (blob.Count < floor) continue;
            foreach (int index in blob) kept[index % w, index / w] = true;
        }
        return kept;
    }

    // Paints the gradient through the mask: the mask supplies coverage, the
    // gradient supplies colour.
    private static Bitmap Plate(Bitmap mask, RectangleF box)
    {
        var scaled = new Bitmap((int)Design, (int)Design, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(scaled))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(mask, box.X, box.Y, box.Width, box.Height);
        }

        var chrome = new Bitmap((int)Design, (int)Design, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(chrome))
        using (Brush brush = Chrome(box.Y, box.Bottom))
            g.FillRectangle(brush, 0, 0, Design, Design);

        var result = new Bitmap((int)Design, (int)Design, PixelFormat.Format32bppArgb);
        for (int y = 0; y < result.Height; y++)
        {
            for (int x = 0; x < result.Width; x++)
            {
                int alpha = scaled.GetPixel(x, y).A;
                Color tone = chrome.GetPixel(x, y);
                result.SetPixel(x, y, Color.FromArgb(alpha, tone.R, tone.G, tone.B));
            }
        }

        scaled.Dispose();
        chrome.Dispose();
        return result;
    }

    private static RectangleF Fit(int width, int height, RectangleF target)
    {
        float scale = Math.Min(target.Width / width, target.Height / height);
        float w = width * scale, h = height * scale;
        return new RectangleF(target.X + (target.Width - w) / 2f,
                             target.Y + (target.Height - h) / 2f, w, h);
    }

    // Every path in the file, with how pale its fill is. Brand marks are often
    // several subpaths, and taking only the first silently drops most of the logo.
    private static List<SvgShape> ExtractShapes(string svg)
    {
        var found = new List<SvgShape>();

        foreach (Match element in Regex.Matches(svg, "<path[^>]*>", RegexOptions.Singleline))
        {
            Match data = Regex.Match(element.Value, " d=\"([^\"]*)\"");
            if (!data.Success) continue;

            Match fill = Regex.Match(element.Value, "fill=\"([^\"]*)\"");
            found.Add(new SvgShape
            {
                Data = data.Groups[1].Value,
                Light = IsLight(fill.Success ? fill.Groups[1].Value : null)
            });
        }
        return found;
    }

    // Anything that is not an explicit dark colour counts as light: "none",
    // "currentColor" and a missing fill all mean "this is the mark itself".
    private static bool IsLight(string fill)
    {
        if (string.IsNullOrEmpty(fill)) return true;

        string value = fill.Trim();
        if (!value.StartsWith("#", StringComparison.Ordinal)) return true;

        value = value.Substring(1);
        if (value.Length == 3)
            value = new string(new[] { value[0], value[0], value[1], value[1], value[2], value[2] });
        if (value.Length != 6) return true;

        int red, green, blue;
        if (!int.TryParse(value.Substring(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out red) ||
            !int.TryParse(value.Substring(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out green) ||
            !int.TryParse(value.Substring(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out blue))
            return true;

        return (red * 299 + green * 587 + blue * 114) / 1000 > 128;
    }

    // SVG path reader: move, line, horizontal, vertical, cubic, smooth cubic,
    // elliptical arc and close, absolute and relative. That covers the brand
    // outlines used here; an unknown command is reported rather than silently
    // producing a wrong shape.
    // Full-colour logos start with a coloured plate the rest of the mark sits on.
    // Flattening everything to one gradient merges plate and mark into mush, so a
    // first path that covers nearly the whole drawing is dropped and the mark
    // itself is what gets rendered.
    private static List<string> WithoutBackdrop(List<string> paths)
    {
        if (paths.Count < 3) return paths;

        // Compared in the file's own coordinates: a plate is a first path whose
        // box is essentially the whole drawing's box.
        RectangleF first = RawBounds(new[] { paths[0] });
        RectangleF all = RawBounds(paths);

        float firstArea = first.Width * first.Height;
        float allArea = all.Width * all.Height;
        if (allArea <= 0f || firstArea < allArea * 0.85f) return paths;

        Console.WriteLine("      dropped a background plate path");
        return paths.GetRange(1, paths.Count - 1);
    }

    private static RectangleF RawBounds(IEnumerable<string> paths)
    {
        using (var path = new GraphicsPath { FillMode = FillMode.Winding })
        {
            foreach (string data in paths) AppendPath(path, data);
            return path.GetBounds();
        }
    }

    private static GraphicsPath SvgPath(IEnumerable<string> paths, RectangleF target)
    {
        GraphicsPath path = BuildPath(paths);
        using (Matrix matrix = FitTo(path.GetBounds(), target))
            path.Transform(matrix);
        return path;
    }

    private static GraphicsPath BuildPath(IEnumerable<string> paths)
    {
        // Nonzero winding is the SVG default, and holes in brand marks depend on it.
        var path = new GraphicsPath { FillMode = FillMode.Winding };
        foreach (string data in paths) AppendPath(path, data);
        return path;
    }

    // Maps an outline's own bounds onto the target box, keeping the aspect ratio.
    private static Matrix FitTo(RectangleF bounds, RectangleF target)
    {
        var matrix = new Matrix();
        if (bounds.Width <= 0 || bounds.Height <= 0) return matrix;

        float scale = Math.Min(target.Width / bounds.Width, target.Height / bounds.Height);
        matrix.Translate(-(bounds.X + bounds.Width / 2f), -(bounds.Y + bounds.Height / 2f),
                         MatrixOrder.Append);
        matrix.Scale(scale, scale, MatrixOrder.Append);
        matrix.Translate(target.X + target.Width / 2f, target.Y + target.Height / 2f,
                         MatrixOrder.Append);
        return matrix;
    }

    private static void AppendPath(GraphicsPath path, string data)
    {
        PointF current = PointF.Empty, figureStart = PointF.Empty;

        // Needed by the smooth-curve commands, which mirror them.
        PointF lastCubicControl = PointF.Empty, lastQuadControl = PointF.Empty;
        bool lastWasCubic = false, lastWasQuadratic = false;

        char command = '\0';
        int i = 0;

        while (true)
        {
            SkipSeparators(data, ref i);
            if (i >= data.Length) break;

            if (char.IsLetter(data[i]))
            {
                command = data[i++];
                if (command == 'Z' || command == 'z')
                {
                    path.CloseFigure();
                    current = figureStart;
                    lastWasCubic = false;
                    lastWasQuadratic = false;
                    continue;
                }
            }

            char kind = char.ToUpperInvariant(command);
            bool cubic = kind == 'C' || kind == 'S';
            bool quadratic = kind == 'Q' || kind == 'T';

            bool relative = char.IsLower(command);
            switch (kind)
            {
                case 'M':
                {
                    PointF point = ReadPoint(data, ref i, current, relative);
                    path.StartFigure();
                    current = figureStart = point;

                    // Further pairs after a moveto are an implicit lineto.
                    command = relative ? 'l' : 'L';
                    break;
                }
                case 'L':
                {
                    PointF point = ReadPoint(data, ref i, current, relative);
                    path.AddLine(current, point);
                    current = point;
                    break;
                }
                case 'H':
                {
                    float x = ReadNumber(data, ref i);
                    var point = new PointF(relative ? current.X + x : x, current.Y);
                    path.AddLine(current, point);
                    current = point;
                    break;
                }
                case 'V':
                {
                    float y = ReadNumber(data, ref i);
                    var point = new PointF(current.X, relative ? current.Y + y : y);
                    path.AddLine(current, point);
                    current = point;
                    break;
                }
                case 'C':
                {
                    PointF c1 = ReadPoint(data, ref i, current, relative);
                    PointF c2 = ReadPoint(data, ref i, current, relative);
                    PointF end = ReadPoint(data, ref i, current, relative);
                    path.AddBezier(current, c1, c2, end);
                    lastCubicControl = c2;
                    current = end;
                    break;
                }
                case 'S':
                {
                    // First control point mirrors the previous curve's second one.
                    PointF c1 = lastWasCubic ? Mirror(current, lastCubicControl) : current;
                    PointF c2 = ReadPoint(data, ref i, current, relative);
                    PointF end = ReadPoint(data, ref i, current, relative);
                    path.AddBezier(current, c1, c2, end);
                    lastCubicControl = c2;
                    current = end;
                    break;
                }
                case 'Q':
                {
                    PointF control = ReadPoint(data, ref i, current, relative);
                    PointF end = ReadPoint(data, ref i, current, relative);
                    AddQuadratic(path, current, control, end);
                    lastQuadControl = control;
                    current = end;
                    break;
                }
                case 'T':
                {
                    PointF control = lastWasQuadratic ? Mirror(current, lastQuadControl) : current;
                    PointF end = ReadPoint(data, ref i, current, relative);
                    AddQuadratic(path, current, control, end);
                    lastQuadControl = control;
                    current = end;
                    break;
                }
                case 'A':
                {
                    float rx = ReadNumber(data, ref i);
                    float ry = ReadNumber(data, ref i);
                    float rotation = ReadNumber(data, ref i);

                    // The flags are single characters and may run straight into
                    // the next number: "a5 5 0 015 5" is 0, 1, 5, 5.
                    bool largeArc = ReadFlag(data, ref i);
                    bool sweep = ReadFlag(data, ref i);

                    PointF end = ReadPoint(data, ref i, current, relative);
                    AddArc(path, current, end, rx, ry, rotation, largeArc, sweep);
                    current = end;
                    break;
                }
                default:
                    throw new NotSupportedException("SVG path command '" + command + "' is not handled");
            }

            lastWasCubic = cubic;
            lastWasQuadratic = quadratic;
        }

        path.CloseFigure();
    }

    private static PointF Mirror(PointF about, PointF point)
    {
        return new PointF(2f * about.X - point.X, 2f * about.Y - point.Y);
    }

    // GDI+ has no quadratic primitive; the equivalent cubic controls sit two
    // thirds of the way from each end towards the quadratic's control point.
    private static void AddQuadratic(GraphicsPath path, PointF from, PointF control, PointF to)
    {
        var c1 = new PointF(from.X + 2f / 3f * (control.X - from.X),
                            from.Y + 2f / 3f * (control.Y - from.Y));
        var c2 = new PointF(to.X + 2f / 3f * (control.X - to.X),
                            to.Y + 2f / 3f * (control.Y - to.Y));
        path.AddBezier(from, c1, c2, to);
    }

    // Endpoint form to centre form (SVG spec F.6.5), then approximated with cubic
    // segments of at most a quarter turn each - GDI+ has no elliptical-arc-with-
    // rotation primitive.
    private static void AddArc(GraphicsPath path, PointF from, PointF to,
                               float rx, float ry, float rotationDegrees, bool largeArc, bool sweep)
    {
        if (rx == 0f || ry == 0f || (from.X == to.X && from.Y == to.Y))
        {
            if (from != to) path.AddLine(from, to);
            return;
        }

        rx = Math.Abs(rx);
        ry = Math.Abs(ry);

        double phi = rotationDegrees * Math.PI / 180d;
        double cosPhi = Math.Cos(phi), sinPhi = Math.Sin(phi);

        double dx = (from.X - to.X) / 2d, dy = (from.Y - to.Y) / 2d;
        double x1 = cosPhi * dx + sinPhi * dy;
        double y1 = -sinPhi * dx + cosPhi * dy;

        // Grow the radii if they are too small to span the two points.
        double lambda = x1 * x1 / (rx * rx) + y1 * y1 / (ry * ry);
        if (lambda > 1d)
        {
            double growth = Math.Sqrt(lambda);
            rx = (float)(rx * growth);
            ry = (float)(ry * growth);
        }

        double numerator = rx * rx * ry * ry - rx * rx * y1 * y1 - ry * ry * x1 * x1;
        double denominator = rx * rx * y1 * y1 + ry * ry * x1 * x1;
        double factor = Math.Sqrt(Math.Max(0d, numerator / denominator));
        if (largeArc == sweep) factor = -factor;

        double cxp = factor * rx * y1 / ry;
        double cyp = -factor * ry * x1 / rx;
        double cx = cosPhi * cxp - sinPhi * cyp + (from.X + to.X) / 2d;
        double cy = sinPhi * cxp + cosPhi * cyp + (from.Y + to.Y) / 2d;

        double start = Math.Atan2((y1 - cyp) / ry, (x1 - cxp) / rx);
        double end = Math.Atan2((-y1 - cyp) / ry, (-x1 - cxp) / rx);
        double sweepAngle = end - start;

        if (!sweep && sweepAngle > 0d) sweepAngle -= 2d * Math.PI;
        else if (sweep && sweepAngle < 0d) sweepAngle += 2d * Math.PI;

        int segments = Math.Max(1, (int)Math.Ceiling(Math.Abs(sweepAngle) / (Math.PI / 2d)));
        double step = sweepAngle / segments;
        double alpha = Math.Sin(step) * (Math.Sqrt(4d + 3d * Math.Pow(Math.Tan(step / 2d), 2d)) - 1d) / 3d;

        PointF previous = from;
        for (int segment = 0; segment < segments; segment++)
        {
            double t1 = start + segment * step, t2 = t1 + step;

            PointF p2 = ArcPoint(cx, cy, rx, ry, cosPhi, sinPhi, t2);
            PointF d1 = ArcSlope(rx, ry, cosPhi, sinPhi, t1);
            PointF d2 = ArcSlope(rx, ry, cosPhi, sinPhi, t2);

            var c1 = new PointF((float)(previous.X + alpha * d1.X), (float)(previous.Y + alpha * d1.Y));
            var c2 = new PointF((float)(p2.X - alpha * d2.X), (float)(p2.Y - alpha * d2.Y));

            path.AddBezier(previous, c1, c2, p2);
            previous = p2;
        }
    }

    private static PointF ArcPoint(double cx, double cy, float rx, float ry,
                                   double cosPhi, double sinPhi, double t)
    {
        double x = cx + rx * Math.Cos(t) * cosPhi - ry * Math.Sin(t) * sinPhi;
        double y = cy + rx * Math.Cos(t) * sinPhi + ry * Math.Sin(t) * cosPhi;
        return new PointF((float)x, (float)y);
    }

    private static PointF ArcSlope(float rx, float ry, double cosPhi, double sinPhi, double t)
    {
        double x = -rx * Math.Sin(t) * cosPhi - ry * Math.Cos(t) * sinPhi;
        double y = -rx * Math.Sin(t) * sinPhi + ry * Math.Cos(t) * cosPhi;
        return new PointF((float)x, (float)y);
    }

    private static bool ReadFlag(string data, ref int i)
    {
        SkipSeparators(data, ref i);
        if (i >= data.Length) return false;

        char c = data[i];
        if (c == '0' || c == '1') { i++; return c == '1'; }

        // Malformed, but a number is still a truthy flag in practice.
        return ReadNumber(data, ref i) != 0f;
    }

    private static PointF ReadPoint(string data, ref int i, PointF current, bool relative)
    {
        float x = ReadNumber(data, ref i);
        float y = ReadNumber(data, ref i);
        return relative ? new PointF(current.X + x, current.Y + y) : new PointF(x, y);
    }

    private static float ReadNumber(string data, ref int i)
    {
        SkipSeparators(data, ref i);

        int start = i;
        if (i < data.Length && (data[i] == '-' || data[i] == '+')) i++;

        bool dot = false;
        while (i < data.Length)
        {
            char c = data[i];
            if (char.IsDigit(c)) i++;
            else if (c == '.' && !dot) { dot = true; i++; }
            else if ((c == 'e' || c == 'E') && i + 1 < data.Length) { i += 2; }
            else break;
        }

        return float.Parse(data.Substring(start, i - start), CultureInfo.InvariantCulture);
    }

    private static void SkipSeparators(string data, ref int i)
    {
        while (i < data.Length && (data[i] == ' ' || data[i] == ',' ||
                                   data[i] == '\r' || data[i] == '\n' || data[i] == '\t')) i++;
    }

    #endregion

    #region glyphs

    private static void DrawHeadphones(Graphics g)
    {
        using (Brush chrome = Chrome(50f, 106f))
        {
            using (GraphicsPath band = HalfRing(new RectangleF(42, 50, 60, 60), 13f))
                g.FillPath(chrome, band);

            using (GraphicsPath left = Rounded(new RectangleF(39, 74, 20, 32), 8f))
            using (GraphicsPath right = Rounded(new RectangleF(85, 74, 20, 32), 8f))
            {
                g.FillPath(chrome, left);
                g.FillPath(chrome, right);
            }
        }
    }

    // Cone and waves are symmetric around GlyphAxis: the headphones look centred
    // while hanging lower because the ear cups weigh the bottom down, but a
    // symmetric shape has to be centred outright.
    private static void DrawSpeakers(Graphics g)
    {
        using (Brush chrome = Chrome(GlyphAxis - 27f, GlyphAxis + 27f))
        {
            PointF[] cone =
            {
                new PointF(46, GlyphAxis - 12), new PointF(61, GlyphAxis - 12),
                new PointF(83, GlyphAxis - 27), new PointF(83, GlyphAxis + 27),
                new PointF(61, GlyphAxis + 12), new PointF(46, GlyphAxis + 12)
            };
            g.FillPolygon(chrome, cone);

            using (var pen = new Pen(chrome, 6.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                g.DrawArc(pen, new RectangleF(75, GlyphAxis - 13, 17, 26), -55, 110);
                g.DrawArc(pen, new RectangleF(80, GlyphAxis - 24, 21, 48), -55, 110);
            }
        }
    }

    // Minus left, plus right, on the axis the glyph is balanced on.
    private static void DrawVolumeSigns(Graphics g)
    {
        using (Brush chrome = Chrome(50f, 106f))
        {
            using (GraphicsPath minus = Rounded(new RectangleF(6, GlyphAxis - 6, 28, 12), 6f))
                g.FillPath(chrome, minus);

            // Two overlapping bars: the shared vertical gradient hides the seam.
            using (GraphicsPath across = Rounded(new RectangleF(110, GlyphAxis - 6, 28, 12), 6f))
            using (GraphicsPath down = Rounded(new RectangleF(118, GlyphAxis - 14, 12, 28), 6f))
            {
                g.FillPath(chrome, across);
                g.FillPath(chrome, down);
            }
        }
    }

    // Three levels, the way every mixer has drawn them forever.
    private static void DrawMixer(Graphics g)
    {
        using (Brush chrome = Chrome(ValueGlyphTop, ValueGlyphBottom))
        {
            float[] heights = { 26f, 48f, 34f };
            float x = 44f;
            foreach (float height in heights)
            {
                using (GraphicsPath bar = Rounded(
                    new RectangleF(x, ValueGlyphBottom - height, 16f, height), 6f))
                    g.FillPath(chrome, bar);
                x += 22f;
            }
        }
    }

    private static void DrawSun(Graphics g)
    {
        float cx = 72f, cy = (ValueGlyphTop + ValueGlyphBottom) / 2f;
        using (Brush chrome = Chrome(ValueGlyphTop, ValueGlyphBottom))
        {
            g.FillEllipse(chrome, cx - 16f, cy - 16f, 32f, 32f);

            using (var pen = new Pen(chrome, 7.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                for (int i = 0; i < 8; i++)
                {
                    double angle = i * Math.PI / 4d;
                    float dx = (float)Math.Cos(angle), dy = (float)Math.Sin(angle);
                    g.DrawLine(pen, cx + dx * 22f, cy + dy * 22f, cx + dx * 28f, cy + dy * 28f);
                }
            }
        }
    }

    private static void DrawScreens(Graphics g)
    {
        using (Brush chrome = Chrome(ValueGlyphTop, ValueGlyphBottom))
        using (var pen = new Pen(chrome, 6f) { LineJoin = LineJoin.Round })
        {
            using (GraphicsPath back = Rounded(new RectangleF(40, ValueGlyphTop, 46, 32), 5f))
            using (GraphicsPath front = Rounded(new RectangleF(62, ValueGlyphBottom - 32, 46, 32), 5f))
            {
                g.DrawPath(pen, back);

                // Punched out so the front screen reads as being in front.
                using (var cut = new Pen(Color.Black, 13f) { LineJoin = LineJoin.Round })
                    g.DrawPath(cut, front);
                g.DrawPath(pen, front);
            }
        }
    }

    // Reads as a small chart: what the live metric tiles draw at runtime.
    private static void DrawGauge(Graphics g)
    {
        using (Brush chrome = Chrome(ValueGlyphTop, ValueGlyphBottom))
        {
            float[] heights = { 14f, 26f, 20f, 38f, 30f };
            float x = 38f;
            foreach (float height in heights)
            {
                using (GraphicsPath bar = Rounded(
                    new RectangleF(x, ValueGlyphBottom - height, 12f, height), 4f))
                    g.FillPath(chrome, bar);
                x += 15f;
            }
        }
    }

    // Only the artwork the host shows in its action list. The key itself traces
    // its own play or pause glyph at runtime, at whatever size the text rows
    // leave it, so a fixed image would be no use there.
    private static void DrawMedia(Graphics g)
    {
        using (Brush chrome = Chrome(CaptionGlyphTop, CaptionGlyphBottom))
        {
            float middle = (CaptionGlyphTop + CaptionGlyphBottom) / 2f;

            // A pause bar and a play triangle side by side: the two states the key
            // moves between, in one mark.
            using (GraphicsPath left = Rounded(new RectangleF(38f, middle - 24f, 13f, 48f), 5f))
                g.FillPath(chrome, left);
            using (GraphicsPath right = Rounded(new RectangleF(57f, middle - 24f, 13f, 48f), 5f))
                g.FillPath(chrome, right);

            var play = new[]
            {
                new PointF(84f, middle - 22f),
                new PointF(112f, middle),
                new PointF(84f, middle + 22f)
            };
            using (var path = new GraphicsPath())
            {
                path.AddPolygon(play);
                using (var pen = new Pen(chrome, 9f) { LineJoin = LineJoin.Round })
                    g.DrawPath(pen, path);
                g.FillPath(chrome, path);
            }
        }
    }

    // A display with an arrow leaving it: the usual shorthand for sharing a
    // screen. The stop variant is the same glyph struck through.
    private static void DrawShareScreen(Graphics g)
    {
        using (Brush chrome = Chrome(40f, 106f))
        using (var pen = new Pen(chrome, 7f) { LineJoin = LineJoin.Round })
        {
            using (GraphicsPath frame = Rounded(new RectangleF(34, 40, 76, 52), 7f))
                g.DrawPath(pen, frame);

            using (GraphicsPath stand = Rounded(new RectangleF(58, 98, 28, 8), 4f))
                g.FillPath(chrome, stand);

            using (GraphicsPath shaft = Rounded(new RectangleF(66, 62, 12, 22), 5f))
                g.FillPath(chrome, shaft);

            g.FillPolygon(chrome, new[]
            {
                new PointF(72, 46), new PointF(88, 64), new PointF(56, 64)
            });
        }
    }

    // A power symbol with signal arcs beside it: waking a machine from across the
    // network.
    private static void DrawWake(Graphics g)
    {
        const float cx = 66f, cy = 72f, r = 26f;

        using (Brush chrome = Chrome(40f, 104f))
        {
            using (GraphicsPath ring = Ring(new RectangleF(cx - r, cy - r, r * 2f, r * 2f), 9f, 290, 320))
                g.FillPath(chrome, ring);

            using (GraphicsPath stem = Rounded(new RectangleF(cx - 4.5f, cy - r - 10f, 9f, 24f), 4.5f))
                g.FillPath(chrome, stem);

            using (var signal = new Pen(chrome, 7f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                g.DrawArc(signal, new RectangleF(cx + 12f, cy - 16f, 20f, 32f), -60, 120);
                g.DrawArc(signal, new RectangleF(cx + 18f, cy - 26f, 30f, 52f), -60, 120);
            }
        }
    }

    // A frame with an arrow leaving it: a shortcut. Used when a launcher item has
    // no icon of its own to show.
    private static void DrawShortcut(Graphics g)
    {
        using (Brush chrome = Chrome(46f, 98f))
        using (var pen = new Pen(chrome, 7f) { LineJoin = LineJoin.Round })
        {
            // Open on the top right, where the arrow leaves.
            using (var frame = new GraphicsPath())
            {
                frame.AddLine(88, 46, 46, 46);
                frame.AddLine(46, 46, 40, 52);
                frame.AddLine(40, 52, 40, 92);
                frame.AddLine(40, 92, 46, 98);
                frame.AddLine(46, 98, 98, 98);
                frame.AddLine(98, 98, 104, 92);
                frame.AddLine(104, 92, 104, 74);
                g.DrawPath(pen, frame);
            }

            using (var shaft = new Pen(chrome, 9f) { StartCap = LineCap.Round })
                g.DrawLine(shaft, 62, 82, 92, 54);

            g.FillPolygon(chrome, new[]
            {
                new PointF(104, 44), new PointF(102, 68), new PointF(80, 46)
            });
        }
    }

    // A shell prompt in a window. On its own this is a terminal; with the padlock
    // added it is a secure one.
    private static void DrawTerminal(Graphics g)
    {
        using (Brush chrome = Chrome(44f, 100f))
        using (var pen = new Pen(chrome, 7f) { LineJoin = LineJoin.Round })
        {
            using (GraphicsPath frame = Rounded(new RectangleF(36, 44, 72, 56), 8f))
                g.DrawPath(pen, frame);

            using (var prompt = new Pen(chrome, 8f)
                   { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                g.DrawLines(prompt, new[]
                {
                    new PointF(52, 62), new PointF(63, 72), new PointF(52, 82)
                });

            using (GraphicsPath cursor = Rounded(new RectangleF(70, 76, 22, 8), 4f))
                g.FillPath(chrome, cursor);
        }
    }

    // The padlock *is* the terminal: a solid body with the prompt knocked out of
    // it and the shackle over the top. A separate lock badge beside a window frame
    // steals a corner of the tile and leaves dead space in the opposite one.
    private static void DrawSsh(Graphics g)
    {
        using (Brush chrome = Chrome(37f, 116f))
        using (var black = new SolidBrush(Color.Black))
        {
            using (var shackle = new Pen(chrome, 9f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                g.DrawArc(shackle, new RectangleF(52, 42, 40, 40), 180, 180);

            using (GraphicsPath body = Rounded(new RectangleF(34, 60, 76, 56), 11f))
                g.FillPath(chrome, body);

            // Prompt, cut out of the body.
            using (var prompt = new Pen(Color.Black, 7f)
                   { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                g.DrawLines(prompt, new[]
                {
                    new PointF(52, 76), new PointF(62, 87), new PointF(52, 98)
                });

            using (GraphicsPath cursor = Rounded(new RectangleF(70, 92, 22, 7), 3.5f))
                g.FillPath(black, cursor);
        }
    }

    // A bar the length of the tile: a key that divides the deck into groups and
    // does nothing else. The gradient runs down the bar's length, the way it does
    // through every other glyph in the pack.
    private static void DrawSeparator(Graphics g)
    {
        using (Brush chrome = Chrome(14f, 130f))
        using (GraphicsPath bar = Rounded(new RectangleF(64, 14, 16, 116), 8f))
            g.FillPath(chrome, bar);
    }

    // The same bar laid flat, for dividing rows rather than columns. Its gradient
    // crosses the short side: run down the length of a horizontal bar it would be
    // one flat tone and the thing would look painted on rather than machined.
    private static void DrawSeparatorFlat(Graphics g)
    {
        using (Brush chrome = Chrome(64f, 80f))
        using (GraphicsPath bar = Rounded(new RectangleF(14, 64, 116, 16), 8f))
            g.FillPath(chrome, bar);
    }

    // The stats bars inside a monitor: someone else's machine, measured. Same
    // frame as the remote-session glyph so the two read as a pair on the deck.
    private static void DrawRemoteGauge(Graphics g)
    {
        using (Brush chrome = Chrome(38f, 106f))
        using (var pen = new Pen(chrome, 7f) { LineJoin = LineJoin.Round })
        {
            using (GraphicsPath frame = Rounded(new RectangleF(34, 38, 76, 56), 7f))
                g.DrawPath(pen, frame);

            using (GraphicsPath stand = Rounded(new RectangleF(58, 98, 28, 8), 4f))
                g.FillPath(chrome, stand);

            // Sitting on a baseline inside the frame rather than on the tile's, or
            // the tallest bar grows straight through the bezel.
            float[] heights = { 12f, 24f, 17f, 30f };
            float x = 48f;
            foreach (float height in heights)
            {
                using (GraphicsPath bar = Rounded(new RectangleF(x, 84f - height, 10f, height), 3.5f))
                    g.FillPath(chrome, bar);
                x += 14f;
            }
        }
    }

    // A screen inside a screen: the usual shorthand for a remote session.
    private static void DrawRemote(Graphics g)
    {
        using (Brush chrome = Chrome(38f, 106f))
        using (var pen = new Pen(chrome, 7f) { LineJoin = LineJoin.Round })
        {
            using (GraphicsPath frame = Rounded(new RectangleF(34, 38, 76, 56), 7f))
                g.DrawPath(pen, frame);

            using (GraphicsPath inner = Rounded(new RectangleF(52, 52, 40, 28), 4f))
                g.FillPath(chrome, inner);

            using (GraphicsPath stand = Rounded(new RectangleF(58, 98, 28, 8), 4f))
                g.FillPath(chrome, stand);
        }
    }

    // RustDesk's mark is one thick ring with a single diagonal slit through it,
    // which is why the four cut edges are parallel rather than radial - two
    // separate arcs give the wrong shape. Drawn here in this pack's style rather
    // than lifted from their artwork.
    private static void DrawRustDesk(Graphics g)
    {
        using (Brush chrome = Chrome(28f, 116f))
        using (GraphicsPath ring = Ring(new RectangleF(28, 28, 88, 88), 24f, 0, 360))
            g.FillPath(chrome, ring);

        using (var slit = new GraphicsPath())
        using (var black = new SolidBrush(Color.Black))
        {
            slit.AddRectangle(new RectangleF(72 - 70, 72 - 8, 140, 16));
            using (var rotate = new Matrix())
            {
                rotate.RotateAt(-38f, new PointF(72, 72));
                slit.Transform(rotate);
            }
            g.FillPath(black, slit);
        }
    }

    // The Universal+ app mark: a ring holding a heavy U, with a small plus tucked
    // against the U's foot. No outline of it exists in any icon set, so it is
    // rebuilt here from the app's own artwork.
    private static void DrawUniversalPlus(Graphics g)
    {
        const float cx = 72f, cy = 72f, r = 40f;

        using (Brush chrome = Chrome(cy - r, cy + r))
        {
            using (var ring = new Pen(chrome, 8f))
                g.DrawEllipse(ring, cx - r, cy - r, r * 2f, r * 2f);

            // The letter comes from the font rather than being traced by hand: as
            // a path it takes the same gradient as everything else.
            // Shifted a little left, which is where the plus goes.
            using (var letter = new GraphicsPath())
            using (FontFamily family = Heaviest("Segoe UI Black", "Arial Black", "Segoe UI"))
            using (var centred = new StringFormat { Alignment = StringAlignment.Center })
            {
                letter.AddString("U", family, (int)FontStyle.Bold, 58f,
                                 new RectangleF(cx - 44f, cy - 38f, 80f, 76f), centred);
                g.FillPath(chrome, letter);
            }

            // Tucked against the letter's foot and kept inside the ring: the ring's
            // inner edge is 32 from the centre, so the plus has to stay within that
            // radius or it collides with the stroke.
            var across = new RectangleF(83.5f, 83.75f, 13f, 4.5f);
            var down = new RectangleF(87.75f, 79.5f, 4.5f, 13f);

            // A black halo, in case the letter reaches this far.
            using (var halo = new SolidBrush(Color.Black))
            {
                g.FillRectangle(halo, RectangleF.Inflate(across, 2.5f, 2.5f));
                g.FillRectangle(halo, RectangleF.Inflate(down, 2.5f, 2.5f));
            }

            // Sharp-cornered, or it rounds into a blob at this size.
            g.FillRectangle(chrome, across);
            g.FillRectangle(chrome, down);
        }
    }

    // A closed ring with the indicator inside it: leave a gap and put a bar
    // across it and the result reads as a power button instead of a knob.
    private static void DrawDial(Graphics g)
    {
        using (Brush chrome = Chrome(18f, 126f))
        {
            using (GraphicsPath ring = Ring(new RectangleF(34, 34, 76, 76), 10f, 0, 360))
                g.FillPath(chrome, ring);

            using (GraphicsPath pointer = Rounded(new RectangleF(68, 48, 8, 28), 4f))
                g.FillPath(chrome, pointer);

            using (var pen = new Pen(chrome, 7f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                g.DrawArc(pen, new RectangleF(18, 18, 108, 108), 148, 32);
                g.DrawArc(pen, new RectangleF(18, 18, 108, 108), 0, 32);
            }
        }
    }

    private static void DrawSlash(Graphics g)
    {
        DrawSlash(g, 34, ValueGlyphBottom + 4f, 110, ValueGlyphTop - 4f, 15f, 8f);
    }

    private static void DrawSlash(Graphics g, float x1, float y1, float x2, float y2)
    {
        DrawSlash(g, x1, y1, x2, y2, 15f, 8f);
    }

    // Struck through, with a black outline so it stays legible over the glyph.
    private static void DrawSlash(Graphics g, float x1, float y1, float x2, float y2,
                                  float gapWidth, float lineWidth)
    {
        using (Brush chrome = Chrome(Math.Min(y1, y2), Math.Max(y1, y2)))
        using (var gap = new Pen(Color.Black, gapWidth) { StartCap = LineCap.Round, EndCap = LineCap.Round })
        using (var line = new Pen(chrome, lineWidth) { StartCap = LineCap.Round, EndCap = LineCap.Round })
        {
            g.DrawLine(gap, x1, y1, x2, y2);
            g.DrawLine(line, x1, y1, x2, y2);
        }
    }

    #endregion

    #region primitives

    private static void Write(string path, int size, Action<Graphics> draw)
    {
        using (var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb))
        using (Graphics g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;

            using (var background = new SolidBrush(Color.Black))
                g.FillRectangle(background, 0, 0, size, size);

            g.ScaleTransform(size / Design, size / Design);
            draw(g);
            bitmap.Save(path, ImageFormat.Png);
        }
    }

    // First of the named families that is actually installed.
    private static FontFamily Heaviest(params string[] names)
    {
        foreach (string name in names)
        {
            try { return new FontFamily(name); }
            catch (Exception) { /* not installed */ }
        }
        return new FontFamily(System.Drawing.Text.GenericFontFamilies.SansSerif);
    }

    private static Brush Chrome(float top, float bottom)
    {
        var brush = new LinearGradientBrush(
            new PointF(0, top - 1f), new PointF(0, bottom + 1f), Color.White, Color.White);

        var blend = new ColorBlend(5);
        blend.Positions = new[] { 0f, 0.34f, 0.62f, 0.86f, 1f };
        blend.Colors = new[]
        {
            ColorTranslator.FromHtml("#DCE2E7"),
            ColorTranslator.FromHtml("#8B9298"),
            ColorTranslator.FromHtml("#5F666C"),
            ColorTranslator.FromHtml("#C6CDD3"),
            ColorTranslator.FromHtml("#F6F9FB")
        };
        brush.InterpolationColors = blend;
        return brush;
    }

    // Filled upper half of an annulus - the headphone band.
    private static GraphicsPath HalfRing(RectangleF outer, float thickness)
    {
        RectangleF inner = RectangleF.Inflate(outer, -thickness, -thickness);
        var path = new GraphicsPath();
        path.AddArc(outer, 180, 180);
        path.AddArc(inner, 0, -180);
        path.CloseFigure();
        return path;
    }

    private static GraphicsPath Ring(RectangleF outer, float thickness, float start, float sweep)
    {
        RectangleF inner = RectangleF.Inflate(outer, -thickness, -thickness);
        var path = new GraphicsPath();
        path.AddArc(outer, start, sweep);
        path.AddArc(inner, start + sweep, -sweep);
        path.CloseFigure();
        return path;
    }

    private static GraphicsPath Rounded(RectangleF rect, float radius)
    {
        float d = radius * 2f;
        var path = new GraphicsPath();
        path.AddArc(rect.X, rect.Y, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    #endregion
}
