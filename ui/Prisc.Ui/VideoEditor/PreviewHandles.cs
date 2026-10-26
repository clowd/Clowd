using Avalonia;
using Avalonia.Media;

namespace Prisc.UI.VideoEditor
{
    /// <summary>
    /// The handle disc every preview overlay draws (the transform gizmo's resize handles, an
    /// arrow's start, end and bend handles), so a handle looks the same whichever control owns it,
    /// and the same as the image editor's tracker, so the two editors' selection chrome reads as
    /// one control.
    /// </summary>
    internal static class PreviewHandles
    {
        /// <summary>Diameter of a handle, matching the image editor's
        /// <c>GraphicBase.UnscaledControlSize</c>: the two editors draw the same control, so they
        /// are the same size as well as the same shape.</summary>
        public const double Size = 12;

        /// <summary>The image editor's tracker: three concentric circles, accent, a white ring 1px
        /// in, accent again (see <c>GraphicBase.DrawSingleTracker</c>).</summary>
        public static void Draw(DrawingContext context, Point center, IBrush accent)
        {
            var radius = Size / 2;
            context.DrawEllipse(accent, null, center, radius, radius);
            context.DrawEllipse(Brushes.White, null, center, radius - 1, radius - 1);
            context.DrawEllipse(accent, null, center, radius - 3, radius - 3);
        }
    }
}
