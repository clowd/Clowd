using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Clowd.VideoSDK.Composition;
using SkiaSharp;
using Sheet = Clowd.VideoSDK.Composition.BackgroundTileSheet;

namespace Clowd.Tools.BackgroundTiles
{
    /// <summary>
    /// Generates the pre-rendered loop sheets the video editor's background STYLE tiles play
    /// instead of animating the wallpaper live, and measures what animating it live actually
    /// costs. See README.md next to this file for the why, the numbers and the review checklist.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Frames come out of <see cref="BackgroundRenderer.Draw(SKCanvas, SKRect, string, string, double, double)"/>,
    /// the same public entry point <c>FrameComposer</c> and the tiles themselves call, so a sheet
    /// is the renderer's own output and cannot drift from what the canvas paints. Nothing here
    /// reimplements any part of the wallpaper drawing.
    /// </para>
    /// <para>
    /// <b>The loop is seamless by construction.</b> Frame <c>i</c> of <c>n</c> is drawn at
    /// <c>i * PeriodSeconds / n</c>, so frame <c>n</c> would land exactly on
    /// <c>PeriodSeconds</c>, which <see cref="BackgroundRenderer.PhaseOf(BackgroundStyle, long, double)"/>
    /// wraps to phase 0, which is frame 0. There is no blend, no crossfade and no hand-picked cut
    /// point: the wrap is the artwork's own period.
    /// </para>
    /// </remarks>
    internal static class Program
    {
        // The frame size, the grid and the frame count rule are NOT declared here: they are
        // BackgroundTileSheet's, in the SDK, so that this generator, the inspector tile that plays
        // the sheets and the test that checks them cannot drift apart. Everything below is about
        // producing the file, which is this tool's alone.

        /// <summary>
        /// The size at which a sheet stops being encoded losslessly. Skia's WebP encoder switches
        /// to its lossless mode at quality 100, which is bit-exact and, for artwork made of flat
        /// fills and hard edges, smaller than the lossy encoding of the same thing: Moving Blob
        /// and Moving Corners both come out under this and ship bit-identical to the renderer's
        /// output. Breathing Field is a 90 frame Gaussian wash whose every pixel is unique, so its
        /// lossless sheet is 634 KB, six times the rest of the library put together, and it drops
        /// to 34 KB at <see cref="LossyQuality"/> with a mean error under 2 levels out of 255 on
        /// artwork that is nothing but smooth gradients.
        /// </summary>
        private const int LosslessBudgetBytes = 128 * 1024;

        /// <summary>The quality a sheet over <see cref="LosslessBudgetBytes"/> is re-encoded
        /// at.</summary>
        private const int LossyQuality = 92;

        /// <summary>Where the sheets are written, relative to the repo root.</summary>
        private const string DefaultOutput = "clowd_ui/Clowd.Ui/Assets/BackgroundTiles";

        private static int Main(string[] args)
        {
            string output = null;
            string contact = null;
            string format = "webp";
            int quality = -1;
            bool bench = false;
            var filters = new List<string>();

            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--bench":
                        bench = true;
                        break;
                    case "--out" when i + 1 < args.Length:
                        output = args[++i];
                        break;
                    case "--contact" when i + 1 < args.Length:
                        contact = args[++i];
                        break;
                    case "--format" when i + 1 < args.Length:
                        format = args[++i].ToLowerInvariant();
                        break;
                    case "--quality" when i + 1 < args.Length:
                        quality = int.Parse(args[++i]);
                        break;
                    case "--help":
                    case "-h":
                        Usage();
                        return 0;
                    default:
                        if (args[i].StartsWith("-", StringComparison.Ordinal))
                        {
                            Usage();
                            return 2;
                        }
                        filters.Add(args[i]);
                        break;
                }
            }

            if (bench)
            {
                Bench();
                return 0;
            }

