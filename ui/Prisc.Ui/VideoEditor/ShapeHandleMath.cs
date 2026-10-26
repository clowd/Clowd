using System;
using Prisc.Geometry2D;
using Prisc.VideoSDK.Composition;
using Prisc.VideoSDK.Model;
using ModelTransform = Prisc.VideoSDK.Model.Transform;

namespace Prisc.UI.VideoEditor
{
    /// <summary>What a press on a line kind lands on: one of the image editor's three line handles
    /// (start, end, and the bend handle riding the ink at t = 0.5), the ink itself, or nothing.</summary>
    public enum ShapeHandlePart
    {
        None,
        Start,
        End,
        Bend,
        Body,
    }

    /// <summary>
    /// The pointer maths of <see cref="ShapeHandlesControl"/>, free of Avalonia types so it can be
    /// tested without a UI thread, and the ink hit test the preview's click-to-select shares with it
    /// (<see cref="ItemPlacement.HitTest"/>), so a line is grabbed and selected by the same pixels.
    ///
    /// Everything is in <b>canvas pixels</b>: positions relative to the video rect's top-left, the
    /// canvas the video rect's size, and <c>pxScale</c> the canvas pixels per output pixel
    /// (<see cref="FrameComposer.PixelScale"/>). The line comes from
    /// <see cref="ShapeLineLayout.TryResolve"/>, so it is where the ink is drawn at rest, rotation
    /// included.
    /// </summary>
    public static class ShapeHandleMath
    {
        /// <summary>How far from a handle's center a press still grabs it, in canvas pixels: a
        /// little more than the drawn disc (<see cref="PreviewHandles.Size"/>), as the gizmo's
        /// handles take a box larger than they draw.</summary>
        public const double HandleHitRadius = 9;

        /// <summary>The least half-width of the band along the spine that grabs a line, in canvas
        /// pixels, so a 1 px line is still a target.</summary>
        public const double MinCorridorPx = 6;

        /// <summary>A bend drag within this many canvas pixels of the chord snaps to exactly
        /// straight, so a bowed arrow can be straightened by hand (the image editor's
        /// <c>GraphicLine.StraightSnapDistance</c>).</summary>
        public const double StraightSnapDistance = 1.5;

        // which handle an exact tie goes to: the bend handle first, so it stays reachable on an
        // arrow so short that both endpoints sit on top of it
        private static readonly ShapeHandlePart[] HandleOrder =
            { ShapeHandlePart.Bend, ShapeHandlePart.Start, ShapeHandlePart.End };

        /// <summary>Where a handle sits on <paramref name="line"/>; the origin for a part that is
        /// not a handle.</summary>
        public static Vec2 HandlePoint(ShapeLine line, ShapeHandlePart part) => part switch
        {
            ShapeHandlePart.Start => line.Start,
            ShapeHandlePart.End => line.End,
            ShapeHandlePart.Bend => line.Handle,
            _ => default,
        };

        /// <summary>
        /// The part under <paramref name="p"/>. Among the handles within
        /// <paramref name="handleRadius"/> the nearest wins (an exact tie goes to the bend handle,
        /// then the start), so on a short arrow whose three handles overlap, a press toward an end
        /// still grabs that end to lengthen it. Off the handles, the ink (<see cref="HitsShape"/>)
        /// is the body.
        /// </summary>
        public static ShapeHandlePart HitPart(ShapeContent shape, ShapeLine line, Vec2 p, double pxScale,
            double handleRadius = HandleHitRadius)
        {
            var best = ShapeHandlePart.None;
            double bestDistance = Double.PositiveInfinity;
            foreach (var part in HandleOrder)
            {
                double distance = QuadraticCurve.Distance(p, HandlePoint(line, part));
                if (distance <= handleRadius && distance < bestDistance)
                {
                    best = part;
                    bestDistance = distance;
                }
            }

            if (best != ShapeHandlePart.None)
                return best;

            return HitsShape(shape, line, p, pxScale) ? ShapeHandlePart.Body : ShapeHandlePart.None;
        }

