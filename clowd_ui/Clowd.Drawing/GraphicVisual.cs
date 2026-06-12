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
        // drop shadow parameters (§2.5) — shared by the on-screen effect and the export path
        internal const double ShadowOffsetX = 1.414;
        internal const double ShadowOffsetY = 1.414;
        internal const double ShadowBlurRadius = 5;
        internal const byte ShadowAlpha = 0x80;

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

        /// <summary>
        /// Export-only translation (graphic space → bitmap space), applied inside Render. The
        /// export visual itself must stay pinned at (0,0) with the full bitmap size, because
        /// RenderTargetBitmap.Render culls any visual whose arranged rect does not intersect
        /// the render target (§2.10).
        /// </summary>
        public Avalonia.Vector ObjectOffset { get; set; }

        /// <summary>
        /// Export-only pre-rendered drop shadow (RenderTargetBitmap.Render ignores Effect, so the
        /// export path bakes the shadow into a bitmap via ShadowRenderer). Drawn underneath the
        /// graphic at <see cref="ShadowPosition"/> (graphic space, offset already applied).
        /// </summary>
        public Avalonia.Media.Imaging.Bitmap ShadowBitmap { get; set; }

        public Avalonia.Point ShadowPosition { get; set; }

        private readonly GraphicCollection _collection;
        private bool _objectOnly;

        public GraphicVisual(GraphicBase graphic, GraphicCollection collection = null)
        {
            Graphic = graphic;
            _collection = collection;

            // Graphic hit-testing is geometry-based (ToolPointer.MakeHitTest); the canvas receives
            // pointer events through its _clickable surface. As a visual-only child (no logical
            // parent) this control cannot inherit the canvas Cursor, so letting it win pointer
            // hit-tests would reset the cursor to the system default over every graphic.
            IsHitTestVisible = false;

            UpdateEffect();
        }

        public override void Render(DrawingContext context)
        {
            if (ObjectOnly)
            {
                using (context.PushTransform(Avalonia.Matrix.CreateTranslation(ObjectOffset.X, ObjectOffset.Y)))
                {
                    if (ShadowBitmap != null)
                        context.DrawImage(ShadowBitmap, new Avalonia.Rect(ShadowPosition, ShadowBitmap.Size));
                    Graphic.DrawObject(context);
                }
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
                    OffsetX = ShadowOffsetX,
                    OffsetY = ShadowOffsetY,
                    BlurRadius = ShadowBlurRadius,
                    Color = Color.FromArgb(ShadowAlpha, 0, 0, 0),
                };
            }
            else if (!Graphic.DropShadowEffect && Effect != null)
            {
                Effect = null;
            }
        }
    }
}
