using System;
using Clowd.Geometry2D;
using Clowd.VideoSDK.Model;
using SkiaSharp;

namespace Clowd.VideoSDK.Composition
{
    /// <summary>
    /// Draws a <see cref="ShapeContent"/> into a box: the composer calls it for every shape item
    /// (inside the rotation, wipe and surround it owns) and the inspector's picker tiles call it
    /// for their previews, so a tile can never drift from what the shape will look like.
    ///
    /// <para><b>Output pixels.</b> Stroke widths, corner radii and the arrow's floors (its least
    /// head length and tail width) are output pixels, so <see cref="Draw"/> scales the canvas by
    /// <c>pxScale</c> (canvas px per output px) and builds everything in output space. A uniform
    /// scale is exact, which is how a preview half the output's size shows the export's picture at
    /// half size rather than a differently proportioned arrow.</para>
    ///
    /// <para><b>Closed kinds</b> fill the box: the vertices are fitted to it (a star or triangle
    /// is stretched until its own bounds are the box), and the outline is stroked <b>inside</b>:
    /// clipped to the outline and stroked at twice the width, so only the inner half shows and the
    /// ink never leaves the box the gizmo draws. A dashed or dotted outline is fitted to the
    /// shape's corners (<see cref="DashOutline"/>, <see cref="DotOutline"/>) rather than repeated
    /// blindly from its start. <b>Line kinds</b> take their endpoints as fractions of the box and
    /// draw with the image editor's arrow geometry (<see cref="ArrowGeometry"/>) and its dashes, so
    /// an arrow here is the image editor's arrow.</para>
    ///
    /// <para><b>Drawing on.</b> The Draw transition (<see cref="TransitionKind.Draw"/>) shows only
    /// part of the path, <c>drawFrom</c> to <c>drawTo</c> as fractions of its length. A closed
    /// outline is traced from where it starts (<see cref="BuildOutline"/>) while its fill fades
    /// in, and its dashes or dots are cut from the fitted pattern rather than laid out again, so
    /// they stay put while the trace moves. A line kind is cut to the drawn piece of its curve and
    /// the arrow is built on that piece, so the head rides the growing tip; its dashes keep their
    /// places along the full path in the same way.</para>
    /// </summary>
    public static class ShapeRenderer
    {
        private static readonly SKColor DefaultStroke = SKColor.Parse(ShapeContent.DefaultStrokeColor);

        private static readonly SKColor DefaultFill = SKColor.Parse(ShapeContent.DefaultFillColor);

        /// <summary>The image editor's dash patterns, as multiples of the stroke width.</summary>
        private static readonly float[] DashPattern = { 4, 2 };

        private static readonly float[] DotPattern = { 1, 2 };

        /// <summary>A closed outline's miter limit: past it a needle-sharp corner bevels instead
        /// of spiking. The spike would point out of the shape and be clipped anyway; this only
        /// bounds the work.</summary>
        private const float StrokeMiterLimit = 10;

        /// <summary>The speech bubble's body ends this far down its box; the tail fills the rest.</summary>
        private const double BubbleBodyBottom = 0.78;

        // the tail's base runs along the body's bottom edge between these fractions of the width,
        // and its tip sits at the box's bottom edge at TailTipX
        private const double BubbleTailLeft = 0.18;
        private const double BubbleTailRight = 0.36;
        private const double BubbleTailTipX = 0.12;

        /// <summary>
        /// The heart: the Material "favorite" glyph (24-unit grid, ink spanning x 2..22 and
        /// y 3..21.35), as absolute cubics running clockwise on screen from the short straight run
        /// out of its bottom tip (<see cref="HeartTip"/> to <see cref="HeartShoulder"/>) round to
        /// the mirrored run back into it. The glyph's right half is mirrored from its left one (the
        /// original differs by 0.01 at one point), so the heart is exactly symmetric. Each cubic is
        /// two control points and an end point.
        /// </summary>
        private static readonly (double X, double Y)[] HeartCubics =
        {
            (5.4, 15.36), (2, 12.28), (2, 8.5),
            (2, 5.42), (4.42, 3), (7.5, 3),
            (9.24, 3), (10.91, 3.81), (12, 5.09),
            (13.09, 3.81), (14.76, 3), (16.5, 3),
            (19.58, 3), (22, 5.42), (22, 8.5),
            (22, 12.28), (18.6, 15.36), (13.45, 20.03),
        };

        private static readonly (double X, double Y) HeartTip = (12, 21.35);
        private static readonly (double X, double Y) HeartShoulder = (10.55, 20.03);

        // the top notch, and the control points either side of it (its two tangents)
        private static readonly (double X, double Y) HeartNotch = (12, 5.09);
        private static readonly (double X, double Y) HeartNotchLeft = (10.91, 3.81);
        private static readonly (double X, double Y) HeartNotchRight = (13.09, 3.81);
        private const double HeartMinX = 2, HeartMaxX = 22, HeartMinY = 3, HeartMaxY = 21.35;

        /// <summary>
        /// Draws <paramref name="shape"/> into <paramref name="box"/> (canvas pixels).
        /// <paramref name="pxScale"/> maps output pixels onto this canvas (the composer's text
        /// scale; a tile passes its own) and <paramref name="alpha"/> multiplies everything,
        /// applied once over the whole shape. <paramref name="drawFrom"/> and
        /// <paramref name="drawTo"/> are the part of the path a Draw transition shows, as
        /// fractions of its length (all of it by default). No rotation and no clips: the caller
        /// owns those.
        /// </summary>
        public static void Draw(SKCanvas canvas, SKRect box, ShapeContent shape, double pxScale, double alpha = 1,
            double drawFrom = 0, double drawTo = 1)
        {
            ArgumentNullException.ThrowIfNull(canvas);
            if (shape == null || !(box.Width > 0) || !(box.Height > 0) || !(pxScale > 0) || !(alpha > 0))
                return;

            // a NaN draws the whole path, as no transition would
            double from = drawFrom > 0 ? Math.Min(drawFrom, 1) : 0;
            double to = drawTo < 1 ? Math.Max(drawTo, 0) : 1;
            if (!(to > from))
                return;

            int save = canvas.Save();
            try
            {
                canvas.Scale((float)pxScale);
                var outputBox = ToOutput(box, pxScale);
                alpha = Math.Min(alpha, 1);
                if (shape.IsLine)
                    DrawLineKind(canvas, outputBox, shape, alpha, from, to);
                else
                    DrawClosed(canvas, outputBox, shape, alpha, from, to);
            }
            finally
            {
                canvas.RestoreToCount(save);
            }
        }

