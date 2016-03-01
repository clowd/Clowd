using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Resources;
using System.IO;


namespace DrawToolsLib
{
    /// <summary>
    ///  PolyLine graphics object.
    /// </summary>
    public class GraphicsPolyLine : GraphicsBase
    {
        #region Class Members

        // This class member contains all required geometry.
        // It is ready for drawing and hit testing.
        protected PathGeometry pathGeometry;

        // Points from pathGeometry, including StartPoint
        protected Point[] points;

        static Cursor handleCursor;

        #endregion Class Members

        #region Constructors

        public GraphicsPolyLine(Point[] points, double lineWidth, Color objectColor, double actualScale)
        {
            Fill(points, lineWidth, objectColor, actualScale);

            //RefreshDrawng();
        }


        public GraphicsPolyLine()
            :
            this(new Point[] { new Point(0.0, 0.0), new Point(100.0, 100.0) }, 1.0, Colors.Black, 1.0)
        {
        }

        static GraphicsPolyLine()
        {
            MemoryStream stream = new MemoryStream(Properties.Resources.PolyHandle);

            handleCursor = new Cursor(stream);
        }

        #endregion Constructors

        #region Other Functions

        /// <summary>
        /// Convert geometry to array of points.
        /// </summary>
        void MakePoints()
        {
            points = new Point[pathGeometry.Figures[0].Segments.Count + 1];

            points[0] = pathGeometry.Figures[0].StartPoint;

            for (int i = 0; i < pathGeometry.Figures[0].Segments.Count; i++)
            {
                points[i + 1] = ((LineSegment)(pathGeometry.Figures[0].Segments[i])).Point;
            }
        }

        /// <summary>
        /// Return array of points.
        /// </summary>
        public Point[] GetPoints()
        {
            return points;
        }

        /// <summary>
        /// Convert array of points to geometry.
        /// </summary>
        void MakeGeometryFromPoints(ref Point[] points)
        {
            if ( points == null )
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

            for (int i = 1; i < points.Length; i++)
            {
                LineSegment segment = new LineSegment(points[i], true);
                segment.IsSmoothJoin = true;

                figure.Segments.Add(segment);
            }

            pathGeometry = new PathGeometry();

            pathGeometry.Figures.Add(figure);

            MakePoints();   // keep points array up to date
        }

        // Called from constructors
        void Fill(Point[] points, double lineWidth, Color objectColor, double actualScale)
        {
            MakeGeometryFromPoints(ref points);

            this.graphicsLineWidth = lineWidth;
            this.graphicsObjectColor = objectColor;
            this.graphicsActualScale = actualScale;
        }


        /// <summary>
        /// Add new point (line segment)
        /// </summary>
        public void AddPoint(Point point)
        {
            LineSegment segment = new LineSegment(point, true);
            segment.IsSmoothJoin = true;

            pathGeometry.Figures[0].Segments.Add(segment);

            MakePoints();   // keep points array up to date
        }

        #endregion Other Functions

        #region Overrides

        /// <summary>
        /// Draw object
        /// </summary>
        public override void Draw(DrawingContext drawingContext)
        {
            if ( drawingContext == null )
            {
                throw new ArgumentNullException("drawingContext");
            }

            drawingContext.DrawGeometry(
                null,
                new Pen(new SolidColorBrush(ObjectColor), ActualLineWidth),
                pathGeometry);

            base.Draw(drawingContext);
        }

        /// <summary>
        /// Test whether object contains point
        /// </summary>
        public override bool Contains(Point point)
        {
            return pathGeometry.FillContains(point) ||
                pathGeometry.StrokeContains(new Pen(Brushes.Black, LineHitTestWidth), point);
        }

        /// <summary>
        /// XML serialization support
        /// </summary>
        public override PropertiesGraphicsBase CreateSerializedObject()
        {
            return new PropertiesGraphicsPolyLine(this);
        }


        /// <summary>
        /// Get number of handles
        /// </summary>
        public override int HandleCount
        {
            get
            {
                return pathGeometry.Figures[0].Segments.Count + 1;
            }
        }


        /// <summary>
        /// Get handle point by 1-based number
        /// </summary>
        public override Point GetHandle(int handleNumber)
        {
            if (handleNumber < 1)
                handleNumber = 1;

            if (handleNumber > points.Length)
                handleNumber = points.Length;

            return points[handleNumber - 1];
        }

        /// <summary>
        /// Get cursor for the handle
        /// </summary>
        public override Cursor GetHandleCursor(int handleNumber)
        {
            return handleCursor;
        }

        /// <summary>
        /// Move handle to new point (resizing).
        /// handleNumber is 1-based.
        /// </summary>
        public override void MoveHandleTo(Point point, int handleNumber)
        {
            if ( handleNumber == 1 )
            {
                pathGeometry.Figures[0].StartPoint = point;
            }
            else
            {
                ((LineSegment)(pathGeometry.Figures[0].Segments[handleNumber-2])).Point = point;
            }

            MakePoints();

            RefreshDrawing();
        }


        /// <summary>
        /// Move object
        /// </summary>
        public override void Move(double deltaX, double deltaY)
        {
            for (int i = 0; i < points.Length; i++ )
            {
                points[i].X += deltaX;
                points[i].Y += deltaY;
            }

            MakeGeometryFromPoints(ref points);

            RefreshDrawing();
        }


        /// <summary>
        /// Hit test.
        /// Return value: -1 - no hit
        ///                0 - hit anywhere
        ///                > 1 - handle number
        /// </summary>
        public override int MakeHitTest(Point point)
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


        #endregion Overrides

        /// <summary>
        /// Test whether object intersects with rectangle
        /// </summary>
        public override bool IntersectsWith(Rect rectangle)
        {
            RectangleGeometry rg = new RectangleGeometry(rectangle);

            PathGeometry p = Geometry.Combine(rg, pathGeometry, GeometryCombineMode.Intersect, null);

            return (!p.IsEmpty());
        }

    }
}
