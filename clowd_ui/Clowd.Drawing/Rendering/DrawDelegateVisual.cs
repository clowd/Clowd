using System;
using Avalonia.Controls;
using Avalonia.Media;

namespace Clowd.Drawing.Rendering
{
    /// <summary>
    /// Minimal detached render host whose Render invokes a delegate (final-design §A.3). The
    /// shadow bake pipeline reuses one instance to rasterize a graphic's silhouette into a
    /// RenderTargetBitmap — replacing the throwaway per-bake GraphicVisual. Never attached to
    /// the visual tree; Measure/Arrange are driven manually by the baker.
    /// </summary>
    internal sealed class DrawDelegateVisual : Control
    {
        public Action<DrawingContext> Draw { get; set; }

        public override void Render(DrawingContext context) => Draw?.Invoke(context);
    }
}
