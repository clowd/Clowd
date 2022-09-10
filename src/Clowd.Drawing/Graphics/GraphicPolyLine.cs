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
        [ClassifyIgnore] private CurveBuilder _realtime;
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
                if (_final == null) EndDrawing();

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
            if (_drawing) return -1;
            if (_final == null) EndDrawing();

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
            _realtime = new CurveBuilder(8, 1);
            _segments = new List<Geometry>();
            _points = new List<Point>();
            _final = null;
            _drawing = true;
        }

        internal void EndDrawing()
        {
            _drawing = false;
            _realtime = null;
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
        }

        internal void AddPoint(Point p)
        {
            if (!_drawing) throw new InvalidOperationException("Cannot add points after poly shape is closed");

            _points.Add(p);
            var result = _realtime.AddPoint((Vector)p);
            if (!result.WasChanged) return;

            // remove any changed segments
            while (_segments.Count > 0 && _segments.Count >= result.FirstChangedIndex)
            {
                _segments.RemoveAt(_segments.Count - 1);
            }

            List<Rect> all_bounds = new List<Rect>();

            // add any missing segments to master list
            for (int i = _segments.Count; i < _realtime.Curves.Count; i++)
            {
                CubicBezier curve = _realtime.Curves[i];
                StreamGeometry geo = new StreamGeometry();
                using (StreamGeometryContext gctx = geo.Open())
                {
                    gctx.BeginFigure((Point)curve.p0, false, false);
                    gctx.BezierTo((Point)curve.p1, (Point)curve.p2, (Point)curve.p3, true, false);
                }

                geo.Freeze();
                _segments.Add(geo);
                all_bounds.Add(geo.GetRenderBounds(new Pen(null, LineWidth)));
            }

            Left = Math.Min(Left, all_bounds.Min(x => x.Left));
            Right = Math.Max(Right, all_bounds.Max(x => x.Right));
            Top = Math.Min(Top, all_bounds.Min(x => x.Top));
            Bottom = Math.Max(Bottom, all_bounds.Max(x => x.Bottom));
            
            OnPropertyChanged(nameof(Bounds));
        }
    }
}