        /// <summary>
        /// Where the shape's ink can land, in canvas pixels: the box itself for a closed kind (its
        /// stroke is inside), and for a line kind the box united with the bounds of everything the
        /// arrow draws: the head polygons grown by half their corner stroke, the taper outline,
        /// the spine grown by half its width. An arrow's head can reach out of its box after the
        /// box is stretched, which is why the composer's wipe sweeps across this rather than the
        /// box.
        /// </summary>
        public static SKRect InkBounds(SKRect box, ShapeContent shape, double pxScale)
        {
            if (shape == null || !shape.IsLine || !(pxScale > 0))
                return box;

            var outputBox = ToOutput(box, pxScale);
            var ink = LineInkBounds(outputBox, shape);
            float k = (float)pxScale;
            var canvasInk = new SKRect(ink.Left * k, ink.Top * k, ink.Right * k, ink.Bottom * k);
            canvasInk.Union(box);
            return canvasInk;
        }

        /// <summary>
        /// A closed kind's outline in canvas pixels, or null for a line kind. Runs clockwise on
        /// screen from the midpoint of its first edge: the top center for a rectangle, bubble and
        /// ellipse, the middle of the upper right edge for a triangle, diamond, polygon or star
        /// (the edge leaving the top vertex), and the bottom tip for a heart. The caller disposes
        /// it.
        /// </summary>
        public static SKPath BuildOutline(SKRect box, ShapeContent shape, double pxScale)
        {
            if (shape == null || shape.IsLine)
                return null;
            return BuildClosedOutline(box, shape, CornerRadiusOf(shape) * (pxScale > 0 ? pxScale : 1), out _);
        }

        // ------------------------------------------------------------------------ closed kinds

        /// <summary>A closed kind, its outline traced from <paramref name="from"/> to
        /// <paramref name="to"/> of its length and its fill shown at that share of its alpha (the
        /// Draw transition; 0 and 1 draw it whole).</summary>
        private static void DrawClosed(SKCanvas canvas, SKRect box, ShapeContent shape, double alpha, double from, double to)
        {
            var fill = ParseColor(shape.FillColor, DefaultFill);
            var stroke = ParseColor(shape.StrokeColor, DefaultStroke);
            bool hasFill = shape.Fill && fill.Alpha > 0;
            double width = ClosedStrokeWidth(shape);
            bool hasStroke = width > 0 && stroke.Alpha > 0;
            if (!hasFill && !hasStroke)
                return;

            bool partial = from > 0 || to < 1;
            if (partial)
                fill = fill.WithAlpha(FrameComposer.AlphaByte(fill.Alpha / 255.0 * (to - from)));

            using var outline = BuildClosedOutline(box, shape, CornerRadiusOf(shape), out var corners);
            using var dashes = hasStroke && shape.Dash == ShapeDash.Dashed ? DashOutline(outline, corners, width, from, to) : null;
            using var dots = hasStroke && shape.Dash == ShapeDash.Dotted ? DotOutline(outline, corners, width, from, to) : null;
            using var traced = hasStroke && partial && dashes == null && dots == null ? TraceOutline(outline, from, to) : null;

            int save = canvas.Save();
            try
            {
                // one layer for the item's opacity: a translucent outline over a translucent fill
                // is meant to show the fill through it, but the item's own fade applies once
                if (alpha < 1)
                {
                    using var layer = new SKPaint { Color = SKColors.White.WithAlpha(FrameComposer.AlphaByte(alpha)) };
                    canvas.SaveLayer(box, layer);
                }

                if (hasFill && fill.Alpha > 0)
                {
                    using var paint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = fill };
                    canvas.DrawPath(outline, paint);
                }

                if (hasStroke)
                {
                    // inside alignment: clip to the outline and stroke at double width, so the
                    // inner half is all that shows and the stroke's inner edge is the true inset
                    // (sharp where the outline is sharp, round where it is round). Dots are
                    // already inside, and the clip only trims their antialiased rim.
                    canvas.ClipPath(outline, SKClipOperation.Intersect, antialias: true);
                    using var paint = new SKPaint
                    {
                        IsAntialias = true,
                        Style = dots != null ? SKPaintStyle.Fill : SKPaintStyle.Stroke,
                        StrokeWidth = (float)(2 * width),
                        StrokeJoin = SKStrokeJoin.Miter,
                        StrokeMiter = StrokeMiterLimit,
                        StrokeCap = SKStrokeCap.Butt,
                        Color = stroke,
                    };
                    canvas.DrawPath(dots ?? dashes ?? traced ?? outline, paint);
                }
            }
            finally
            {
                canvas.RestoreToCount(save);
            }
        }