            output ??= Path.Combine(RepoRoot(), DefaultOutput.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(output);

            var written = new List<(BackgroundStyle Style, string Path)>();
            foreach (var style in BackgroundCatalog.Styles.Where(s => s.IsAnimated))
            {
                if (filters.Count > 0 && !filters.Any(f => style.Id.Contains(f, StringComparison.OrdinalIgnoreCase)))
                    continue;
                written.Add((style, Write(style, output, format, quality)));
            }

            if (contact != null)
                WriteContactSheet(written, contact);
            return 0;
        }

        private static void Usage()
        {
            Console.WriteLine("Usage: dotnet run --project tools/background-tiles [--bench] [--out DIR]");
            Console.WriteLine("                  [--format jpg|png|webp] [--quality N] [style-filter ...]");
            Console.WriteLine();
            Console.WriteLine("  --bench     measure what one live tile draw costs per style, and exit");
            Console.WriteLine("  --out DIR   write the sheets here (default: " + DefaultOutput + ")");
            Console.WriteLine("  --contact P also write a review contact sheet to this PNG path");
            Console.WriteLine("  --format    sheet encoding; webp is what ships, the others are for comparison");
            Console.WriteLine("  --quality   force an encoder quality instead of the lossless-first policy");
            Console.WriteLine("  filter      only regenerate styles whose id contains one of these");
        }

        // ------------------------------------------------------------------------- generation

        private static string Write(BackgroundStyle style, string output, string format, int quality)
        {
            int frames = Sheet.FrameCountOf(style);
            var size = Sheet.SizeOf(style);
            var info = new SKImageInfo(size.Width, size.Height, SKColorType.Bgra8888,
                SKAlphaType.Premul, SKColorSpace.CreateSrgb());

            using var surface = SKSurface.Create(info);
            var canvas = surface.Canvas;
            // Opaque black under the art rather than transparent: every wallpaper covers its own
            // rectangle, so this only ever shows in the cells of a part-filled last row, which
            // nothing ever samples.
            canvas.Clear(SKColors.Black);

            for (int i = 0; i < frames; i++)
            {
                var dest = Sheet.RectOf(i);
                int save = canvas.Save();
                canvas.ClipRect(dest);
                // The theme is left null on purpose: the STYLE row's tiles pass no theme either,
                // so both resolve to the style's first colorway (the file as authored).
                BackgroundRenderer.Draw(canvas, dest, style.Id, null, Sheet.TimeSecondsOf(style, i));
                canvas.RestoreToCount(save);
            }

            using var image = surface.Snapshot();
            var (encoded, extension) = Encode(image, format, quality);
            string name = extension == ".webp" ? Sheet.FileNameOf(style.Id) : style.Id + extension;
            string path = Path.Combine(output, name);
            File.WriteAllBytes(path, encoded);
            var error = Error(surface, encoded);
            Console.WriteLine($"{style.Id,-18} {frames,3} frames  {info.Width}x{info.Height}  " +
                $"{encoded.Length / 1024.0,6:0.0} KB  ({style.PeriodSeconds:0} s period)  " +
                $"encoder error mean {error.Mean:0.00} max {error.Max} of 255");
            return path;
        }

        /// <summary>
        /// How far the encoded sheet drifted from the pixels that went into it, per channel out of
        /// 255. Printed next to the size because the two are the whole trade being made here: the
        /// three animated wallpapers are smooth fields, which is both why a lossy encoding is so
        /// small and where banding would show up first if the quality were pushed too low.
        /// </summary>
        private static (double Mean, int Max) Error(SKSurface source, byte[] encoded)
        {
            using var decoded = SKBitmap.Decode(encoded);
            using var original = SKBitmap.FromImage(source.Snapshot());
            if (decoded == null || decoded.Width != original.Width || decoded.Height != original.Height)
                return (double.NaN, -1);

            long total = 0;
            int max = 0;
            int count = 0;
            for (int y = 0; y < original.Height; y++)
            {
                for (int x = 0; x < original.Width; x++)
                {
                    var a = original.GetPixel(x, y);
                    var b = decoded.GetPixel(x, y);
                    foreach (int d in new[] { Math.Abs(a.Red - b.Red), Math.Abs(a.Green - b.Green), Math.Abs(a.Blue - b.Blue) })
                    {
                        total += d;
                        count++;
                        if (d > max)
                            max = d;
                    }
                }
            }
            return ((double)total / count, max);
        }