        /// <summary>
        /// Whether <paramref name="p"/> is on the line's ink: inside a head (its polygon, grown by
        /// the round-joined corner stroke the renderer draws around it), inside the tapered shaft,
        /// or within the spine corridor, at least <paramref name="minCorridorPx"/> canvas pixels
        /// either side of the curve so a thin line stays a target. The head's wings reach well past
        /// any corridor (0.62 of the head length either side, about 18 output pixels at the default
        /// width), which is why they are tested as polygons.
        ///
        /// The parts are built in output pixels, exactly as <see cref="ShapeRenderer.Draw"/>
        /// builds them: the head length's floor and the corner rounding are output pixels, so a
        /// head built on canvas points would be the wrong size on a small preview.
        /// </summary>
        public static bool HitsShape(ShapeContent shape, ShapeLine line, Vec2 p, double pxScale,
            double minCorridorPx = MinCorridorPx)
        {
            if (shape is not { IsLine: true } || !(pxScale > 0))
                return false;

            var output = new ShapeLine(line.Start / pxScale, line.End / pxScale, line.Bend, line.BendSkew);
            var q = p / pxScale;
            double width = ShapeLineLayout.LineStrokeWidth(shape);

            var parts = BuildParts(shape, output, width);
            if (parts != null)
            {
                double rounding = parts.CornerStrokeWidth / 2;
                if (HitsPolygon([parts.Tip, parts.HeadLeft, parts.HeadNotch, parts.HeadRight], q, rounding))
                    return true;
                if (parts.HasStartHead &&
                    HitsPolygon([parts.StartTip, parts.StartLeft, parts.StartNotch, parts.StartRight], q, rounding))
                    return true;
                if (parts.ShaftKind == ArrowShaftKind.Taper && HitsPolygon(parts.ShaftOutline, q, 0))
                    return true;
            }

            double reach = Math.Max(width / 2, minCorridorPx / pxScale);
            return DistanceToSpine(output, q) <= reach;
        }

        /// <summary>
        /// What a press at <paramref name="p"/> on <paramref name="item"/> grabs. A body press is
        /// declined (None) while another item is composed on top there, so it falls through to the
        /// preview's click-to-select of that item, the rule the transform gizmo follows for its
        /// body; the handles are chrome of their own and always win.
        /// </summary>
        public static ShapeHandlePart ResolvePress(Project project, Item item, long timeTicks, Vec2 p,
            double canvasWidth, double canvasHeight)
        {
            if (project == null || item?.Content is not ShapeContent { IsLine: true } shape ||
                !(canvasWidth > 0) || !(canvasHeight > 0) ||
                !ShapeLineLayout.TryResolve(item.Transform, shape, canvasWidth, canvasHeight, out var line))
                return ShapeHandlePart.None;

            var part = HitPart(shape, line, p, FrameComposer.PixelScale(project, canvasHeight));
            if (part != ShapeHandlePart.Body)
                return part;

            var top = ItemPlacement.HitTest(project, timeTicks, p.X, p.Y, canvasWidth, canvasHeight);
            return top == null || top.Id == item.Id ? ShapeHandlePart.Body : ShapeHandlePart.None;
        }

        /// <summary>The arrow's parts for an arrow kind, or null for a plain line (it has no head);
        /// the same switch as the renderer's.</summary>
        private static ArrowParts BuildParts(ShapeContent shape, ShapeLine line, double width)
        {
            bool dashed = shape.Dash != ShapeDash.Solid;
            return shape.Kind switch
            {
                ShapeKind.Arrow => ArrowGeometry.Build(line.Start, line.End, line.Control, width, dashed),
                ShapeKind.DoubleArrow => ArrowGeometry.BuildDouble(line.Start, line.End, line.Control, width, dashed),
                _ => null,
            };
        }

        /// <summary>Inside the polygon (even-odd, so a concave head notch is honored), or within
        /// <paramref name="tolerance"/> of its outline.</summary>
        private static bool HitsPolygon(ReadOnlySpan<Vec2> polygon, Vec2 p, double tolerance)
        {
            if (polygon.Length < 3)
                return false;

            bool inside = false;
            for (int i = 0, j = polygon.Length - 1; i < polygon.Length; j = i++)
            {
                Vec2 a = polygon[i], b = polygon[j];
                if ((a.Y > p.Y) != (b.Y > p.Y) &&
                    p.X < (b.X - a.X) * (p.Y - a.Y) / (b.Y - a.Y) + a.X)
                    inside = !inside;
            }

            if (inside)
                return true;

            if (tolerance > 0)
            {
                for (int i = 0, j = polygon.Length - 1; i < polygon.Length; j = i++)
                {
                    if (DistanceToSegment(p, polygon[j], polygon[i]) <= tolerance)
                        return true;
                }
            }

            return false;
        }

        /// <summary>Distance from <paramref name="p"/> to the line's whole spine: the chord, or the
        /// curve as the <see cref="QuadraticCurve.SampleCount"/>-segment polyline the image editor
        /// measures it by.</summary>
        private static double DistanceToSpine(ShapeLine line, Vec2 p)
        {
            if (line.Control is not { } control)
                return DistanceToSegment(p, line.Start, line.End);

            double best = Double.PositiveInfinity;
            var previous = line.Start;
            for (int i = 1; i <= QuadraticCurve.SampleCount; i++)
            {
                var next = QuadraticCurve.Eval(line.Start, control, line.End, (double)i / QuadraticCurve.SampleCount);
                best = Math.Min(best, DistanceToSegment(p, previous, next));
                previous = next;
            }

            return best;
        }

