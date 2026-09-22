using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Styling;

namespace Clowd.Drawing
{
    /// <summary>
    /// Procedural replacement for the WPF "CheckeredLargeLightWhiteBackgroundBrush" resource
    /// (decision table #14): a 2x2 checker tiled at 50px in canvas units, tinted white over the dark
    /// canvas surround and black over the light one. This is the "_clickable" surface —
    /// DrawingCanvas positions and sizes it each time the zoom/pan changes (UpdateClickableSurface)
    /// so that mouse events are always received, and applies the parallax offset so the background
    /// appears to scroll while remaining fixed to the viewport.
    /// </summary>
    internal sealed class CheckeredBackground : Control
    {
        // WPF source brush (App.xaml):
        //   <DrawingBrush TileMode="Tile" Viewport="0,0,50,50" ViewportUnits="Absolute">
        //       <GeometryDrawing Brush="#11FFFFFF" Geometry="M0,0 H1 V1 H2 V2 H1 V1 H0Z" />
        //   </DrawingBrush>
        // The 2x2-unit geometry scaled into a 50x50 tile yields two 25px checker squares.
        private static readonly IBrush _checkerDark = CreateCheckerBrush(Color.FromArgb(0x11, 0xFF, 0xFF, 0xFF));

        // Slightly stronger than the dark variant's alpha: the same nominal contrast reads fainter
        // as black-on-light than as white-on-dark.
        private static readonly IBrush _checkerLight = CreateCheckerBrush(Color.FromArgb(0x14, 0x00, 0x00, 0x00));

        private static IBrush CreateCheckerBrush(Color color)
        {
            var drawing = new GeometryDrawing
            {
                Brush = new ImmutableSolidColorBrush(color),
                Geometry = StreamGeometry.Parse("M0,0 H1 V1 H2 V2 H1 V1 H0Z"),
            };

            return new DrawingBrush
            {
                Drawing = drawing,
                TileMode = TileMode.Tile,
                DestinationRect = new RelativeRect(0, 0, 50, 50, RelativeUnit.Absolute),
            };
        }

        public CheckeredBackground()
        {
            // Render reads the variant rather than binding a brush, so it has to be told when the
            // variant moves under it.
            ActualThemeVariantChanged += (_, _) => InvalidateVisual();
        }

        public override void Render(DrawingContext context)
        {
            // filling the full bounds with a brush also makes the entire surface hit-test visible,
            // mirroring the WPF Border whose Background was the checkered DrawingBrush.
            var brush = ActualThemeVariant == ThemeVariant.Light ? _checkerLight : _checkerDark;
            context.FillRectangle(brush, new Rect(Bounds.Size));
        }
    }
}
