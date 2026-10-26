using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Immutable;
using Clowd.Drawing.Graphics;
using Clowd.Geometry2D;
using Xunit;

namespace Clowd.Drawing.Tests
{
    /// <summary>
    /// The arrow maths moved into the shared <c>Clowd.Geometry</c> core so the video editor's shapes
    /// draw the same arrow; the image editor's arrows must not move by a single bit. Every case is
    /// checked against <see cref="LegacyArrowShape"/>, a frozen copy of the old builder: the points
    /// the core emits must equal the old ones exactly (no tolerance; a one-ULP drift is a Vec2
    /// operation-order bug, never a reason to loosen this), and the drawn arrow must raster to the
    /// same bytes. The curve bend, the angle snap and the quadratic helpers that
    /// <see cref="GraphicLine"/> and <see cref="HelperFunctions"/> forward to the core are held to
    /// inline copies of their old maths the same way, over a grid of chords, bends and pointers.
    /// </summary>
    public class ArrowGeometryParityTests
    {
        private const int CanvasWidth = 400, CanvasHeight = 300;

        internal sealed record ArrowCase(string Name, Point Start, Point End, Point? Control, double Width, bool Dashed, bool Tapered)
        {
            public override string ToString() =>
                $"{Name} w={Width}{(Dashed ? " dashed" : "")}{(Tapered ? "" : " untapered")}";
        }

        // GraphicLine's control point rule (chord midpoint + 2·(offset·normal + skew·length·tangent)),
        // so the curved cases bow the way a dragged mid handle would
        private static Point Bowed(Point start, Point end, double offset, double skew)
        {
            var dx = end.X - start.X;
            var dy = end.Y - start.Y;
            var length = Math.Sqrt(dx * dx + dy * dy);
            var nx = -dy / length;
            var ny = dx / length;
            var along = skew * length;
            return new Point((start.X + end.X) / 2 + 2 * (offset * nx + along * ny),
                             (start.Y + end.Y) / 2 + 2 * (offset * ny - along * nx));
        }

        internal static IEnumerable<ArrowCase> Cases()
        {
            var shapes = new List<(string Name, Point Start, Point End, Point? Control)>
            {
                ("straight", new Point(40, 150), new Point(360, 150), null),
                ("curved", new Point(40, 220), new Point(360, 220), Bowed(new Point(40, 220), new Point(360, 220), -80, 0)),
                ("skew+", new Point(40, 220), new Point(360, 220), Bowed(new Point(40, 220), new Point(360, 220), -80, 0.25)),
                ("skew-", new Point(40, 220), new Point(360, 220), Bowed(new Point(40, 220), new Point(360, 220), 60, -0.25)),
                ("short10", new Point(195, 150), new Point(205, 150), null),
                ("short20", new Point(190, 150), new Point(210, 150), null),
                ("short20 curved", new Point(190, 150), new Point(210, 150), Bowed(new Point(190, 150), new Point(210, 150), 4, 0.1)),
                ("zero", new Point(200, 150), new Point(200, 150), null),
                ("control on end", new Point(80, 150), new Point(300, 150), new Point(300, 150)),
                ("steep", new Point(200, 280), new Point(210, 20), null),
                ("steep curved", new Point(200, 280), new Point(210, 20), Bowed(new Point(200, 280), new Point(210, 20), 50, -0.1)),
                ("negative", new Point(360, 40), new Point(40, 260), null),
                ("negative curved", new Point(360, 40), new Point(40, 260), Bowed(new Point(360, 40), new Point(40, 260), -70, 0.2)),
                ("fractional", new Point(33.3333, 71.17), new Point(301.9, 233.0001), new Point(170.25, 12.5)),
            };

            foreach (var (name, start, end, control) in shapes)
                foreach (var width in new[] { 1.0, 3, 8, 20 })
                    foreach (var dashed in new[] { false, true })
                        foreach (var tapered in new[] { true, false })
                            yield return new ArrowCase(name, start, end, control, width, dashed, tapered);
        }

