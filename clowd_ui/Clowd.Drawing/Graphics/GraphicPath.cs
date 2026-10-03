using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Clowd.Drawing.Rendering;

namespace Clowd.Drawing.Graphics
{
    /// <summary>
    /// A bezier path drawn with the pen tool: a run of <see cref="PathAnchor"/>s, each with two
    /// handle offsets, stroked as one open or closed figure. The anchors ARE the geometry — there
    /// is no bounding box to resize or rotate in v1 (follow-up: an Angle like the rectangles) —
    /// so Move shifts every anchor and the handles ride along for free.
    ///
    /// Handles are numbered in three runs of n (1-based): anchor i is 1+i, its In handle n+1+i,
    /// its Out handle 2n+1+i. In/Out handles that are absent (shorter than
    /// <see cref="MinHandleLength"/>) are neither drawn nor hit-tested. Anchors draw as squares so
    /// they read apart from the round handle dots and the round resize handles elsewhere.
    ///
    /// Two transients drive the pen tool's chrome: <see cref="PreviewPoint"/> is the cursor while
    /// the path is being extended (a half-alpha rubber band from the last anchor), and
    /// <see cref="ActiveAnchor"/> is the anchor last clicked or dragged (Delete/Backspace removes it).
    ///
    /// Cache slots: Geometry = the open/closed cubic figure shared by Bounds/Contains/DrawObject.
    /// </summary>
    [GraphicDesc("Path", Skills = Skill.Color | Skill.Stroke | Skill.DashStyle)]
    public class GraphicPath : GraphicBase
    {
        /// <summary>Handles shorter than this (canvas units) count as absent.</summary>
        internal const double MinHandleLength = 0.5;

        // chrome sizes in screen px (multiplied by uiscale.DpiScaleX at use)
        internal const double AnchorSize = 7;
        internal const double EndpointSize = 9;
        internal const double HandleDotRadius = 3;

        /// <summary>How far (screen px) the pointer must travel from a press before it is a drag.</summary>
        internal const double ClickThreshold = 3;

        internal enum HandleKind
        {
            Anchor = 0,
            In = 1,
            Out = 2,
        }

        public PathAnchor[] Anchors
        {
            get => _anchors;
            set => Set(ref _anchors, value ?? Array.Empty<PathAnchor>());
        }

        public bool Closed
        {
            get => _closed;
            set => Set(ref _closed, value);
        }

        private PathAnchor[] _anchors = Array.Empty<PathAnchor>();
        private bool _closed;

        // not persisted by GraphicsSerializer
        [Transient] private Point? _previewPoint;
        [Transient] private int _activeAnchor = -1;
        [Transient] private bool _mirrorNext;

        protected GraphicPath() // serializer constructor
        { }

        public GraphicPath(DrawingCanvas canvas, Point first)
            : this(canvas.ObjectColor, canvas.LineWidth, first)
        {
            DashStyle = canvas.DashStyle;
        }

        public GraphicPath(Color objectColor, double lineWidth, Point first)
            : base(objectColor, lineWidth)
        {
            _anchors = new[] { PathAnchor.Corner(first) };
        }

        /// <summary>The cursor while the pen is extending this path (rubber band + close
        /// affordance); null otherwise. Set only by ToolPen.</summary>
        internal Point? PreviewPoint
        {
            get => _previewPoint;
            set => Set(ref _previewPoint, value);
        }

        /// <summary>Index of the anchor last clicked or dragged, or -1. Drawn filled; the editor's
        /// Delete/Backspace removes it (see <see cref="TryRemoveActiveAnchor"/>).</summary>
        internal int ActiveAnchor => _activeAnchor;

        internal int AnchorCount => _anchors.Length;

        internal PathAnchor LastAnchor => _anchors[_anchors.Length - 1];

        public override bool IsSelected
        {
            get => base.IsSelected;
            set
            {
                base.IsSelected = value;
                if (!value)
                {
                    // nothing of the editing chrome survives a deselection
                    _activeAnchor = -1;
                    _previewPoint = null;
                }
            }
        }

        internal override void DeclarePropertyEffects(Dictionary<string, InvalidationAspects> map)
        {
            base.DeclarePropertyEffects(map);
            const InvalidationAspects shape = InvalidationAspects.Bounds | InvalidationAspects.Geometry | InvalidationAspects.Shadow;
            map[nameof(Anchors)] = shape;
            map[nameof(Closed)] = shape;
            map[nameof(PreviewPoint)] = InvalidationAspects.None; // chrome only; the raise still schedules a redraw
            map[nameof(ActiveAnchor)] = InvalidationAspects.None;
        }

