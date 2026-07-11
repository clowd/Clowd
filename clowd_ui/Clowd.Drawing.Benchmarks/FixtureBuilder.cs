using System;
using System.IO;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Clowd.Drawing;
using Clowd.Drawing.Graphics;

namespace Clowd.Drawing.Benchmarks
{
    /// <summary>
    /// Deterministic, seeded document builders (benchmark-spec.md §2). Positions lie on a grid over a
    /// 3200×1800 canvas area; the same seed produces the same document every run so medians are
    /// comparable across builds.
    /// </summary>
    public static class FixtureBuilder
    {
        private const double AreaW = 3200;
        private const double AreaH = 1800;

        private static readonly Color[] Palette =
        {
            Colors.Red, Colors.OrangeRed, Colors.Orange, Colors.Gold, Colors.Yellow, Colors.Lime,
            Colors.Green, Colors.Teal, Colors.Cyan, Colors.DodgerBlue, Colors.Blue, Colors.Indigo,
            Colors.Purple, Colors.Magenta, Colors.HotPink, Colors.Black,
        };

        private const string Lorem = "Lorem ipsum dolor\r\nsit amet consectetur\r\nadipiscing elit sed";

        private static string _imagePath;
        private static readonly object _imageLock = new object();

        /// <summary>
        /// The proportional-mix document. Exact counts are the floor of each ratio; any remainder is
        /// added as rectangles so the total is exactly <paramref name="n"/>.
        /// </summary>
        public static DrawingCanvas BuildDoc(int n)
        {
            var canvas = new DrawingCanvas();
            var rnd = new Random(1234);

            int rects = (int)(0.30 * n);
            int ellipses = (int)(0.15 * n);
            int arrows = (int)(0.15 * n);
            int lines = (int)(0.15 * n);
            int polys = (int)(0.10 * n);
            int texts = (int)(0.10 * n);
            int counts = (int)(0.025 * n);
            int filled = (int)(0.025 * n);
            int placed = rects + ellipses + arrows + lines + polys + texts + counts + filled;
            rects += Math.Max(0, n - placed); // remainder → rects

            int cols = (int)Math.Ceiling(Math.Sqrt(Math.Max(1, n)));
            double cellW = AreaW / cols;
            double cellH = AreaH / cols;
            int idx = 0;
            int rectOrdinal = 0;

            Point Cell(ref int i)
            {
                int col = i % cols;
                int row = i / cols;
                i++;
                return new Point(col * cellW + cellW * 0.15, row * cellH + cellH * 0.15);
            }

            Color NextColor() => Palette[rnd.Next(Palette.Length)];
            double NextWidth() => 1 + rnd.Next(4);

            for (int k = 0; k < rects; k++)
            {
                var p = Cell(ref idx);
                double angle = (rectOrdinal % 4 == 3) ? 15 : 0; // every 4th rect rotated 15°
                rectOrdinal++;
                canvas.GraphicsList.Add(new GraphicRectangle(NextColor(), NextWidth(),
                    new Rect(p.X, p.Y, 40 + rnd.Next(80), 30 + rnd.Next(60)), angle));
            }
            for (int k = 0; k < ellipses; k++)
            {
                var p = Cell(ref idx);
                canvas.GraphicsList.Add(new GraphicEllipse(NextColor(), NextWidth(),
                    new Rect(p.X, p.Y, 40 + rnd.Next(80), 30 + rnd.Next(60))));
            }
            for (int k = 0; k < arrows; k++)
            {
                var p = Cell(ref idx);
                canvas.GraphicsList.Add(new GraphicArrow(NextColor(), NextWidth(),
                    p, new Point(p.X + 60 + rnd.Next(60), p.Y + 40 + rnd.Next(40))));
            }
            for (int k = 0; k < lines; k++)
            {
                var p = Cell(ref idx);
                canvas.GraphicsList.Add(new GraphicLine(NextColor(), NextWidth(),
                    p, new Point(p.X + 60 + rnd.Next(60), p.Y + 40 + rnd.Next(40))));
            }
            for (int k = 0; k < polys; k++)
            {
                var p = Cell(ref idx);
                var poly = new GraphicPolyLine(NextColor(), NextWidth(), p);
                for (int j = 1; j < 80; j++) // 80 points total incl start
                    poly.AddPoint(new Point(p.X + rnd.Next(120), p.Y + rnd.Next(120)));
                poly.EndDrawing(true);
                canvas.GraphicsList.Add(poly);
            }
            for (int k = 0; k < texts; k++)
            {
                var p = Cell(ref idx);
                canvas.GraphicsList.Add(new GraphicText(NextColor(), 2, p, 0, Lorem));
            }
            for (int k = 0; k < counts; k++)
            {
                var p = Cell(ref idx);
                canvas.GraphicsList.Add(new GraphicCount(NextColor(), 2, p, (k + 1).ToString()));
            }
            for (int k = 0; k < filled; k++)
            {
                var p = Cell(ref idx);
                canvas.GraphicsList.Add(new GraphicFilledRectangle(NextColor(),
                    new Rect(p.X, p.Y, 40 + rnd.Next(80), 30 + rnd.Next(60))));
            }

            canvas.AddCommandToHistory(false); // baseline (non-mergable) commit
            return canvas;
        }

