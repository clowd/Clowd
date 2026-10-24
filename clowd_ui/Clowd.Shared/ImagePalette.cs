using System;
using System.Collections.Generic;
using Avalonia.Media;

namespace Clowd
{
    /// <summary>
    /// A picture's pixels as plain data: unpremultiplied BGRA bytes, row after row with no padding.
    /// What the color dialog's Image tab extracts its palette from and samples its rings on, kept
    /// free of any bitmap type so that work can run off the UI thread and be tested without a
    /// platform.
    /// </summary>
    public sealed class ImagePixels
    {
        public ImagePixels(int width, int height, byte[] bgra)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
            ArgumentNullException.ThrowIfNull(bgra);
            if (bgra.Length < width * height * 4)
                throw new ArgumentException($"{width} x {height} pixels need {width * height * 4} bytes.", nameof(bgra));

            Width = width;
            Height = height;
            Bgra = bgra;
        }

        public int Width { get; }

        public int Height { get; }

        public byte[] Bgra { get; }

        public Color GetPixel(int x, int y)
        {
            var i = (y * Width + x) * 4;
            return Color.FromArgb(Bgra[i + 3], Bgra[i + 2], Bgra[i + 1], Bgra[i]);
        }

        /// <summary>
        /// The part of a <paramref name="width"/> x <paramref name="height"/> picture that a
        /// UniformToFill stretch shows in a box of the given size: the largest centred rectangle of
        /// the box's shape, in the picture's pixels.
        /// </summary>
        public static (double X, double Y, double Width, double Height) FillCrop(int width, int height, double boxWidth, double boxHeight)
        {
            var scale = Math.Max(boxWidth / width, boxHeight / height);
            double w = Math.Min(width, boxWidth / scale), h = Math.Min(height, boxHeight / scale);
            return ((width - w) / 2, (height - h) / 2, w, h);
        }