        // ---- handle numbering ------------------------------------------------------------

        internal override int HandleCount => 3 * _anchors.Length;

        internal static int AnchorOf(int handleNumber, int anchorCount) =>
            anchorCount == 0 ? -1 : (handleNumber - 1) % anchorCount;

        internal static HandleKind KindOf(int handleNumber, int anchorCount) =>
            anchorCount == 0 ? HandleKind.Anchor : (HandleKind)((handleNumber - 1) / anchorCount);

        internal int AnchorHandle(int index) => 1 + index;

        internal int InHandle(int index) => _anchors.Length + 1 + index;

        internal int OutHandle(int index) => 2 * _anchors.Length + 1 + index;

        /// <summary>True for the anchor handle at either end of an OPEN path — the ones a click
        /// continues the path from.</summary>
        internal bool IsEndpointHandle(int handleNumber)
        {
            var n = _anchors.Length;
            if (_closed || handleNumber < 1 || handleNumber > n)
                return false;
            return handleNumber == 1 || handleNumber == n;
        }

        internal override Point GetHandle(int handleNumber, DpiScale uiscale)
        {
            var n = _anchors.Length;
            var a = _anchors[AnchorOf(handleNumber, n)];
            return KindOf(handleNumber, n) switch
            {
                HandleKind.In => a.P + a.In,
                HandleKind.Out => a.P + a.Out,
                _ => a.P,
            };
        }

        // the end anchors of an open path offer to continue it; everything else just drags
        internal override Cursor GetHandleCursor(int handleNumber) =>
            IsEndpointHandle(handleNumber) ? CursorResources.Pen : CursorResources.SizeAll;

        internal override int MakeHitTest(Point point, DpiScale uiscale)
        {
            var n = _anchors.Length;
            if (IsSelected && n > 0)
            {
                // handles before anchors: a handle dot that overlaps its own anchor square must stay
                // grabbable, and an absent handle sits ON the anchor so it is skipped entirely
                for (int i = 0; i < n; i++)
                {
                    if (_anchors[i].HasOut && GetHandleRectangle(OutHandle(i), uiscale).Contains(point))
                        return OutHandle(i);
                    if (_anchors[i].HasIn && GetHandleRectangle(InHandle(i), uiscale).Contains(point))
                        return InHandle(i);
                }

                for (int i = 0; i < n; i++)
                    if (GetHandleRectangle(AnchorHandle(i), uiscale).Contains(point))
                        return AnchorHandle(i);
            }

            // decision #25: StrokeContains against a widened corridor, like the line and the pencil
            if (n >= 2 && GetGeometry().StrokeContains(RenderResources.GetPen(Colors.Black, LineWidth + 8 * uiscale.DpiScaleX), point))
                return 0;

            return -1;
        }

        // min-8px hit corridor, the GraphicLine idiom (no dpi available here)
        internal override bool Contains(Point point) =>
            _anchors.Length >= 2 &&
            GetGeometry().StrokeContains(RenderResources.GetPen(Colors.Black, Math.Max(LineWidth, 8)), point);

        /// <summary>True while the pointer sits on anchor 0 of an open path with at least two
        /// anchors — the next click closes the path.</summary>
        internal bool IsNearFirstAnchor(Point point, DpiScale uiscale) =>
            _anchors.Length >= 2 && !_closed && GetHandleRectangle(1, uiscale).Contains(point);

        // ---- editing ---------------------------------------------------------------------

        internal override void MoveHandleTo(Point point, int handleNumber) =>
            MoveHandleTo(point, handleNumber, KeyModifiers.None);

        /// <summary>
        /// Drags a handle. An anchor handle translates the anchor (its handles are offsets, so they
        /// follow). An In/Out handle is re-aimed at the pointer: Shift snaps it to 45°; Alt moves
        /// that side alone and makes the anchor a corner; otherwise a smooth anchor turns the
        /// opposite handle to stay collinear (keeping its length) — or, right after an Alt-press
        /// on the anchor itself (<see cref="ResolveGrab"/>), mirrors it exactly.
        /// </summary>
        internal void MoveHandleTo(Point point, int handleNumber, KeyModifiers modifiers)
        {
            var n = _anchors.Length;
            var index = AnchorOf(handleNumber, n);
            var kind = KindOf(handleNumber, n);
            ref var a = ref _anchors[index];

            if (kind == HandleKind.Anchor)
            {
                a.P = point;
            }
            else
            {
                var v = point - a.P;
                if ((modifiers & KeyModifiers.Shift) != 0)
                    v = PathMath.Snap45(v);

                if (_mirrorNext)
                {
                    // the fresh-handles gesture: both sides come from this one drag
                    a.Smooth = true;
                    if (kind == HandleKind.Out) { a.Out = v; a.In = -v; }
                    else { a.In = v; a.Out = -v; }
                }
                else if ((modifiers & KeyModifiers.Alt) != 0)
                {
                    a.Smooth = false;
                    if (kind == HandleKind.Out) a.Out = v;
                    else a.In = v;
                }
                else if (kind == HandleKind.Out)
                {
                    a.Out = v;
                    if (a.Smooth) a.In = PathMath.Collinear(a.In, v);
                }
                else
                {
                    a.In = v;
                    if (a.Smooth) a.Out = PathMath.Collinear(a.Out, v);
                }
            }

            _activeAnchor = index;
            OnPropertyChanged(nameof(Anchors)); // in-place edit; one named raise per pointer event
        }