        /// <summary>
        /// The closed outline of <paramref name="shape"/> fitted to <paramref name="box"/>, its
        /// corners rounded by <paramref name="radius"/> in the box's own units.
        /// <paramref name="corners"/> receives the corners along it, in order from its start, which
        /// is where <see cref="DashOutline"/> centers its dashes: every polygon vertex, the heart's
        /// tip and top notch, and the top of an ellipse (no corner, but a dash there keeps the
        /// pattern symmetric).
        /// </summary>
        private static SKPath BuildClosedOutline(SKRect box, ShapeContent shape, double radius, out OutlineCorner[] corners)
        {
            var path = new SKPath();
            switch (shape.Kind)
            {
                case ShapeKind.Ellipse:
                    path.ArcTo(box, -90, 180, true);
                    path.ArcTo(box, 90, 180, false);
                    path.Close();
                    corners = [new OutlineCorner(0, new Vec2(box.MidX, box.Top), new Vec2(0, 1), Math.PI, 0, 0, convex: true)];
                    break;

                case ShapeKind.Heart:
                    corners = AddHeart(path, box);
                    break;

                case ShapeKind.Triangle:
                    corners = AddRoundedPolygon(path, FitToBox(RegularVertices(3, 1), box), Uniform(3, radius));
                    break;

                case ShapeKind.Diamond:
                    corners = AddRoundedPolygon(path, FitToBox(RegularVertices(4, 1), box), Uniform(4, radius));
                    break;

                case ShapeKind.Polygon:
                {
                    int sides = Math.Clamp(shape.PolygonSides, ShapeContent.MinPolygonSides, ShapeContent.MaxPolygonSides);
                    corners = AddRoundedPolygon(path, FitToBox(RegularVertices(sides, 1), box), Uniform(sides, radius));
                    break;
                }

                case ShapeKind.Star:
                {
                    int points = Math.Clamp(shape.StarPoints, ShapeContent.MinStarPoints, ShapeContent.MaxStarPoints);
                    double inner = Double.IsNaN(shape.StarInnerRatio)
                        ? ShapeContent.DefaultStarInnerRatio
                        : Math.Clamp(shape.StarInnerRatio, ShapeContent.MinStarInnerRatio, ShapeContent.MaxStarInnerRatio);
                    corners = AddRoundedPolygon(path, FitToBox(RegularVertices(2 * points, inner), box), Uniform(2 * points, radius));
                    break;
                }

                case ShapeKind.Bubble:
                    corners = AddBubble(path, box, radius);
                    break;

                default:
                {
                    var vertices = new[]
                    {
                        new Vec2(box.Left, box.Top), new Vec2(box.Right, box.Top),
                        new Vec2(box.Right, box.Bottom), new Vec2(box.Left, box.Bottom),
                    };
                    corners = AddRoundedPolygon(path, vertices, Uniform(4, radius));
                    break;
                }
            }

            return path;
        }

        /// <summary>
        /// One corner of a closed outline, as <see cref="DashOutline"/> and <see cref="DotOutline"/>
        /// need it: where its middle falls along the outline (<see cref="At"/>, a fraction of the
        /// length from the start), the vertex and the unit bisector pointing into the shape, the
        /// unsigned angle between its edges (<see cref="Theta"/>, π where there is no corner), its
        /// rounding radius and arc length, and whether it is convex (the shape's inside lies within
        /// the angle).
        /// </summary>
        private readonly struct OutlineCorner
        {
            public OutlineCorner(double at, Vec2 vertex, Vec2 inward, double theta, double radius, double arc, bool convex)
            {
                At = at;
                Vertex = vertex;
                Inward = inward;
                Theta = theta;
                Radius = radius;
                Arc = arc;
                Convex = convex;
            }

            public double At { get; }

            public Vec2 Vertex { get; }

            public Vec2 Inward { get; }

            public double Theta { get; }

            public double Radius { get; }

            public double Arc { get; }

            public bool Convex { get; }

            private bool IsCorner => Theta > 1e-6 && Math.PI - Theta > 1e-6;

            /// <summary>
            /// How far a dash centered here must run along each edge for the inner stroke of
            /// <paramref name="width"/> to reach all the way into the corner. A stroke kept inside
            /// a convex corner meets itself at the inset vertex, (width - radius) / tan(θ/2) along
            /// each edge from the corner (past the arc, when rounded): a shorter dash leaves a bare
            /// notch there. A concave corner is filled by the stroke's own join, so it needs nothing
            /// beyond half its arc.
            /// </summary>
            public double Reach(double width)
            {
                if (!Convex || Radius >= width || !IsCorner)
                    return Arc / 2;
                return (width - Radius) / Math.Tan(Theta / 2) + Arc / 2;
            }

            /// <summary>
            /// Where a dot of diameter <paramref name="width"/> sits in this convex corner, as a
            /// distance from the vertex along <see cref="Inward"/>: half the width inside the
            /// rounded arc's middle (which lies r / sin(θ/2) - r in from the vertex), but never
            /// closer than the point a half-width from both edges, so a dot in a sharp corner is
            /// wedged into it rather than poking out.
            /// </summary>
            public double DotOffset(double width)
            {
                if (!IsCorner)
                    return width / 2;

                double sin = Math.Sin(Theta / 2);
                return Math.Max(Radius / sin - Radius + width / 2, width / 2 / sin);
            }

            /// <summary>
            /// How much further along the outline, either side of this corner, the dot at
            /// <see cref="DotOffset"/> reaches than a dot on the inner line would, so the dots beside
            /// it can be spaced from it rather than from the vertex. A wedged dot (radius under a
            /// half-width) sits square to each edge (w/2 - r) / tan(θ/2) past the arc. A dot on the
            /// rounded arc is itself on the inner line, but that line runs only (r - w/2) / r as far
            /// round the arc as the outline does, which leaves the outline the rest.
            /// </summary>
            public double DotLead(double width)
            {
                if (!IsCorner)
                    return 0;
                if (Radius < width / 2)
                    return (width / 2 - Radius) / Math.Tan(Theta / 2) + Arc / 2;
                return Arc / 2 * (width / 2) / Radius;
            }
        }

        /// <summary>The part of a solid outline between <paramref name="from"/> and
        /// <paramref name="to"/> of its length, as one open piece from its start. Butt-capped and
        /// stroked inside like the whole outline, so its leading edge is a clean square cut.</summary>
        private static SKPath TraceOutline(SKPath outline, double from, double to)
        {
            using var measure = new SKPathMeasure(outline, true);
            double length = measure.Length;
            var traced = new SKPath();
            measure.GetSegment((float)(from * length), (float)(to * length), traced, true);
            return traced;
        }

        /// <summary>The share of the stretch beside it a corner dash may take at most, so a very
        /// sharp corner (whose inset vertex is far from it) still leaves room for the pattern.</summary>
        private const double MaxCornerDashShare = 0.4;