        /// <summary>
        /// <see cref="BuildDoc"/> with n-1 graphics plus one <see cref="GraphicImage"/> at the bottom of
        /// the z-order over a generated 4K PNG. <paramref name="withObscure"/> adds 3 pixelate shapes.
        /// </summary>
        public static DrawingCanvas BuildImageDoc(int n, bool withObscure = false)
        {
            // Build the smaller doc, then insert the image at index 0 (bottom of z-order).
            var canvas = BuildDoc(Math.Max(1, n - 1));

            string path = EnsureTestImage();
            var img = new GraphicImage(path, new Rect(0, 0, 3840, 2160), new PixelRect(0, 0, 3840, 2160));
            if (withObscure)
            {
                img.ObscuredShapes = new[]
                {
                    new GraphicImage.ObscuredShape(new Point(200, 200), new Point(600, 200), new Point(600, 500), new Point(200, 500), 8),
                    new GraphicImage.ObscuredShape(new Point(1000, 800), new Point(1400, 800), new Point(1400, 1100), new Point(1000, 1100), 8),
                    new GraphicImage.ObscuredShape(new Point(2000, 1200), new Point(2400, 1200), new Point(2400, 1500), new Point(2000, 1500), 8),
                };
            }
            canvas.GraphicsList.Insert(0, img);
            canvas.AddCommandToHistory(false);
            return canvas;
        }

        /// <summary>Generates the 4K test PNG once per process (random noise + gradient), in a temp dir.</summary>
        public static string EnsureTestImage()
        {
            lock (_imageLock)
            {
                if (_imagePath != null && File.Exists(_imagePath)) return _imagePath;

                const int w = 3840, h = 2160;
                var wb = new WriteableBitmap(new PixelSize(w, h), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
                var rnd = new Random(1234);
                using (var fb = wb.Lock())
                {
                    unsafe
                    {
                        byte* p = (byte*)fb.Address;
                        for (int y = 0; y < h; y++)
                        {
                            byte gr = (byte)(255.0 * y / h); // vertical gradient
                            byte* row = p + (long)y * fb.RowBytes;
                            for (int x = 0; x < w; x++)
                            {
                                byte* px = row + x * 4;
                                byte noise = (byte)rnd.Next(64);
                                px[0] = (byte)((255.0 * x / w) / 2 + noise); // B
                                px[1] = (byte)(gr / 2 + noise);              // G
                                px[2] = (byte)(128 + noise);                 // R
                                px[3] = 255;                                 // A
                            }
                        }
                    }
                }

                string dir = Path.Combine(Path.GetTempPath(), "clowd_bench_" + Environment.ProcessId);
                Directory.CreateDirectory(dir);
                string path = Path.Combine(dir, "testimage.png");
                wb.Save(path);
                _imagePath = path;
                return path;
            }
        }
    }
}