        /// <summary>
        /// The region (<paramref name="x"/>, <paramref name="y"/>, <paramref name="w"/>,
        /// <paramref name="h"/>, in this picture's pixels) resampled to <paramref name="width"/> x
        /// <paramref name="height"/>. Shrinking, each output pixel is the mean of the source pixels
        /// under it, weighted by their alpha so transparent pixels lend no color; growing, it is the
        /// nearest source pixel.
        /// </summary>
        public ImagePixels Resample(double x, double y, double w, double h, int width, int height)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);

            var output = new byte[width * height * 4];
            double sx = w / width, sy = h / height;

            for (var oy = 0; oy < height; oy++)
            {
                var (y0, y1) = Span(y + oy * sy, sy, Height);
                for (var ox = 0; ox < width; ox++)
                {
                    var (x0, x1) = Span(x + ox * sx, sx, Width);

                    long b = 0, g = 0, r = 0, a = 0;
                    for (var yy = y0; yy < y1; yy++)
                    {
                        var i = (yy * Width + x0) * 4;
                        for (var xx = x0; xx < x1; xx++, i += 4)
                        {
                            int alpha = Bgra[i + 3];
                            b += Bgra[i] * alpha;
                            g += Bgra[i + 1] * alpha;
                            r += Bgra[i + 2] * alpha;
                            a += alpha;
                        }
                    }

                    var count = (y1 - y0) * (x1 - x0);
                    var o = (oy * width + ox) * 4;
                    if (a > 0)
                    {
                        output[o] = (byte)((b + a / 2) / a);
                        output[o + 1] = (byte)((g + a / 2) / a);
                        output[o + 2] = (byte)((r + a / 2) / a);
                    }

                    output[o + 3] = (byte)((a + count / 2) / count);
                }
            }

            return new ImagePixels(width, height, output);
        }

        /// <summary>The source pixels [start, end) under an output pixel that starts at
        /// <paramref name="from"/> and is <paramref name="size"/> source pixels long: all of them
        /// when shrinking, the one under its centre when growing.</summary>
        private static (int Start, int End) Span(double from, double size, int limit)
        {
            if (size < 1)
            {
                var nearest = Math.Clamp((int)Math.Floor(from + size / 2), 0, limit - 1);
                return (nearest, nearest + 1);
            }

            var start = Math.Clamp((int)Math.Floor(from + 1e-9), 0, limit - 1);
            var end = Math.Clamp((int)Math.Ceiling(from + size - 1e-9), start + 1, limit);
            return (start, end);
        }
    }

    /// <summary>The Image tab's mood chips: which colors of a picture its palette leans toward.</summary>
    public enum PaletteMood
    {
        /// <summary>The most saturated colors, at any comfortable lightness (the default).</summary>
        Colorful,

        /// <summary>Light, clear colors.</summary>
        Bright,

        /// <summary>Low-saturation, mid-light colors: the greys and dusty tones.</summary>
        Muted,

        /// <summary>Saturated colors at a darker, richer lightness.</summary>
        Deep,

        /// <summary>The shadows.</summary>
        Dark,
    }

    /// <summary>One palette color and where in the picture it came from, as fractions of the
    /// picture's width and height (0 to 1): where the Image tab puts that color's ring.</summary>
    public readonly record struct PaletteSample(Color Color, double X, double Y);

    /// <summary>
    /// Five-color palettes from pictures, for the color dialog's Image tab (COLOR_VIEWER_REWORK.md
    /// §7.1).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="Extract"/> shrinks the picture to a small grid, clusters the grid with k-means in
    /// OKLab (a perceptual space, so a cluster is a set of colors that look alike), then picks five
    /// clusters one at a time: each pick is the cluster that best fits the mood, weighted by how
    /// much of the picture it covers and by how far it is from the clusters already picked, so the
    /// five stay distinct. Each cluster is then pinned to a real pixel of it: the one that fits the
    /// mood best while staying typical of its cluster, sitting in a flat patch rather than on an
    /// edge, and not on top of another ring. The palette color is what <see cref="Sample"/> reads at
    /// that pixel, so a ring dropped back on its own spot reads its own color.
    /// </para>
    /// <para>
    /// Deterministic: the same picture and mood always give the same palette. The colors come back
    /// lightest first, so the bars read as a ramp.
    /// </para>
    /// </remarks>
    public static class ImagePalette
    {
        public const int ColorCount = 5;

        // the clustering grid's pixel budget: about 128 x 128, plenty to find a picture's colors
        // and quick enough to redo on every mood change
        private const int GridPixels = 128 * 128;
        private const int ClusterCount = 20;

        // the seeding histogram's bin size in OKLab, and the share of the picture a bin needs to
        // seed a cluster
        private const double SeedBin = 0.04;
        private const double SeedMinShare = 0.002;
        private const int Iterations = 12;

        // a ring reads the pixels within this many of its centre that look like the centre pixel
        // (within SampleSimilarity in OKLab): noise in a photo averages out, but on a sharp edge
        // (text, a UI border) the ring still reads one side's exact color rather than a blend
        private const int SampleRadius = 2;
        private const double SampleSimilarity = 0.05;

        private static readonly double[] Linear = BuildLinearTable();

        /// <summary>The color a ring at (<paramref name="x"/>, <paramref name="y"/>) — fractions of
        /// the picture's width and height — reads, as a palette sample at that spot. Fully opaque.</summary>
        public static PaletteSample Sample(ImagePixels image, double x, double y)
        {
            ArgumentNullException.ThrowIfNull(image);

            x = Math.Clamp(x, 0, 1);
            y = Math.Clamp(y, 0, 1);
            var cx = Math.Clamp((int)(x * image.Width), 0, image.Width - 1);
            var cy = Math.Clamp((int)(y * image.Height), 0, image.Height - 1);
            var centre = Lab(image, cx, cy);

            double b = 0, g = 0, r = 0, weight = 0;
            for (var yy = Math.Max(0, cy - SampleRadius); yy <= Math.Min(image.Height - 1, cy + SampleRadius); yy++)
            {
                for (var xx = Math.Max(0, cx - SampleRadius); xx <= Math.Min(image.Width - 1, cx + SampleRadius); xx++)
                {
                    var i = (yy * image.Width + xx) * 4;
                    var alpha = image.Bgra[i + 3] / 255d;
                    if (alpha <= 0 || (xx != cx || yy != cy) && Lab(image, xx, yy).DistanceTo(centre) > SampleSimilarity)
                        continue;

                    b += image.Bgra[i] * alpha;
                    g += image.Bgra[i + 1] * alpha;
                    r += image.Bgra[i + 2] * alpha;
                    weight += alpha;
                }
            }

            var color = weight > 0
                ? Color.FromRgb(ToByte(r / weight), ToByte(g / weight), ToByte(b / weight))
                : Colors.Black;
            return new PaletteSample(color, x, y);
        }

        /// <summary>Five colors from <paramref name="image"/>, leaning toward <paramref name="mood"/>,
        /// each with the spot it was read from. Always five: a picture with fewer distinct colors
        /// repeats them, read from different spots.</summary>
        public static PaletteSample[] Extract(ImagePixels image, PaletteMood mood)
        {
            ArgumentNullException.ThrowIfNull(image);

            var grid = new Grid(image);
            var clusters = FindClusters(grid);

            var picked = new List<Cluster>();
            var spots = new List<int>();
            var samples = new List<PaletteSample>();

            for (var slot = 0; slot < ColorCount; slot++)
            {
                var cluster = PickCluster(clusters, picked, mood) ?? PickRepeat(clusters, picked, mood);
                var spot = PickSpot(grid, cluster, spots, mood);
                picked.Add(cluster);
                spots.Add(spot);

                var (gx, gy) = (spot % grid.Width, spot / grid.Width);
                samples.Add(Sample(image, (gx + 0.5) / grid.Width, (gy + 0.5) / grid.Height));
            }

            // lightest first, and stable, so equal lightness keeps pick order
            var ordered = samples.ToArray();
            var keys = new (double Darkness, int Pick)[ordered.Length];
            for (var i = 0; i < keys.Length; i++)
                keys[i] = (-OkLab.From(ordered[i].Color.R, ordered[i].Color.G, ordered[i].Color.B).L, i);
            Array.Sort(keys, ordered);
            return ordered;
        }

        /// <summary>How well a color of OKLab lightness <paramref name="l"/> (0 to 1) and chroma
        /// <paramref name="c"/> (0 to about 0.32) suits a mood, before any weighting by area.</summary>
        private static double MoodFit(PaletteMood mood, double l, double c) => mood switch
        {
            PaletteMood.Bright => (0.03 + c) * Bell(l, 0.80, 0.13),
            PaletteMood.Muted => Bell(c, 0.045, 0.03) * Bell(l, 0.62, 0.2),
            PaletteMood.Deep => (0.03 + c) * Bell(l, 0.42, 0.12),
            PaletteMood.Dark => (0.05 + 0.5 * c) * Bell(l, 0.24, 0.1),
            _ => (0.02 + c) * Bell(l, 0.66, 0.34),
        };

        private static double Bell(double x, double mean, double width)
        {
            var z = (x - mean) / width;
            return Math.Exp(-z * z / 2);
        }

        /// <summary>The next cluster: the best fit for the mood, by weight, and far in color from
        /// those already picked. Null once every cluster has been picked.</summary>
        private static Cluster PickCluster(List<Cluster> clusters, List<Cluster> picked, PaletteMood mood)
        {
            Cluster best = null;
            var bestScore = -1d;
            foreach (var cluster in clusters)
            {
                if (picked.Contains(cluster))
                    continue;

                var nearest = double.MaxValue;
                foreach (var other in picked)
                    nearest = Math.Min(nearest, cluster.Centroid.DistanceTo(other.Centroid));

                var apart = picked.Count == 0 ? 1 : 1 - Math.Exp(-Square(nearest / 0.12));
                var score = MoodFit(mood, cluster.Centroid.L, cluster.Centroid.Chroma) * cluster.Weight * apart;
                if (score > bestScore)
                {
                    best = cluster;
                    bestScore = score;
                }
            }

            return best;
        }

        /// <summary>A picture with fewer clusters than palette colors repeats its best one (PickSpot
        /// then finds it another spot).</summary>
        private static Cluster PickRepeat(List<Cluster> clusters, List<Cluster> picked, PaletteMood mood)
        {
            Cluster best = null;
            var bestScore = -1d;
            foreach (var cluster in clusters)
            {
                var uses = picked.FindAll(c => c == cluster).Count;
                var score = MoodFit(mood, cluster.Centroid.L, cluster.Centroid.Chroma) * cluster.Weight / (1 + uses);
                if (score > bestScore)
                {
                    best = cluster;
                    bestScore = score;
                }
            }

            return best;
        }

        /// <summary>
        /// The grid cell that stands for <paramref name="cluster"/>: one that suits the mood, is
        /// typical of the cluster (near its centroid, allowing for how spread out it is), sits in a
        /// flat patch rather than on an edge (so the ring's sample is steady), and keeps clear of the
        /// spots already taken.
        /// </summary>
        private static int PickSpot(Grid grid, Cluster cluster, List<int> taken, PaletteMood mood)
        {
            var reach = cluster.Spread + 0.03;
            var best = cluster.Members[0];
            var bestScore = -1d;

            foreach (var cell in cluster.Members)
            {
                var color = grid.Colors[cell];
                var typical = Math.Exp(-Square(color.DistanceTo(cluster.Centroid) / reach));
                var flat = grid.Flatness(cell);

                var clear = 1d;
                foreach (var other in taken)
                {
                    var dx = (double)(cell % grid.Width - other % grid.Width) / grid.Width;
                    var dy = (double)(cell / grid.Width - other / grid.Width) / grid.Height;
                    clear = Math.Min(clear, 1 - Math.Exp(-(dx * dx + dy * dy) / Square(0.1)));
                }

                var score = MoodFit(mood, color.L, color.Chroma) * typical * flat * grid.Inset(cell) * clear;
                if (score > bestScore)
                {
                    best = cell;
                    bestScore = score;
                }
            }

            return best;
        }

        /// <summary>
        /// k-means over the grid's opaque cells in OKLab. Seeded deterministically by maximin over a
        /// coarse color histogram: the most common color first, then each time the color furthest
        /// from every seed so far. That gives every distinct region of the picture (a small sun in a
        /// big sky) a seed of its own however the pixels happen to fall, where random seeding would
        /// sometimes merge it into a neighbour. Bins too small to be more than noise or edge blends
        /// are not seeds. Empty clusters are dropped.
        /// </summary>
        private static List<Cluster> FindClusters(Grid grid)
        {
            var points = grid.Points;

            var binOf = new Dictionary<(int, int, int), int>();
            var bins = new List<(double L, double A, double B, int Count)>();
            foreach (var point in points)
            {
                var color = grid.Colors[point];
                var key = ((int)Math.Round(color.L / SeedBin), (int)Math.Round(color.A / SeedBin), (int)Math.Round(color.B / SeedBin));
                if (!binOf.TryGetValue(key, out var bin))
                {
                    binOf[key] = bin = bins.Count;
                    bins.Add(default);
                }

                var sum = bins[bin];
                bins[bin] = (sum.L + color.L, sum.A + color.A, sum.B + color.B, sum.Count + 1);
            }

            var minimum = Math.Max(1, points.Length * SeedMinShare);
            var candidates = new List<(OkLab Color, int Count)>();
            foreach (var bin in bins)
            {
                if (bin.Count >= minimum)
                    candidates.Add((new OkLab(bin.L / bin.Count, bin.A / bin.Count, bin.B / bin.Count), bin.Count));
            }

            // a picture of nothing but noise still needs seeds
            if (candidates.Count == 0)
            {
                foreach (var bin in bins)
                    candidates.Add((new OkLab(bin.L / bin.Count, bin.A / bin.Count, bin.B / bin.Count), bin.Count));
            }

            var k = Math.Min(ClusterCount, candidates.Count);
            var centres = new OkLab[k];
            var first = 0;
            for (var i = 1; i < candidates.Count; i++)
            {
                if (candidates[i].Count > candidates[first].Count)
                    first = i;
            }

            centres[0] = candidates[first].Color;
            var nearest = new double[candidates.Count];
            for (var i = 0; i < candidates.Count; i++)
                nearest[i] = candidates[i].Color.DistanceTo(centres[0]);

            for (var c = 1; c < k; c++)
            {
                // furthest from every seed so far, with a slight lean toward common colors so a
                // tie goes to the one that covers more of the picture
                var chosen = 0;
                var bestScore = -1d;
                for (var i = 0; i < candidates.Count; i++)
                {
                    var score = nearest[i] * Math.Pow(candidates[i].Count, 0.1);
                    if (score > bestScore)
                    {
                        chosen = i;
                        bestScore = score;
                    }
                }

                centres[c] = candidates[chosen].Color;
                for (var i = 0; i < candidates.Count; i++)
                    nearest[i] = Math.Min(nearest[i], candidates[i].Color.DistanceTo(centres[c]));
            }

            var assignment = new int[points.Length];
            for (var iteration = 0; iteration < Iterations; iteration++)
            {
                var changed = false;
                for (var p = 0; p < points.Length; p++)
                {
                    var color = grid.Colors[points[p]];
                    int bestCentre = 0;
                    var bestDistance = double.MaxValue;
                    for (var c = 0; c < k; c++)
                    {
                        var d = color.DistanceSquaredTo(centres[c]);
                        if (d < bestDistance)
                        {
                            bestDistance = d;
                            bestCentre = c;
                        }
                    }

                    if (iteration == 0 || assignment[p] != bestCentre)
                        changed = true;
                    assignment[p] = bestCentre;
                }

                if (!changed)
                    break;

                var sums = new (double L, double A, double B, int Count)[k];
                for (var p = 0; p < points.Length; p++)
                {
                    var color = grid.Colors[points[p]];
                    ref var sum = ref sums[assignment[p]];
                    sum = (sum.L + color.L, sum.A + color.A, sum.B + color.B, sum.Count + 1);
                }

                for (var c = 0; c < k; c++)
                {
                    if (sums[c].Count > 0)
                        centres[c] = new OkLab(sums[c].L / sums[c].Count, sums[c].A / sums[c].Count, sums[c].B / sums[c].Count);
                }
            }

            var members = new List<int>[k];
            for (var p = 0; p < points.Length; p++)
                (members[assignment[p]] ??= new List<int>()).Add(points[p]);

            var clusters = new List<Cluster>();
            for (var c = 0; c < k; c++)
            {
                if (members[c] == null)
                    continue;

                double spread = 0, flat = 0;
                foreach (var cell in members[c])
                {
                    spread += grid.Colors[cell].DistanceTo(centres[c]);
                    flat += grid.Flatness(cell);
                }

                var count = members[c].Count;
                clusters.Add(new Cluster(centres[c], members[c], (double)count / points.Length, spread / count, flat / count));
            }

            return clusters;
        }

        private static OkLab Lab(ImagePixels image, int x, int y)
        {
            var i = (y * image.Width + x) * 4;
            return OkLab.From(image.Bgra[i + 2], image.Bgra[i + 1], image.Bgra[i]);
        }

        private static double Square(double x) => x * x;

        private static byte ToByte(double channel) => (byte)Math.Clamp(Math.Round(channel), 0, 255);

        private static double[] BuildLinearTable()
        {
            var table = new double[256];
            for (var i = 0; i < 256; i++)
            {
                var c = i / 255d;
                table[i] = c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
            }

            return table;
        }

        /// <summary>A cluster: its mean color, its cells, the share of the picture it covers, how
        /// far its cells stray from the mean, and how flat its cells are on average.</summary>
        private sealed record Cluster(OkLab Centroid, List<int> Members, double Share, double Spread, double Flatness)
        {
            /// <summary>How much the cluster counts for, whatever the mood: its area, to a low power
            /// (so a small vivid accent can still beat a large dull field), and its flatness (a
            /// cluster made of edges is the blend between two colors, not a color of the picture).</summary>
            public double Weight => Math.Pow(Share, 0.3) * Math.Sqrt(Flatness);
        }

        /// <summary>The picture shrunk to the clustering budget, in OKLab, with each cell's edge
        /// strength (its mean color distance to its four neighbours) and the cells opaque enough to
        /// count.</summary>
        private sealed class Grid
        {
            public readonly int Width;
            public readonly int Height;
            public readonly OkLab[] Colors;
            public readonly double[] Edges;
            public readonly int[] Points;

            public Grid(ImagePixels image)
            {
                var scale = Math.Min(1, Math.Sqrt((double)GridPixels / ((long)image.Width * image.Height)));
                Width = Math.Max(1, (int)Math.Round(image.Width * scale));
                Height = Math.Max(1, (int)Math.Round(image.Height * scale));
                var cells = scale < 1 ? image.Resample(0, 0, image.Width, image.Height, Width, Height) : image;

                Colors = new OkLab[Width * Height];
                var opaque = new List<int>();
                for (var i = 0; i < Colors.Length; i++)
                {
                    Colors[i] = OkLab.From(cells.Bgra[i * 4 + 2], cells.Bgra[i * 4 + 1], cells.Bgra[i * 4]);
                    if (cells.Bgra[i * 4 + 3] >= 128)
                        opaque.Add(i);
                }

                // a wholly transparent picture still has colors under its alpha; use them rather
                // than nothing
                if (opaque.Count == 0)
                {
                    for (var i = 0; i < Colors.Length; i++)
                        opaque.Add(i);
                }

                Points = opaque.ToArray();

                Edges = new double[Colors.Length];
                for (var y = 0; y < Height; y++)
                {
                    for (var x = 0; x < Width; x++)
                    {
                        var i = y * Width + x;
                        double sum = 0;
                        var count = 0;
                        if (x > 0) { sum += Colors[i].DistanceTo(Colors[i - 1]); count++; }
                        if (x < Width - 1) { sum += Colors[i].DistanceTo(Colors[i + 1]); count++; }
                        if (y > 0) { sum += Colors[i].DistanceTo(Colors[i - Width]); count++; }
                        if (y < Height - 1) { sum += Colors[i].DistanceTo(Colors[i + Width]); count++; }
                        Edges[i] = count > 0 ? sum / count : 0;
                    }
                }
            }

            /// <summary>1 for a cell in a flat patch, falling toward 0 on an edge.</summary>
            public double Flatness(int cell) => 1 / (1 + Square(Edges[cell] / 0.03));

            /// <summary>1 for a cell clear of the picture's border, falling toward a floor on it: a
            /// ring right on the edge is half off the picture and hard to grab.</summary>
            public double Inset(int cell)
            {
                var x = (cell % Width + 0.5) / Width;
                var y = (cell / Width + 0.5) / Height;
                var margin = Math.Min(Math.Min(x, 1 - x), Math.Min(y, 1 - y));
                var t = Math.Clamp(margin / 0.09, 0, 1);
                return 0.1 + 0.9 * t * t * (3 - 2 * t);
            }
        }

        /// <summary>OKLab (Björn Ottosson, 2020): lightness 0 to 1 and two opponent axes, laid out
        /// so equal distances look about equally different.</summary>
        private readonly record struct OkLab(double L, double A, double B)
        {
            public double Chroma => Math.Sqrt(A * A + B * B);

            public static OkLab From(byte r, byte g, byte b)
            {
                double lr = Linear[r], lg = Linear[g], lb = Linear[b];
                var l = Math.Cbrt(0.4122214708 * lr + 0.5363325363 * lg + 0.0514459929 * lb);
                var m = Math.Cbrt(0.2119034982 * lr + 0.6806995451 * lg + 0.1073969566 * lb);
                var s = Math.Cbrt(0.0883024619 * lr + 0.2817188376 * lg + 0.6299787005 * lb);
                return new OkLab(
                    0.2104542553 * l + 0.7936177850 * m - 0.0040720468 * s,
                    1.9779984951 * l - 2.4285922050 * m + 0.4505937099 * s,
                    0.0259040371 * l + 0.7827717662 * m - 0.8086757660 * s);
            }

            public double DistanceSquaredTo(OkLab other)
            {
                double dl = L - other.L, da = A - other.A, db = B - other.B;
                return dl * dl + da * da + db * db;
            }

            public double DistanceTo(OkLab other) => Math.Sqrt(DistanceSquaredTo(other));
        }
    }
}
