using System;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Clowd.Drawing.Rendering;
using Clowd.Geometry2D;

namespace Clowd.Drawing.Graphics
{
    /// <summary>
    /// The arrow look shared by <see cref="GraphicArrow"/> and the numeric step badge's pointer: a
    /// filled shaft that tapers from a thin round tail to a heavy end, and a swept-back head
    /// (concave rear notch) whose corners are softened by a round-joined outline in the same
    /// color. A dashed arrow keeps a constant-width dashed shaft — a tapered fill cannot carry a
    /// dash pattern. Callers cache the two geometries <see cref="Build"/> returns; nothing here
    /// holds state.
    ///
    /// The maths lives in the shared <see cref="ArrowGeometry"/>, which the video editor's arrow
    /// shapes draw with too; this class only turns its points into Avalonia geometries.
    /// </summary>
    internal static class ArrowShape
    {
        /// <summary>
        /// Builds the head and the shaft (null when the arrow is too short to have one) of an arrow
        /// from <paramref name="start"/> to <paramref name="end"/>, optionally bowed through the
        /// quadratic <paramref name="control"/> point. <paramref name="tapered"/> false keeps the
        /// solid shaft at full weight all the way to its start — for an arrow growing out of
        /// another shape, where a thin tail would meet that shape at its thinnest point.
        /// </summary>
        public static void Build(Point start, Point end, Point? control, double width, bool dashed,
                                 out Geometry shaft, out Geometry head, bool tapered = true)
        {
            var parts = ArrowGeometry.Build(start.ToVec(), end.ToVec(), control?.ToVec(), width, dashed, tapered);

            head = BuildPolygon([parts.Tip, parts.HeadLeft, parts.HeadNotch, parts.HeadRight]);
            shaft = parts.ShaftKind switch
            {
                ArrowShaftKind.Taper => BuildPolygon(parts.ShaftOutline),
                ArrowShaftKind.Spine => parts.SpineControl is { } c
                    ? GraphicLine.BuildQuadratic(parts.SpineStart.ToPoint(), c.ToPoint(), parts.SpineEnd.ToPoint())
                    : new LineGeometry(parts.SpineStart.ToPoint(), parts.SpineEnd.ToPoint()),
                _ => null,
            };
        }

        /// <summary>Width of a solid shaft at full weight (an untapered shaft is this wide throughout),
        /// for shapes that should read as the same weight as an arrow of <paramref name="width"/>.</summary>
        public static double StemWidth(double width) => ArrowGeometry.StemWidth(width);

        /// <summary>Render bounds of the parts <see cref="Build"/> produced.</summary>
        public static Rect GetBounds(Geometry shaft, Geometry head, double width, ImmutableDashStyle dash)
        {
            var bounds = head.GetRenderBounds(CornerPen(default, width));
            if (shaft != null)
                bounds = bounds.Union(dash != null ? shaft.GetRenderBounds(DashedShaftPen(default, width, dash)) : shaft.Bounds);
            return bounds;
        }

        public static void Draw(DrawingContext ctx, Geometry shaft, Geometry head, Color color, double width, ImmutableDashStyle dash)
        {
            var brush = RenderResources.GetBrush(color);

            if (shaft != null)
            {
                if (dash != null)
                    ctx.DrawGeometry(null, DashedShaftPen(color, width, dash), shaft);
                else
                    ctx.DrawGeometry(brush, null, shaft);
            }

            ctx.DrawGeometry(brush, CornerPen(color, width), head);
        }

        /// <summary>Strokes the silhouette of the parts <see cref="Build"/> produced. A solid shaft
        /// is a filled taper, merged with the head so the seam between them is not outlined; a
        /// dashed shaft is a bare spine, stroked as is.</summary>
        public static void DrawOutline(DrawingContext ctx, Geometry shaft, Geometry head, bool dashed, IPen pen, Geometry extra = null)
        {
            Geometry fill = head;
            if (dashed)
                ctx.DrawGeometry(null, pen, shaft);
            else if (shaft != null)
                fill = new CombinedGeometry(GeometryCombineMode.Union, shaft, fill);

            if (extra != null)
                fill = new CombinedGeometry(GeometryCombineMode.Union, extra, fill);

            ctx.DrawGeometry(null, pen, fill);
        }

        private static ImmutablePen CornerPen(Color color, double width) =>
            RenderResources.GetPen(color, CornerRounding(width), null, PenLineCap.Round, PenLineJoin.Round);

        private static ImmutablePen DashedShaftPen(Color color, double width, ImmutableDashStyle dash) =>
            RenderResources.GetPen(color, width, dash, PenLineCap.Round);

        // thickness of the round-joined outline that softens the head's corners
        private static double CornerRounding(double width) => ArrowGeometry.CornerRounding(width);

        // a closed, filled figure through the points in order
        private static Geometry BuildPolygon(ReadOnlySpan<Vec2> points)
        {
            var geometry = new StreamGeometry();
            using (var g = geometry.Open())
            {
                g.BeginFigure(points[0].ToPoint(), true);
                for (int i = 1; i < points.Length; i++)
                    g.LineTo(points[i].ToPoint());
                g.EndFigure(true);
            }

            return geometry;
        }
    }
}
