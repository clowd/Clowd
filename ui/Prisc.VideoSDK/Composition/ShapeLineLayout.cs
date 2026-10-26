using System;
using Prisc.Geometry2D;
using Prisc.VideoSDK.Model;

namespace Prisc.VideoSDK.Composition
{
    /// <summary>
    /// A line kind's geometry on one canvas, in canvas pixels: the two endpoints and the bow,
    /// which stays chord-relative (<see cref="ShapeContent.Bend"/>,
    /// <see cref="ShapeContent.BendSkew"/>). A plain struct rather than a record on purpose:
    /// <see cref="Control"/> and <see cref="Handle"/> are worked out from the four stored values on
    /// every read, and a <c>with</c> copy of a record that cached them would carry stale ones.
    /// </summary>
    public readonly struct ShapeLine
    {
        public ShapeLine(Vec2 start, Vec2 end, double bend, double bendSkew)
        {
            Start = start;
            End = end;
            Bend = bend;
            BendSkew = bendSkew;
        }

        public Vec2 Start { get; }

        public Vec2 End { get; }

        /// <summary>The bow as a fraction of the chord, along its left normal (y down).</summary>
        public double Bend { get; }

        /// <summary>The bow's lean along the chord, as a fraction of it.</summary>
        public double BendSkew { get; }

        public double Chord => (End - Start).Length;

        /// <summary>The quadratic's control point, or null for a straight line (no bend, or a
        /// zero-length chord).</summary>
        public Vec2? Control => CurveBend.TryControlPoint(Start, End, Bend * Chord, BendSkew, out var c) ? c : null;

        /// <summary>Where the bend handle sits: on the ink at t = 0.5, the image editor's mid
        /// handle.</summary>
        public Vec2 Handle => CurveBend.HandlePoint(Start, End, Bend * Chord, BendSkew);
    }

    /// <summary>
    /// How a line kind's endpoints and box live in the model, and the one place a shape's kind
    /// changes. The endpoints are fractions of the item box, so a move (X/Y) or a stretch of the box
    /// carries the line with it and the composer needs nothing special; the editor's handles go the
    /// other way, from canvas points back to a box (<see cref="Write"/>), re-fitting it tightly
    /// around the line, padded by the ink's reach, so the box keeps meaning "where the arrow is" for
    /// the wipe band, the slide distance and the surround.
    /// </summary>
    /// <remarks>
    /// Every method takes the canvas it works in. The pad is output pixels, converted by the
    /// caller's <c>pxScale</c> (canvas px per output px): the editor passes the video rect and
    /// <see cref="FrameComposer.PixelScale"/>, the session works in output pixels with a scale of
    /// 1. Both give the same box fractions, because the pad scales with the canvas.
    /// </remarks>
    public static class ShapeLineLayout
    {
        /// <summary>The least padding on each side of a line's box, in output pixels, so a
        /// horizontal or vertical line never gets a box of zero height or width.</summary>
        public const double MinLineBoxPadPx = 8;

        /// <summary>
        /// The line as drawn at rest (no transition), in canvas pixels, with the item's rotation
        /// applied to the endpoints: the composer turns the whole drawing about the box center,
        /// and the arrow builder is rotation-equivariant, so this is exactly where the ink is.
        /// False for a closed kind or a null transform.
        /// </summary>
        public static bool TryResolve(Transform transform, ShapeContent shape, double canvasWidth,
            double canvasHeight, out ShapeLine line)
        {
            line = default;
            if (transform == null || shape == null || !shape.IsLine)
                return false;

            double cx = transform.X * canvasWidth;
            double cy = transform.Y * canvasHeight;
            double width = transform.Scale * canvasWidth;
            double height = (transform.ScaleY ?? transform.Scale) * canvasHeight;
            double left = cx - width / 2;
            double top = cy - height / 2;

            var start = new Vec2(left + shape.StartX * width, top + shape.StartY * height);
            var end = new Vec2(left + shape.EndX * width, top + shape.EndY * height);
            if (transform.Rotation != 0)
            {
                // clockwise on screen (y down), as SKCanvas.RotateDegrees turns the drawing
                double radians = transform.Rotation * Math.PI / 180;
                double cos = Math.Cos(radians), sin = Math.Sin(radians);
                start = Rotate(start, cx, cy, cos, sin);
                end = Rotate(end, cx, cy, cos, sin);
            }

            line = new ShapeLine(start, end, shape.Bend, shape.BendSkew);
            return true;
        }

