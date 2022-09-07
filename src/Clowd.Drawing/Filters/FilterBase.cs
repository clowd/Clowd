using System;
using System.Collections.Generic;
using System.Windows;
using Clowd.Drawing.Graphics;

namespace Clowd.Drawing.Filters
{
    internal abstract class FilterBase
    {
        public DrawingCanvas MyCanvas { get; }
        public GraphicImage Source { get; }

        private Point? _lastPoint;

        protected FilterBase(DrawingCanvas canvas, GraphicImage source)
        {
            MyCanvas = canvas;
            Source = source;
        }

        public void Handle(DrawingBrush brush, Point p)
        {
            if (_lastPoint.HasValue)
            {
                foreach (var point in PlotPointsBetween(_lastPoint.Value, p))
                    HandleInternal(brush, point);
            }
            else
            {
                HandleInternal(brush, p);
            }

            _lastPoint = p;
        }

        /// <summary>
        /// Returns all the points between two points, including the last point but not the first. 
        /// Uses the Bresenham line algorithm
        /// </summary>
        private static IEnumerable<Point> PlotPointsBetween(Point p0, Point p1)
        {
            // https://en.wikipedia.org/wiki/Bresenham%27s_line_algorithm

            if (Math.Abs(p1.X - p0.X) < 1)
            {
                if (Math.Abs(p1.Y - p0.Y) < 1)
                {
                    yield return p0;
                }
                else
                {
                    var min = Math.Min(p1.Y, p0.Y);
                    var max = Math.Max(p1.Y, p0.Y);
                    for (var ye = min + 1; ye <= max; ye++)
                        yield return new Point(p1.X, ye);
                }

                yield break;
            }

            if (p1.X < p0.X)
            {
                // switch points as this algorithm expects to be drawing towards the right
                var tmp = p0;
                p0 = p1;
                p1 = tmp;
            }

            var deltax = p1.X - p0.X;
            var deltay = p1.Y - p0.Y;

            // Assume deltax != 0 (line is not vertical),
            // note that this division needs to be done in a way that preserves the fractional part
            var deltaerr = Math.Abs(deltay / deltax);

            // no error at start
            var error = 0d;

            var y = p0.Y;
            for (var x = p0.X; x <= p1.X; x++)
            {
                if (x > p0.X)
                    yield return new Point(x, y);

                error = error + deltaerr;
                while (error >= 0.5)
                {
                    y += deltay > 0 ? 1 : -1;
                    error = error - 1;
                }
            }
        }

        protected abstract void HandleInternal(DrawingBrush brush, Point p);

        public abstract void Close();
    }
}
