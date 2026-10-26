using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media;
using Clowd.Drawing.Graphics;
using static Clowd.Drawing.Ink.FreehandStroke;

namespace Clowd.Drawing.Ink
{
    /// <summary>
    /// Builds a brush stroke's outline incrementally, so a long stroke costs the same per pointer
    /// event as a short one. The stages of <see cref="FreehandStroke"/> are causal with a short
    /// frontier: a sample is final once the next one exists (only the tip is the raw cursor), and
    /// a centreline point's edge vertices are final once the point after it is final and the
    /// stroke has run <see cref="EndNoiseThreshold"/> units past it (the trim before the tip can no
    /// longer reach it). Everything behind that frontier is kept — stroke points, edge vertices,
    /// and the stage states — and only the few points on the frontier are redone per update.
    ///
    /// What is drawn is two pieces: <see cref="Settled"/>, the final edge vertices sealed in
    /// chunks of <see cref="ChunkPoints"/> points, each a closed figure, merged into one NonZero
    /// <see cref="GeometryGroup"/> (one Skia path, so chunks that overlap or cross add winding
    /// rather than paint twice, and translucent ink stays even); and <see cref="Tail"/>, one small
    /// figure from the open chunk through the tip with the end cap. Consecutive figures share
    /// <see cref="OverlapVertices"/> vertices per edge, so the smoothed cut that closes one is
    /// inside the next and the union is the batch outline. Chunk boundaries are a function of the
    /// settled state alone (never of how many samples arrived together), so a stroke rebuilt cold
    /// from its persisted samples has the same pieces as the one drawn live.
    /// </summary>
    internal sealed class FreehandStrokeBuilder : IInkOutline
    {
        /// <summary>Points per sealed chunk: ~500 outline vertices, a few hundred quads each.</summary>
        internal const int ChunkPoints = 256;

        /// <summary>Edge vertices two consecutive figures share, so the cut of one is covered by the other.</summary>
        internal const int OverlapVertices = 3;

        private readonly double _size;
        private int _sampleCount;
        private bool _compacted;

        // stage one: the settled centreline (never includes the tip) and its carry
        private List<StrokePoint> _points = new List<StrokePoint>();
        private StrokeState _strokeState;
        private int _consumed; // samples folded into _points; the last sample never is

        // stage two: the settled edge vertices, per settled point the vertex counts after it, and the carry
        private List<Point> _left = new List<Point>();
        private List<Point> _right = new List<Point>();
        private List<int> _leftEnd = new List<int>();
        private List<int> _rightEnd = new List<int>();
        private OutlineState _outlineState;
        private int _outlineSettled; // points [0, _outlineSettled) have final vertices

        // sealed chunks and the open range
        private List<Geometry> _chunks = new List<Geometry>();
        private int _openStart;
        private Geometry _settled;
        private Rect _settledBounds;

        // the tail of the last Update: its own vertices (after the settled ones), its tip and cap radius
        private List<Point> _tailLeft = new List<Point>();
        private List<Point> _tailRight = new List<Point>();
        private StrokePoint _tailTip;
        private double _tailRadius;
        private bool _tailIsDot;
        private Geometry _tail;
        private Rect _bounds;

        private List<Point> _outline = new List<Point>(); // figure scratch

        public FreehandStrokeBuilder(double size)
        {
            _size = size;
        }

        public double Size => _size;

        /// <summary>The samples the last <see cref="Update"/> saw.</summary>
        public int SampleCount => _sampleCount;

        /// <summary>The sealed chunks as one NonZero group; null until the first chunk seals.</summary>
        public Geometry Settled => _settled;

        /// <summary>The open chunk through the tip, end cap included; never null after an Update.</summary>
        public Geometry Tail => _tail;

        /// <summary>Union of the two pieces' bounds, in the samples' (origin-local) space.</summary>
        public Rect Bounds => _bounds;

        /// <summary>Centreline points whose edge vertices are final.</summary>
        public int OutlineSettled => _outlineSettled;

        /// <summary>First point of the open chunk (what <see cref="Tail"/> starts at).</summary>
        public int OpenStart => _openStart;

        /// <summary>
        /// True when <see cref="Update"/> can take <paramref name="sampleCount"/> samples as a
        /// continuation of what it has: the same samples it saw plus appended ones, unless the
        /// incremental state was dropped by <see cref="Compact"/> — then only the same count.
        /// Anything else needs a fresh builder.
        /// </summary>
        public bool CanAppend(int sampleCount) =>
            sampleCount >= _sampleCount && (!_compacted || sampleCount == _sampleCount);