        /// <summary>
        /// The outline cut into dashes (null for a solid stroke), with the pattern fitted to the
        /// corners as a careful drawing would have it: a dash is centered on every corner and runs
        /// far enough along both edges to fill it (<see cref="OutlineCorner.Reach"/>), and between
        /// two corners the dashes and gaps are spaced evenly, scaled a little so a whole number of
        /// them fits. A plain repeating dash would land on the corners by chance, leaving some bare
        /// and others clipped, and leave a stub where the outline closes. Only the part of the
        /// pattern between <paramref name="from"/> and <paramref name="to"/> of the outline's length
        /// is kept (the Draw transition), so the dashes stay where they are while the trace moves.
        /// </summary>
        private static SKPath DashOutline(SKPath outline, OutlineCorner[] corners, double width, double from, double to)
        {
            if (corners.Length == 0)
                return null;

            using var measure = new SKPathMeasure(outline, true);
            double length = measure.Length;
            double on = DashPattern[0] * width, off = DashPattern[1] * width;
            if (!(length > 0) || !(on + off > 0))
                return null;

            double lo = from * length, hi = to * length;

            // how far each corner's dash runs either side of it
            int n = corners.Length;
            var half = new double[n];
            for (int j = 0; j < n; j++)
            {
                double room = MaxCornerDashShare * Math.Min(Stretch(corners, j, length), Stretch(corners, (j + n - 1) % n, length));
                half[j] = Math.Max(on / 2, Math.Min(corners[j].Reach(width), room));
            }

            var dashed = new SKPath();
            for (int j = 0; j < n; j++)
            {
                double at = corners[j].At * length;
                AddDash(measure, dashed, at - half[j], 2 * half[j], length, lo, hi);

                // between this corner's dash and the next one's: k gaps around k - 1 dashes, the
                // count nearest the pattern's own spacing, scaled to fit exactly
                double free = Stretch(corners, j, length) - half[j] - half[(j + 1) % n];
                if (!(free > 0))
                    continue;
                int gaps = Math.Max(1, (int)Math.Round((free + on) / (on + off)));
                double scale = free / (gaps * off + (gaps - 1) * on);
                for (int q = 0; q < gaps - 1; q++)
                    AddDash(measure, dashed, at + half[j] + (off + q * (on + off)) * scale, on * scale, length, lo, hi);
            }

            return dashed;
        }

        /// <summary>
        /// The outline as round dots a stroke width across, sitting inside it on the line a
        /// half-width in (where the middle of the inner stroke would run): one wedged into every
        /// convex corner (<see cref="OutlineCorner.DotOffset"/>), and between two of those as many
        /// as the image editor's dotted spacing fits, spread evenly. A dotted stroke cut from the
        /// outline would turn each corner's dot into a bent sliver; dots are what a dotted border
        /// means. Concave corners (a star's inner vertices, a bubble's tail joints) get no dot of
        /// their own: the even spacing passes through them, where a forced dot would crowd the
        /// convex corner's beside it. Only the dots between <paramref name="from"/> and
        /// <paramref name="to"/> of the outline's length are kept (the Draw transition), each one
        /// appearing whole as the trace reaches it.
        /// </summary>
        private static SKPath DotOutline(SKPath outline, OutlineCorner[] corners, double width, double from, double to)
        {
            corners = Array.FindAll(corners, c => c.Convex);
            if (corners.Length == 0)
                return null;

            using var measure = new SKPathMeasure(outline, true);
            double length = measure.Length;
            double period = (DotPattern[0] + DotPattern[1]) * width;
            if (!(length > 0) || !(period > 0))
                return null;

            double lo = from * length, hi = to * length;
            bool Drawn(double at) => at >= lo && at <= hi;

            int n = corners.Length;
            var lead = new double[n];
            for (int j = 0; j < n; j++)
                lead[j] = corners[j].DotLead(width);

            float radius = (float)(width / 2);
            var dots = new SKPath();
            for (int j = 0; j < n; j++)
            {
                var corner = corners[j];
                if (Drawn(corner.At * length))
                {
                    var center = corner.Vertex + corner.Inward * corner.DotOffset(width);
                    dots.AddCircle((float)center.X, (float)center.Y, radius);
                }

                // spaced between this corner's dot and the next one's, not between the vertices:
                // a sharp corner's dot sits well along both edges, and counting from its vertex
                // would crowd the dots beside it
                double first = corner.At * length + lead[j];
                double span = Stretch(corners, j, length) - lead[j] - lead[(j + 1) % n];
                int count = Math.Max(1, (int)Math.Round(span / period));
                for (int q = 1; q < count; q++)
                {
                    double at = (first + q * span / count) % length;
                    if (!Drawn(at) || !measure.GetPositionAndTangent((float)at, out var position, out var tangent))
                        continue;
                    // clockwise on screen, so the inside is to the right of travel
                    dots.AddCircle(position.X - tangent.Y * radius, position.Y + tangent.X * radius, radius);
                }
            }

            return dots;
        }

        /// <summary>The length of outline from corner <paramref name="j"/> to the next one, round
        /// the end of the outline for the last.</summary>
        private static double Stretch(OutlineCorner[] corners, int j, double length) =>
            ((j + 1 < corners.Length ? corners[j + 1].At : corners[0].At + 1) - corners[j].At) * length;

        /// <summary>Appends <paramref name="dash"/> length of the outline from
        /// <paramref name="start"/> (wrapped into the outline's length) as one open piece, cut to
        /// the drawn part <paramref name="lo"/>..<paramref name="hi"/> of the outline. A dash that
        /// runs over the outline's start continues from it without a break, so a corner there
        /// keeps its join.</summary>
        private static void AddDash(SKPathMeasure measure, SKPath into, double start, double dash, double length,
            double lo, double hi)
        {
            start %= length;
            if (start < 0)
                start += length;
            double end = start + dash;

            // the piece up to the outline's end, then the rest from its start
            double a = Math.Max(start, lo), b = Math.Min(Math.Min(end, length), hi);
            bool reachesEnd = b > a && b >= length;
            if (b > a)
                measure.GetSegment((float)a, (float)b, into, true);
            if (end <= length)
                return;

            a = Math.Max(0, lo);
            b = Math.Min(end - length, hi);
            if (b > a)
                measure.GetSegment((float)a, (float)b, into, !(reachesEnd && a == 0));
        }