        // AvaloniaFact only because the oracle builds Avalonia geometries alongside its points
        [AvaloniaFact]
        public void EveryCase_EmitsExactlyTheLegacyPoints()
        {
            foreach (var c in Cases())
            {
                var legacy = LegacyArrowShape.Build(c.Start, c.End, c.Control, c.Width, c.Dashed, out _, out _, c.Tapered);
                var parts = ArrowGeometry.Build(c.Start.ToVec(), c.End.ToVec(), c.Control?.ToVec(), c.Width, c.Dashed, c.Tapered);

                AssertExact(legacy.Head, new[] { parts.Tip, parts.HeadLeft, parts.HeadNotch, parts.HeadRight }, $"{c} head");
                Assert.False(parts.HasStartHead, $"{c}: a single arrow has no start head");
                Assert.Equal(legacy.CornerStrokeWidth, parts.CornerStrokeWidth);
                Assert.Equal(legacy.SpineStrokeWidth, parts.SpineStrokeWidth);
                Assert.Equal(legacy.Shaft.ToString(), parts.ShaftKind.ToString());

                switch (parts.ShaftKind)
                {
                    case ArrowShaftKind.Taper:
                        AssertExact(legacy.ShaftOutline, parts.ShaftOutline, $"{c} taper");
                        break;
                    case ArrowShaftKind.Spine:
                        AssertExact(legacy.SpineStart, parts.SpineStart, $"{c} spine start");
                        AssertExact(legacy.SpineEnd, parts.SpineEnd, $"{c} spine end");
                        Assert.Equal(legacy.SpineControl.HasValue, parts.SpineControl.HasValue);
                        if (legacy.SpineControl is { } control)
                            AssertExact(control, parts.SpineControl.Value, $"{c} spine control");
                        break;
                }

                Assert.Equal(LegacyArrowShape.StemWidth(c.Width), ArrowGeometry.StemWidth(c.Width));
                Assert.Equal(LegacyArrowShape.CornerRounding(c.Width), ArrowGeometry.CornerRounding(c.Width));
            }
        }

        [Fact]
        public void TheCaseMatrixCoversEveryShaftKind()
        {
            // the point test would pass vacuously for a kind no case reaches
            var kinds = new HashSet<ArrowShaftKind>();
            foreach (var c in Cases())
                kinds.Add(ArrowGeometry.Build(c.Start.ToVec(), c.End.ToVec(), c.Control?.ToVec(), c.Width, c.Dashed, c.Tapered).ShaftKind);
            Assert.Equal(3, kinds.Count);
        }

        // exact: the bit patterns must match, so a one-ULP drift (or a -0 for a +0) fails
        private static void AssertExact(Point expected, Vec2 actual, string what)
        {
            Assert.True(BitConverter.DoubleToInt64Bits(expected.X) == BitConverter.DoubleToInt64Bits(actual.X)
                        && BitConverter.DoubleToInt64Bits(expected.Y) == BitConverter.DoubleToInt64Bits(actual.Y),
                        $"{what}: expected {expected.X:R}, {expected.Y:R} actual {actual.X:R}, {actual.Y:R}");
        }

        private static void AssertExact(IReadOnlyList<Point> expected, IReadOnlyList<Vec2> actual, string what)
        {
            Assert.Equal(expected.Count, actual.Count);
            for (int i = 0; i < expected.Count; i++)
                AssertExact(expected[i], actual[i], $"{what}[{i}]");
        }