        /// <summary>
        /// Drops the incremental state and keeps only what is drawn (the two geometries and the
        /// bounds): once a stroke is finished its builder is just a cache, and the stroke points and
        /// vertices of a long stroke are several times the size of its samples.
        /// </summary>
        public void Compact()
        {
            if (_compacted)
                return;
            _compacted = true;
            _points = null;
            _left = _right = _tailLeft = _tailRight = _outline = null;
            _leftEnd = _rightEnd = null;
            _chunks = null;
        }

        /// <summary>
        /// Folds the samples appended since the last call into the settled state, seals any chunk
        /// that became complete, and rebuilds the tail. <paramref name="samples"/> must start with
        /// the samples of the previous call (see <see cref="CanAppend"/>).
        /// </summary>
        public void Update(ReadOnlySpan<GraphicBrush.Sample> samples)
        {
            if (!CanAppend(samples.Length))
                throw new InvalidOperationException("the samples are not a continuation of the builder's");
            if (samples.Length == _sampleCount && _tail != null)
                return;

            _sampleCount = samples.Length;
            int n = samples.Length;

            // a click or a flick has its own rules (the dot, the dash); batch them
            if (n <= 2)
            {
                _tail = BuildGeometry(samples, _size);
                _bounds = _tail.Bounds;
                return;
            }

            // stage one over the samples that are no longer last
            if (_consumed == 0)
            {
                _points.Add(FirstPoint(samples[0].P));
                _strokeState = StrokeState.Start(samples[0].P);
                _consumed = 1;
            }

            for (int i = _consumed; i <= n - 2; i++)
            {
                if (StrokeStep(ref _strokeState, samples[i - 1], samples[i], false, _size, out var point))
                {
                    _points.Add(point);
                    if (_points.Count == 2)
                        _points[0] = WithV(_points[0], point.V);
                }
            }

            _consumed = n - 1;

            // stage two over the points the trim can no longer reach
            int lastSettled = _points.Count - 1;
            while (_outlineSettled < lastSettled
                   && _points[lastSettled].RunningLength - _points[_outlineSettled].RunningLength >= EndNoiseThreshold)
            {
                if (_outlineSettled == 0)
                    _outlineState = OutlineState.Start(_points[0], _size, _points[0].Pressure);
                OutlineStep(ref _outlineState, _points, _outlineSettled, _points.Count, _size, _left, _right);
                _leftEnd.Add(_left.Count);
                _rightEnd.Add(_right.Count);
                _outlineSettled++;
            }

            SealChunks();

            // the tip, on a copy of the carry (it is redone next time, smoothed)
            var tipState = _strokeState;
            bool hasTip = StrokeStep(ref tipState, samples[n - 2], samples[n - 1], true, _size, out var tip);
            BuildTailVertices(hasTip, tip);

            _tail = _tailIsDot ? DotGeometry(_tailTip) : BuildTail(_openStart);
            _bounds = _settled != null ? _settledBounds.Union(_tail.Bounds) : _tail.Bounds;
        }

        /// <summary>
        /// Seals [open, open + k·ChunkPoints) once those points are settled, for the smallest k
        /// whose range (its first point aside) has at least 2·OverlapVertices vertices per edge —
        /// so the next chunk's overlap start lands strictly inside it and the open start always
        /// advances (a hand jittering in place can settle hundreds of points that emit no
        /// vertex). Both tests read only settled state, which keeps the chunking deterministic.
        /// </summary>
        private void SealChunks()
        {
            bool sealed_ = false;
            for (int k = 1; ; k++)
            {
                int end = _openStart + k * ChunkPoints - 1;
                if (end >= _outlineSettled)
                    break;

                if (_leftEnd[end] - LeftStart(_openStart + 1) < 2 * OverlapVertices
                    || _rightEnd[end] - RightStart(_openStart + 1) < 2 * OverlapVertices)
                    continue;

                _chunks.Add(BuildSegment(_openStart, end));
                _openStart = OverlapStart(end);
                sealed_ = true;
                k = 0;
            }

            if (sealed_)
            {
                _settled = new GeometryGroup { FillRule = FillRule.NonZero, Children = new GeometryCollection(_chunks) };
                _settledBounds = _settled.Bounds;
            }
        }

        private int LeftStart(int point) => point == 0 ? 0 : _leftEnd[point - 1];

        private int RightStart(int point) => point == 0 ? 0 : _rightEnd[point - 1];