        /// <summary>
        /// Writes a canvas-space line back into the model: the rotation is baked into the points
        /// (<see cref="Transform.Rotation"/> becomes 0, which draws the same thing), the box is
        /// re-fitted to the curve's tight bounds padded on every side by
        /// <c>Max(LinePadPx, MinLineBoxPadPx) · pxScale</c> (X, Y, Scale and ScaleY), the endpoints
        /// become fractions of that box (so each lies in [0, 1]), and the bow is clamped to the
        /// validated range. A zero-length chord keeps the bow it is given, clamped, since there is
        /// no chord to measure it against. Nothing is written for a canvas or scale that is not
        /// positive.
        /// </summary>
        public static void Write(Transform transform, ShapeContent shape, ShapeLine line,
            double canvasWidth, double canvasHeight, double pxScale)
        {
            ArgumentNullException.ThrowIfNull(transform);
            ArgumentNullException.ThrowIfNull(shape);
            if (!(canvasWidth > 0) || !(canvasHeight > 0) || !(pxScale > 0))
                return;

            // the box is measured around the curve as it will be stored, so clamp first
            var stored = new ShapeLine(line.Start, line.End,
                ClampSymmetric(line.Bend, ShapeContent.MaxBend),
                ClampSymmetric(line.BendSkew, ShapeContent.MaxBendSkew));
            var (minX, minY, maxX, maxY) = Bounds(stored);

            double pad = Math.Max(LinePadPx(shape), MinLineBoxPadPx) * pxScale;
            minX -= pad;
            minY -= pad;
            maxX += pad;
            maxY += pad;
            double width = maxX - minX;
            double height = maxY - minY;

            transform.Rotation = 0;
            transform.X = (minX + maxX) / 2 / canvasWidth;
            transform.Y = (minY + maxY) / 2 / canvasHeight;
            transform.Scale = width / canvasWidth;
            transform.ScaleY = height / canvasHeight;

            shape.StartX = (stored.Start.X - minX) / width;
            shape.StartY = (stored.Start.Y - minY) / height;
            shape.EndX = (stored.End.X - minX) / width;
            shape.EndY = (stored.End.Y - minY) / height;
            shape.Bend = stored.Bend;
            shape.BendSkew = stored.BendSkew;
        }

        /// <summary>Re-pads a line kind's box in output pixels, keeping its ink where it is: what a
        /// stroke width change (which changes the pad) or a change between line kinds needs. Bakes
        /// a rotation into the endpoints as <see cref="Write"/> does. Closed kinds are left
        /// alone.</summary>
        public static void Refit(Item item, OutputSettings output)
        {
            ArgumentNullException.ThrowIfNull(item);
            if (item.Content is not ShapeContent { IsLine: true } shape)
                return;

            var transform = Materialize(item);
            var (width, height) = OutputCanvas(output);
            if (TryResolve(transform, shape, width, height, out var line))
                Write(transform, shape, line, width, height, 1);
        }

        /// <summary>
        /// Changes the shape's kind to <paramref name="to"/>, the single entry point for every kind
        /// change, because a change between a closed kind and a line kind has to rebuild the
        /// geometry. All measured in output pixels:
        /// <list type="bullet">
        /// <item>Closed to closed: only the kind changes; the box and rotation stay.</item>
        /// <item>Line to line: the kind changes and the box is re-padded (<see cref="Refit"/>),
        /// since an arrow's head reaches further than a line's caps.</item>
        /// <item>Closed to line: the box, center and rotation stay, the stroke rises to at least
        /// <see cref="ShapeContent.MinLineStrokeWidth"/>, and the line runs straight along the box's
        /// diagonal from bottom left to top right, inset by the pad (at most a quarter of the box)
        /// so the ink stays inside the old box. With the inset equal to the pad, the box already is
        /// the tight padded box a handle drag would fit, so converting straight back gives the
        /// original box.</item>
        /// <item>Line to closed: a sliver box (one side under half the other) becomes a square
        /// about the same center, as long as its longer side (capped at four canvases); any other
        /// box and the rotation stay. A center off the canvas, which only a line kind may have,
        /// comes back inside [0, 1] so the inspector and gizmo clamps of a closed kind do not jump
        /// it on the first touch. The endpoint fields are left as they are: a closed kind ignores
        /// them, and the next conversion back overwrites them.</item>
        /// </list>
        /// </summary>
        public static void ConvertKind(Item item, ShapeKind to, OutputSettings output)
        {
            ArgumentNullException.ThrowIfNull(item);
            if (item.Content is not ShapeContent shape)
                throw new ArgumentException("The item is not a shape.", nameof(item));

            bool fromLine = shape.IsLine;
            bool toLine = ShapeContent.IsLineKind(to);
            var transform = Materialize(item);
            shape.Kind = to;

            if (!fromLine && !toLine)
                return;

            if (fromLine && toLine)
            {
                Refit(item, output);
                return;
            }

            var (canvasWidth, canvasHeight) = OutputCanvas(output);
            double boxW = transform.Scale * canvasWidth;
            double boxH = transform.ScaleY.Value * canvasHeight;

            if (toLine)
            {
                shape.StrokeWidth = LineStrokeWidth(shape);
                double pad = Math.Max(LinePadPx(shape), MinLineBoxPadPx);
                double ix = Math.Min(pad, boxW / 4);
                double iy = Math.Min(pad, boxH / 4);
                shape.StartX = Fraction(ix, boxW);
                shape.StartY = 1 - Fraction(iy, boxH);
                shape.EndX = 1 - Fraction(ix, boxW);
                shape.EndY = Fraction(iy, boxH);
                shape.Bend = 0;
                shape.BendSkew = 0;
                return;
            }

            if (Math.Min(boxW, boxH) < 0.5 * Math.Max(boxW, boxH))
            {
                double side = Math.Min(Math.Max(boxW, boxH), Math.Min(4 * canvasWidth, 4 * canvasHeight));
                transform.Scale = side / canvasWidth;
                transform.ScaleY = side / canvasHeight;
            }

            transform.X = Math.Clamp(transform.X, 0, 1);
            transform.Y = Math.Clamp(transform.Y, 0, 1);
        }

