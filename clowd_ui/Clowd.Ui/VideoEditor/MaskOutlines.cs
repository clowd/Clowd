using System;
using Avalonia;
using Avalonia.Media;
using Clowd.VideoSDK.Model;

namespace Clowd.UI.VideoEditor
{
    /// <summary>
    /// Avalonia geometry for the mask shapes the toolkit cannot draw with a primitive. The points
    /// come from <see cref="MaskGeometry"/>, the same source the compositor traces, so the gizmo
    /// outline and the shape picker's icon match the composed picture exactly.
    /// </summary>
    public static class MaskOutlines
    {
        /// <summary>A squircle in a 100x100 box, for XAML that scales it to fit (the picker icon).
        /// Built once — the curve has no parameters to vary.</summary>
        public static readonly Geometry SquircleIcon = Squircle(new Rect(0, 0, 100, 100));

        /// <summary>The squircle inscribed in <paramref name="bounds"/>.</summary>
        public static Geometry Squircle(Rect bounds)
        {
            Span<double> xy = stackalloc double[MaskGeometry.SquircleSegments * 2];
            MaskGeometry.BuildSquircle(bounds.Center.X, bounds.Center.Y, bounds.Width / 2, bounds.Height / 2, xy);

            var geometry = new StreamGeometry();
            using (var context = geometry.Open())
            {
                context.BeginFigure(new Point(xy[0], xy[1]), isFilled: true);
                for (int i = 1; i < MaskGeometry.SquircleSegments; i++)
                    context.LineTo(new Point(xy[i * 2], xy[i * 2 + 1]));
                context.EndFigure(true);
            }

            return geometry;
        }
    }
}
