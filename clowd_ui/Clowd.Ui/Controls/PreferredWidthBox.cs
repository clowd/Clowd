using System;
using Avalonia;
using Avalonia.Controls;

namespace Clowd.UI.Controls
{
    /// <summary>
    /// Gives its child a width it would like to have rather than one it insists on: the child is
    /// laid out at <see cref="PreferredWidth"/> when there is room (or when nothing constrains it,
    /// e.g. inside a horizontal StackPanel), and at whatever width is on offer when there is not.
    /// </summary>
    /// <remarks>
    /// This is what a settings editor needs in place of MinWidth. A MinWidth is a floor the layout
    /// cannot go under, so once the window is narrower than the label column plus the editor the
    /// editor simply runs off the right edge and is clipped - or, docked beside a Browse button,
    /// paints over it. A preferred width lets the box shrink instead.
    ///
    /// The child is measured with the full available width and laid out at the larger of its
    /// natural width and the resolved one, so a Stretch-aligned child (the TextBox and ComboBox
    /// default) fills the preferred width, and one whose content is wider keeps its own width.
    /// <see cref="Decorator.Padding"/> is ignored.
    /// </remarks>
    public class PreferredWidthBox : Decorator
    {
        public static readonly StyledProperty<double> PreferredWidthProperty =
            AvaloniaProperty.Register<PreferredWidthBox, double>(nameof(PreferredWidth));

        /// <summary>Width the child gets whenever the available width allows it.</summary>
        public double PreferredWidth
        {
            get => GetValue(PreferredWidthProperty);
            set => SetValue(PreferredWidthProperty, value);
        }

        static PreferredWidthBox()
        {
            AffectsMeasure<PreferredWidthBox>(PreferredWidthProperty);
        }

        public PreferredWidthBox()
        {
        }

        public PreferredWidthBox(double preferredWidth, Control child)
        {
            PreferredWidth = preferredWidth;
            Child = child;
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            var child = Child;
            if (child == null)
                return default;

            // measured with the full available width, not the preferred one, so the child
            // reports its natural size: a ComboBox whose selected item is wider than the
            // preferred width grows to fit it (as it did with MinWidth) rather than wrapping
            // the text to the preferred width while there is room to spare.
            child.Measure(availableSize);

            var width = Double.IsInfinity(availableSize.Width)
                ? PreferredWidth
                : Math.Min(PreferredWidth, availableSize.Width);

            return new Size(Math.Max(width, child.DesiredSize.Width), child.DesiredSize.Height);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            Child?.Arrange(new Rect(finalSize));
            return finalSize;
        }
    }
}