        /// <summary>How far a line kind's ink reaches from its spine, in output pixels, without the
        /// <see cref="MinLineBoxPadPx"/> floor: an arrow's widest possible head
        /// (<see cref="ArrowGeometry.MaxInkRadius"/>), a line's round cap. 0 for closed kinds,
        /// whose ink is inside their box.</summary>
        public static double LinePadPx(ShapeContent shape)
        {
            ArgumentNullException.ThrowIfNull(shape);
            double width = LineStrokeWidth(shape);
            return shape.Kind switch
            {
                ShapeKind.Arrow or ShapeKind.DoubleArrow => ArrowGeometry.MaxInkRadius(width),
                ShapeKind.Line => width / 2,
                _ => 0,
            };
        }

        /// <summary>The width a line kind draws at, in output pixels: the stored stroke width
        /// raised to <see cref="ShapeContent.MinLineStrokeWidth"/> and capped at the validated
        /// maximum, with a NaN drawing the thinnest line rather than nothing.</summary>
        public static double LineStrokeWidth(ShapeContent shape)
        {
            ArgumentNullException.ThrowIfNull(shape);
            return shape.StrokeWidth >= ShapeContent.MinLineStrokeWidth
                ? Math.Min(shape.StrokeWidth, ShapeContent.MaxStrokeWidth)
                : ShapeContent.MinLineStrokeWidth;
        }

        /// <summary>The output canvas a project's fractions are measured against, falling back
        /// to 1920×1080 while the output is unknown (a 16:9 canvas, the editor's default).</summary>
        internal static (double Width, double Height) OutputCanvas(OutputSettings output) =>
            output is { WidthPx: > 0, HeightPx: > 0 } ? (output.WidthPx, output.HeightPx) : (1920, 1080);

        /// <summary>The tight bounds of the line's curve (its endpoints when straight).</summary>
        private static (double MinX, double MinY, double MaxX, double MaxY) Bounds(ShapeLine line)
        {
            if (line.Control is { } control)
                return QuadraticCurve.Bounds(line.Start, control, line.End);

            return (Math.Min(line.Start.X, line.End.X), Math.Min(line.Start.Y, line.End.Y),
                    Math.Max(line.Start.X, line.End.X), Math.Max(line.Start.Y, line.End.Y));
        }

        /// <summary>The item's transform with its free height written out, the state every shape
        /// lives in (<see cref="Project.Normalize"/>), so the box maths never meets a null.</summary>
        private static Transform Materialize(Item item)
        {
            var transform = item.Transform ??= new Transform();
            transform.ScaleY ??= transform.Scale;
            return transform;
        }

        private static Vec2 Rotate(Vec2 p, double cx, double cy, double cos, double sin)
        {
            double dx = p.X - cx, dy = p.Y - cy;
            return new Vec2(cx + dx * cos - dy * sin, cy + dx * sin + dy * cos);
        }

        private static double Fraction(double inset, double extent) => extent > 0 ? inset / extent : 0;

        // NaN collapses to 0 (straight), the one value that is always valid
        private static double ClampSymmetric(double value, double max) =>
            Double.IsNaN(value) ? 0 : Math.Clamp(value, -max, max);
    }
}
