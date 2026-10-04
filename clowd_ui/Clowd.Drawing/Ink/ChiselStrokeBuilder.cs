using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Media;
using Clowd.Drawing.Graphics;
using SkiaSharp;
using static Clowd.Drawing.Ink.FreehandStroke;

namespace Clowd.Drawing.Ink
{
    /// <summary>
    /// The highlighter's outline: a tall rectangular tip (a chisel marker) swept along the stroke's
    /// centreline. The centreline is the brush's — stage one of <see cref="FreehandStroke"/>, the
    /// same streamline, start rule and raw-cursor tip — but there is no pressure: the width only
    /// depends on the direction of travel against the tip, the full height across a pass along its
    /// short side and the width across one along its long side.
    ///
    /// The tip is held at a slant, as a hand holds a marker, and the slant drifts a little along the
    /// stroke (<see cref="TipAngle"/>): each stroke's slant and drift come from its seed, so the
    /// flat ends lean at their own angles rather than standing as straight verticals, and a stroke
    /// rebuilt from its persisted samples and seed is the stroke that was drawn. The angle is a
    /// function of the distance travelled so far, never of what comes later, which keeps the
    /// outline causal (only the tip is redone per update).
    ///
    /// The swept shape needs no offsetting: each centreline segment sweeps the convex hull of the
    /// tip at its two ends, and the stroke is the union of those hulls, which share the tip at every
    /// joint. Every hull is wound the same way and filled NonZero, so overlaps add winding rather
    /// than paint twice and a loop leaves no hole. <see cref="Settled"/> grows in sealed chunks of
    /// <see cref="ChunkSegments"/> segments and <see cref="Tail"/> is the open chunk through the
    /// tip; the chunking is a function of the centreline alone, so a stroke rebuilt cold has the
    /// same pieces as the one drawn live. Each piece is also kept as its raw hulls
    /// (<see cref="Runs"/>), which is what the blended ink is painted from.
    /// </summary>
    internal sealed class ChiselStrokeBuilder : IInkOutline
    {
        /// <summary>The tip's width as a fraction of its height.</summary>
        internal const double WidthRatio = 0.3;

        /// <summary>Segments per sealed chunk.</summary>
        internal const int ChunkSegments = 256;

        /// <summary>The slant a stroke is held at: between these, either way, in degrees.</summary>
        internal const double MinSlantDeg = 8, MaxSlantDeg = 24;

        /// <summary>How far the slant drifts along the stroke (degrees, either way) and over what
        /// distance, in multiples of the tip height, a full wobble takes at the slowest.</summary>
        internal const double DriftDeg = 6, DriftPeriod = 14;

        /// <summary>
        /// One piece of the stroke as its hulls, <see cref="HullVertices"/> vertices each, in
        /// origin-local space; immutable once built. The Skia path is made on first use by the
        /// render thread and kept (it is the only thread that reads it).
        /// </summary>
        internal sealed class Run
        {
            public readonly Point[] Hulls;
            private SKPath _path;

            public Run(Point[] hulls) => Hulls = hulls;

            public SKPath GetPath()
            {
                if (_path != null)
                    return _path;

                var path = new SKPath { FillType = SKPathFillType.Winding };
                for (int i = 0; i < Hulls.Length; i += HullVertices)
                {
                    path.MoveTo((float)Hulls[i].X, (float)Hulls[i].Y);
                    for (int k = 1; k < HullVertices; k++)
                        path.LineTo((float)Hulls[i + k].X, (float)Hulls[i + k].Y);
                    path.Close();
                }

                return _path = path;
            }
        }

        /// <summary>Every hull is padded to this many vertices (a hull of two rectangles has at
        /// most 8; a shorter one repeats its last vertex, which draws nothing).</summary>
        internal const int HullVertices = 8;

        private readonly double _size;
        private readonly double _halfW, _halfH;
        private readonly Tilt _tilt;
        private int _sampleCount;
        private bool _compacted;

        // stage one: the settled centreline (never includes the tip), the distance travelled to
        // each point, and the carry
        private List<Point> _points = new List<Point>();
        private List<double> _lengths = new List<double>();
        private StrokeState _strokeState;
        private int _consumed; // samples folded into _points; the last sample never is

        private List<Geometry> _chunks = new List<Geometry>();
        private List<Run> _chunkRuns = new List<Run>();
        private int _openStart; // first point of the open chunk
        private Geometry _settled;
        private Rect _settledBounds;
        private Geometry _tail;
        private Run _tailRun;
        private Rect _bounds;

        private readonly List<Point> _hullScratch = new List<Point>();

        /// <param name="size">The tip's height.</param>
        /// <param name="seed">The stroke's slant and drift (see <see cref="TipAngle"/>); null holds
        /// the tip upright, an axis-aligned rectangle (tests).</param>
        public ChiselStrokeBuilder(double size, int? seed)
        {
            _size = size;
            var tip = TipSize(size);
            _halfW = tip.Width / 2;
            _halfH = tip.Height / 2;
            _tilt = seed is { } s ? new Tilt(s, size) : null;
        }

