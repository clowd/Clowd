using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Clowd.Drawing.Ink;
using Clowd.Drawing.Rendering;

namespace Clowd.Drawing.Graphics
{
    /// <summary>
    /// A freehand brush stroke. What is persisted is the raw pointer input — the samples, local to
    /// <see cref="Origin"/>, each with its time — and nothing else: the ink is a filled outline
    /// that <see cref="FreehandStroke"/> rebuilds from them, a pure function, so what you saw while
    /// drawing is exactly what gets saved and a lift never changes the stroke. The outline is local
    /// to the origin, so Move is a translation of the origin and the geometry survives it. There
    /// are no handles (nothing to resize without re-widening the ink); the selection chrome is a
    /// dashed marquee, like text.
    ///
    /// The outline is kept by a <see cref="FreehandStrokeBuilder"/> that only redoes the few points
    /// at the tip per pointer event, whether the stroke is being drawn or was just loaded (a cold
    /// rebuild is the same builder fed everything at once), and is drawn as its two pieces: the
    /// settled chunks and the tail. Cache slots: Geometry = the tail, SecondaryGeometry = the
    /// settled group, both local to Origin; a null Geometry slot means "ask the builder again".
    /// </summary>
    [GraphicDesc("Brush Stroke", Skills = Skill.Color | Skill.Stroke)]
    public class GraphicBrush : GraphicBase, IIncrementalShadow
    {
        /// <summary>One raw pointer sample, relative to <see cref="Origin"/>, with its time in
        /// milliseconds since the stroke began. Raw on purpose: smoothing and pressure are recomputed
        /// from these by <see cref="FreehandStroke"/>, so the live stroke and the saved stroke are
        /// the same function of the same input.</summary>
        public record struct Sample(Point P, double T);

        /// <summary>The mouse-down point; the samples are offsets from it.</summary>
        public Point Origin
        {
            get => _origin;
            set => Set(ref _origin, value);
        }

        /// <summary>The persisted samples — the live array, edit it in place and raise. While the
        /// brush tool is still drawing this stroke they lag the ones being collected, which only
        /// <see cref="GetSamples"/> / <see cref="SampleCount"/> see until <see cref="EndStroke"/>.</summary>
        public Sample[] Samples
        {
            get => _samples;
            set
            {
                _building = null;
                _stroke = null;
                Set(ref _samples, value ?? Array.Empty<Sample>());
            }
        }

        private Point _origin;
        private Sample[] _samples = Array.Empty<Sample>();

        // not persisted by GraphicsSerializer: the stroke being drawn grows here (amortized
        // appends) and is published to _samples once by EndStroke, so a long stroke is not
        // reallocated per sample. Only the brush tool adds samples, and nothing commits history
        // mid-drag, so the persisted array lagging the live one is never observed.
        [Transient] private List<Sample> _building;

        // the incremental outline (see the class doc); dropped whenever the samples are replaced
        // rather than appended to, and compacted to its geometries once the stroke is finished
        [Transient] private IInkOutline _stroke;

        // the in-place shadow sprite while the stroke is being drawn (see BakeShadowIncrementally)
        [Transient] private BrushShadowBaker _shadowBaker;

        // the hover outline: the stroke's pieces unioned, so chunk seams and self-crossings are not
        // outlined. A path-op over the whole stroke, so it is kept for the stroke it was made from.
        [Transient] private Geometry _hoverOutline;
        [Transient] private IInkOutline _hoverOutlineStroke;
        [Transient] private int _hoverOutlineSamples;

        protected GraphicBrush() // serializer constructor
        { }

        public GraphicBrush(DrawingCanvas canvas, Point origin)
            : this(canvas.ObjectColor, canvas.LineWidth, origin)
        { }

        public GraphicBrush(Color objectColor, double lineWidth, Point origin)
            : base(objectColor, lineWidth)
        {
            _origin = origin;
            _samples = new[] { new Sample(default, 0) };
        }

        /// <summary>
        /// perfect-freehand size per unit of stroke width. With thinning 0.5 the ink runs from
        /// 0.5·size (at speed) to 1.5·size (at rest), so at 3 a fast stroke is 1.5·LineWidth, a slow
        /// one 4.5·LineWidth and a click dots 3·LineWidth across. It was 2, which put a quick stroke
        /// at exactly LineWidth: since most of a hand-drawn line is quick, the brush read noticeably
        /// thinner than a shape outlined at the same width.
        /// </summary>
        internal const double SizePerLineWidth = 3;

        /// <summary>The perfect-freehand size: <see cref="SizePerLineWidth"/> times the stroke width.</summary>
        internal virtual double Size => Math.Max(1, SizePerLineWidth * LineWidth);

        /// <summary>A fresh, empty outline for the current <see cref="Size"/>: the ink's shape.</summary>
        internal virtual IInkOutline CreateOutline() => new FreehandStrokeBuilder(Size);

