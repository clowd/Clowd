using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Resources;
using System.IO;

namespace DrawToolsLib.Graphics
{
    [Serializable]
    public class GraphicsPolyLine : GraphicsBase
    {
        public Point[] Points
        {
            get { return _points; }
            set
            {
                if (Equals(value, _points)) return;
                _points = value;
                OnPropertyChanged();
            }
        }

        protected PathGeometry pathGeometry;
        private Point[] _points;

        static readonly Cursor HandleCursor;
        static GraphicsPolyLine()
        {
            MemoryStream stream = new MemoryStream(Properties.Resources.PolyHandle);
            HandleCursor = new Cursor(stream);
        }

        protected GraphicsPolyLine()
        {
        }
        public GraphicsPolyLine(DrawingCanvas canvas, Point[] points)
            : this(canvas.ObjectColor, canvas.LineWidth, points)
        {
        }

        public GraphicsPolyLine(Color objectColor, double lineWidth, Point[] points)
            : base(objectColor, lineWidth)
        {
            MakeGeometryFromPoints(points);
        }

        public Point[] GetPoints()
        {
            return Points;
        }
        public void AddPoint(Point point)
        {
            LineSegment segment = new LineSegment(point, true);
            segment.IsSmoothJoin = true;

            pathGeometry.Figures[0].Segments.Add(segment);
            MakePoints();   // keep points array up to date
        }

        private void MakeGeometryFromPoints(Point[] points)
        {
            if (points == null)
            {
                // This really sucks, XML file contains Points object,
                // but list of points is empty. Do something to prevent program crush.
                points = new Point[2];
            }

            PathFigure figure = new PathFigure();
            if (points.Length >= 1)
            {
                figure.StartPoint = points[0];
            }
            //TODO: http://blog.scottlogic.com/2010/11/22/adding-a-smoothed-line-series-bezier-curve-to-a-visiblox-chart.html
            for (int i = 1; i < points.Length; i++)
            {
                LineSegment segment = new LineSegment(points[i], true);
                segment.IsSmoothJoin = true;
                figure.Segments.Add(segment);
            }

            pathGeometry = new PathGeometry();
            pathGeometry.Figures.Add(figure);
            MakePoints();
        }
        private void MakePoints()
        {
            Points = new Point[pathGeometry.Figures[0].Segments.Count + 1];
            Points[0] = pathGeometry.Figures[0].StartPoint;

            for (int i = 0; i < pathGeometry.Figures[0].Segments.Count; i++)
            {
                Points[i + 1] = ((LineSegment)(pathGeometry.Figures[0].Segments[i])).Point;
            }
        }

        public override Rect Bounds => pathGeometry.Bounds;
        internal override int HandleCount => pathGeometry.Figures[0].Segments.Count + 1;

        internal override void Draw(DrawingContext drawingContext)
        {
            if (drawingContext == null)
            {
                throw new ArgumentNullException("drawingContext");
            }

            drawingContext.DrawGeometry(
                null,
                new Pen(new SolidColorBrush(ObjectColor), LineWidth),
                pathGeometry);

            base.Draw(drawingContext);
        }
        internal override bool Contains(Point point)
        {
            return pathGeometry.FillContains(point) ||
                pathGeometry.StrokeContains(new Pen(Brushes.Black, LineHitTestWidth), point);
        }
        internal override Point GetHandle(int handleNumber)
        {
            if (handleNumber < 1)
                handleNumber = 1;

            if (handleNumber > Points.Length)
                handleNumber = Points.Length;

            return Points[handleNumber - 1];
        }
        internal override Cursor GetHandleCursor(int handleNumber)
        {
            return HandleCursor;
        }
        internal override void MoveHandleTo(Point point, int handleNumber)
        {
            if (handleNumber == 1)
            {
                pathGeometry.Figures[0].StartPoint = point;
            }
            else
            {
                ((LineSegment)(pathGeometry.Figures[0].Segments[handleNumber - 2])).Point = point;
            }

            MakePoints();
        }
        internal override void Move(double deltaX, double deltaY)
        {
            for (int i = 0; i < Points.Length; i++)
            {
                Points[i].X += deltaX;
                Points[i].Y += deltaY;
            }

            MakeGeometryFromPoints(Points);
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
            RectangleGeometry rg = new RectangleGeometry(rectangle);

            PathGeometry p = Geometry.Combine(rg, pathGeometry, GeometryCombineMode.Intersect, null);

            return (!p.IsEmpty());
        }
        public override GraphicsBase Clone()
        {
            return new GraphicsPolyLine(ObjectColor, LineWidth, Points) { ObjectId = ObjectId };
        }
    }
}