        /// <summary>The tip for a stroke size: <paramref name="size"/> tall, <see cref="WidthRatio"/> as wide.</summary>
        internal static Size TipSize(double size) => new Size(Math.Max(1, size * WidthRatio), size);

        /// <summary>The tip's slant (radians, clockwise on screen) for a stroke of this seed and
        /// size, <paramref name="distance"/> units into it.</summary>
        internal static double TipAngle(int seed, double size, double distance) => new Tilt(seed, size).At(distance);

        public double Size => _size;

        public int SampleCount => _sampleCount;

        public Geometry Settled => _settled;

        public Geometry Tail => _tail;

        public Rect Bounds => _bounds;

        /// <summary>Sealed chunks so far (tests).</summary>
        internal int ChunkCount => _chunkRuns.Count;

        /// <summary>The pieces as hulls — the sealed chunks, then the tail — for painting. A new
        /// array each call: the caller may hold it while the stroke keeps growing.</summary>
        internal Run[] Runs
        {
            get
            {
                var runs = new Run[_chunkRuns.Count + (_tailRun != null ? 1 : 0)];
                _chunkRuns.CopyTo(runs);
                if (_tailRun != null)
                    runs[^1] = _tailRun;
                return runs;
            }
        }

        public bool CanAppend(int sampleCount) =>
            sampleCount >= _sampleCount && (!_compacted || sampleCount == _sampleCount);

        public void Compact()
        {
            if (_compacted)
                return;
            _compacted = true;
            _points = null;
            _lengths = null;
            _chunks = null;
        }

        public void Update(ReadOnlySpan<GraphicBrush.Sample> samples)
        {
            if (!CanAppend(samples.Length))
                throw new InvalidOperationException("the samples are not a continuation of the builder's");
            if (samples.Length == _sampleCount && _tail != null)
                return;

            _sampleCount = samples.Length;
            int n = samples.Length;
            if (n == 0)
            {
                _tail = new StreamGeometry();
                _tailRun = null;
                _bounds = default;
                return;
            }

            if (_consumed == 0)
            {
                _points.Add(samples[0].P);
                _lengths.Add(0);
                _strokeState = StrokeState.Start(samples[0].P);
                _consumed = 1;
            }

            // stage one over the samples that are no longer last
            for (int i = _consumed; i <= n - 2; i++)
                if (StrokeStep(ref _strokeState, samples[i - 1], samples[i], false, _size, out var point))
                    AddPoint(point.P);

            _consumed = Math.Max(_consumed, n - 1);

            // seal every complete chunk; the next one starts on the last point of this one, so
            // the tip at the joint is in both and the union is seamless
            bool sealed_ = false;
            while (_points.Count - 1 - _openStart >= ChunkSegments)
            {
                var run = BuildRun(_openStart, _openStart + ChunkSegments, null, 0);
                _chunkRuns.Add(run);
                _chunks.Add(ToGeometry(run));
                _openStart += ChunkSegments;
                sealed_ = true;
            }

            if (sealed_)
            {
                _settled = new GeometryGroup { FillRule = FillRule.NonZero, Children = new GeometryCollection(_chunks) };
                _settledBounds = _settled.Bounds;
            }

            // the tip, on a copy of the carry (it is redone next time, smoothed)
            Point? tip = null;
            double tipLength = 0;
            if (n >= 2)
            {
                var tipState = _strokeState;
                if (StrokeStep(ref tipState, samples[n - 2], samples[n - 1], true, _size, out var tipPoint))
                {
                    tip = tipPoint.P;
                    tipLength = _lengths[^1] + GraphicLine.Distance(_points[^1], tipPoint.P);
                }
            }

            _tailRun = BuildRun(_openStart, _points.Count - 1, tip, tipLength);
            _tail = ToGeometry(_tailRun);
            _bounds = _settled != null ? _settledBounds.Union(_tail.Bounds) : _tail.Bounds;
        }

        private void AddPoint(Point p)
        {
            _lengths.Add(_lengths[^1] + GraphicLine.Distance(_points[^1], p));
            _points.Add(p);
        }

        /// <summary>The swept tip over points [<paramref name="from"/>, <paramref name="to"/>],
        /// then on to <paramref name="tip"/> when given; a lone point is the tip itself.</summary>
        private Run BuildRun(int from, int to, Point? tip, double tipLength)
        {
            int count = to - from + (tip != null ? 1 : 0);
            var hulls = new Point[Math.Max(1, count) * HullVertices];
            int at = 0;

            if (count == 0)
                AddHull(hulls, ref at, _points[from], _lengths[from], _points[from], _lengths[from]);

            for (int i = from; i < to; i++)
                AddHull(hulls, ref at, _points[i], _lengths[i], _points[i + 1], _lengths[i + 1]);

            if (tip is { } t)
                AddHull(hulls, ref at, _points[to], _lengths[to], t, tipLength);

            return new Run(hulls);
        }

