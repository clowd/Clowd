using System;
using System.Text.Json.Serialization;
using Avalonia;
using Avalonia.Media;

namespace Clowd.Drawing.Graphics
{
    /// <summary>One node of a <see cref="GraphicPath"/>: the on-curve point and its two bezier
    /// handles as OFFSETS from it (so moving the point moves its handles for free, and the JSON
    /// is three "x,y" leaves). A handle shorter than <see cref="GraphicPath.MinHandleLength"/>
    /// counts as absent. Smooth keeps In and Out collinear (lengths may differ, Illustrator-style).</summary>
    public record struct PathAnchor(Point P, Point In, Point Out, bool Smooth)
    {
        // [JsonIgnore]: the serializer's default object contract would otherwise write these as
        // leaves, and the history codec's path grammar (item.N/P, In, Out, Smooth) must match the
        // JSON diff exactly
        [JsonIgnore]
        public bool HasIn => PathMath.Length(In) >= GraphicPath.MinHandleLength;

        [JsonIgnore]
        public bool HasOut => PathMath.Length(Out) >= GraphicPath.MinHandleLength;

        public static PathAnchor Corner(Point p) => new PathAnchor(p, default, default, false);
    }

    /// <summary>Pure vector/bezier helpers for the pen path, platform-free except the geometry
    /// builder at the bottom. Handles are Points, not Vectors, so they serialize as "x,y" leaves
    /// like every other point; Point has the arithmetic operators, and the two measures it lacks
    /// (length, normalize) go through its implicit Vector conversion here.</summary>
    internal static class PathMath
    {
        public static double Length(Point v) => ((Vector)v).Length;

        public static Point Normalize(Point v)
        {
            var len = Length(v);
            return len > 0 ? v / len : default;
        }

        /// <summary>Rotates a handle vector to the nearest multiple of 45°, keeping its length;
        /// zero stays zero.</summary>
        public static Point Snap45(Point v)
        {
            var len = Length(v);
            if (len == 0)
                return v;

            const double step = Math.PI / 4;
            var theta = Math.Round(Math.Atan2(v.Y, v.X) / step) * step;
            return new Point(Math.Cos(theta) * len, Math.Sin(theta) * len);
        }

        /// <summary>The opposite handle of a smooth anchor after <paramref name="moved"/> was
        /// dragged: it turns to stay collinear but keeps its own length. A zero drag leaves it as is.</summary>
        public static Point Collinear(Point opposite, Point moved)
        {
            var len = Length(moved);
            if (len == 0)
                return opposite;

            return moved * (-Length(opposite) / len);
        }

        /// <summary>Inkscape-style auto-smooth handles for an anchor between its neighbours: both
        /// handles lie along the prev→next chord, each a third of the distance to its own
        /// neighbour. A missing neighbour gives that side a zero handle (and the tangent then comes
        /// from the one neighbour there is); no neighbours at all gives zeros.</summary>
        public static (Point In, Point Out) AutoHandles(Point? prev, Point p, Point? next)
        {
            Point tangent;
            if (prev is { } pr && next is { } nx)
                tangent = Normalize(nx - pr);
            else if (next is { } n2)
                tangent = Normalize(n2 - p);
            else if (prev is { } p2)
                tangent = Normalize(p - p2);
            else
                return (default, default);

            var inHandle = prev is { } pv ? tangent * (-Length(p - pv) / 3) : default;
            var outHandle = next is { } nv ? tangent * (Length(nv - p) / 3) : default;
            return (inHandle, outHandle);
        }

        /// <summary>The same path walked the other way: reversed order with In/Out swapped on each
        /// anchor. Applying it twice is the bitwise identity.</summary>
        public static PathAnchor[] Reverse(PathAnchor[] anchors)
        {
            var result = new PathAnchor[anchors.Length];
            for (int i = 0; i < anchors.Length; i++)
            {
                var a = anchors[anchors.Length - 1 - i];
                result[i] = new PathAnchor(a.P, a.Out, a.In, a.Smooth);
            }

            return result;
        }

