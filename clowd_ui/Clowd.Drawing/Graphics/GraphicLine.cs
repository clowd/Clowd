using System;
using Avalonia;
using Avalonia.Input;
using Avalonia.Media;

namespace Clowd.Drawing.Graphics
{
    [GraphicDesc("Line", Skills = Skill.Stroke | Skill.Color)]
    public class GraphicLine : GraphicBase
    {
        public Point LineStart
        {
            get => _lineStart;
            set => Set(ref _lineStart, value);
        }

        public Point LineEnd
        {
            get => _lineEnd;
            set => Set(ref _lineEnd, value);
        }

        private Point _lineStart;
        private Point _lineEnd;

        public GraphicLine()
        { }

        public GraphicLine(Color objectColor, double lineWidth, Point start, Point end)
            : base(objectColor, lineWidth)
        {
            _lineStart = start;
            _lineEnd = end;
        }

        public override Rect Bounds
        {
            get
            {
                var pad = Math.Max(LineWidth, 1) / 2;
                var l = Math.Min(LineStart.X, LineEnd.X) - pad;
                var t = Math.Min(LineStart.Y, LineEnd.Y) - pad;
                var r = Math.Max(LineStart.X, LineEnd.X) + pad;
                var b = Math.Max(LineStart.Y, LineEnd.Y) + pad;
                return new Rect(l, t, r - l, b - t);
            }
        }

        internal override int HandleCount => 2;

        internal override bool Contains(Point point)
        {
            // 8 px or LineWidth, whichever is larger -- matches the original hit
            // tolerance which built a widened path geometry behind the line.
            var threshold = Math.Max(LineWidth, 8.0) / 2.0;
            return DistancePointToSegment(LineStart, LineEnd, point) <= threshold;
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

        internal override Point GetHandle(int handleNumber, DpiScale uiscale)
        {
            return handleNumber == 1 ? LineStart : LineEnd;
        }

        internal override void Move(double deltaX, double deltaY)
        {
            _lineStart = new Point(LineStart.X + deltaX, LineStart.Y + deltaY);
            _lineEnd = new Point(LineEnd.X + deltaX, LineEnd.Y + deltaY);
            OnPropertyChanged(nameof(LineStart));
            OnPropertyChanged(nameof(LineEnd));
        }

        internal override void MoveHandleTo(Point point, int handleNumber)
        {
            if (handleNumber == 1) LineStart = point;
            else LineEnd = point;
        }

        // Shared cursors (same pattern as GraphicRectangle).
        private static readonly Cursor _cursorHorizontal = new Cursor(StandardCursorType.RightSide);       // ↔
        private static readonly Cursor _cursorVertical   = new Cursor(StandardCursorType.TopSide);         // ↕
        private static readonly Cursor _cursorDiagNwSe   = new Cursor(StandardCursorType.BottomRightCorner); // ↘↖
        private static readonly Cursor _cursorDiagNeSw   = new Cursor(StandardCursorType.BottomLeftCorner);  // ↙↗

        internal override Cursor GetHandleCursor(int handleNumber)
        {
            // Pick a cursor that points along the line so the user sees which
            // direction the handle will drag. The line's screen-space angle
            // (from start to end) determines the visual octant.
            double dx = LineEnd.X - LineStart.X;
            double dy = LineEnd.Y - LineStart.Y;
            if (dx == 0 && dy == 0)
                return _cursorHorizontal;

            double deg = Math.Atan2(dy, dx) * 180.0 / Math.PI; // 0° = east, clockwise positive
            if (deg < 0) deg += 360;
            int octant = ((int)Math.Round(deg / 45.0)) % 8;

            return octant switch
            {
                0 or 4 => _cursorHorizontal,
                2 or 6 => _cursorVertical,
                1 or 5 => _cursorDiagNwSe,
                3 or 7 => _cursorDiagNeSw,
                _      => _cursorHorizontal,
            };
        }

        internal override void DrawObject(DrawingContext ctx)
        {
            var pen = new Pen(new SolidColorBrush(ObjectColor), LineWidth)
            {
                LineCap = PenLineCap.Round,
                LineJoin = PenLineJoin.Round,
            };
            ctx.DrawLine(pen, LineStart, LineEnd);
        }
    }
}
