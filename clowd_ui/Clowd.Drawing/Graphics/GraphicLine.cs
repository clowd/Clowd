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

        protected GraphicLine()
        { }

        public GraphicLine(Color objectColor, double lineWidth, Point start, Point end)
            : base(objectColor, lineWidth)
        {
            _lineStart = start;
            _lineEnd = end;
        }

        // decision #25: widened-geometry bounds replaced by GetRenderBounds with a LineWidth pen.
        public override Rect Bounds => GetLineGeometry().GetRenderBounds(new Pen(null, LineWidth));

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
            if (handleNumber == 1) LineStart = point;
            else LineEnd = point;
        }

        internal override Cursor GetHandleCursor(int handleNumber) => CursorResources.SizeAll;

        internal override void DrawObject(DrawingContext ctx)
        {
            // decision #25: the WPF widened-geometry fill is replaced by a stroked line (flat caps, identical visual).
            var brush = new SolidColorBrush(ObjectColor);
            ctx.DrawLine(new Pen(brush, LineWidth), LineStart, LineEnd);
        }

        protected virtual Geometry GetLineGeometry()
        {
            return new LineGeometry(_lineStart, _lineEnd);
        }
    }
}
