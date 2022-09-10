#if SYSTEM_WINDOWS_VECTOR
using VECTOR = System.Windows.Vector;
using FLOAT = System.Double;
#elif SYSTEM_NUMERICS_VECTOR
using VECTOR = System.Numerics.Vector2;
using FLOAT = System.Single;
#elif UNITY
using VECTOR = UnityEngine.Vector2;
using FLOAT = System.Single;
#else
#error Unknown vector type -- must define one of SYSTEM_WINDOWS_VECTOR, SYSTEM_NUMERICS_VECTOR or UNITY
#endif

using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using System.Linq;
using Clowd.Drawing.Curves;
using RT.Serialization;

namespace Clowd.Drawing.Graphics
{
    public class GraphicPolyLine : GraphicRectangle
    {
        private List<Point> _points;
        [ClassifyIgnore] private List<Geometry> _segments;
        [ClassifyIgnore] private bool _drawing;
        [ClassifyIgnore] private Geometry _final;

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
                var group = new TransformGroup();
                group.Children.Add(new TranslateTransform(offsetX, offsetY));
                group.Children.Add(new ScaleTransform(scaleX, scaleY, geometryBounds.Left + offsetX, geometryBounds.Top + offsetY));
                _final.Transform = group;

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

            var widened = _final.GetWidenedPathGeometry(new Pen(null, LineWidth + (8 * uiscale.DpiScaleX)));
            var hit = widened.FillContains(rotatedPt);
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

            List<VECTOR> ppPts = CurvePreprocess.Linearize(_points.Select(p => (Vector)p).ToList(), 8);
            CubicBezier[] curves = CurveFit.Fit(ppPts, 2);

            StreamGeometry geo = new StreamGeometry();
            using (StreamGeometryContext gctx = geo.Open())
            {
                foreach (CubicBezier curve in curves)
                {
                    gctx.BeginFigure((Point)curve.p0, false, false);
                    gctx.BezierTo((Point)curve.p1, (Point)curve.p2, (Point)curve.p3, true, false);
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
            
            OnPropertyChanged(nameof(Bounds));
        }
    }
}
