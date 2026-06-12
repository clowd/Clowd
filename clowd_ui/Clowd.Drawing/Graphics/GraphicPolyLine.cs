using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Media;
using Clowd.Drawing.Curves;
using RT.Serialization;

namespace Clowd.Drawing.Graphics
{
    [GraphicDesc("PolyLine", Skills = Skill.Angle | Skill.Color | Skill.Stroke)]
    public class GraphicPolyLine : GraphicRectangle
    {
        private List<Point> _points;
        [ClassifyIgnore] private List<Geometry> _segments;
        [ClassifyIgnore] private bool _drawing;
        [ClassifyIgnore] private Geometry _final;

#if DEBUG
        static GraphicPolyLine()
        {
            // §6 smoke assert: Geometry.Transform must be respected by GetRenderBounds (and hit tests).
            // If this ever fails, the pre-decided fallback is to bake the transform into the points at draw time.
            try
            {
                var pen = new Pen(Brushes.Black, 2);
                var g = new LineGeometry(new Point(0, 0), new Point(10, 0));
                var before = g.GetRenderBounds(pen);
                g.Transform = new TranslateTransform(5, 5);
                var after = g.GetRenderBounds(pen);
                System.Diagnostics.Debug.Assert(
                    Math.Abs(after.X - before.X - 5) < 0.01 && Math.Abs(after.Y - before.Y - 5) < 0.01,
                    "Geometry.Transform is not respected by GetRenderBounds — GraphicPolyLine bounds/hit-tests will be wrong.");
            }
            catch
            {
                // Avalonia platform not initialized (e.g. bare unit test) — skip the smoke check.
            }
        }
#endif

        protected GraphicPolyLine() // serializer constructor
        { }

        public GraphicPolyLine(Color objectColor, double lineWidth, Point start)
            : base(objectColor, lineWidth, new Rect(start, new Size(0, 0)))
        {
            BeginDrawing();
            AddPoint(start);
        }

        public override Rect Bounds
        {
            get
            {
                if (_final != null)
                    return _final.GetRenderBounds(new Pen(null, LineWidth));

                var half = LineWidth / 2;
                return new Rect(Left - half, Top - half, Right - Left + LineWidth, Bottom - Top + LineWidth);
            }
        }

        internal override void DrawRectangle(DrawingContext context)
        {
            Pen pen = new Pen(new SolidColorBrush(ObjectColor), LineWidth);
            if (_drawing)
            {
                foreach (var geo in _segments)
                {
                    context.DrawGeometry(null, pen, geo);
                }
            }
            else
            {
                if (_final == null) EndDrawing(false);

                // geometry points will be at the original location they were drawn. we need to translate them into
                // the correct location as this rectangle may have been moved or resized.
                _final.Transform = null;
                var geometryBounds = _final.GetRenderBounds(pen);
                var desiredBounds = UnrotatedBounds;
                double offsetX = desiredBounds.Left - geometryBounds.Left;
                double offsetY = desiredBounds.Top - geometryBounds.Top;
                double scaleX = (desiredBounds.Right - (geometryBounds.Left + offsetX)) / geometryBounds.Width;
                double scaleY = (desiredBounds.Bottom - (geometryBounds.Top + offsetY)) / geometryBounds.Height;

                // we set this on the geometry instead of as a PushTransform so that it will also be
                // respected for MakeHitTest. Render is called every time a property updates, so this should work fine.
                // (WPF used TransformGroup{Translate, ScaleAt}; same row-vector order: translate first, then scale.)
                var scaleCenter = new Point(geometryBounds.Left + offsetX, geometryBounds.Top + offsetY);
                _final.Transform = new MatrixTransform(
                    Matrix.CreateTranslation(offsetX, offsetY) * MatrixHelper.ScaleAt(scaleX, scaleY, scaleCenter));

                context.DrawGeometry(null, pen, _final);
            }
        }

        internal override int MakeHitTest(Point point, DpiScale uiscale)
        {
            if (_drawing || _final == null) return -1;

            var rotatedPt = UnapplyRotation(point);

            if (IsSelected)
            {
                for (int i = 1; i <= HandleCount; i++)
                {
                    if (GetHandleRectangle(i, uiscale).Contains(rotatedPt))
                        return i;
                }
            }

            // decision #25: GetWidenedPathGeometry + FillContains → StrokeContains with a real (black) brush pen.
            var hit = _final.StrokeContains(new Pen(Brushes.Black, LineWidth + (8 * uiscale.DpiScaleX)), rotatedPt);
            return hit ? 0 : -1;
        }

        internal void BeginDrawing()
        {
            _segments = new List<Geometry>();
            _points = new List<Point>();
            _final = null;
            _drawing = true;
        }

        internal void EndDrawing(bool updateBounds)
        {
            _drawing = false;
            _segments = null;

            List<Vector> ppPts = CurvePreprocess.Linearize(_points.Select(p => new Vector(p.X, p.Y)).ToList(), 8);
            CubicBezier[] curves = CurveFit.Fit(ppPts, 2);

            StreamGeometry geo = new StreamGeometry();
            using (StreamGeometryContext gctx = geo.Open())
            {
                foreach (CubicBezier curve in curves)
                {
                    gctx.BeginFigure(new Point(curve.p0.X, curve.p0.Y), false);
                    gctx.CubicBezierTo(new Point(curve.p1.X, curve.p1.Y), new Point(curve.p2.X, curve.p2.Y),
                                       new Point(curve.p3.X, curve.p3.Y));
                    gctx.EndFigure(false);
                }
            }

            _final = geo;

            if (updateBounds)
            {
                Left = _points.Min(p => p.X);
                Right = _points.Max(p => p.X);
                Top = _points.Min(p => p.Y);
                Bottom = _points.Max(p => p.Y);
            }

            Normalize(); // set CenterOfRotation
            OnPropertyChanged(nameof(Bounds));
        }

        internal void AddPoint(Point p)
        {
            if (!_drawing) throw new InvalidOperationException("Cannot add points after poly shape is closed");

            if (!_points.Any())
            {
                _points.Add(p);
                return;
            }

            var startPoint = _points.Last();
            _points.Add(p);

            var geometry = new LineGeometry(startPoint, p);
            _segments.Add(geometry);

            Left = Math.Min(Left, p.X);
            Right = Math.Max(Right, p.X);
            Top = Math.Min(Top, p.Y);
            Bottom = Math.Max(Bottom, p.Y);

            // If none of the above update actually change (eg. drawing a point inside the bounds)
            // then it will not fire a PropertyChanged event, which is what triggers redraw.
            OnPropertyChanged(nameof(Bounds));
        }
    }
}
