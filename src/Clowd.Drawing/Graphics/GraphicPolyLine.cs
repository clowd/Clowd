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

namespace Clowd.Drawing.Graphics
{
    public class GraphicPolyLine : GraphicRectangle
    {
        public Point[] Points
        {
            get
            {
                return _points.ToArray();
            }
            set
            {
                _points = new List<Point>();
                _builder = new CurveBuilder(4, 2);

                foreach (var v in value)
                    AddPointInternal(v, false);

                UpdateGeometry();
                OnPropertyChanged(nameof(Points));
            }
        }

        private List<Point> _points;
        private CurveBuilder _builder;
        private Geometry _geometry;
        private Rect _vectorBounds;

        protected GraphicPolyLine() // serializer constructor
        {
        }

        public GraphicPolyLine(Color objectColor, double lineWidth, Point start)
            : base(objectColor, lineWidth, new Rect(start, new Size(1, 1)))
        {
            this.Points = new[] { start };
        }

        public GraphicPolyLine(Color objectColor, double lineWidth, Rect rect, double angle, Point[] points) // clone constructor
            : base(objectColor, lineWidth, rect, angle)
        {
            this.Points = points;
        }

        internal override void DrawRectangle(DrawingContext context)
        {
            var desiredBounds = UnrotatedBounds;
            double offsetX = desiredBounds.Left - _vectorBounds.Left;
            double offsetY = desiredBounds.Top - _vectorBounds.Top;
            double scaleX = (desiredBounds.Right - (_vectorBounds.Left + offsetX)) / _vectorBounds.Width;
            double scaleY = (desiredBounds.Bottom - (_vectorBounds.Top + offsetY)) / _vectorBounds.Height;

            var group = new TransformGroup();
            group.Children.Add(new TranslateTransform(offsetX, offsetY));
            group.Children.Add(new ScaleTransform(scaleX, scaleY, _vectorBounds.Left + offsetX, _vectorBounds.Top + offsetY));
            _geometry.Transform = group;

            Pen pen = new Pen(new SolidColorBrush(ObjectColor), LineWidth);
            context.DrawGeometry(null, pen, _geometry);
        }

        internal override int MakeHitTest(Point point, DpiScale uiscale)
        {
            var rotatedPt = UnapplyRotation(point);
            if (IsSelected)
            {
                for (int i = 1; i <= HandleCount; i++)
                {
                    if (GetHandleRectangle(i, uiscale).Contains(rotatedPt))
                        return i;
                }
            }

            var widened = _geometry.GetWidenedPathGeometry(new Pen(Brushes.Black, 8));
            var hit = widened.FillContains(rotatedPt);
            return hit ? 0 : -1;
        }

        internal void AddPoint(Point p)
        {
            AddPointInternal(p, true);
        }

        private void AddPointInternal(Point p, bool updateGeometry)
        {
            Left = Math.Min(Left, p.X);
            Right = Math.Max(Right, p.X);
            Top = Math.Min(Top, p.Y);
            Bottom = Math.Max(Bottom, p.Y);

            _points.Add(p);

            var vector = new VECTOR((FLOAT)p.X, (FLOAT)p.Y);
            _builder.AddPoint(vector);

            if (updateGeometry)
                UpdateGeometry();
        }

        private void UpdateGeometry()
        {
            var curves = _builder.Curves;
            var curveLength = curves.Count();

            Point toWpfPoint(VECTOR wpp)
            {
                return new Point(VectorHelper.GetX(wpp), VectorHelper.GetY(wpp));
            }

            StreamGeometry geo = new StreamGeometry();
            using (StreamGeometryContext gctx = geo.Open())
            {
                for (int index = 0; index < curveLength; index++)
                {
                    CubicBezier curve = curves[index];
                    gctx.BeginFigure(toWpfPoint(curve.p0), false, false);
                    gctx.BezierTo(toWpfPoint(curve.p1), toWpfPoint(curve.p2), toWpfPoint(curve.p3), true, false);
                }
            }

            _geometry = geo;

            var xmin = _points.Min(p => p.X);
            var xmax = _points.Max(p => p.X);
            var ymin = _points.Min(p => p.Y);
            var ymax = _points.Max(p => p.Y);

            _vectorBounds = new Rect(new Point(xmin, ymin), new Point(xmax, ymax));

            OnPropertyChanged(nameof(Points)); // causes re-render
        }
    }
}
