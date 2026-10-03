using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using Clowd.Drawing.Rendering;

namespace Clowd.Drawing.Graphics
{
    /// <summary>
    /// A numbered step badge: a white circle (a capsule once the number is wider than it is tall)
    /// ringed in the object color with the number in black, plus an optional pointer arrow whose
    /// base sits at the badge's center. Badge and arrow are ONE graphic, so they move, select and
    /// delete together, and the drop shadow is baked from their joint silhouette — the badge's
    /// shadow never falls across its own arrow, which visibly starts at the ring.
    ///
    /// The arrow is stored as <see cref="ArrowOffset"/>, the tip relative to the badge center, so
    /// translation needs no bookkeeping. (0,0) — the field default, and what documents from before
    /// the arrow was part of the badge load as — means no arrow. Badges never rotate: the
    /// inherited Angle is ignored when drawing and reset to 0 by Normalize.
    ///
    /// Two handles, like a line's two ends: <see cref="ArrowHandle"/> at the arrow tip, and
    /// <see cref="BadgeHandle"/> on the rim point facing away from it, which drags the badge while
    /// the tip stays put. Without an arrow they sit on the right and left of the rim.
    ///
    /// Cache slots: Geometry = badge outline (hit-testing and the "inside the badge" tests),
    /// SecondaryGeometry = arrow head, TertiaryGeometry = arrow shaft. Text = the number.
    /// </summary>
    [GraphicDesc("Step Count", Skills = Skill.Stroke | Skill.Color | Skill.Font)]
    public class GraphicCount : GraphicText
    {
        /// <summary>The arrow tip (or, with no arrow, the rim point an arrow is pulled out from).</summary>
        internal const int ArrowHandle = 1;

        /// <summary>The rim point facing away from the arrow tip; drags the badge, tip anchored.</summary>
        internal const int BadgeHandle = 2;

        // badge size, from the font size and the ring width only — never from the font's line
        // metrics, which vary between faces — so the white space around the number keeps the same
        // proportion at any size. The inner (white) diameter is this many font sizes...
        private const double InnerDiameterFactor = 1.9;

        // ...and a number too wide for that circle stretches the badge into a capsule, keeping this
        // much white (in font sizes) beside it on each side
        private const double PadXFactor = 0.55;

        public Point ArrowOffset
        {
            get => _arrowOffset;
            set => Set(ref _arrowOffset, value);
        }

        private Point _arrowOffset; // absent from JSON → (0,0) = no arrow

        protected GraphicCount()
        { }

        public GraphicCount(DrawingCanvas canvas, Point center, string body = null)
            : this(canvas.ObjectColor, canvas.LineWidth, center, body)
        {
            // each font change re-measures the badge around its center (see Normalize)
            FontName = canvas.TextFontFamilyName;
            FontSize = canvas.TextFontSize;
            FontStretch = canvas.TextFontStretch;
            FontStyle = canvas.TextFontStyle;
            FontWeight = canvas.TextFontWeight;
        }

        public GraphicCount(Color objectColor, double lineWidth, Point center, string body = null)
            : base(objectColor, lineWidth, center, 0, body ?? "#")
        {
            var c = Center;
            Move(center.X - c.X, center.Y - c.Y);
        }

        // the ring width is part of the badge size (see Normalize), so a stroke change re-measures it
        public override double LineWidth
        {
            get => base.LineWidth;
            set
            {
                var old = base.LineWidth;
                base.LineWidth = value;
                if (old != value)
                    Normalize();
            }
        }

        internal Point Center => new Point((Left + Right) / 2, (Top + Bottom) / 2);

        /// <summary>The badge's exact rect. UnrotatedBounds rounds to whole units, which would put
        /// the outline off-center from <see cref="Center"/>, where the arrow and text are anchored.</summary>
        internal Rect BadgeRect => new Rect(Left, Top, Right - Left, Bottom - Top);

        internal bool HasArrow => _arrowOffset != default;

        internal Point ArrowTip => Center + new Vector(_arrowOffset.X, _arrowOffset.Y);

        // the badge always casts its shadow, whatever the ring color
        internal override bool HasShadowSurface => true;

        // the color swatch edits the ring, not the number
        internal override string ColorPropertyName => nameof(ObjectColor);

        // the number is always black on the white badge, whatever the ring color
        internal override Color TextColor => Colors.Black;

