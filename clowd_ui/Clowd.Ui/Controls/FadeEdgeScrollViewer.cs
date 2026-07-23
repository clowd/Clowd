using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Clowd.UI.Controls
{
    /// <summary>ScrollViewer that dissolves content at whichever vertical edge has more content
    /// scrolled out of view, instead of ending in a hard clip line. Implemented as an opacity
    /// mask (not a painted gradient) so it composes with the Mica/acrylic window backdrop.</summary>
    public class FadeEdgeScrollViewer : ScrollViewer
    {
        public static readonly StyledProperty<double> FadeSizeProperty =
            AvaloniaProperty.Register<FadeEdgeScrollViewer, double>(nameof(FadeSize), 28d);

        public double FadeSize
        {
            get => GetValue(FadeSizeProperty);
            set => SetValue(FadeSizeProperty, value);
        }

        protected override Type StyleKeyOverride => typeof(ScrollViewer);

        private readonly GradientStops _stops;
        private readonly LinearGradientBrush _mask;

        public FadeEdgeScrollViewer()
        {
            _stops = new GradientStops
            {
                new GradientStop(Colors.White, 0), // top edge
                new GradientStop(Colors.White, 0), // end of top fade
                new GradientStop(Colors.White, 1), // start of bottom fade
                new GradientStop(Colors.White, 1), // bottom edge
            };
            _mask = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                GradientStops = _stops,
            };

            ScrollChanged += (_, _) => UpdateEdgeFade();
            AttachedToVisualTree += (_, _) => UpdateEdgeFade();
        }

        private void UpdateEdgeFade()
        {
            var height = Bounds.Height;
            var fade = FadeSize;
            var moreAbove = Offset.Y > 1;
            var moreBelow = Offset.Y + Viewport.Height < Extent.Height - 1;

            if ((!moreAbove && !moreBelow) || height <= fade * 2)
            {
                OpacityMask = null;
                return;
            }

            _stops[0].Color = moreAbove ? Colors.Transparent : Colors.White;
            _stops[1].Offset = moreAbove ? fade / height : 0;
            _stops[2].Offset = moreBelow ? 1 - (fade / height) : 1;
            _stops[3].Color = moreBelow ? Colors.Transparent : Colors.White;
            OpacityMask = _mask;
        }
    }
}