        [AvaloniaFact]
        public void EveryCase_RastersToTheSameBytesAsTheLegacyBuilder()
        {
            foreach (var c in Cases())
            {
                LegacyArrowShape.Build(c.Start, c.End, c.Control, c.Width, c.Dashed, out var legacyShaft, out var legacyHead, c.Tapered);
                ArrowShape.Build(c.Start, c.End, c.Control, c.Width, c.Dashed, out var shaft, out var head, c.Tapered);

                var legacy = Render(legacyShaft, legacyHead, c);
                var current = Render(shaft, head, c);

                // a zero-length arrow has a zero-length head, so it is the one case with no ink
                if (c.Start != c.End)
                    Assert.True(Array.Exists(legacy, b => b != 255), $"{c}: the legacy arrow drew nothing");
                Assert.True(legacy.AsSpan().SequenceEqual(current), $"{c}: the raster differs from the legacy builder's");
            }
        }

        private static byte[] Render(Geometry shaft, Geometry head, ArrowCase c)
        {
            var dash = c.Dashed ? new ImmutableDashStyle(new double[] { 4, 2 }, 0) : null;
            using var rtb = new RenderTargetBitmap(new PixelSize(CanvasWidth, CanvasHeight), new Vector(96, 96));
            using (var ctx = rtb.CreateDrawingContext())
            {
                ctx.FillRectangle(Brushes.White, new Rect(0, 0, CanvasWidth, CanvasHeight));
                ArrowShape.Draw(ctx, shaft, head, Colors.Black, c.Width, dash);
            }

            var px = new byte[CanvasWidth * CanvasHeight * 4];
            var handle = GCHandle.Alloc(px, GCHandleType.Pinned);
            try
            {
                rtb.CopyPixels(new PixelRect(0, 0, CanvasWidth, CanvasHeight), handle.AddrOfPinnedObject(), px.Length, CanvasWidth * 4);
            }
            finally
            {
                handle.Free();
            }

            return px;
        }

        // ---- the curve bend and angle snap GraphicLine and HelperFunctions forward to the core ----

        private static readonly (Point Start, Point End)[] Chords =
        {
            (new Point(0, 0), new Point(100, 0)),
            (new Point(10, 20), new Point(-37.3, 81.9)),
            (new Point(300.25, -12.5), new Point(-41, 233.75)),
            (new Point(0, 0), new Point(0.001, 0)),
            (new Point(5, 5), new Point(5, 5)), // zero-length: no normal, so no bend
        };

        private static readonly (double Offset, double Skew)[] Bends =
        {
            (0, 0), (0, 0.2), (0.5, 0), (-30, 0.1), (77.123, -0.25), (12.5, 0.25),
        };

        private static readonly Point[] Pointers =
        {
            new Point(50, 30), new Point(65, 25), new Point(95, 25), new Point(70, 27), new Point(60, -4),
            new Point(50, 0.4), new Point(-12.75, 140.2), new Point(5, 5), new Point(1e-3, -0.9),
        };

        private sealed class ProbeLine : GraphicLine
        {
            public ProbeLine(Point start, Point end) : base(Colors.Black, 2, start, end)
            { }

            public bool Control(out Point control) => TryGetControlPoint(out control);
        }

        [AvaloniaFact]
        public void ControlPointAndMidHandle_MatchTheLegacyMaths()
        {
            foreach (var (start, end) in Chords)
                foreach (var (offset, skew) in Bends)
                {
                    var line = new ProbeLine(start, end) { CurveOffset = offset, CurveSkew = skew };
                    var what = $"{start}->{end} offset={offset} skew={skew}";

                    var curved = LegacyControlPoint(start, end, offset, skew, out var expected);
                    Assert.Equal(curved, line.Control(out var control));
                    AssertExact(expected, control.ToVec(), $"{what} control");

                    AssertExact(LegacyHandle(start, end, offset, skew), line.GetHandle(3, new DpiScale(1, 1)).ToVec(), $"{what} handle");
                }
        }