        /// <summary>
        /// Where a figure must start to share <see cref="OverlapVertices"/> vertices per edge with
        /// one that ends at settled point <paramref name="end"/>.
        /// </summary>
        public int OverlapStart(int end)
        {
            int s = end;
            while (s > 0 && (_leftEnd[end] - LeftStart(s) < OverlapVertices || _rightEnd[end] - RightStart(s) < OverlapVertices))
                s--;
            return s;
        }

        private void BuildTailVertices(bool hasTip, StrokePoint tip)
        {
            _tailLeft.Clear();
            _tailRight.Clear();
            _tailIsDot = false;

            if (hasTip)
                _points.Add(tip);
            int last = _points.Count - 1;

            if (last == 0)
            {
                // every sample after the first was swallowed and the cursor is back on it
                _tailIsDot = true;
                _tailTip = _points[0];
                return;
            }

            // with no settled second point the first point's direction is the tip's, as in batch
            bool patched = hasTip && last == 1;
            var saved = _points[0];
            if (patched)
                _points[0] = WithV(saved, _points[1].V);

            var st = _outlineSettled == 0 ? OutlineState.Start(_points[0], _size, _points[last].Pressure) : _outlineState;
            for (int i = _outlineSettled; i <= last; i++)
            {
                if (!IsEndNoise(_points, i, last))
                    OutlineStep(ref st, _points, i, last, _size, _tailLeft, _tailRight);
            }

            _tailTip = _points[last];
            _tailRadius = st.Radius;

            if (patched)
                _points[0] = saved;
            if (hasTip)
                _points.RemoveAt(last);
        }

        /// <summary>The figure of settled points [<paramref name="from"/>, <paramref name="to"/>],
        /// closed across the stroke at both ends (the start cap when from is 0). Needs the
        /// incremental state (see <see cref="Compact"/>).</summary>
        public Geometry BuildSegment(int from, int to)
        {
            if (to >= _outlineSettled || from > to)
                throw new ArgumentOutOfRangeException(nameof(to));

            var left = CollectionsMarshal.AsSpan(_left).Slice(LeftStart(from), _leftEnd[to] - LeftStart(from));
            var right = CollectionsMarshal.AsSpan(_right).Slice(RightStart(from), _rightEnd[to] - RightStart(from));
            return ToFigure(left, right, from == 0, null);
        }

        /// <summary>The figure from settled point <paramref name="from"/> through the tip of the
        /// last Update, end cap included (the start cap too when from is 0); null when the stroke
        /// is a dot. Needs the incremental state (see <see cref="Compact"/>).</summary>
        public Geometry BuildTail(int from)
        {
            if (_tailIsDot)
                return null;
            if (from > _outlineSettled)
                throw new ArgumentOutOfRangeException(nameof(from));

            // the settled run and the tail's own vertices make one edge each: splice the tail's
            // onto the settled lists for the call and take them off again
            int lc = _left.Count, rc = _right.Count;
            _left.AddRange(_tailLeft);
            _right.AddRange(_tailRight);
            try
            {
                var left = CollectionsMarshal.AsSpan(_left).Slice(LeftStart(from));
                var right = CollectionsMarshal.AsSpan(_right).Slice(RightStart(from));
                return ToFigure(left, right, from == 0, _tailTip);
            }
            finally
            {
                _left.RemoveRange(lc, _tailLeft.Count);
                _right.RemoveRange(rc, _tailRight.Count);
            }
        }

        private Geometry ToFigure(ReadOnlySpan<Point> left, ReadOnlySpan<Point> right, bool startCap, StrokePoint? tip)
        {
            _outline.Clear();
            AddOutline(left, right, _outline, _points[0].P, startCap, tip, _tailRadius);
            return ToGeometry(_outline);
        }

        private Geometry DotGeometry(StrokePoint point)
        {
            _outline.Clear();
            AddDot(point, _size, _outline);
            return ToGeometry(_outline);
        }

        /// <summary>Sealed chunks so far (tests).</summary>
        internal int ChunkCount => _chunks?.Count ?? -1;

        /// <summary>The whole outline's edge vertices, settled then tail, as the batch pass would
        /// list them (tests). Needs the incremental state.</summary>
        internal void GetEdges(List<Point> left, List<Point> right)
        {
            left.Clear();
            right.Clear();
            left.AddRange(_left);
            left.AddRange(_tailLeft);
            right.AddRange(_right);
            right.AddRange(_tailRight);
        }

        // ---- queries over both pieces ---------------------------------------------------------

        public bool FillContains(Point p) =>
            (_settled != null && _settled.FillContains(p)) || _tail.FillContains(p);

        public bool StrokeContains(IPen pen, Point p) =>
            (_settled != null && _settled.StrokeContains(pen, p)) || _tail.StrokeContains(pen, p);
    }
}