        internal int SampleCount => _building?.Count ?? _samples.Length;

        internal ReadOnlySpan<Sample> GetSamples() =>
            _building != null ? CollectionsMarshal.AsSpan(_building) : _samples;

        internal override void DeclarePropertyEffects(Dictionary<string, InvalidationAspects> map)
        {
            base.DeclarePropertyEffects(map);
            map[nameof(Origin)] = InvalidationAspects.Bounds; // the outline is local: only where it sits changes
            map[nameof(Samples)] = InvalidationAspects.Bounds | InvalidationAspects.Geometry | InvalidationAspects.Shadow;
        }

        // the geometry is local to the origin, so a translation stales nothing (see Move)
        internal override InvalidationAspects TranslationAspects => InvalidationAspects.None;

        // ---- capture API (ToolBrush) ----------------------------------------------------------

        /// <summary>
        /// Appends a pointer sample (canvas coordinates, milliseconds since the stroke began).
        /// A sample closer than <paramref name="dedupeDistance"/> to the previous one is dropped —
        /// sub-pixel jitter adds points, not shape — and time never runs backwards. True when a
        /// sample was added. A caller adding several per pointer event passes
        /// <paramref name="notify"/> false and raises once with <see cref="NotifySamplesChanged"/>:
        /// every raise is a cache clear plus the collection funnel, and one per event is enough.
        /// </summary>
        internal bool AddSample(Point canvasPoint, double tMs, double dedupeDistance, bool notify = true)
        {
            var building = _building ??= new List<Sample>(_samples);
            var local = new Point(canvasPoint.X - _origin.X, canvasPoint.Y - _origin.Y);

            if (building.Count > 0)
            {
                var last = building[building.Count - 1];
                if (GraphicLine.Distance(local, last.P) < dedupeDistance)
                    return false;
                if (tMs < last.T)
                    tMs = last.T;
            }

            building.Add(new Sample(local, tMs));
            if (notify)
                NotifySamplesChanged();
            return true;
        }

        /// <summary>Raises for samples appended with <c>notify: false</c>.</summary>
        internal void NotifySamplesChanged() => OnPropertyChanged(nameof(Samples));

        /// <summary>Publishes the collected samples as the persisted array. The outline is the
        /// same function of the same points, so the cached geometry stays — no rebuild, no
        /// re-bake, and nothing on screen changes. The builder keeps only its geometries from
        /// here (a finished stroke's builder is a cache) and the in-progress shadow goes; the
        /// drag-end validation re-bakes the sprite at rest.</summary>
        internal void EndStroke()
        {
            if (_building == null)
                return;

            _samples = _building.ToArray();
            _building = null;
            _shadowBaker = null;
            _stroke?.Compact();
        }

        // ---- handles: none --------------------------------------------------------------------

        internal override int HandleCount => 0;

        internal override Point GetHandle(int handleNumber, DpiScale uiscale) => _origin;

        internal override Cursor GetHandleCursor(int handleNumber) => HelperFunctions.DefaultCursor;

        internal override void MoveHandleTo(Point point, int handleNumber)
        { }

        // ---- hit testing ----------------------------------------------------------------------

        private Point ToLocal(Point point) => new Point(point.X - _origin.X, point.Y - _origin.Y);

        internal override bool Contains(Point point) => GetOutline().FillContains(ToLocal(point));

        // the ink itself, plus a margin so a thin, fast stroke stays clickable
        internal override int MakeHitTest(Point point, DpiScale uiscale)
        {
            var local = ToLocal(point);
            var stroke = GetOutline();
            if (stroke.FillContains(local) ||
                stroke.StrokeContains(RenderResources.GetPen(Colors.Black, 8 * uiscale.DpiScaleX), local))
                return 0;

            return -1;
        }

        // PORT NOTE (_translating fast path): pure translation offsets the cached bounds once and
        // — because the outline is local to the origin — clears nothing (TranslationAspects).
        internal override void Move(double deltaX, double deltaY)
        {
            _translating = true;
            try
            {
                _origin = new Point(_origin.X + deltaX, _origin.Y + deltaY);
                RenderCache.TranslateCachedBounds(deltaX, deltaY);
                OnPropertyChanged();
            }
            finally
            {
                _translating = false;
            }
        }

        internal override void OnFieldsRestored(IReadOnlyCollection<string> changedJsonNames)
        {
            base.OnFieldsRestored(changedJsonNames);
            if (changedJsonNames.Contains("samples"))
            {
                _building = null;
                _stroke = null;
            }
        }

        internal override void TrimTransientCaches()
        {
            base.TrimTransientCaches();
            _stroke = null;
            _shadowBaker = null;
        }

        // ---- geometry / rendering -------------------------------------------------------------