        /// <summary>
        /// Rewrites a grabbed handle before a drag begins, the GraphicCount.BadgeHandle idiom:
        /// Alt on an anchor pulls a fresh pair of mirrored handles out of it (Illustrator's
        /// "convert anchor" gesture), so the drag drives its Out handle with the In handle kept as
        /// the exact negation until the gesture ends (Normalize clears the flag). Everything else
        /// is returned unchanged.
        /// </summary>
        internal int ResolveGrab(int handleNumber, KeyModifiers modifiers)
        {
            var n = _anchors.Length;
            if ((modifiers & KeyModifiers.Alt) != 0 && KindOf(handleNumber, n) == HandleKind.Anchor)
            {
                _mirrorNext = true;
                return OutHandle(AnchorOf(handleNumber, n));
            }

            return handleNumber;
        }

        /// <summary>Marks the anchor a grabbed handle belongs to as the active one.</summary>
        internal void SetActiveAnchor(int handleNumber)
        {
            var index = AnchorOf(handleNumber, _anchors.Length);
            if (index == _activeAnchor)
                return;
            _activeAnchor = index;
            OnPropertyChanged(nameof(ActiveAnchor));
        }

        internal void ClearActiveAnchor()
        {
            if (_activeAnchor < 0)
                return;
            _activeAnchor = -1;
            OnPropertyChanged(nameof(ActiveAnchor));
        }

        /// <summary>
        /// Removes the active anchor, keeping its neighbours' handles as they are. False when
        /// there is no active anchor or only two anchors remain — the editor's Delete then deletes
        /// the whole graphic, which is what "fewer than two" means for a path.
        /// </summary>
        internal bool TryRemoveActiveAnchor()
        {
            if (_activeAnchor < 0 || _anchors.Length <= 2)
                return false;

            var list = new List<PathAnchor>(_anchors);
            list.RemoveAt(_activeAnchor);
            _anchors = list.ToArray();
            _activeAnchor = -1;
            OnPropertyChanged(nameof(Anchors));
            return true;
        }

        // PORT NOTE (_translating fast path): pure translation offsets the cached bounds once and
        // clears only the Geometry aspect. The handles are offsets, so only P moves.
        internal override void Move(double deltaX, double deltaY)
        {
            _translating = true;
            try
            {
                for (int i = 0; i < _anchors.Length; i++)
                    _anchors[i].P = new Point(_anchors[i].P.X + deltaX, _anchors[i].P.Y + deltaY);
                RenderCache.TranslateCachedBounds(deltaX, deltaY);
                OnPropertyChanged();
            }
            finally
            {
                _translating = false;
            }
        }

        /// <summary>Drops handles too short to count to exactly zero and ends any fresh-handles
        /// gesture. Runs after every drag and on every history restore.</summary>
        internal override void Normalize()
        {
            _mirrorNext = false;

            bool changed = false;
            for (int i = 0; i < _anchors.Length; i++)
            {
                ref var a = ref _anchors[i];
                if (!a.HasIn && a.In != default)
                {
                    a.In = default;
                    changed = true;
                }

                if (!a.HasOut && a.Out != default)
                {
                    a.Out = default;
                    changed = true;
                }
            }

            if (changed)
                OnPropertyChanged(nameof(Anchors));
        }

        internal override void OnFieldsRestored(IReadOnlyCollection<string> changedJsonNames)
        {
            base.OnFieldsRestored(changedJsonNames);
            _previewPoint = null;
            _activeAnchor = -1;
            _mirrorNext = false;
        }

        // ---- creation API (ToolPen) --------------------------------------------------------