        /// <summary>
        /// The vertices of a regular polygon (or, with <paramref name="innerRatio"/> under 1, a
        /// star: every odd vertex pulled in to that radius) on the unit circle, vertex 0 at the
        /// top and the rest clockwise on screen.
        /// </summary>
        private static Vec2[] RegularVertices(int count, double innerRatio)
        {
            var vertices = new Vec2[count];
            for (int k = 0; k < count; k++)
            {
                double angle = -Math.PI / 2 + k * 2 * Math.PI / count;
                double r = (k & 1) == 1 ? innerRatio : 1;
                vertices[k] = new Vec2(r * Math.Cos(angle), r * Math.Sin(angle));
            }
            return vertices;
        }

        /// <summary>Maps the vertices' own bounds onto the box, so the ink fills the gizmo's box:
        /// a triangle's apex lands at the top center and its base on the bottom corners, a
        /// diamond's vertices on the edge midpoints.</summary>
        private static Vec2[] FitToBox(Vec2[] vertices, SKRect box)
        {
            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
            foreach (var v in vertices)
            {
                minX = Math.Min(minX, v.X);
                minY = Math.Min(minY, v.Y);
                maxX = Math.Max(maxX, v.X);
                maxY = Math.Max(maxY, v.Y);
            }

            double sx = box.Width / (maxX - minX), sy = box.Height / (maxY - minY);
            var fitted = new Vec2[vertices.Length];
            for (int i = 0; i < vertices.Length; i++)
                fitted[i] = new Vec2(box.Left + (vertices[i].X - minX) * sx, box.Top + (vertices[i].Y - minY) * sy);
            return fitted;
        }

        private static double[] Uniform(int count, double radius)
        {
            var radii = new double[count];
            Array.Fill(radii, radius);
            return radii;
        }

        /// <summary>
        /// A closed polygon (clockwise on screen) with each vertex rounded by a tangent arc
        /// (Skia's <c>arcTo</c>), from the midpoint of edge 0→1 through vertices 1, 2, …, n-1 and
        /// then 0. Each radius is clamped so its tangent points stay within half of each edge beside
        /// it, so neighbouring arcs never overlap however large the request. The concave vertices of
        /// a star round too, which gives a soft star. Returns the corners in outline order
        /// (vertex 1 first, vertex 0 last).
        /// </summary>
        private static OutlineCorner[] AddRoundedPolygon(SKPath path, Vec2[] p, double[] radii)
        {
            int n = p.Length;

            // each vertex's angle and radius, how far its arc's tangent points sit from it, and the
            // arc's length
            var theta = new double[n];
            var radius = new double[n];
            var tangent = new double[n];
            var arc = new double[n];
            for (int i = 0; i < n; i++)
            {
                theta[i] = CornerAngle(p[(i + n - 1) % n], p[i], p[(i + 1) % n]);
                radius[i] = ClampedCornerRadius(p[(i + n - 1) % n], p[i], p[(i + 1) % n], theta[i], radii[i]);
                if (radius[i] > 0)
                {
                    tangent[i] = radius[i] / Math.Tan(theta[i] / 2);
                    arc[i] = radius[i] * (Math.PI - theta[i]);
                }
            }

            var at = new double[n];
            path.MoveTo(ToPoint((p[0] + p[1]) / 2));
            double along = QuadraticCurve.Distance(p[0], p[1]) / 2 - tangent[1];
            for (int step = 1; step <= n; step++)
            {
                int i = step % n;
                var vertex = p[i];
                var next = p[(i + 1) % n];
                if (radius[i] > 0)
                    path.ArcTo(ToPoint(vertex), ToPoint(next), (float)radius[i]);
                else
                    path.LineTo(ToPoint(vertex));

                at[step - 1] = along + arc[i] / 2;
                along += arc[i] + QuadraticCurve.Distance(vertex, next) - tangent[i] - tangent[(i + 1) % n];
            }
            path.Close();

            // the last step ran on to vertex 1's arc; the outline closes at edge 0→1's midpoint
            double total = along - QuadraticCurve.Distance(p[0], p[1]) / 2 + tangent[1];
            var corners = new OutlineCorner[n];
            for (int step = 1; step <= n; step++)
            {
                int i = step % n;
                var previous = p[(i + n - 1) % n];
                var next = p[(i + 1) % n];
                // a right turn on screen (y down) is convex on a clockwise outline
                bool convex = (p[i].X - previous.X) * (next.Y - p[i].Y) - (p[i].Y - previous.Y) * (next.X - p[i].X) > 0;
                corners[step - 1] = new OutlineCorner(at[step - 1] / total, p[i], Inward(previous, p[i], next, convex),
                    theta[i], radius[i], arc[i], convex);
            }
            return corners;
        }

        /// <summary>The unit bisector at <paramref name="v"/> pointing into a clockwise outline:
        /// within the angle at a convex vertex, out of it at a concave one, and square to the edge
        /// (to its right) where the edges run straight on.</summary>
        private static Vec2 Inward(Vec2 previous, Vec2 v, Vec2 next, bool convex)
        {
            var a = previous - v;
            var b = next - v;
            double la = a.Length, lb = b.Length;
            if (la < 1e-9 || lb < 1e-9)
                return new Vec2(0, 0);

            var bisector = a / la + b / lb;
            if (bisector.Length < 1e-9)
                return new Vec2(-b.Y / lb, b.X / lb);
            bisector = bisector.Normalize();
            return convex ? bisector : -bisector;
        }

        /// <summary>The unsigned angle between a vertex's two edge vectors, in [0, π] (π for a
        /// vertex on a straight line, or one with an edge of no length). This is the angle the
        /// tangent arc works with: a star's reflex inner vertex has an interior angle above 180°,
        /// but its θ is the complement, still under π.</summary>
        private static double CornerAngle(Vec2 previous, Vec2 v, Vec2 next)
        {
            var a = previous - v;
            var b = next - v;
            double la = a.Length, lb = b.Length;
            if (la < 1e-9 || lb < 1e-9)
                return Math.PI;
            return Math.Acos(Math.Clamp((a.X * b.X + a.Y * b.Y) / (la * lb), -1, 1));
        }

