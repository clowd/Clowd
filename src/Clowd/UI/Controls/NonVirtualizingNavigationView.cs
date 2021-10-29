using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using ModernWpf.Controls;

namespace Clowd.UI.Controls
{
    class NonVirtualizingNavigationView : NavigationView
    {
        public override void OnApplyTemplate()
        {
            base.OnApplyTemplate();

            if (GetTemplateChild("MenuItemsHost") is ItemsRepeater leftNavRepeater)
            {
                leftNavRepeater.Layout = new NonVirtualizingStackLayout();
            }
        }
    }

    public class NonVirtualizingStackLayout : NonVirtualizingLayout
    {
        public static readonly DependencyProperty OrientationProperty =
            DependencyProperty.Register(
                nameof(Orientation),
                typeof(Orientation),
                typeof(NonVirtualizingStackLayout),
                new PropertyMetadata(Orientation.Vertical, OnOrientationChanged));

        public Orientation Orientation
        {
            get => (Orientation)GetValue(OrientationProperty);
            set => SetValue(OrientationProperty, value);
        }

        private static void OnOrientationChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            ((NonVirtualizingStackLayout)d).InvalidateMeasure();
        }

        protected override Size MeasureOverride(NonVirtualizingLayoutContext context, Size availableSize)
        {
            Size stackDesiredSize = new Size();
            var children = context.Children;
            Size layoutSlotSize = availableSize;
            bool fHorizontal = Orientation == Orientation.Horizontal;

            if (fHorizontal)
            {
                layoutSlotSize.Width = double.PositiveInfinity;
            }
            else
            {
                layoutSlotSize.Height = double.PositiveInfinity;
            }

            for (int i = 0, count = children.Count; i < count; ++i)
            {
                UIElement child = children[i];

                if (child == null) { continue; }

                child.Measure(layoutSlotSize);
                Size childDesiredSize = child.DesiredSize;

                if (fHorizontal)
                {
                    stackDesiredSize.Width += childDesiredSize.Width;
                    stackDesiredSize.Height = Math.Max(stackDesiredSize.Height, childDesiredSize.Height);
                }
                else
                {
                    stackDesiredSize.Width = Math.Max(stackDesiredSize.Width, childDesiredSize.Width);
                    stackDesiredSize.Height += childDesiredSize.Height;
                }
            }

            if (fHorizontal)
            {
                if (double.IsFinite(availableSize.Height))
                {
                    stackDesiredSize.Height = Math.Max(stackDesiredSize.Height, availableSize.Height);
                }
            }
            else
            {
                if (double.IsFinite(availableSize.Width))
                {
                    stackDesiredSize.Width = Math.Max(stackDesiredSize.Width, availableSize.Width);
                }
            }

            return stackDesiredSize;
        }

        protected override Size ArrangeOverride(NonVirtualizingLayoutContext context, Size finalSize)
        {
            var children = context.Children;
            bool fHorizontal = Orientation == Orientation.Horizontal;
            Rect rcChild = new Rect(finalSize);
            double previousChildSize = 0.0;

            for (int i = 0, count = children.Count; i < count; ++i)
            {
                UIElement child = children[i];

                if (child == null) { continue; }

                if (fHorizontal)
                {
                    rcChild.X += previousChildSize;
                    previousChildSize = child.DesiredSize.Width;
                    rcChild.Width = previousChildSize;
                    rcChild.Height = Math.Max(finalSize.Height, child.DesiredSize.Height);
                }
                else
                {
                    rcChild.Y += previousChildSize;
                    previousChildSize = child.DesiredSize.Height;
                    rcChild.Height = previousChildSize;
                    rcChild.Width = Math.Max(finalSize.Width, child.DesiredSize.Width);
                }

                child.Arrange(rcChild);
            }
            return finalSize;
        }
    }
}
