using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

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
            if (handleNumber == 1)
                LineStart = point;
            else
                LineEnd = point;
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