        /// <summary>
        /// The outline, current for the samples: the builder is fed what was appended since it
        /// last looked (everything, cold), and a stroke width change or a samples replacement
        /// starts a new one. Fills the cache slots as a side effect, like any lazy geometry.
        /// </summary>
        internal IInkOutline GetOutline()
        {
            if (RenderCache.Geometry != null && _stroke != null)
                return _stroke;

            var samples = GetSamples();
            if (_stroke == null || _stroke.Size != Size || !_stroke.CanAppend(samples.Length))
                _stroke = CreateOutline();

            _stroke.Update(samples);
            RenderCache.Geometry = _stroke.Tail;
            RenderCache.SecondaryGeometry = _stroke.Settled;
            return _stroke;
        }

        /// <summary><see cref="GetOutline"/> as the brush's own builder, for what needs its
        /// incremental internals (the shadow baker). Only for a plain brush stroke.</summary>
        internal FreehandStrokeBuilder GetStroke() => (FreehandStrokeBuilder)GetOutline();

        protected override Rect ComputeBounds()
        {
            if (SampleCount == 0)
            {
                // nothing to outline (a damaged document): the nominal footprint around the origin
                var size = Size;
                return new Rect(_origin.X - size / 2, _origin.Y - size / 2, size, size);
            }

            return GetOutline().Bounds.Translate(new Vector(_origin.X, _origin.Y));
        }

        internal override void DrawObject(DrawingContext ctx)
        {
            if (SampleCount == 0)
                return;

            var stroke = GetOutline();
            using (ctx.PushTransform(Matrix.CreateTranslation(_origin.X, _origin.Y)))
            {
                if (stroke.Settled == null)
                {
                    ctx.DrawGeometry(RenderResources.GetBrush(ObjectColor), null, stroke.Tail);
                }
                else if (ObjectColor.A == 255)
                {
                    ctx.DrawGeometry(RenderResources.GetBrush(ObjectColor), null, stroke.Settled);
                    ctx.DrawGeometry(RenderResources.GetBrush(ObjectColor), null, stroke.Tail);
                }
                else
                {
                    // the tail overlaps the settled chunks (see FreehandStrokeBuilder): translucent
                    // ink is painted opaque into one layer over the stroke's bounds, masked to the
                    // ink's alpha, so the overlap does not show through darker. (PushOpacity is
                    // not a layer by default — it scales each draw's paint — and its layer form
                    // has no bounds; the mask form is a bounded layer.)
                    var opaque = RenderResources.GetBrush(Color.FromRgb(ObjectColor.R, ObjectColor.G, ObjectColor.B));
                    var mask = RenderResources.GetBrush(Color.FromArgb(ObjectColor.A, 255, 255, 255));
                    using (ctx.PushOpacityMask(mask, stroke.Bounds.Inflate(1)))
                    {
                        ctx.DrawGeometry(opaque, null, stroke.Settled);
                        ctx.DrawGeometry(opaque, null, stroke.Tail);
                    }
                }
            }
        }

        internal override void DrawHoverOutline(DrawingContext ctx, IPen pen)
        {
            if (SampleCount == 0)
                return;

            var stroke = GetOutline();
            if (_hoverOutline == null || _hoverOutlineStroke != stroke || _hoverOutlineSamples != stroke.SampleCount)
            {
                _hoverOutline = new CombinedGeometry(GeometryCombineMode.Union, stroke.Settled ?? stroke.Tail, stroke.Tail);
                _hoverOutlineStroke = stroke;
                _hoverOutlineSamples = stroke.SampleCount;
            }

            using (ctx.PushTransform(Matrix.CreateTranslation(_origin.X, _origin.Y)))
                ctx.DrawGeometry(null, pen, _hoverOutline);
        }

        internal override void Draw(DrawingContext ctx, DpiScale uiscale)
        {
            DrawObject(ctx);

            // no handles to show, so the selection is the text-style marquee
            if (IsSelected)
                DrawDashedBorder(ctx, Bounds.Inflate(2 * uiscale.DpiScaleX), 1 * uiscale.DpiScaleX);
        }

        // ---- shadow while drawing (IIncrementalShadow) ----------------------------------------

        // the outline is local to the origin, so the origin is the point a translation moves and
        // nothing else does — the in-place sprite hangs off it
        internal override Point ShadowAnchor => _origin;

        bool IIncrementalShadow.CanBakeShadowIncrementally => _building != null && SampleCount > 0;

        WriteableBitmap IIncrementalShadow.BakeShadowIncrementally(double zoomBucket, int maxDimension,
                                                                   out Vector originFromAnchor, out double bakeScale)
        {
            _shadowBaker ??= new BrushShadowBaker(this);
            return _shadowBaker.Bake(zoomBucket, maxDimension, out originFromAnchor, out bakeScale);
        }
    }
}