        /// <summary>Where the number is laid out, in canvas space — the in-place editor sits here.</summary>
        internal Rect TextRect
        {
            get
            {
                var form = CreateFormattedText();
                var c = Center;
                return new Rect(c.X - form.Width / 2, c.Y - form.Height / 2, form.Width, form.Height);
            }
        }

        internal override void DeclarePropertyEffects(Dictionary<string, InvalidationAspects> map)
        {
            base.DeclarePropertyEffects(map);
            map[nameof(ArrowOffset)] = InvalidationAspects.Bounds | InvalidationAspects.Geometry | InvalidationAspects.Shadow;
        }

        internal override int HandleCount => 2;

        internal override Point GetHandle(int handleNumber, DpiScale uiscale)
        {
            var toward = ArrowDirection();
            if (handleNumber == ArrowHandle)
                return HasArrow ? ArrowTip : RimPoint(toward);
            return RimPoint(-toward);
        }

        internal override Cursor GetHandleCursor(int handleNumber) =>
            handleNumber is ArrowHandle or BadgeHandle ? CursorResources.SizeAll : HelperFunctions.DefaultCursor;

        internal override void MoveHandleTo(Point point, int handleNumber)
        {
            if (handleNumber == ArrowHandle)
            {
                // point the arrow at the pointer; a tip dropped back inside the badge removes it
                var c = Center;
                ArrowOffset = GetBadgeGeometry().FillContains(point) ? default : new Point(point.X - c.X, point.Y - c.Y);
            }
            else if (handleNumber == BadgeHandle)
            {
                // the grabbed rim point follows the pointer and the badge turns to keep facing the
                // anchored tip: the new center lies one rim-distance from the pointer, toward the tip
                var tip = HasArrow ? ArrowTip : (Point?)null;
                var toward = ArrowDirection();
                if (tip is { } t)
                {
                    var v = new Vector(t.X - point.X, t.Y - point.Y);
                    if (v.Length > 0)
                        toward = v.Normalize();
                }

                var newCenter = point + toward * RimDistance(-toward);
                var c = Center;
                Move(newCenter.X - c.X, newCenter.Y - c.Y);

                if (tip is { } anchored)
                    ArrowOffset = GetBadgeGeometry().FillContains(anchored) ? default : new Point(anchored.X - newCenter.X, anchored.Y - newCenter.Y);
            }
        }

        internal override int MakeHitTest(Point point, DpiScale uiscale)
        {
            if (IsSelected)
                for (int i = 1; i <= HandleCount; i++)
                    if (GetHandleRectangle(i, uiscale).Contains(point))
                        return i;

            return Contains(point) ? 0 : -1;
        }

        internal override bool Contains(Point point)
        {
            if (IsSelected ? BadgeRect.Contains(point) : GetBadgeGeometry().FillContains(point))
                return true;

            if (!HasArrow)
                return false;

            // a corridor along the arrow, at least 8 units wide so a thin arrow stays clickable
            var corridor = Math.Max(LineWidth * 1.5, 8) / 2;
            return DistanceToSegment(point, Center, ArrowTip) <= corridor;
        }

        protected override Rect ComputeBounds()
        {
            var bounds = BadgeRect;
            if (GetArrowParts(out var shaft, out var head))
                bounds = bounds.Union(ArrowShape.GetBounds(shaft, head, LineWidth, null));
            return bounds;
        }

        internal override void Draw(DrawingContext context, DpiScale uiscale)
        {
            // while the in-place editor is open it shows the number, so the badge draws without it
            DrawObjectImpl(context, !Editing);
            if (IsSelected && !Editing)
                DrawTrackers(context, uiscale);
        }

        internal override void DrawObject(DrawingContext context) => DrawObjectImpl(context, true);

        protected override void DrawObjectImpl(DrawingContext context, bool showText)
        {
            // the arrow goes first: its base is under the white badge, so it visibly starts at the ring
            if (GetArrowParts(out var shaft, out var head))
                ArrowShape.Draw(context, shaft, head, ObjectColor, LineWidth, null);

            // the ring matches the arrow's stem, so badge and arrow read as one weight; inset by
            // half its width since the pen straddles the edge
            var ringWidth = ArrowShape.StemWidth(LineWidth);
            var b = BadgeRect;
            var ring = new Rect(b.Left + ringWidth / 2, b.Top + ringWidth / 2,
                                Math.Max(1, b.Width - ringWidth), Math.Max(1, b.Height - ringWidth));
            var radius = Math.Min(ring.Width, ring.Height) / 2;
            context.DrawRectangle(Brushes.White, RenderResources.GetPen(ObjectColor, ringWidth), ring, radius, radius);

            if (showText)
            {
                var form = CreateFormattedText();
                form.TextAlignment = TextAlignment.Center;
                context.DrawText(form, TextRect.TopLeft);
            }
        }