        internal void AppendAnchor(PathAnchor anchor)
        {
            var n = _anchors.Length;
            Array.Resize(ref _anchors, n + 1);
            _anchors[n] = anchor;
            _activeAnchor = n;
            OnPropertyChanged(nameof(Anchors));
        }

        internal void RemoveLastAnchor()
        {
            var n = _anchors.Length;
            if (n == 0)
                return;
            Array.Resize(ref _anchors, n - 1);
            if (_activeAnchor >= n - 1)
                _activeAnchor = -1;
            OnPropertyChanged(nameof(Anchors));
        }

        /// <summary>Walks the path the other way (In/Out swapped), so extending from the first
        /// anchor is the same code as extending from the last. Applied twice it is the identity.</summary>
        internal void ReverseDirection()
        {
            _anchors = PathMath.Reverse(_anchors);
            if (_activeAnchor >= 0)
                _activeAnchor = _anchors.Length - 1 - _activeAnchor;
            OnPropertyChanged(nameof(Anchors));
        }

        /// <summary>
        /// The press-and-drag that shapes a newly placed anchor: the dragged side follows the
        /// cursor (Shift snaps it) and, unless Alt is held, the other side mirrors it and the anchor
        /// becomes smooth. With Alt only the dragged side is set and the anchor is a corner (a cusp).
        /// <paramref name="incoming"/> is the closing drag onto anchor 0, which shapes its In handle.
        /// </summary>
        internal void SetHandlesFromDrag(int index, Point cursor, KeyModifiers modifiers, bool incoming = false)
        {
            ref var a = ref _anchors[index];
            var v = cursor - a.P;
            if ((modifiers & KeyModifiers.Shift) != 0)
                v = PathMath.Snap45(v);

            var mirror = (modifiers & KeyModifiers.Alt) == 0;
            if (incoming)
            {
                a.In = v;
                if (mirror) a.Out = -v;
            }
            else
            {
                a.Out = v;
                if (mirror) a.In = -v;
            }

            a.Smooth = mirror;
            _activeAnchor = index;
            OnPropertyChanged(nameof(Anchors));
        }

        /// <summary>Smooth ↔ corner. A corner loses both handles; a smooth anchor gets Inkscape-style
        /// auto handles along its neighbours' chord (wrapping on a closed path).</summary>
        internal void ToggleAnchorType(int index)
        {
            var n = _anchors.Length;
            ref var a = ref _anchors[index];
            if (a.Smooth)
            {
                a.Smooth = false;
                a.In = default;
                a.Out = default;
            }
            else
            {
                Point? prev = index > 0 ? _anchors[index - 1].P : (_closed && n > 1 ? _anchors[n - 1].P : null);
                Point? next = index < n - 1 ? _anchors[index + 1].P : (_closed && n > 1 ? _anchors[0].P : null);
                (a.In, a.Out) = PathMath.AutoHandles(prev, a.P, next);
                a.Smooth = true;
            }

            _activeAnchor = index;
            OnPropertyChanged(nameof(Anchors));
        }

        /// <summary>Double-click: finishes the path if the pen is extending it (an untouched
        /// continuation falls through), otherwise toggles the anchor under the pointer between
        /// smooth and corner.</summary>
        internal override void Activate(DrawingCanvas canvas, Point point)
        {
            var pen = canvas.ToolPen;

            // the second press of the double-click whose first press closed the path on anchor 0:
            // that gesture finished the path; it must not also toggle the anchor it landed on
            if (pen.ConsumeJustClosed(this))
                return;

            if (pen.IsExtending(this))
            {
                var handedOver = pen.ContinuedFromPointer;
                if (pen.Finish(canvas))
                    return;

                // an untouched continuation that the pointer tool handed over on the first press
                // of this double-click: the user is toggling the anchor, not switching tools
                if (handedOver)
                    canvas.Tool = ToolType.Pointer;
            }

            var n = _anchors.Length;
            var hit = MakeHitTest(point, canvas.CanvasUiElementScale);
            if (hit > 0 && KindOf(hit, n) == HandleKind.Anchor)
            {
                ToggleAnchorType(AnchorOf(hit, n));
                canvas.AddCommandToHistory(false);
            }

            // follow-up: a double-click on a segment should insert an anchor there (PathMath.Split
            // is in place); bounding-box scaling/rotation of a path is also still to come
        }

        // ---- geometry / rendering --------------------------------------------------------

        internal Geometry GetGeometry() => RenderCache.Geometry ??= PathMath.Build(_anchors, _closed);

