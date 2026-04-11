using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using Avalonia;
using Avalonia.Media;
using Clowd.Drawing.Curves;

namespace Clowd.Drawing.Graphics
{
    [GraphicDesc("PolyLine", Skills = Skill.Angle | Skill.Color | Skill.Stroke)]
    public class GraphicPolyLine : GraphicRectangle
    {
        // Raw user input points (in their original content-space positions).
        // Public so System.Text.Json can round-trip; on deserialize OnDeserialized
        // re-runs the bezier fit.
        public List<Point> Points
        {
            get => _points;
            set => _points = value ?? new List<Point>();
        }

        private List<Point> _points = new();

        // Smoothed bezier curves built when the user lifts the pen.
        [JsonIgnore] private CubicBezier[]? _curves;

        // Cached StreamGeometry built from _curves once at EndDrawing.
        [JsonIgnore] private StreamGeometry? _final;

        // Bounds at the moment we finalised, used to compute scale-on-resize.
        [JsonIgnore] private Rect _originalBounds;

        [JsonIgnore] private bool _drawing;

        public GraphicPolyLine() // serializer constructor
        { }

        protected override void OnDeserializedCore()
        {
            // Rebuild the bezier curve cache from the deserialized points.
            _drawing = false;
            if (_points.Count >= 2)
            {
                EndDrawing(false);
            }
        }

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
                var half = LineWidth / 2;
                return new Rect(Left - half, Top - half, Right - Left + LineWidth, Bottom - Top + LineWidth);
            }
        }

        internal override void DrawRectangle(DrawingContext context)
        {
            var pen = new Pen(new SolidColorBrush(ObjectColor), LineWidth);

            if (_drawing)
            {
                // In-progress: draw raw segments live as the user moves.
                for (int i = 1; i < _points.Count; i++)
                {
                    context.DrawLine(pen, _points[i - 1], _points[i]);
                }
                return;
            }

            if (_final == null)
                EndDrawing(false);

            if (_final == null)
                return;

            var dest = UnrotatedBounds;
            var src = _originalBounds;

            // If the user resized the polyline, scale the cached geometry to fit
            // the new bounds via a PushTransform. (Avalonia geometries are
            // immutable so we can't mutate Geometry.Transform like in WPF.)
            if (src.Width > 0 && src.Height > 0 &&
                (Math.Abs(dest.Width - src.Width) > 0.001 ||
                 Math.Abs(dest.Height - src.Height) > 0.001 ||
                 Math.Abs(dest.X - src.X) > 0.001 ||
                 Math.Abs(dest.Y - src.Y) > 0.001))
            {
                var sx = dest.Width / src.Width;
                var sy = dest.Height / src.Height;
                var transform =
                    Matrix.CreateTranslation(-src.X, -src.Y) *
                    Matrix.CreateScale(sx, sy) *
                    Matrix.CreateTranslation(dest.X, dest.Y);

                using (context.PushTransform(transform))
                {
                    context.DrawGeometry(null, pen, _final);
                }
            }
            else
            {
                context.DrawGeometry(null, pen, _final);
            }
        }

        internal override int MakeHitTest(Point point, DpiScale uiscale)
        {
            if (_drawing) return -1;

            var rotatedPt = UnapplyRotation(point);

            if (IsSelected)
            {
                for (int i = 1; i <= HandleCount; i++)
                {
                    if (GetHandleRectangle(i, uiscale).Contains(rotatedPt))
                        return i;
                }
            }

            if (_curves == null) return -1;

            // Threshold = stroke + 8 px screen-space tolerance.
            var threshold = (LineWidth + 8 * uiscale.DpiScaleX) / 2;

            // Map the test point from current bounds back to original-bounds space.
            var dest = UnrotatedBounds;
            var src = _originalBounds;
            Point sample = rotatedPt;
            if (src.Width > 0 && src.Height > 0 && (dest.Width > 0 && dest.Height > 0))
            {
                var nx = (rotatedPt.X - dest.X) / dest.Width;
                var ny = (rotatedPt.Y - dest.Y) / dest.Height;
                sample = new Point(src.X + nx * src.Width, src.Y + ny * src.Height);
            }

            // Sample each cubic and find the minimum distance.
            const int samplesPerCurve = 12;
            foreach (var c in _curves)
            {
                Point prev = (Point)c.p0;
                for (int i = 1; i <= samplesPerCurve; i++)
                {
                    var t = i / (double)samplesPerCurve;
                    var s = (Point)c.Sample(t);
                    if (DistancePointToSegment(prev, s, sample) <= threshold)
                        return 0;
                    prev = s;
                }
            }

            return -1;
        }

        private static double DistancePointToSegment(Point a, Point b, Point p)
        {
            var dx = b.X - a.X;
            var dy = b.Y - a.Y;
            var lenSq = dx * dx + dy * dy;
            if (lenSq < 1e-12)
            {
                var ddx = p.X - a.X;
                var ddy = p.Y - a.Y;
                return Math.Sqrt(ddx * ddx + ddy * ddy);
            }
            var t = ((p.X - a.X) * dx + (p.Y - a.Y) * dy) / lenSq;
            t = Math.Clamp(t, 0.0, 1.0);
            var qx = a.X + t * dx;
            var qy = a.Y + t * dy;
            var ex = p.X - qx;
            var ey = p.Y - qy;
            return Math.Sqrt(ex * ex + ey * ey);
        }

        internal void BeginDrawing()
        {
            _points = new List<Point>();
            _curves = null;
            _final = null;
            _drawing = true;
        }

        internal void EndDrawing(bool updateBounds)
        {
            _drawing = false;

            if (_points.Count >= 2)
            {
                var input = _points.Select(p => new Vector(p.X, p.Y)).ToList();
                var processed = CurvePreprocess.Linearize(input, 8);
                _curves = processed.Count >= 2
                    ? CurveFit.Fit(processed, 2)
                    : Array.Empty<CubicBezier>();
            }
            else
            {
                _curves = Array.Empty<CubicBezier>();
            }

            // Build the StreamGeometry once.
            var geo = new StreamGeometry();
            using (var sgc = geo.Open())
            {
                foreach (var c in _curves)
                {
                    sgc.BeginFigure((Point)c.p0, isFilled: false);
                    sgc.CubicBezierTo((Point)c.p1, (Point)c.p2, (Point)c.p3);
                    sgc.EndFigure(isClosed: false);
                }
            }
            _final = geo;

            if (updateBounds && _points.Count > 0)
            {
                Left = _points.Min(p => p.X);
                Right = _points.Max(p => p.X);
                Top = _points.Min(p => p.Y);
                Bottom = _points.Max(p => p.Y);
            }

            Normalize(); // recompute CenterOfRotation
            _originalBounds = UnrotatedBounds;
            OnPropertyChanged(nameof(Bounds));
        }

        internal void AddPoint(Point p)
        {
            if (!_drawing) throw new InvalidOperationException("Cannot add points after poly shape is closed");

            if (!_points.Any())
            {
                _points.Add(p);
                Left = Right = p.X;
                Top = Bottom = p.Y;
                return;
            }

            _points.Add(p);

            Left = Math.Min(Left, p.X);
            Right = Math.Max(Right, p.X);
            Top = Math.Min(Top, p.Y);
            Bottom = Math.Max(Bottom, p.Y);

            // If the bounds didn't grow, the property setters won't notify, so
            // poke explicitly so the canvas re-renders.
            OnPropertyChanged(nameof(Bounds));
        }
    }
}