        [AvaloniaFact]
        public void MidHandleDrag_MatchesTheLegacyMaths()
        {
            foreach (var (start, end) in Chords)
                foreach (var (offset, skew) in Bends)
                    foreach (var pointer in Pointers)
                        foreach (var shift in new[] { false, true })
                        {
                            var line = new ProbeLine(start, end) { CurveOffset = offset, CurveSkew = skew };
                            line.MoveHandleTo(pointer, 3, shift ? Avalonia.Input.KeyModifiers.Shift : Avalonia.Input.KeyModifiers.None);

                            double expectedOffset = offset, expectedSkew = skew;
                            LegacyMoveMidHandle(start, end, pointer, shift, ref expectedOffset, ref expectedSkew);

                            var what = $"{start}->{end} from offset={offset} skew={skew} to {pointer} shift={shift}";
                            Assert.True(BitConverter.DoubleToInt64Bits(expectedOffset) == BitConverter.DoubleToInt64Bits(line.CurveOffset),
                                        $"{what}: offset expected {expectedOffset:R} actual {line.CurveOffset:R}");
                            Assert.True(BitConverter.DoubleToInt64Bits(expectedSkew) == BitConverter.DoubleToInt64Bits(line.CurveSkew),
                                        $"{what}: skew expected {expectedSkew:R} actual {line.CurveSkew:R}");
                        }
        }

        [Fact]
        public void AngleSnap_MatchesTheLegacyMaths()
        {
            var anchors = new[] { new Point(0, 0), new Point(10, 20), new Point(-37.3, 81.9), new Point(300.25, -12.5) };
            foreach (var anchor in anchors)
                foreach (var pointer in Pointers)
                    foreach (var diagOnly in new[] { false, true })
                    {
                        var what = $"{anchor} {pointer} diag={diagOnly}";
                        var expected = LegacySnap(anchor, pointer, diagOnly);
                        AssertExact(expected, HelperFunctions.SnapPointToCommonAngle(anchor, pointer, diagOnly).ToVec(), what);
                        AssertExact(expected, AngleSnap.Snap(anchor.ToVec(), pointer.ToVec(), diagOnly), $"{what} core");
                    }

            Assert.Equal(15, HelperFunctions.SnapAngleStepDegrees);
        }

        [Fact]
        public void QuadraticHelpers_MatchTheLegacyMaths()
        {
            foreach (var (start, end) in Chords)
                foreach (var pointer in Pointers)
                    foreach (var t in new[] { 0, 0.125, 1.0 / 3, 0.5, 0.9, 1 })
                    {
                        var what = $"{start} {pointer} {end} t={t}";
                        AssertExact(LegacyEval(start, pointer, end, t), GraphicLine.EvalQuadratic(start, pointer, end, t).ToVec(), $"{what} eval");
                        AssertExact(LegacyLerp(start, end, t), GraphicLine.Lerp(start, end, t).ToVec(), $"{what} lerp");
                        Assert.Equal(BitConverter.DoubleToInt64Bits(LegacyDistance(start, pointer)),
                                     BitConverter.DoubleToInt64Bits(GraphicLine.Distance(start, pointer)));
                    }

            // a curve's running lengths, then lengths before, on, between and past the samples
            var cumulative = new double[GraphicLine.CurveSampleCount + 1];
            QuadraticCurve.Cumulative(new Vec2(0, 0), new Vec2(60, -90), new Vec2(140, 10), cumulative);
            var total = cumulative[^1];
            foreach (var length in new[] { 0, 1e-9, cumulative[1], cumulative[7] * 0.5 + cumulative[8] * 0.5, total * 0.618, total, total + 1 })
                Assert.Equal(BitConverter.DoubleToInt64Bits(LegacyArrowShape.ParameterAtLength(cumulative, length)),
                             BitConverter.DoubleToInt64Bits(GraphicLine.ParameterAtLength(cumulative, length)));

            Assert.Equal(32, GraphicLine.CurveSampleCount);
            Assert.Equal(0.25, GraphicLine.MaxCurveSkew);
            Assert.Equal(0.1, GraphicLine.SnappedOffsetStep);
        }

        // GraphicLine's and HelperFunctions' maths as they were before forwarding, inlined