        // PORT NOTE (ComputeBounds): the measuring pen carries the ink's round caps and joins —
        // round caps extend half the stroke width past each open end
        protected override Rect ComputeBounds()
        {
            if (_anchors.Length < 2)
            {
                // a lone anchor (mid-creation only): just its stroke footprint
                var p = _anchors.Length == 1 ? _anchors[0].P : default;
                var half = LineWidth / 2;
                return new Rect(p.X - half, p.Y - half, LineWidth, LineWidth);
            }

            var pen = RenderResources.GetPen(default, LineWidth, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
            return GetGeometry().GetRenderBounds(pen);
        }

        private ImmutablePen InkPen =>
            RenderResources.GetPen(ObjectColor, LineWidth, StrokeDash, PenLineCap.Round, PenLineJoin.Round);

        internal override void DrawObject(DrawingContext ctx)
        {
            if (_anchors.Length < 2)
                return; // a lone anchor exists only mid-creation; nothing to export

            ctx.DrawGeometry(null, InkPen, GetGeometry());
        }

        internal override void Draw(DrawingContext ctx, DpiScale uiscale)
        {
            DrawObject(ctx);

            if (_previewPoint is { } cursor && _anchors.Length >= 1 && !_closed)
                DrawRubberBand(ctx, cursor, uiscale);

            if (IsSelected)
                DrawTrackers(ctx, uiscale);
        }

        // the segment the next click would add, at half alpha, with a hollow dot at the cursor;
        // when the cursor is over anchor 0 that anchor becomes a ring to say "click to close"
        private void DrawRubberBand(DrawingContext ctx, Point cursor, DpiScale uiscale)
        {
            var dpi = uiscale.DpiScaleX;
            var last = LastAnchor;
            var c = ObjectColor;
            var pen = RenderResources.GetPen(Color.FromArgb((byte)(c.A / 2), c.R, c.G, c.B), LineWidth, StrokeDash,
                                             PenLineCap.Round, PenLineJoin.Round);

            if (last.HasOut)
            {
                var band = new StreamGeometry();
                using (var gctx = band.Open())
                {
                    gctx.BeginFigure(last.P, false);
                    gctx.CubicBezierTo(last.P + last.Out, cursor, cursor);
                    gctx.EndFigure(false);
                }

                ctx.DrawGeometry(null, pen, band);
            }
            else
            {
                ctx.DrawLine(pen, last.P, cursor);
            }

            var chrome = RenderResources.GetPen(HandleColor, 1 * dpi);
            var radius = HandleDotRadius * dpi;
            ctx.DrawEllipse(null, chrome, cursor, radius, radius);

            if (IsNearFirstAnchor(cursor, uiscale))
            {
                var ring = EndpointSize * dpi / 2;
                ctx.DrawEllipse(HandleBrush2, RenderResources.GetPen(HandleColor, 2 * dpi), _anchors[0].P, ring, ring);
            }
        }

        // handle stems and dots first, then the anchor squares on top (endpoints of an open path a
        // size larger; the active anchor filled)
        protected override void DrawTrackers(DrawingContext ctx, DpiScale uiscale)
        {
            var dpi = uiscale.DpiScaleX;
            var stem = RenderResources.GetPen(HandleColor, 1 * dpi);
            var ring = RenderResources.GetPen(Colors.White, 1 * dpi);
            var dot = HandleDotRadius * dpi;
            var n = _anchors.Length;

            for (int i = 0; i < n; i++)
            {
                var a = _anchors[i];
                if (a.HasIn)
                    DrawHandleDot(ctx, a.P, a.P + a.In, stem, ring, dot);
                if (a.HasOut)
                    DrawHandleDot(ctx, a.P, a.P + a.Out, stem, ring, dot);
            }

            for (int i = 0; i < n; i++)
            {
                var p = _anchors[i].P;
                var endpoint = !_closed && (i == 0 || i == n - 1);
                var size = (endpoint ? EndpointSize : AnchorSize) * dpi;
                var rect = new Rect(p.X - size / 2, p.Y - size / 2, size, size);
                ctx.DrawRectangle(i == _activeAnchor ? HandleBrush : HandleBrush2, stem, rect);
            }
        }

        private static void DrawHandleDot(DrawingContext ctx, Point anchor, Point tip, IPen stem, IPen ring, double radius)
        {
            ctx.DrawLine(stem, anchor, tip);
            ctx.DrawEllipse(HandleBrush, ring, tip, radius, radius);
        }

        // the accent color behind HandleBrush, for the pen cache (handles are drawn per frame)
        private static Color HandleColor => HandleBrush is ISolidColorBrush solid ? solid.Color : Color.FromRgb(0, 0, 255);
    }
}