        /// <summary>The cubic between two consecutive anchors, in absolute coordinates.</summary>
        public static (Point P0, Point P1, Point P2, Point P3) Segment(PathAnchor a, PathAnchor b) =>
            (a.P, a.P + a.Out, b.P + b.In, b.P);

        /// <summary>De Casteljau split at <paramref name="t"/>: returns the five new control points
        /// in order — Q0 (the first cubic's P1), Q1 (its P2), S (the split point, on the curve),
        /// R0 (the second cubic's P1) and R1 (its P2). The outer anchors are unchanged.</summary>
        public static Point[] Split(Point p0, Point p1, Point p2, Point p3, double t)
        {
            var q0 = GraphicLine.Lerp(p0, p1, t);
            var m = GraphicLine.Lerp(p1, p2, t);
            var r1 = GraphicLine.Lerp(p2, p3, t);
            var q1 = GraphicLine.Lerp(q0, m, t);
            var r0 = GraphicLine.Lerp(m, r1, t);
            var s = GraphicLine.Lerp(q1, r0, t);
            return new[] { q0, q1, s, r0, r1 };
        }

        /// <summary>The point on the cubic nearest <paramref name="target"/>: a coarse sample to
        /// find the right span, then a ternary search inside it. Returns its parameter, the point,
        /// and the distance.</summary>
        public static (double T, Point P, double Distance) Nearest(Point p0, Point p1, Point p2, Point p3, Point target)
        {
            const int Samples = 32;
            double bestT = 0, bestD = double.MaxValue;
            for (int i = 0; i <= Samples; i++)
            {
                var t = (double)i / Samples;
                var d = GraphicLine.Distance(Evaluate(p0, p1, p2, p3, t), target);
                if (d < bestD)
                {
                    bestD = d;
                    bestT = t;
                }
            }

            double lo = Math.Max(0, bestT - 1.0 / Samples), hi = Math.Min(1, bestT + 1.0 / Samples);
            for (int i = 0; i < 24; i++)
            {
                var m1 = lo + (hi - lo) / 3;
                var m2 = hi - (hi - lo) / 3;
                if (GraphicLine.Distance(Evaluate(p0, p1, p2, p3, m1), target) < GraphicLine.Distance(Evaluate(p0, p1, p2, p3, m2), target))
                    hi = m2;
                else
                    lo = m1;
            }

            var tt = (lo + hi) / 2;
            var p = Evaluate(p0, p1, p2, p3, tt);
            return (tt, p, GraphicLine.Distance(p, target));
        }

        public static Point Evaluate(Point p0, Point p1, Point p2, Point p3, double t)
        {
            var u = 1 - t;
            return p0 * (u * u * u) + p1 * (3 * u * u * t) + p2 * (3 * u * t * t) + p3 * (t * t * t);
        }

        /// <summary>A segment with no handle on either end is a straight line; drawn as one so
        /// it never picks up bezier flattening artifacts.</summary>
        public static bool IsStraight(PathAnchor a, PathAnchor b) => !a.HasOut && !b.HasIn;

        /// <summary>The open (or closed) figure through every anchor. Fewer than two anchors give
        /// a degenerate single-point figure; callers guard for drawing purposes.</summary>
        public static StreamGeometry Build(PathAnchor[] anchors, bool closed)
        {
            var geometry = new StreamGeometry();
            using (var gctx = geometry.Open())
            {
                if (anchors.Length == 0)
                    return geometry;

                gctx.BeginFigure(anchors[0].P, false);

                int segments = closed ? anchors.Length : anchors.Length - 1;
                for (int i = 0; i < segments; i++)
                {
                    var a = anchors[i];
                    var b = anchors[(i + 1) % anchors.Length];
                    if (IsStraight(a, b))
                    {
                        gctx.LineTo(b.P);
                    }
                    else
                    {
                        var (_, p1, p2, p3) = Segment(a, b);
                        gctx.CubicBezierTo(p1, p2, p3);
                    }
                }

                gctx.EndFigure(closed);
            }

            return geometry;
        }
    }
}