        /// <summary>
        /// The radius a vertex with angle <paramref name="theta"/> (<see cref="CornerAngle"/>) can
        /// carry. A tangent arc of radius r touches each edge r / tan(θ/2) from the vertex, which
        /// must not pass half the shorter edge. 0 (a sharp corner) for a degenerate vertex: no
        /// radius asked for, edges in a straight line (where the arc is undefined) or folded back
        /// on each other.
        /// </summary>
        private static double ClampedCornerRadius(Vec2 previous, Vec2 v, Vec2 next, double theta, double requested)
        {
            if (!(requested > 0) || Math.PI - theta < 1e-6 || theta < 1e-6)
                return 0;

            double shorter = Math.Min((previous - v).Length, (next - v).Length);
            return Math.Max(0, Math.Min(requested, shorter / 2 * Math.Tan(theta / 2)));
        }

        /// <summary>The speech bubble: a rounded body over the top <see cref="BubbleBodyBottom"/>
        /// of the box and a tail down to its bottom edge at the left. The tail's base corners round
        /// a quarter of the base's width at most, so a big radius softens the joint without
        /// swallowing the tail; its tip stays sharp.</summary>
        private static OutlineCorner[] AddBubble(SKPath path, SKRect box, double radius)
        {
            Vec2 At(double fx, double fy) => new Vec2(box.Left + fx * box.Width, box.Top + fy * box.Height);

            var points = new[]
            {
                At(0, 0), At(1, 0), At(1, BubbleBodyBottom),
                At(BubbleTailRight, BubbleBodyBottom), At(BubbleTailTipX, 1), At(BubbleTailLeft, BubbleBodyBottom),
                At(0, BubbleBodyBottom),
            };
            double baseRadius = Math.Min(radius, 0.25 * (BubbleTailRight - BubbleTailLeft) * box.Width);
            var radii = new[] { radius, radius, radius, baseRadius, 0, baseRadius, radius };
            return AddRoundedPolygon(path, points, radii);
        }

        /// <summary>The heart, stretched to the box. Its corners are the tip (the start: sharp and
        /// convex) and the top notch (concave), which lies exactly halfway round because the heart
        /// is mirror-symmetric.</summary>
        private static OutlineCorner[] AddHeart(SKPath path, SKRect box)
        {
            double sx = box.Width / (HeartMaxX - HeartMinX), sy = box.Height / (HeartMaxY - HeartMinY);
            Vec2 Map((double X, double Y) p) =>
                new Vec2(box.Left + (p.X - HeartMinX) * sx, box.Top + (p.Y - HeartMinY) * sy);

            var tip = Map(HeartTip);
            var shoulder = Map(HeartShoulder);
            path.MoveTo(ToPoint(tip));
            path.LineTo(ToPoint(shoulder));
            for (int i = 0; i < HeartCubics.Length; i += 3)
                path.CubicTo(ToPoint(Map(HeartCubics[i])), ToPoint(Map(HeartCubics[i + 1])), ToPoint(Map(HeartCubics[i + 2])));
            path.Close(); // the mirrored straight run back into the tip

            var mirrored = new Vec2(2 * tip.X - shoulder.X, shoulder.Y);
            var notch = Map(HeartNotch);
            return
            [
                new OutlineCorner(0, tip, new Vec2(0, -1), CornerAngle(mirrored, tip, shoulder), 0, 0, convex: true),
                new OutlineCorner(0.5, notch, new Vec2(0, 1), CornerAngle(Map(HeartNotchLeft), notch, Map(HeartNotchRight)), 0, 0, convex: false),
            ];
        }

        // -------------------------------------------------------------------------- line kinds

        /// <summary>
        /// A line, arrow or double arrow. Every part is drawn opaque inside one layer that carries
        /// the ink's alpha (the stroke color's own times the item's): shaft, head and the head's
        /// corner stroke overlap, and drawing them translucent one by one would darken the seams.
        /// The layer is skipped when the ink is opaque, the common case.
        ///
        /// <para>Only <paramref name="from"/> to <paramref name="to"/> of the path is drawn (the
        /// Draw transition; 0 and 1 draw it whole): the curve is cut to that piece and the arrow
        /// built on it, so the head sits on the drawn tip and shrinks through the builder's own cap
        /// when little of the path is left. A head on a cut is told how much path lies beyond it,
        /// so on a bowed path it aims along the arc it covers rather than the cut's tangent. The dashes are phased by how far along the full path
        /// the piece's spine starts, so they hold still rather than crawl as an exit erases the
        /// tail.</para>
        /// </summary>
        private static void DrawLineKind(SKCanvas canvas, SKRect box, ShapeContent shape, double alpha, double from, double to)
        {
            var color = ParseColor(shape.StrokeColor, DefaultStroke);
            byte inkAlpha = FrameComposer.AlphaByte(alpha * color.Alpha / 255.0);
            if (inkAlpha == 0)
                return;

            var line = LineIn(box, shape);
            Vec2 start = line.Start, end = line.End;
            Vec2? control = line.Control;
            double width = ShapeLineLayout.LineStrokeWidth(shape);
            bool partial = from > 0 || to < 1;
            double length = 0;
            double phase = partial ? CutPiece(ref start, ref control, ref end, from, to, out length) : 0;
            var parts = BuildParts(shape, start, control, end, width, from * length, (1 - to) * length);
            if (partial && parts is { HasStartHead: true, ShaftKind: ArrowShaftKind.Spine })
            {
                // a double arrow's spine starts past its start head, which is smaller on a short
                // piece: phase it by where it starts against where the whole arrow's starts
                var whole = BuildParts(shape, line.Start, line.Control, line.End, width);
                phase += SpineLead(parts, start, control, end) - SpineLead(whole, line.Start, line.Control, line.End);
            }

            int save = canvas.Save();
            try
            {
                if (inkAlpha < 255)
                {
                    using var layer = new SKPaint { Color = SKColors.White.WithAlpha(inkAlpha) };
                    canvas.SaveLayer(LineInkBounds(start, control, end, width, parts), layer);
                }

                var ink = color.WithAlpha(255);
                if (parts == null)
                {
                    // a plain line: the image editor's GraphicLine, a round-capped stroke
                    using var spine = SpinePath(start, control, end);
                    using var dash = CreateDash(shape.Dash, width, phase);
                    using var paint = SpinePaint(ink, width, dash);
                    canvas.DrawPath(spine, paint);
                    return;
                }

                DrawArrow(canvas, parts, ink, shape.Dash, width, phase);
            }
            finally
            {
                canvas.RestoreToCount(save);
            }
        }

