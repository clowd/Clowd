using System;
using System.Numerics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using RT.Util.Geometry;

namespace Clowd.Drawing.Graphics
{
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

        protected GraphicLine()
        { }

        public GraphicLine(Color objectColor, double lineWidth, Point start, Point end)
            : base(objectColor, lineWidth)
        {
            _lineStart = start;
            _lineEnd = end;
        }

        public override Rect Bounds => GetLineGeometry().Bounds;

        internal override int HandleCount => 2;

        internal override bool Contains(Point point)
        {
            LineGeometry g = new LineGeometry(LineStart, LineEnd);
            return g.StrokeContains(new Pen(Brushes.Black, Math.Max(LineWidth, 8)), point);
        }

        internal override Point GetHandle(int handleNumber, DpiScale uiscale)
        {
            return handleNumber == 1 ? LineStart : LineEnd;
        }

        internal override void Move(double deltaX, double deltaY)
        {
            _lineStart = new Point(LineStart.X + deltaX, LineStart.Y + deltaY);
            _lineEnd = new Point(LineEnd.X + deltaX, LineEnd.Y + deltaY);
            OnPropertyChanged();
        }

        internal override void MoveHandleTo(Point point, int handleNumber)
        {
            var anchor = handleNumber == 1 ? LineEnd : LineStart;
            var dragging = point;

            if (Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.LeftShift))
            {
                double x1 = anchor.X, y1 = anchor.Y, x2 = dragging.X, y2 = dragging.Y;
                double xDiff = x2 - x1;
                double yDiff = y2 - y1;
                var angle = (Math.Atan2(yDiff, xDiff) * 180.0 / Math.PI + 360) % 360;
                var closest45 = Math.Round(angle / 45d) * 45d;
                var vecSnap = new PointD(closest45 / 180 * Math.PI);
                var snapLen = new PointD(xDiff, yDiff).LengthProjectedOnto(vecSnap);
                dragging.X = anchor.X + (snapLen * vecSnap).X;
                dragging.Y = anchor.Y + (snapLen * vecSnap).Y;
            }

            if (handleNumber == 1) LineStart = dragging;
            else LineEnd = dragging;
        }

        internal override Cursor GetHandleCursor(int handleNumber) => Cursors.SizeAll;

        internal override void DrawObject(DrawingContext ctx)
        {
            ctx.DrawGeometry(new SolidColorBrush(ObjectColor), null, GetLineGeometry());
        }

        protected virtual Geometry GetLineGeometry()
        {
            var line = new LineGeometry(_lineStart, _lineEnd);
            return line.GetWidenedPathGeometry(new Pen(null, LineWidth));
        }
    }
}