        private static double DistanceToSegment(Vec2 p, Vec2 a, Vec2 b)
        {
            var ab = b - a;
            double lengthSquared = ab.X * ab.X + ab.Y * ab.Y;
            if (!(lengthSquared > 0))
                return QuadraticCurve.Distance(p, a);

            var ap = p - a;
            double t = Math.Clamp((ap.X * ab.X + ap.Y * ab.Y) / lengthSquared, 0, 1);
            return QuadraticCurve.Distance(p, a + ab * t);
        }
    }

    /// <summary>
    /// One drag of a line kind's handle or body, from the press-time snapshot: every move is
    /// worked out from the line as it was at the press and the pointer now, never from the
    /// previous move, so the box re-fit each move writes cannot feed back into the drag (the
    /// gizmo's discipline). <see cref="ShapeHandlesControl"/> opens a gesture with
    /// <see cref="Label"/> and calls <see cref="Apply"/> inside one coalesced
    /// <c>EditItem</c> per pointer move.
    ///
    /// <list type="bullet">
    /// <item><b>Start / End</b>: the endpoint follows the pointer (Shift snaps the line's angle to
    /// <see cref="AngleSnap.StepDegrees"/> about the other end), and the bow keeps its height in
    /// pixels, as the image editor keeps <c>CurveOffset</c> absolute on an endpoint drag; only the
    /// lean (<see cref="ShapeContent.BendSkew"/>) is chord-relative there.</item>
    /// <item><b>Bend</b>: the image editor's mid handle (<see cref="CurveBend.TryBendFromPointer"/>,
    /// Shift steps by a tenth of the chord).</item>
    /// <item><b>Body</b>: the box center moves by the pointer delta, clamped so neither endpoint
    /// passes -1 or 2 canvases. No re-fit, and a zero delta writes back exactly what was
    /// there.</item>
    /// </list>
    /// Handle drags go through <see cref="ShapeLineLayout.Write"/>, which bakes any rotation into
    /// the endpoints and re-fits the box around the new line.
    /// </summary>
    public sealed class ShapeHandleDrag
    {
        private readonly ShapeLine _press;
        private readonly double _pressX, _pressY;
        private readonly Vec2 _pressPointer;
        private readonly Vec2 _grabOffset;
        private readonly double _canvasWidth, _canvasHeight, _pxScale;

        private ShapeHandleDrag(ShapeHandlePart part, string label, ShapeLine press, double pressX, double pressY,
            Vec2 pressPointer, Vec2 grabOffset, double canvasWidth, double canvasHeight, double pxScale)
        {
            Part = part;
            Label = label;
            _press = press;
            _pressX = pressX;
            _pressY = pressY;
            _pressPointer = pressPointer;
            _grabOffset = grabOffset;
            _canvasWidth = canvasWidth;
            _canvasHeight = canvasHeight;
            _pxScale = pxScale;
        }

        public ShapeHandlePart Part { get; }

        /// <summary>The undo label, in the item's own word ("arrow" or "line").</summary>
        public string Label { get; }

        /// <summary>The line as it was drawn at the press, rotation applied.</summary>
        public ShapeLine PressLine => _press;

        /// <summary>
        /// Snapshots a drag of <paramref name="part"/> pressed at <paramref name="pointer"/>, or null
        /// when there is nothing to drag (no part, not a line kind, no canvas). The grab offset (the
        /// pointer's distance from the grabbed handle's center) is kept for the whole drag, so
        /// nothing jumps on the press: the handle moves by exactly what the pointer moves.
        /// </summary>
        public static ShapeHandleDrag Begin(Item item, ShapeHandlePart part, Vec2 pointer,
            double canvasWidth, double canvasHeight, double pxScale)
        {
            if (part == ShapeHandlePart.None || item?.Content is not ShapeContent { IsLine: true } shape ||
                !(canvasWidth > 0) || !(canvasHeight > 0) || !(pxScale > 0) ||
                !ShapeLineLayout.TryResolve(item.Transform, shape, canvasWidth, canvasHeight, out var line))
                return null;

            var grab = part == ShapeHandlePart.Body ? default : pointer - ShapeHandleMath.HandlePoint(line, part);
            var noun = shape.Kind == ShapeKind.Line ? "line" : "arrow";
            var label = part switch
            {
                ShapeHandlePart.Start => $"Move {noun} start",
                ShapeHandlePart.End => $"Move {noun} end",
                ShapeHandlePart.Bend => $"Bend {noun}",
                _ => $"Move {noun}",
            };

            return new ShapeHandleDrag(part, label, line, item.Transform.X, item.Transform.Y,
                pointer, grab, canvasWidth, canvasHeight, pxScale);
        }