        /// <summary>The convex hull of the tip at <paramref name="a"/> and at <paramref name="b"/>
        /// (each at its own slant), padded to <see cref="HullVertices"/>.</summary>
        private void AddHull(Point[] hulls, ref int at, Point a, double sa, Point b, double sb)
        {
            Span<Point> corners = stackalloc Point[8];
            Corners(a, sa, corners.Slice(0, 4));
            Corners(b, sb, corners.Slice(4, 4));

            int n = ConvexHull(corners, _hullScratch);
            for (int k = 0; k < HullVertices; k++)
                hulls[at + k] = _hullScratch[Math.Min(k, n - 1)];
            at += HullVertices;
        }

        private void Corners(Point c, double distance, Span<Point> output)
        {
            double angle = _tilt?.At(distance) ?? 0;
            double cos = Math.Cos(angle), sin = Math.Sin(angle);
            var u = new Vector(cos * _halfW, sin * _halfW);   // across the tip
            var v = new Vector(-sin * _halfH, cos * _halfH);  // along it
            output[0] = c - u - v;
            output[1] = c + u - v;
            output[2] = c + u + v;
            output[3] = c - u + v;
        }

        /// <summary>Andrew's monotone chain: the hull of <paramref name="points"/> into
        /// <paramref name="hull"/>, always wound the same way, collinear points dropped.</summary>
        internal static int ConvexHull(Span<Point> points, List<Point> hull)
        {
            // insertion sort by (x, y): eight points
            for (int i = 1; i < points.Length; i++)
            {
                var p = points[i];
                int j = i - 1;
                while (j >= 0 && (points[j].X > p.X || (points[j].X == p.X && points[j].Y > p.Y)))
                {
                    points[j + 1] = points[j];
                    j--;
                }
                points[j + 1] = p;
            }

            hull.Clear();
            for (int i = 0; i < points.Length; i++)
            {
                while (hull.Count >= 2 && Cross(hull[^2], hull[^1], points[i]) <= 0)
                    hull.RemoveAt(hull.Count - 1);
                hull.Add(points[i]);
            }

            int lower = hull.Count + 1;
            for (int i = points.Length - 2; i >= 0; i--)
            {
                while (hull.Count >= lower && Cross(hull[^2], hull[^1], points[i]) <= 0)
                    hull.RemoveAt(hull.Count - 1);
                hull.Add(points[i]);
            }

            hull.RemoveAt(hull.Count - 1); // the first point again
            return hull.Count;
        }

        private static double Cross(Point o, Point a, Point b) =>
            (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);

        private static Geometry ToGeometry(Run run)
        {
            var geometry = new StreamGeometry();
            using (var ctx = geometry.Open())
            {
                ctx.SetFillRule(FillRule.NonZero);
                var h = run.Hulls;
                for (int i = 0; i < h.Length; i += HullVertices)
                {
                    ctx.BeginFigure(h[i], true);
                    for (int k = 1; k < HullVertices; k++)
                        ctx.LineTo(h[i + k]);
                    ctx.EndFigure(true);
                }
            }

            return geometry;
        }

        public bool FillContains(Point p) =>
            (_settled != null && _settled.FillContains(p)) || _tail.FillContains(p);

        public bool StrokeContains(IPen pen, Point p) =>
            (_settled != null && _settled.StrokeContains(pen, p)) || _tail.StrokeContains(pen, p);

        /// <summary>
        /// A stroke's slant as a function of the distance travelled: a held angle (random in
        /// magnitude and lean) plus two slow sine wobbles of incommensurate periods, so it drifts
        /// without repeating visibly. Everything is drawn from the seed; nothing from the samples.
        /// </summary>
        private sealed class Tilt
        {
            private readonly double _base, _amp1, _amp2, _k1, _k2, _ph1, _ph2;

            public Tilt(int seed, double size)
            {
                var rnd = new Random(seed);
                double deg = Math.PI / 180;
                double slant = MinSlantDeg + rnd.NextDouble() * (MaxSlantDeg - MinSlantDeg);
                _base = (rnd.Next(2) == 0 ? -slant : slant) * deg;
                _amp1 = DriftDeg * 0.65 * deg;
                _amp2 = DriftDeg * 0.35 * deg;
                double period = Math.Max(1, size) * DriftPeriod;
                _k1 = 2 * Math.PI / (period * (1 + rnd.NextDouble()));
                _k2 = 2 * Math.PI / (period * (0.37 + 0.3 * rnd.NextDouble()));
                _ph1 = rnd.NextDouble() * 2 * Math.PI;
                _ph2 = rnd.NextDouble() * 2 * Math.PI;
            }

            public double At(double s) => _base + _amp1 * Math.Sin(_k1 * s + _ph1) + _amp2 * Math.Sin(_k2 * s + _ph2);
        }
    }
}