        /// <summary>The arrow's parts in the image editor's draw order (<c>ArrowShape.Draw</c>):
        /// the shaft, then each head filled and stroked with its round-joined corner outline.</summary>
        private static void DrawArrow(SKCanvas canvas, ArrowParts parts, SKColor ink, ShapeDash dash, double width, double phase)
        {
            switch (parts.ShaftKind)
            {
                case ArrowShaftKind.Taper:
                {
                    using var taper = PolygonPath(parts.ShaftOutline);
                    using var paint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = ink };
                    canvas.DrawPath(taper, paint);
                    break;
                }

                case ArrowShaftKind.Spine:
                {
                    using var spine = SpinePath(parts.SpineStart, parts.SpineControl, parts.SpineEnd);
                    using var effect = CreateDash(dash, parts.SpineStrokeWidth, phase);
                    using var paint = SpinePaint(ink, parts.SpineStrokeWidth, effect);
                    canvas.DrawPath(spine, paint);
                    break;
                }
            }

            using var headPaint = new SKPaint
            {
                IsAntialias = true,
                Style = SKPaintStyle.StrokeAndFill,
                StrokeWidth = (float)parts.CornerStrokeWidth,
                StrokeJoin = SKStrokeJoin.Round,
                StrokeCap = SKStrokeCap.Round,
                Color = ink,
            };
            using (var head = PolygonPath([parts.Tip, parts.HeadLeft, parts.HeadNotch, parts.HeadRight]))
                canvas.DrawPath(head, headPaint);
            if (parts.HasStartHead)
            {
                using var head = PolygonPath([parts.StartTip, parts.StartLeft, parts.StartNotch, parts.StartRight]);
                canvas.DrawPath(head, headPaint);
            }
        }

        /// <summary>The line's endpoints and bow in the box's own units (output pixels inside
        /// <see cref="Draw"/>). A NaN or out-of-range bow, which validation rejects, draws
        /// clamped rather than as nothing.</summary>
        private static ShapeLine LineIn(SKRect box, ShapeContent shape)
        {
            var start = new Vec2(box.Left + shape.StartX * box.Width, box.Top + shape.StartY * box.Height);
            var end = new Vec2(box.Left + shape.EndX * box.Width, box.Top + shape.EndY * box.Height);
            double bend = Double.IsNaN(shape.Bend) ? 0 : Math.Clamp(shape.Bend, -ShapeContent.MaxBend, ShapeContent.MaxBend);
            double skew = Double.IsNaN(shape.BendSkew) ? 0 : Math.Clamp(shape.BendSkew, -ShapeContent.MaxBendSkew, ShapeContent.MaxBendSkew);
            return new ShapeLine(start, end, bend, skew);
        }

        /// <summary>The arrow geometry for an arrow kind, or null for a plain line. The runouts are
        /// how much of the whole path a drawn piece leaves out before its start and past its end.</summary>
        private static ArrowParts BuildParts(ShapeContent shape, Vec2 start, Vec2? control, Vec2 end, double width,
                                             double startRunout = 0, double endRunout = 0)
        {
            bool dashed = shape.Dash != ShapeDash.Solid;
            return shape.Kind switch
            {
                ShapeKind.Arrow => ArrowGeometry.Build(start, end, control, width, dashed, runout: endRunout),
                ShapeKind.DoubleArrow => ArrowGeometry.BuildDouble(start, end, control, width, dashed, startRunout, endRunout),
                _ => null,
            };
        }

        /// <summary>
        /// Cuts the path down to the piece from <paramref name="from"/> to <paramref name="to"/> of
        /// its length (a curve's by its sampled arc length, the measure the arrow builder uses) and
        /// returns how far along the whole path the piece starts. <paramref name="length"/> is the
        /// whole path's.
        /// </summary>
        private static double CutPiece(ref Vec2 start, ref Vec2? control, ref Vec2 end, double from, double to, out double length)
        {
            if (control is not { } c)
            {
                length = QuadraticCurve.Distance(start, end);
                (start, end) = (QuadraticCurve.Lerp(start, end, from), QuadraticCurve.Lerp(start, end, to));
                return from * length;
            }

            Span<double> cumulative = stackalloc double[QuadraticCurve.SampleCount + 1];
            length = QuadraticCurve.Cumulative(start, c, end, cumulative);
            double t0 = QuadraticCurve.ParameterAtLength(cumulative, from * length);
            double t1 = QuadraticCurve.ParameterAtLength(cumulative, to * length);
            QuadraticCurve.Sub(start, c, end, t0, t1, out start, out var c1, out end);
            control = c1;
            return from * length;
        }

        /// <summary>How far along the path an arrow's dashed spine starts: past the start head on a
        /// double arrow (both heads are the same size, so the spine is cut back equally at each
        /// end), at the path's own start otherwise.</summary>
        private static double SpineLead(ArrowParts parts, Vec2 start, Vec2? control, Vec2 end)
        {
            if (parts is not { HasStartHead: true, ShaftKind: ArrowShaftKind.Spine })
                return 0;
            return (PathLength(start, control, end) - PathLength(parts.SpineStart, parts.SpineControl, parts.SpineEnd)) / 2;
        }

        private static double PathLength(Vec2 start, Vec2? control, Vec2 end)
        {
            if (control is not { } c)
                return QuadraticCurve.Distance(start, end);
            Span<double> cumulative = stackalloc double[QuadraticCurve.SampleCount + 1];
            return QuadraticCurve.Cumulative(start, c, end, cumulative);
        }

        private static SKRect LineInkBounds(SKRect box, ShapeContent shape)
        {
            var line = LineIn(box, shape);
            double width = ShapeLineLayout.LineStrokeWidth(shape);
            return LineInkBounds(line.Start, line.Control, line.End, width,
                BuildParts(shape, line.Start, line.Control, line.End, width));
        }

        private static SKRect LineInkBounds(Vec2 start, Vec2? control, Vec2 end, double width, ArrowParts parts)
        {
            var bounds = new Bounds();
            if (parts == null)
            {
                bounds.AddCurve(start, control, end, width / 2);
                return bounds.ToRect();
            }

            double corner = parts.CornerStrokeWidth / 2;
            bounds.AddPoints([parts.Tip, parts.HeadLeft, parts.HeadNotch, parts.HeadRight], corner);
            if (parts.HasStartHead)
                bounds.AddPoints([parts.StartTip, parts.StartLeft, parts.StartNotch, parts.StartRight], corner);
            if (parts.ShaftKind == ArrowShaftKind.Taper)
                bounds.AddPoints(parts.ShaftOutline, 0);
            else if (parts.ShaftKind == ArrowShaftKind.Spine)
                bounds.AddCurve(parts.SpineStart, parts.SpineControl, parts.SpineEnd, parts.SpineStrokeWidth / 2);
            return bounds.ToRect();
        }

        private static SKPaint SpinePaint(SKColor ink, double width, SKPathEffect dash) => new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = (float)width,
            StrokeCap = SKStrokeCap.Round,
            StrokeJoin = SKStrokeJoin.Round,
            Color = ink,
            PathEffect = dash,
        };

        private static SKPath SpinePath(Vec2 start, Vec2? control, Vec2 end)
        {
            var path = new SKPath();
            path.MoveTo(ToPoint(start));
            if (control is { } c)
                path.QuadTo(ToPoint(c), ToPoint(end));
            else
                path.LineTo(ToPoint(end));
            return path;
        }

        private static SKPath PolygonPath(ReadOnlySpan<Vec2> points)
        {
            var path = new SKPath();
            path.MoveTo(ToPoint(points[0]));
            for (int i = 1; i < points.Length; i++)
                path.LineTo(ToPoint(points[i]));
            path.Close();
            return path;
        }

        /// <summary>Running bounds of points and stroked curves.</summary>
        private struct Bounds
        {
            private double _minX, _minY, _maxX, _maxY;
            private bool _any;

            public void AddPoints(ReadOnlySpan<Vec2> points, double grow)
            {
                foreach (var p in points)
                    Add(p.X - grow, p.Y - grow, p.X + grow, p.Y + grow);
            }

            public void AddCurve(Vec2 start, Vec2? control, Vec2 end, double grow)
            {
                var (minX, minY, maxX, maxY) = control is { } c
                    ? QuadraticCurve.Bounds(start, c, end)
                    : (Math.Min(start.X, end.X), Math.Min(start.Y, end.Y), Math.Max(start.X, end.X), Math.Max(start.Y, end.Y));
                Add(minX - grow, minY - grow, maxX + grow, maxY + grow);
            }

            public readonly SKRect ToRect() =>
                _any ? new SKRect((float)_minX, (float)_minY, (float)_maxX, (float)_maxY) : SKRect.Empty;

            private void Add(double minX, double minY, double maxX, double maxY)
            {
                if (!_any)
                {
                    (_minX, _minY, _maxX, _maxY, _any) = (minX, minY, maxX, maxY, true);
                    return;
                }

                _minX = Math.Min(_minX, minX);
                _minY = Math.Min(_minY, minY);
                _maxX = Math.Max(_maxX, maxX);
                _maxY = Math.Max(_maxY, maxY);
            }
        }

        // ----------------------------------------------------------------------------- helpers

        /// <summary>A line's dash for <paramref name="style"/> at stroke <paramref name="width"/>, or
        /// null for a solid stroke: the image editor's pen-relative pattern, repeating from the
        /// line's start exactly as its lines and arrows dash, or from <paramref name="phase"/> into
        /// the pattern for a piece that starts part way along (the Draw transition).</summary>
        private static SKPathEffect CreateDash(ShapeDash style, double width, double phase)
        {
            var pattern = style switch
            {
                ShapeDash.Dashed => DashPattern,
                ShapeDash.Dotted => DotPattern,
                _ => null,
            };
            if (pattern == null)
                return null;

            var intervals = new float[pattern.Length];
            double period = 0;
            for (int i = 0; i < pattern.Length; i++)
            {
                intervals[i] = (float)(pattern[i] * width);
                period += intervals[i];
            }

            if (period > 0)
            {
                phase %= period;
                if (phase < 0)
                    phase += period;
            }
            return SKPathEffect.CreateDash(intervals, (float)phase);
        }

        /// <summary>A closed outline's stroke width in output pixels: the stored one capped at the
        /// validated maximum, with a NaN or a negative drawing no outline.</summary>
        private static double ClosedStrokeWidth(ShapeContent shape) =>
            shape.StrokeWidth > 0 ? Math.Min(shape.StrokeWidth, ShapeContent.MaxStrokeWidth) : 0;

        private static double CornerRadiusOf(ShapeContent shape) =>
            ShapeContent.HasCorners(shape.Kind) && shape.CornerRadius > 0
                ? Math.Min(shape.CornerRadius, ShapeContent.MaxCornerRadius)
                : 0;

        private static SKColor ParseColor(string color, SKColor fallback) => FrameComposer.ParseColor(color, fallback);

        private static SKRect ToOutput(SKRect box, double pxScale)
        {
            float k = (float)pxScale;
            return new SKRect(box.Left / k, box.Top / k, box.Right / k, box.Bottom / k);
        }

        private static SKPoint ToPoint(Vec2 v) => new SKPoint((float)v.X, (float)v.Y);
    }
}
