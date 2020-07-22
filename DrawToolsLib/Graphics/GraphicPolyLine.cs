﻿#if SYSTEM_WINDOWS_VECTOR
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
    [Serializable]
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
                if (!_drawing)
                    throw new InvalidOperationException("Points can only be updated before FinishDrawing has been called.");

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
        private bool _drawing = true;
        private Geometry _geometry;
        private Rect _vectorBounds;

        protected GraphicPolyLine() // serializer constructor
        {
        }

        public GraphicPolyLine(DrawingCanvas canvas, Point start)
            : this(canvas.ObjectColor, canvas.LineWidth, start)
        {
        }

        public GraphicPolyLine(Color objectColor, double lineWidth, Point start)
            : base(objectColor, lineWidth, new Rect(start, new Size(1, 1)))
        {
            this.Points = new[] { start };
        }

        public GraphicPolyLine(Color objectColor, double lineWidth, Rect rect, bool filled, double angle, Point[] points) // clone constructor
            : base(objectColor, lineWidth, rect, filled, angle)
        {
            this.Points = points;
            FinishDrawing();
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

        internal override void MoveHandleTo(Point point, int handleNumber)
        {
            if (_drawing)
                throw new InvalidOperationException("Must not move handles while drawing. Call FinishDrawing() first.");

            base.MoveHandleTo(point, handleNumber);
        }

        internal override void Move(double deltaX, double deltaY)
        {
            base.Move(deltaX, deltaY);
        }

        internal override int MakeHitTest(Point point)
        {
            var rotatedPt = UnapplyRotation(point);
            if (IsSelected)
            {
                for (int i = 1; i <= HandleCount; i++)
                {
                    if (GetHandleRectangle(i).Contains(rotatedPt))
                        return i;
                }
            }

            var widened = _geometry.GetWidenedPathGeometry(new Pen(Brushes.Black, HitTestWidth));
            var hit = widened.FillContains(rotatedPt);
            return hit ? 0 : -1;
        }

        public void AddPoint(Point p)
        {
            AddPointInternal(p, true);
        }

        public void FinishDrawing()
        {
            _drawing = false;
        }

        public override GraphicBase Clone()
        {
            return new GraphicPolyLine(ObjectColor, LineWidth, UnrotatedBounds, Filled, Angle, Points) { ObjectId = ObjectId };
        }

        private void AddPointInternal(Point p, bool updateGeometry)
        {
            if (!_drawing)
                throw new InvalidOperationException("Must not add points after FinishDrawing() has been called");

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

            InvalidateVisual();
        }

    }
}