        /// <summary>
        /// A review image, three rows per style: ten frames spread across the loop; the wrap seam
        /// (the last frames beside the first, which is the one join a looping picture can get
        /// wrong in a way no single frame shows); and the same frames drawn at the tile's real
        /// size, alternating the sheet against a live render of the same instant, which is where a
        /// cover-crop that samples the wrong part of the frame would be obvious. Everything is
        /// decoded back OUT of the encoded sheet, so what this shows is the shipped quality rather
        /// than the in-memory render.
        /// </summary>
        private static void WriteContactSheet(List<(BackgroundStyle Style, string Path)> sheets, string path)
        {
            const int scale = 2;
            const int cell = 6;
            const int label = 16;
            int cellW = Sheet.FrameWidth * scale + cell;
            int cellH = Sheet.FrameHeight * scale + cell;
            int width = cellW * Sheet.Columns + cell;
            int height = sheets.Count * (label + cellH * 2 + label + Sheet.NominalTileHeight * 4 + cell) + cell;

            using var surface = SKSurface.Create(new SKImageInfo(width, height));
            var canvas = surface.Canvas;
            canvas.Clear(new SKColor(0x1e, 0x1e, 0x1e));
            using var font = new SKFont(SKTypeface.Default, 12);
            using var ink = new SKPaint { Color = SKColors.White, IsAntialias = true };
            using var sampling = new SKPaint { IsAntialias = true };

            int y = cell;
            foreach (var (style, sheetPath) in sheets)
            {
                using var bitmap = SKBitmap.Decode(sheetPath);
                if (bitmap == null)
                    throw new InvalidOperationException("could not decode the sheet just written: " + sheetPath);
                using var image = SKImage.FromBitmap(bitmap);
                int frames = Sheet.FrameCountOf(style);

                canvas.DrawText($"{style.Id}  {frames} frames over {style.PeriodSeconds:0} s", cell, y + 12, font, ink);
                y += label;

                for (int i = 0; i < Sheet.Columns; i++)
                {
                    int index = (int)Math.Round(i * frames / (double)Sheet.Columns);
                    canvas.DrawImage(image, Sheet.RectOf(index),
                        SKRect.Create(cell + i * cellW, y, Sheet.FrameWidth * scale, Sheet.FrameHeight * scale),
                        new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None), sampling);
                }
                y += cellH;

                // The seam, four cells wide: the last two frames of the loop then the first two.
                // A clean loop reads as one continuous movement across this row.
                int[] seam = { frames - 2, frames - 1, 0, 1 };
                for (int i = 0; i < seam.Length; i++)
                {
                    canvas.DrawImage(image, Sheet.RectOf(seam[i]),
                        SKRect.Create(cell + i * cellW, y, Sheet.FrameWidth * scale, Sheet.FrameHeight * scale),
                        new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None), sampling);
                }
                canvas.DrawText("wrap seam: frames n-2, n-1, 0, 1", cell + 4 * cellW + 4, y + 16, font, ink);
                y += cellH;