        private static bool LegacyChordNormal(Point start, Point end, out Vector normal)
        {
            var chord = new Vector(end.X - start.X, end.Y - start.Y);
            var length = chord.Length;
            if (length <= 0)
            {
                normal = default;
                return false;
            }

            normal = new Vector(-chord.Y / length, chord.X / length);
            return true;
        }

        private static Point LegacyMidpoint(Point start, Point end) => new Point((start.X + end.X) / 2, (start.Y + end.Y) / 2);

        private static bool LegacyControlPoint(Point start, Point end, double offset, double skew, out Point control)
        {
            if (offset == 0 || !LegacyChordNormal(start, end, out var normal))
            {
                control = default;
                return false;
            }

            var mid = LegacyMidpoint(start, end);
            var along = skew * LegacyDistance(start, end);
            control = new Point(mid.X + 2 * (offset * normal.X + along * normal.Y),
                                mid.Y + 2 * (offset * normal.Y - along * normal.X));
            return true;
        }

        private static Point LegacyHandle(Point start, Point end, double offset, double skew) =>
            LegacyControlPoint(start, end, offset, skew, out var control)
                ? LegacyEval(start, control, end, 0.5)
                : LegacyMidpoint(start, end);

        private static void LegacyMoveMidHandle(Point start, Point end, Point point, bool shift, ref double curveOffset, ref double curveSkew)
        {
            if (!LegacyChordNormal(start, end, out var normal))
                return;

            var length = LegacyDistance(start, end);
            var tangent = new Vector(normal.Y, -normal.X);
            var mid = LegacyMidpoint(start, end);
            var dx = point.X - mid.X;
            var dy = point.Y - mid.Y;
            var offset = dx * normal.X + dy * normal.Y;
            var skew = (dx * tangent.X + dy * tangent.Y) / length;

            if (shift)
            {
                var step = 0.1 * length;
                offset = Math.Round(offset / step) * step;
                skew = 0;
            }
            else
            {
                skew = Math.Clamp(skew, -0.25, 0.25);
            }

            if (Math.Abs(offset) < 1.0)
                offset = 0;

            if (offset == 0)
                skew = 0;

            curveOffset = offset;
            curveSkew = skew;
        }

        private static Point LegacySnap(Point anchor, Point point, bool diagOnly)
        {
            double x1 = anchor.X, y1 = anchor.Y, x2 = point.X, y2 = point.Y;
            double xDiff = x2 - x1;
            double yDiff = y2 - y1;

            double closest;

            if (diagOnly)
            {
                var angle = (Math.Atan2(yDiff, xDiff) * 180.0 / Math.PI + 360 + 45) % 360;
                closest = Math.Round(angle / 90d) * 90d - 45;
            }
            else
            {
                var angle = (Math.Atan2(yDiff, xDiff) * 180.0 / Math.PI + 360) % 360;
                closest = Math.Round(angle / 15.0) * 15.0;
            }

            var theta = closest / 180 * Math.PI;
            var ux = Math.Cos(theta);
            var uy = Math.Sin(theta);
            var snapLen = xDiff * ux + yDiff * uy;

            var ox = anchor.X + snapLen * ux;
            var oy = anchor.Y + snapLen * uy;

            return new Point(ox, oy);
        }

        private static Point LegacyEval(Point start, Point control, Point end, double t)
        {
            var mt = 1 - t;
            var a = mt * mt;
            var b = 2 * mt * t;
            var c = t * t;
            return new Point(a * start.X + b * control.X + c * end.X,
                             a * start.Y + b * control.Y + c * end.Y);
        }

        private static Point LegacyLerp(Point from, Point to, double t) =>
            new Point(from.X + (to.X - from.X) * t, from.Y + (to.Y - from.Y) * t);

        private static double LegacyDistance(Point a, Point b)
        {
            var dx = b.X - a.X;
            var dy = b.Y - a.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }
    }
}