        /// <summary>
        /// Sizes the badge from the font size and stroke, keeping it centered where it was (so a
        /// step going from 9 to 10 grows both ways instead of drifting right). Never narrower than
        /// tall, so a single digit gets a circle; a wider number stretches it into a capsule.
        /// </summary>
        internal override void Normalize()
        {
            if (Angle != 0)
                Angle = 0; // documents from before badges stopped rotating

            var form = CreateFormattedText();
            var ringWidth = ArrowShape.StemWidth(LineWidth);
            var height = FontSize * InnerDiameterFactor + ringWidth * 2;
            var width = Math.Max(height, form.Width + (FontSize * PadXFactor + ringWidth) * 2);

            var c = Center;
            Left = c.X - width / 2;
            Right = c.X + width / 2;
            Top = c.Y - height / 2;
            Bottom = c.Y + height / 2;
            CenterOfRotation = c;
        }

        /// <summary>Unit vector from the center toward the arrow tip; rightward with no arrow.</summary>
        private Vector ArrowDirection()
        {
            var v = new Vector(_arrowOffset.X, _arrowOffset.Y);
            return v.Length > 0 ? v.Normalize() : new Vector(1, 0);
        }

        private Point RimPoint(Vector direction) => Center + direction * RimDistance(direction);

        /// <summary>
        /// Distance from the center to the badge outline along <paramref name="direction"/> (a unit
        /// vector). The outline is a capsule: every point within half the short side of its central
        /// segment, so the rim is where the distance to that segment reaches that radius — found by
        /// bisection, since a circle's answer is just the radius and a capsule's is close to it.
        /// </summary>
        private double RimDistance(Vector direction)
        {
            var b = BadgeRect;
            var c = Center;
            var radius = Math.Min(b.Width, b.Height) / 2;
            var halfX = b.Width / 2 - radius;
            var halfY = b.Height / 2 - radius;
            var segA = new Point(c.X - halfX, c.Y - halfY);
            var segB = new Point(c.X + halfX, c.Y + halfY);

            double lo = 0, hi = Math.Max(b.Width, b.Height);
            for (int i = 0; i < 32; i++)
            {
                var mid = (lo + hi) / 2;
                if (DistanceToSegment(c + direction * mid, segA, segB) < radius)
                    lo = mid;
                else
                    hi = mid;
            }

            return lo;
        }

        private Geometry GetBadgeGeometry()
        {
            if (RenderCache.Geometry is { } cached)
                return cached;

            var bounds = BadgeRect;
            var radius = Math.Min(bounds.Width, bounds.Height) / 2;
            var geometry = new RectangleGeometry(bounds, radius, radius);
            RenderCache.Geometry = geometry;
            return geometry;
        }

        private bool GetArrowParts(out Geometry shaft, out Geometry head)
        {
            shaft = null;
            head = null;
            if (!HasArrow)
                return false;

            shaft = RenderCache.TertiaryGeometry;
            head = RenderCache.SecondaryGeometry;
            if (head == null)
            {
                // full weight from the start: the shaft meets the badge ring as a solid stem
                ArrowShape.Build(Center, ArrowTip, null, LineWidth, false, out shaft, out head, tapered: false);
                RenderCache.TertiaryGeometry = shaft;
                RenderCache.SecondaryGeometry = head;
            }

            return true;
        }

        private static double DistanceToSegment(Point p, Point a, Point b)
        {
            var ab = new Vector(b.X - a.X, b.Y - a.Y);
            var lengthSquared = ab.X * ab.X + ab.Y * ab.Y;
            var t = lengthSquared > 0 ? Math.Clamp(((p.X - a.X) * ab.X + (p.Y - a.Y) * ab.Y) / lengthSquared, 0, 1) : 0;
            var dx = p.X - (a.X + ab.X * t);
            var dy = p.Y - (a.Y + ab.Y * t);
            return Math.Sqrt(dx * dx + dy * dy);
        }
    }
}