                canvas.DrawText("at tile size, sheet then live, alternating: the pairs must be the same picture",
                    cell, y + 12, font, ink);
                y += label;
                DrawTileSizeComparison(canvas, image, style, cell, y, scale * 2);
                y += Sheet.NominalTileHeight * scale * 2 + cell;
            }

            using var snapshot = surface.Snapshot();
            using var data = snapshot.Encode(SKEncodedImageFormat.Png, 100);
            File.WriteAllBytes(path, data.ToArray());
            Console.WriteLine("contact sheet: " + path);
        }

        /// <summary>
        /// Encodes the sheet. The shipped path is <c>webp</c> with no explicit quality: lossless
        /// first, dropping to <see cref="LossyQuality"/> only for a sheet that would otherwise
        /// blow past <see cref="LosslessBudgetBytes"/>. The other formats and an explicit quality
        /// exist so the choice can be re-checked rather than taken on trust; nothing reads them.
        /// </summary>
        /// <summary>
        /// Alternating pairs of the same instant at the tile's real size: first the sheet as the
        /// tile plays it (cover-cropped through <see cref="BackgroundTileSheet.SourceRectFor"/>),
        /// then the wallpaper rendered live into the same rectangle. Magnified only after both
        /// have been drawn at the true size, so the comparison is of what the tile shows and not
        /// of two large renders. A cover-crop that sampled the wrong band of the frame, or a sheet
        /// whose aspect no longer matches the tile's, shows up here as two visibly different
        /// pictures side by side while every other row still looks fine.
        /// </summary>
        private static void DrawTileSizeComparison(SKCanvas canvas, SKImage sheet, BackgroundStyle style,
            int x, int y, int magnify)
        {
            int frames = Sheet.FrameCountOf(style);
            var tile = SKRect.Create(0, 0, Sheet.NominalTileWidth, Sheet.NominalTileHeight);
            var info = new SKImageInfo(Sheet.NominalTileWidth, Sheet.NominalTileHeight,
                SKColorType.Bgra8888, SKAlphaType.Premul, SKColorSpace.CreateSrgb());
            var linear = new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None);
            // Nearest on the way out: the magnification must not smooth over a difference between
            // the two, which is the entire point of putting them next to each other.
            var nearest = new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None);

            for (int i = 0; i < 4; i++)
            {
                int index = (int)Math.Round(i * frames / 4.0);
                for (int fromSheet = 1; fromSheet >= 0; fromSheet--)
                {
                    using var surface = SKSurface.Create(info);
                    surface.Canvas.Clear(SKColors.Black);
                    if (fromSheet == 1)
                        surface.Canvas.DrawImage(sheet, Sheet.SourceRectFor(index, tile), tile, linear, null);
                    else
                        BackgroundRenderer.Draw(surface.Canvas, tile, style.Id, null, Sheet.TimeSecondsOf(style, index));
                    surface.Canvas.Flush();

                    using var drawn = surface.Snapshot();
                    int column = i * 2 + (1 - fromSheet);
                    canvas.DrawImage(drawn, SKRect.Create(
                        x + column * (Sheet.NominalTileWidth * magnify + 4), y,
                        Sheet.NominalTileWidth * magnify, Sheet.NominalTileHeight * magnify), nearest, null);
                }
            }
        }

        private static (byte[] Bytes, string Extension) Encode(SKImage image, string format, int quality)
        {
            switch (format)
            {
                case "png":
                    return (image.Encode(SKEncodedImageFormat.Png, 100).ToArray(), ".png");
                case "jpg":
                case "jpeg":
                    return (image.Encode(SKEncodedImageFormat.Jpeg, quality < 0 ? LossyQuality : quality).ToArray(), ".jpg");
                case "webp":
                    if (quality >= 0)
                        return (image.Encode(SKEncodedImageFormat.Webp, quality).ToArray(), ".webp");
                    var lossless = image.Encode(SKEncodedImageFormat.Webp, 100).ToArray();
                    return lossless.Length <= LosslessBudgetBytes
                        ? (lossless, ".webp")
                        : (image.Encode(SKEncodedImageFormat.Webp, LossyQuality).ToArray(), ".webp");
                default:
                    throw new ArgumentException("unknown format: " + format);
            }
        }

        // ------------------------------------------------------------------------ measurement

        /// <summary>
        /// What one tile-sized draw costs: live for every style, and out of the committed sheet for
        /// the three that have one. This is the measurement the whole feature is justified by, so
        /// it is kept runnable rather than written down once. The numbers in README.md came from
        /// here.
        /// </summary>
        /// <remarks>
        /// A raster surface rather than a GPU one deliberately. What the inspector tile's clock
        /// charges itself is the CPU time inside <see cref="BackgroundRenderer.Draw"/> on
        /// Avalonia's leased canvas, which is scene walking, per-frame path rebuilding and, for
        /// Breathing Field, the fixed 480px CPU blur; the rasterization of the resulting draw
        /// calls happens later on the GPU. Measuring on a raster surface counts all of that plus
        /// the (tiny, 192x64) rasterization, so it is an upper bound on the part the tile pays
        /// synchronously, and for the sheet a pessimistic one, since on the real leased canvas
        /// the quad is recorded against a texture the GPU already holds rather than filtered here.
        /// </remarks>
        private static void Bench()
        {
            var info = new SKImageInfo(Sheet.FrameWidth, Sheet.FrameHeight, SKColorType.Bgra8888, SKAlphaType.Premul,
                SKColorSpace.CreateSrgb());
            var dest = SKRect.Create(0, 0, Sheet.FrameWidth, Sheet.FrameHeight);
            string sheetDir = Path.Combine(RepoRoot(), DefaultOutput.Replace('/', Path.DirectorySeparatorChar));

            Console.WriteLine($"one {Sheet.FrameWidth}x{Sheet.FrameHeight} draw, {Iterations} iterations after {Warmup} warmup");
            Console.WriteLine($"{"style",-18} {"period",7} {"live mean",10} {"live min",10} {"live max",10} {"sheet mean",11}");

            foreach (var style in BackgroundCatalog.Styles)
            {
                using var surface = SKSurface.Create(info);
                var canvas = surface.Canvas;

                var live = Measure(i =>
                {
                    // Spread the samples over the whole period so an animated style is measured
                    // across the geometry it actually draws, not one lucky phase.
                    double seconds = style.IsAnimated ? i * style.PeriodSeconds / (Warmup + Iterations) : 0;
                    canvas.Clear(SKColors.Black);
                    BackgroundRenderer.Draw(canvas, dest, style.Id, null, seconds);
                    canvas.Flush();
                });

                string sheetColumn = "-";
                string sheetPath = Path.Combine(sheetDir, Sheet.FileNameOf(style.Id));
                if (File.Exists(sheetPath))
                {
                    using var bitmap = SKBitmap.Decode(sheetPath);
                    bitmap.SetImmutable();
                    using var image = SKImage.FromBitmap(bitmap);
                    int frames = Sheet.FrameCountOf(style);
                    var sheet = Measure(i =>
                    {
                        int frame = i % frames;
                        canvas.Clear(SKColors.Black);
                        canvas.DrawImage(image, Sheet.SourceRectFor(frame, dest), dest,
                            new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None), null);
                        canvas.Flush();
                    });
                    sheetColumn = sheet.Average().ToString("0.000");
                }

                Console.WriteLine($"{style.Id,-18} {(style.IsAnimated ? style.PeriodSeconds + " s" : "still"),7} " +
                    $"{live.Average(),10:0.000} {live.Min(),10:0.000} {live.Max(),10:0.000} {sheetColumn,11}");
            }
        }

        private const int Warmup = 5;
        private const int Iterations = 60;

        /// <summary>Runs <paramref name="draw"/> <see cref="Warmup"/> + <see cref="Iterations"/>
        /// times and returns the milliseconds each of the timed ones took.</summary>
        private static double[] Measure(Action<int> draw)
        {
            var samples = new double[Iterations];
            for (int i = 0; i < Warmup + Iterations; i++)
            {
                long started = Stopwatch.GetTimestamp();
                draw(i);
                double ms = (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency;
                if (i >= Warmup)
                    samples[i - Warmup] = ms;
            }
            return samples;
        }

        // ------------------------------------------------------------------------------ paths

        /// <summary>The repo root, found by walking up from the running assembly until Clowd.slnx
        /// turns up, so the tool can be run from anywhere.</summary>
        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Clowd.slnx")))
                dir = dir.Parent;
            if (dir == null)
                throw new InvalidOperationException("could not find Clowd.slnx above " + AppContext.BaseDirectory);
            return dir.FullName;
        }
    }
}
