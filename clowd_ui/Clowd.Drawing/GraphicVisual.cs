using Avalonia.Controls;
using Avalonia.Media;
using Clowd.Drawing.Graphics;

namespace Clowd.Drawing
{
    /// <summary>
    /// One visual per graphic (replaces the WPF DrawingVisual; decision table #1).
    /// Child of DrawingCanvas.VisualChildren at index (list position + 2).
    /// </summary>
    internal sealed class GraphicVisual : Control
    {
        public GraphicBase Graphic { get; }

        /// <summary>
        /// Export mode: Render calls DrawObject (no selection chrome) instead of Draw.
        /// </summary>
        public bool ObjectOnly
        {
            get => _objectOnly;
            set
            {
                if (_objectOnly != value)
                {
                    _objectOnly = value;
                    InvalidateVisual();
                }
            }
        }

        private readonly GraphicCollection _collection;
        private bool _objectOnly;

        public GraphicVisual(GraphicBase graphic, GraphicCollection collection = null)
        {
            Graphic = graphic;
            _collection = collection;
            UpdateEffect();
        }

        public override void Render(DrawingContext context)
        {
            if (ObjectOnly)
            {
                Graphic.DrawObject(context);
            }
            else
            {
                // get dpi of editor window so resize handles can be scaled
                Graphic.Draw(context, _collection?.Dpi ?? new DpiScale(1, 1));
            }
        }

        /// <summary>
        /// Syncs the drop shadow effect with the graphic state (decision table #19).
        /// </summary>
        internal void UpdateEffect()
        {
            if (Graphic.DropShadowEffect && Effect == null)
            {
                Effect = new DropShadowEffect
                {
                    OffsetX = 1.414,
                    OffsetY = 1.414,
                    BlurRadius = 5,
                    Color = Color.FromArgb(0x80, 0, 0, 0),
                };
            }
            else if (!Graphic.DropShadowEffect && Effect != null)
            {
                Effect = null;
            }
        }
    }
}
