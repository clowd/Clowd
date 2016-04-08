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
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Resources;
using System.IO;
using System.Linq;
using System.Windows.Shapes;
using DrawToolsLib.Curves;
using Path = System.Windows.Shapes.Path;

namespace DrawToolsLib.Graphics
{
    public class GraphicsPolyLine : GraphicsBase
    {
        public Point[] Points
        {
            get { return _points.ToArray(); }
            set
            {
                _points = value.ToList();
                OnPropertyChanged(nameof(Points));
            }
        }

        private List<Point> _points;
        private bool _drawing;
        private Geometry _geoCache;

        protected GraphicsPolyLine()
        {
        }

        public GraphicsPolyLine(DrawingCanvas canvas, Point start) : this(canvas.ObjectColor, canvas.LineWidth, start)
        {
        }

        public GraphicsPolyLine(Color objectColor, double lineWidth, Point start) : base(objectColor, lineWidth)
        {
            _drawing = true;
            _points = new List<Point>();
            _points.Add(start);
        }
        public GraphicsPolyLine(Color objectColor, double lineWidth, List<Point> points) : base(objectColor, lineWidth)
        {
            _drawing = false;
            _points = points;
        }

        public override Rect Bounds
        {
            get
            {
                if (!_drawing && _geoCache != null && !_geoCache.Bounds.IsEmpty)
                    return _geoCache.Bounds;

                var xmin = _points.Min(p => p.X);
                var xmax = _points.Max(p => p.X);
                var ymin = _points.Min(p => p.Y);
                var ymax = _points.Max(p => p.Y);

                return new Rect(xmin, ymin, xmax - xmin, ymax - ymin);
            }
        }
        internal override int HandleCount => 0;

        internal override void Draw(DrawingContext drawingContext)
        {
            if (drawingContext == null)
            {
                throw new ArgumentNullException("drawingContext");
            }

            if (_drawing)
            {
                DrawBasicPoints(drawingContext);
            }
            else
            {
                DrawCurvedLine(drawingContext);
            }
        }

        private void DrawBasicPoints(DrawingContext context)
        {
            var brush = new SolidColorBrush(ObjectColor);
            foreach (var pt in _points)
            {
                context.DrawEllipse(brush, null, pt, LineWidth, LineWidth);
            }
        }

        private void DrawCurvedLine(DrawingContext context)
        {
            var vectors = _points.Select(toVector).ToList();
            CubicBezier[] curves = CurveFit.Fit(vectors, 8);

            int i = 0;
            Pen pen = new Pen(new SolidColorBrush(ObjectColor), LineWidth);
            StreamGeometry geo = new StreamGeometry();
            using (StreamGeometryContext gctx = geo.Open())
            {
                for (int index = 0; index < curves.Length; index++)
                {
                    CubicBezier curve = curves[index];
                    gctx.BeginFigure(toWpfPoint(curve.p0), false, false);
                    gctx.BezierTo(toWpfPoint(curve.p1), toWpfPoint(curve.p2), toWpfPoint(curve.p3), true, false);
                }
            }
            geo.Freeze();
            _geoCache = geo.GetWidenedPathGeometry(new Pen(Brushes.Black, LineWidth));

            if (IsSelected)
            {
                context.DrawRectangle(null,
                    new Pen(new SolidColorBrush(Color.FromArgb(127, 255, 255, 255)), LineWidth),
                    Bounds);
                DashStyle dashStyle = new DashStyle();
                dashStyle.Dashes.Add(4);
                Pen dashedPen = new Pen(new SolidColorBrush(Color.FromArgb(127, 0, 0, 0)), LineWidth);
                dashedPen.DashStyle = dashStyle;
                context.DrawRectangle(null,
                    dashedPen,
                    Bounds);
                context.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromArgb(127, 0, 0, 0)), LineWidth * 3), geo);
            }

            context.DrawGeometry(null, pen, geo);
        }

        internal override bool Contains(Point point)
        {
            if (_geoCache == null)
                return false;
            return _geoCache.FillContains(point) ||
                _geoCache.StrokeContains(new Pen(Brushes.Black, LineHitTestWidth), point);
        }
        internal override Point GetHandle(int handleNumber)
        {
            return new Point(0, 0);
        }
        internal override Cursor GetHandleCursor(int handleNumber)
        {
            return HelperFunctions.DefaultCursor;
        }

        internal override void MoveHandleTo(Point point, int handleNumber)
        {
        }

        internal override void Move(double deltaX, double deltaY)
        {
            var pt = _points.ToArray();
            for (int i = 0; i < pt.Length; i++)
            {
                pt[i].X += deltaX;
                pt[i].Y += deltaY;
            }
            _points = pt.ToList();
            InvalidateVisual();
        }
        internal override int MakeHitTest(Point point)
        {
            if (IsSelected)
            {
                for (int i = 1; i <= HandleCount; i++)
                {
                    if (GetHandleRectangle(i).Contains(point))
                        return i;
                }
            }

            if (Contains(point))
                return 0;

            return -1;
        }
        internal override bool IntersectsWith(Rect rectangle)
        {
            return rectangle.IntersectsWith(Bounds);
        }
        public override GraphicsBase Clone()
        {
            return new GraphicsPolyLine(ObjectColor, LineWidth, _points) { ObjectId = ObjectId };
        }

        public void AddPoint(Point point)
        {
            _points.Add(point);
            InvalidateVisual();
        }

        public void FinishDrawing()
        {
            _drawing = false;
            var vectors = _points.Select(toVector).ToList();
            List<VECTOR> ppPts = CurvePreprocess.Linearize(vectors, 8);
            _points = ppPts.Select(toWpfPoint).ToList();
            InvalidateVisual();
        }


        private Point toWpfPoint(VECTOR p)
        {
            return new Point(VectorHelper.GetX(p), VectorHelper.GetY(p));
        }
        private VECTOR toVector(Point p)
        {
            return new VECTOR((FLOAT)p.X, (FLOAT)p.Y);
        }
    }
}
