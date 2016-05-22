﻿using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace DrawToolsLib.Graphics
{
    [Serializable]
    public class GraphicsEllipse : GraphicsRectangle
    {
        protected GraphicsEllipse()
        {
        }
        public GraphicsEllipse(DrawingCanvas canvas, Rect rect) : base(canvas, rect)
        {
        }

        public GraphicsEllipse(DrawingCanvas canvas, Rect rect, bool filled) : base(canvas, rect, filled)
        {
        }

        public GraphicsEllipse(Color objectColor, double lineWidth, Rect rect) : base(objectColor, lineWidth, rect)
        {
        }

        public GraphicsEllipse(Color objectColor, double lineWidth, Rect rect, bool filled, double angle = 0) : base(objectColor, lineWidth, rect, filled, angle)
        {
        }

        internal override void DrawRectangle(DrawingContext drawingContext)
        {
            if (drawingContext == null)
                throw new ArgumentNullException(nameof(drawingContext));

            Point center = new Point((Left + Right) / 2.0, (Top + Bottom) / 2.0);
            double radiusX = (Right - Left) / 2.0 - LineWidth / 2;
            double radiusY = (Bottom - Top) / 2.0 - LineWidth / 2;

            var brush = new SolidColorBrush(ObjectColor);
            drawingContext.DrawEllipse(
                Filled ? brush : null,
                new Pen(brush, LineWidth),
                center,
                radiusX,
                radiusY);
        }

        public override Rect Bounds
        {
            get
            {
                var a = (Right - Left) / 2;     // one axis’s radius
                var b = (Bottom - Top) / 2;     // the other axis’s radius
                var cos = Math.Cos(Angle * Math.PI / 180);
                var sin = Math.Sin(Angle * Math.PI / 180);
                var x = Math.Sqrt(a * a * cos * cos + b * b * sin * sin);
                var y = Math.Sqrt(a * a * sin * sin + b * b * cos * cos);
                return new Rect(
                    (Left + Right) / 2.0 - x,
                    (Top + Bottom) / 2.0 - y,
                    2 * x,
                    2 * y);
            }
        }

        internal override bool Contains(Point point)
        {
            point = UnapplyRotation(point);
            if (IsSelected)
                return UnrotatedBounds.Contains(point);

            EllipseGeometry g = new EllipseGeometry(UnrotatedBounds);
            return g.FillContains(point) || g.StrokeContains(new Pen(Brushes.Black, LineWidth), point);
        }

        public override GraphicsBase Clone()
        {
            return new GraphicsEllipse(ObjectColor, LineWidth, Bounds, Filled, Angle) { ObjectId = ObjectId };
        }
    }
}