        /// <summary>The line a handle drag puts the pointer's handle under (the press line for a
        /// body drag, which moves the box instead).</summary>
        public ShapeLine LineAt(Vec2 pointer, bool snap)
        {
            var target = pointer - _grabOffset;
            switch (Part)
            {
                case ShapeHandlePart.Start:
                {
                    var start = Reach(target, _press.Start);
                    if (snap)
                        start = AngleSnap.Snap(_press.End, start, diagOnly: false);
                    return Rebow(start, _press.End);
                }

                case ShapeHandlePart.End:
                {
                    var end = Reach(target, _press.End);
                    if (snap)
                        end = AngleSnap.Snap(_press.Start, end, diagOnly: false);
                    return Rebow(_press.Start, end);
                }

                case ShapeHandlePart.Bend:
                {
                    // the handle rides the ink at t = 0.5, which is exactly the pointer for any
                    // bend this produces, so a press on the handle starts where it is
                    if (!Double.IsFinite(target.X) || !Double.IsFinite(target.Y) ||
                        !CurveBend.TryBendFromPointer(_press.Start, _press.End, target, snap,
                            ShapeHandleMath.StraightSnapDistance, out var offset, out var skew))
                        return _press;

                    return new ShapeLine(_press.Start, _press.End, ClampBend(offset / _press.Chord), skew);
                }

                default:
                    return _press;
            }
        }

        /// <summary>The box center a body drag puts the line at: the press center moved by the
        /// pointer's delta, clamped so neither endpoint passes -1 or 2 canvases. The clamp never
        /// pulls toward the canvas, so a line already outside that range can move back but not
        /// further out, and a press with no movement writes back the very values it read.</summary>
        public (double X, double Y) CenterAt(Vec2 pointer)
        {
            double dx = ClampDelta(pointer.X - _pressPointer.X, _press.Start.X, _press.End.X, _canvasWidth);
            double dy = ClampDelta(pointer.Y - _pressPointer.Y, _press.Start.Y, _press.End.Y, _canvasHeight);
            return (_pressX + dx / _canvasWidth, _pressY + dy / _canvasHeight);
        }

        /// <summary>Writes this drag at <paramref name="pointer"/> into <paramref name="item"/> (the
        /// session's live copy inside an <c>EditItem</c>). Anything that is no longer a line kind
        /// is left alone.</summary>
        public void Apply(Item item, Vec2 pointer, bool snap)
        {
            if (item?.Content is not ShapeContent { IsLine: true } shape)
                return;

            var transform = item.Transform ??= new ModelTransform();
            if (Part == ShapeHandlePart.Body)
            {
                (transform.X, transform.Y) = CenterAt(pointer);
                return;
            }

            ShapeLineLayout.Write(transform, shape, LineAt(pointer, snap), _canvasWidth, _canvasHeight, _pxScale);
        }

        /// <summary>The bow at a new chord, keeping its height in pixels. With no chord to measure
        /// against (either side), the chord-relative bow is kept as it is.</summary>
        private ShapeLine Rebow(Vec2 start, Vec2 end)
        {
            double chord = (end - start).Length;
            double bend = _press.Chord > 0 && chord > 0
                ? ClampBend(_press.Bend * _press.Chord / chord)
                : _press.Bend;
            return new ShapeLine(start, end, bend, _press.BendSkew);
        }

        /// <summary>An endpoint target held to -1..2 canvases on each axis (widened to wherever
        /// the endpoint already was, so a press never jumps it), the range a body drag keeps it
        /// in. A non-finite pointer leaves the endpoint where it was.</summary>
        private Vec2 Reach(Vec2 target, Vec2 pressed) => new Vec2(
            ReachAxis(target.X, pressed.X, _canvasWidth),
            ReachAxis(target.Y, pressed.Y, _canvasHeight));

        private static double ReachAxis(double value, double pressed, double extent)
        {
            if (!Double.IsFinite(value))
                return pressed;

            return Math.Clamp(value, Math.Min(-extent, pressed), Math.Max(2 * extent, pressed));
        }

        private static double ClampDelta(double delta, double a, double b, double extent)
        {
            if (!Double.IsFinite(delta))
                return 0;

            double low = Math.Min(0, -extent - Math.Min(a, b));
            double high = Math.Max(0, 2 * extent - Math.Max(a, b));
            return Math.Clamp(delta, low, high);
        }

        // NaN (a zero chord's 0/0) collapses to straight, the one value that is always valid
        private static double ClampBend(double bend) =>
            Double.IsNaN(bend) ? 0 : Math.Clamp(bend, -ShapeContent.MaxBend, ShapeContent.MaxBend);
    }
}
