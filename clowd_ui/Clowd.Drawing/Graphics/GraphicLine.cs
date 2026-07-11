using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using Clowd.Drawing.Rendering;

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

        // PORT NOTE (aspect map entry): LineStart/LineEnd define the shape, so they invalidate
        // Bounds|Geometry|Shadow. GraphicArrow inherits this map (it adds no persisted property).
        internal override void DeclarePropertyEffects(Dictionary<string, InvalidationAspects> map)
        {
            base.DeclarePropertyEffects(map);
            const InvalidationAspects shape = InvalidationAspects.Bounds | InvalidationAspects.Geometry | InvalidationAspects.Shadow;
            map[nameof(LineStart)] = shape;
            map[nameof(LineEnd)] = shape;
        }

        // PORT NOTE (ComputeBounds): the old Bounds getter body moves here; the cached base Bounds
        // getter now serves reads. decision #25: widened-geometry bounds replaced by GetRenderBounds
        // with a LineWidth pen. Shares the one cached LineGeometry with Contains/DrawObject.
        protected override Rect ComputeBounds()
        {
            return GetLineGeometry().GetRenderBounds(RenderResources.GetPen(default, LineWidth));
        }

        internal override int HandleCount => 2;

        // PORT NOTE (RenderResources): min-8px hit thickness preserved; the black pen only defines
        // the widened hit corridor (color is irrelevant to StrokeContains) so it comes from the cache.
        internal override bool Contains(Point point)
        {
            return GetLineGeometry().StrokeContains(RenderResources.GetPen(Colors.Black, Math.Max(LineWidth, 8)), point);
        }

        internal override Point GetHandle(int handleNumber, DpiScale uiscale)
        {
            return handleNumber == 1 ? LineStart : LineEnd;
        }

        // PORT NOTE (_translating fast path): pure translation offsets the cached bounds once and
        // clears only the Geometry aspect (shadow/text survive). Fields are set directly and a single
        // bare raise is emitted — the existing Move raise pattern is a contract and is unchanged.
        internal override void Move(double deltaX, double deltaY)
        {
            _translating = true;
            try
            {
                _lineStart = new Point(LineStart.X + deltaX, LineStart.Y + deltaY);
                _lineEnd = new Point(LineEnd.X + deltaX, LineEnd.Y + deltaY);
                RenderCache.TranslateCachedBounds(deltaX, deltaY);
                OnPropertyChanged();
            }
            finally
            {
                _translating = false;
            }
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
            ctx.DrawLine(RenderResources.GetPen(ObjectColor, LineWidth), LineStart, LineEnd);
        }

        // Cached full-line geometry (RenderCache.Geometry slot), shared by Bounds/Contains/DrawObject.
        // GraphicArrow reuses this via the inherited Contains (a full LineStart→LineEnd corridor);
        // its shaft/tip parts live in ComputeArrowParts and do not touch this slot.
        protected virtual Geometry GetLineGeometry()
        {
            return RenderCache.Geometry ??= new LineGeometry(_lineStart, _lineEnd);
        }
    }
}
