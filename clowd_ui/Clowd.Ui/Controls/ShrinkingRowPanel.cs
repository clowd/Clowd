using System;
using Avalonia;
using Avalonia.Controls;

namespace Clowd.UI.Controls
{
    /// <summary>
    /// One left-packed horizontal row. Every child keeps its natural width except the one marked with
    /// <see cref="ShrinkProperty"/>, which absorbs whatever space is left over — and gives it back,
    /// measured below its natural width, once the row runs short. That is what lets a long upload URL
    /// ellipsize instead of pushing the buttons sitting next to it off the right edge.
    /// </summary>
    /// <remarks>
    /// Neither stock panel can do this: a StackPanel measures its children with an infinite width in
    /// the stacking direction (so the URL never learns it has to trim, and everything after it is
    /// pushed out), and a Grid's Auto columns are measured with infinity too — while its star column
    /// swallows all the slack rather than only the width its content wanted, which would strand the
    /// buttons at the far right of the row instead of beside the link.
    ///
    /// Invisible children take neither width nor a share of <see cref="Spacing"/>.
    /// </remarks>
    public class ShrinkingRowPanel : Panel
    {
        /// <summary>Marks the one child that gives up width when the row runs out of it. Only the
        /// first child carrying it is treated as flexible.</summary>
        public static readonly AttachedProperty<bool> ShrinkProperty =
            AvaloniaProperty.RegisterAttached<ShrinkingRowPanel, Control, bool>("Shrink");

        public static bool GetShrink(Control control)
        {
            return control.GetValue(ShrinkProperty);
        }

        public static void SetShrink(Control control, bool value)
        {
            control.SetValue(ShrinkProperty, value);
        }

        public static readonly StyledProperty<double> SpacingProperty =
            AvaloniaProperty.Register<ShrinkingRowPanel, double>(nameof(Spacing));

        /// <summary>Gap between two adjacent visible children.</summary>
        public double Spacing
        {
            get => GetValue(SpacingProperty);
            set => SetValue(SpacingProperty, value);
        }

        static ShrinkingRowPanel()
        {
            AffectsMeasure<ShrinkingRowPanel>(SpacingProperty);
        }

        /// <summary>The first visible child marked to shrink, or null.</summary>
        private Control FindShrinkChild()
        {
            foreach (var child in Children)
            {
                if (child.IsVisible && GetShrink(child))
                    return child;
            }

            return null;
        }

        /// <summary>Width the inflexible children and the gaps between every visible child take.</summary>
        private double MeasureInflexible(Control shrinkChild)
        {
            double width = 0;
            var count = 0;

            foreach (var child in Children)
            {
                if (!child.IsVisible)
                    continue;

                count++;
                if (!ReferenceEquals(child, shrinkChild))
                    width += child.DesiredSize.Width;
            }

            return count > 1 ? width + Spacing * (count - 1) : width;
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            var shrinkChild = FindShrinkChild();

            double height = 0;

            // everything else first: what they take is what the flexible child does not get.
            foreach (var child in Children)
            {
                if (!child.IsVisible || ReferenceEquals(child, shrinkChild))
                    continue;

                child.Measure(new Size(Double.PositiveInfinity, availableSize.Height));
                height = Math.Max(height, child.DesiredSize.Height);
            }

            var taken = MeasureInflexible(shrinkChild);

            if (shrinkChild != null)
            {
                // an infinite constraint stays infinite here — with no row width to run out of, the
                // flexible child simply keeps its natural size.
                shrinkChild.Measure(new Size(Math.Max(0, availableSize.Width - taken), availableSize.Height));
                height = Math.Max(height, shrinkChild.DesiredSize.Height);
                taken += shrinkChild.DesiredSize.Width;
            }

            return new Size(taken, height);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            var shrinkChild = FindShrinkChild();

            // recomputed against the width we actually got rather than the one measure was offered,
            // so a host that hands back less than it asked about still shrinks the flexible child
            // rather than clipping whatever follows it.
            var shrinkWidth = 0d;
            if (shrinkChild != null)
            {
                var slack = finalSize.Width - MeasureInflexible(shrinkChild);
                shrinkWidth = Math.Clamp(slack, 0, shrinkChild.DesiredSize.Width);
            }

            var x = 0d;
            var first = true;

            foreach (var child in Children)
            {
                if (!child.IsVisible)
                    continue;

                if (!first)
                    x += Spacing;
                first = false;

                var width = ReferenceEquals(child, shrinkChild) ? shrinkWidth : child.DesiredSize.Width;
                width = Math.Min(width, Math.Max(0, finalSize.Width - x));

                child.Arrange(new Rect(x, 0, width, finalSize.Height));
                x += width;
            }

            return finalSize;
        }
    }
}
