using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using ScreenVersusWpf;

namespace Clowd.WinLegacy
{
    public class ResizingAdorner : Adorner
    {
        public Style ThumbStyle { get; set; }
        // Resizing adorner uses Thumbs for visual elements.  
        // The Thumbs have built-in mouse input handling.
        Thumb topLeft, topRight, bottomLeft, bottomRight, topMiddle, rightMiddle, leftMiddle, bottomMiddle;
        Action<ScreenRect> customResizeHandler = null;


        // To store and manage the adorner's visual children.
        VisualCollection visualChildren;

        public void SetupCustomResizeHandling(Action<ScreenRect> newSizeHandler)
        {
            customResizeHandler = newSizeHandler;
        }

        public ResizingAdorner(UIElement adornedElement)
            : this(adornedElement, null)
        {
        }
        public ResizingAdorner(UIElement adornedElement, Style thumbStyle)
            : base(adornedElement)
        {
            ThumbStyle = thumbStyle;
            visualChildren = new VisualCollection(this);

            BuildAdornerCorner(ref topLeft, Cursors.SizeNWSE);
            BuildAdornerCorner(ref topRight, Cursors.SizeNESW);
            BuildAdornerCorner(ref bottomLeft, Cursors.SizeNESW);
            BuildAdornerCorner(ref bottomRight, Cursors.SizeNWSE);

            BuildAdornerCorner(ref topMiddle, Cursors.SizeNS);
            BuildAdornerCorner(ref rightMiddle, Cursors.SizeWE);
            BuildAdornerCorner(ref leftMiddle, Cursors.SizeWE);
            BuildAdornerCorner(ref bottomMiddle, Cursors.SizeNS);

            bottomLeft.DragDelta += new DragDeltaEventHandler(HandleBottomLeft);
            bottomRight.DragDelta += new DragDeltaEventHandler(HandleBottomRight);
            topLeft.DragDelta += new DragDeltaEventHandler(HandleTopLeft);
            topRight.DragDelta += new DragDeltaEventHandler(HandleTopRight);

            topMiddle.DragDelta += new DragDeltaEventHandler(HandleTopMiddle);
            rightMiddle.DragDelta += new DragDeltaEventHandler(HandleRightMiddle);
            leftMiddle.DragDelta += new DragDeltaEventHandler(HandleLeftMiddle);
            bottomMiddle.DragDelta += new DragDeltaEventHandler(HandleBottomMiddle);
        }

        private void HandleBottomRight(object sender, DragDeltaEventArgs args)
        {
            FrameworkElement adornedElement = this.AdornedElement as FrameworkElement;
            Thumb hitThumb = sender as Thumb;

            if (adornedElement == null || hitThumb == null) return;

            // Ensure that the Width and Height are properly initialized after the resize.
            EnforceSize(adornedElement);

            // Change the size by the amount the user drags the mouse, as long as it's larger 
            // than the width or height of an adorner, respectively.
            var width = Math.Max(adornedElement.Width + args.HorizontalChange, hitThumb.DesiredSize.Width);
            var height = Math.Max(args.VerticalChange + adornedElement.Height, hitThumb.DesiredSize.Height);

            if (customResizeHandler == null)
            {
                adornedElement.Width = width;
                adornedElement.Height = height;
            }
            else
            {
                customResizeHandler(new WpfRect(Canvas.GetLeft(adornedElement), Canvas.GetTop(adornedElement), width, height).ToScreenRect());
            }
        }
        private void HandleTopRight(object sender, DragDeltaEventArgs args)
        {
            FrameworkElement adornedElement = this.AdornedElement as FrameworkElement;
            Thumb hitThumb = sender as Thumb;

            if (adornedElement == null || hitThumb == null) return;

            // Ensure that the Width and Height are properly initialized after the resize.
            EnforceSize(adornedElement);

            double height_old = adornedElement.Height;
            double height_new = Math.Max(adornedElement.Height - args.VerticalChange, hitThumb.DesiredSize.Height);
            double top_old = Canvas.GetTop(adornedElement);
            double top_new = top_old - (height_new - height_old);
            double new_width = Math.Max(adornedElement.Width + args.HorizontalChange, hitThumb.DesiredSize.Width);

            if (customResizeHandler == null)
            {
                adornedElement.Width = new_width;
                adornedElement.Height = height_new;
                Canvas.SetTop(adornedElement, top_new);
            }
            else
            {
                customResizeHandler(new WpfRect(Canvas.GetLeft(adornedElement), top_new, new_width, height_new).ToScreenRect());
            }
        }
        private void HandleTopLeft(object sender, DragDeltaEventArgs args)
        {
            FrameworkElement adornedElement = AdornedElement as FrameworkElement;
            Thumb hitThumb = sender as Thumb;

            if (adornedElement == null || hitThumb == null) return;

            // Ensure that the Width and Height are properly initialized after the resize.
            EnforceSize(adornedElement);

            // Change the size by the amount the user drags the mouse, as long as it's larger 
            // than the width or height of an adorner, respectively.
            //adornedElement.Width = Math.Max(adornedElement.Width - args.HorizontalChange, hitThumb.DesiredSize.Width);
            //adornedElement.Height = Math.Max(adornedElement.Height - args.VerticalChange, hitThumb.DesiredSize.Height);

            double width_old = adornedElement.Width;
            double width_new = Math.Max(adornedElement.Width - args.HorizontalChange, hitThumb.DesiredSize.Width);
            double left_old = Canvas.GetLeft(adornedElement);
            double left_new = left_old - (width_new - width_old);

            double height_old = adornedElement.Height;
            double height_new = Math.Max(adornedElement.Height - args.VerticalChange, hitThumb.DesiredSize.Height);
            double top_old = Canvas.GetTop(adornedElement);
            double top_new = top_old - (height_new - height_old);

            if (customResizeHandler == null)
            {
                adornedElement.Width = width_new;
                Canvas.SetLeft(adornedElement, left_new);
                adornedElement.Height = height_new;
                Canvas.SetTop(adornedElement, top_new);
            }
            else
            {
                customResizeHandler(new WpfRect(left_new, top_new, width_new, height_new).ToScreenRect());
            }
        }
        private void HandleBottomLeft(object sender, DragDeltaEventArgs args)
        {
            FrameworkElement adornedElement = AdornedElement as FrameworkElement;
            Thumb hitThumb = sender as Thumb;

            if (adornedElement == null || hitThumb == null) return;

            // Ensure that the Width and Height are properly initialized after the resize.
            EnforceSize(adornedElement);

            // Change the size by the amount the user drags the mouse, as long as it's larger 
            // than the width or height of an adorner, respectively.
            //adornedElement.Width = Math.Max(adornedElement.Width - args.HorizontalChange, hitThumb.DesiredSize.Width);

            double width_old = adornedElement.Width;
            double width_new = Math.Max(adornedElement.Width - args.HorizontalChange, hitThumb.DesiredSize.Width);
            double left_old = Canvas.GetLeft(adornedElement);
            double left_new = left_old - (width_new - width_old);
            double height_new = Math.Max(args.VerticalChange + adornedElement.Height, hitThumb.DesiredSize.Height);

            if (customResizeHandler == null)
            {
                Canvas.SetLeft(adornedElement, left_new);
                adornedElement.Width = width_new;
                adornedElement.Height = height_new;

            }
            else
            {
                customResizeHandler(new WpfRect(left_new, Canvas.GetTop(adornedElement), width_new, height_new).ToScreenRect());
            }
        }

        private void HandleBottomMiddle(object sender, DragDeltaEventArgs args)
        {
            FrameworkElement adornedElement = this.AdornedElement as FrameworkElement;
            Thumb hitThumb = sender as Thumb;

            if (adornedElement == null || hitThumb == null) return;

            // Ensure that the Width and Height are properly initialized after the resize.
            EnforceSize(adornedElement);

            // Change the size by the amount the user drags the mouse, as long as it's larger 
            // than the width or height of an adorner, respectively.
            var height = Math.Max(args.VerticalChange + adornedElement.Height, hitThumb.DesiredSize.Height);

            if (customResizeHandler == null)
            {
                adornedElement.Height = height;
            }
            else
            {
                customResizeHandler(new WpfRect(Canvas.GetLeft(adornedElement), Canvas.GetTop(adornedElement), adornedElement.Width, height).ToScreenRect());
            }
        }
        private void HandleLeftMiddle(object sender, DragDeltaEventArgs args)
        {
            FrameworkElement adornedElement = AdornedElement as FrameworkElement;
            Thumb hitThumb = sender as Thumb;

            if (adornedElement == null || hitThumb == null) return;

            // Ensure that the Width and Height are properly initialized after the resize.
            EnforceSize(adornedElement);

            // Change the size by the amount the user drags the mouse, as long as it's larger 
            // than the width or height of an adorner, respectively.

            double width_old = adornedElement.Width;
            double width_new = Math.Max(adornedElement.Width - args.HorizontalChange, hitThumb.DesiredSize.Width);
            double left_old = Canvas.GetLeft(adornedElement);
            double left_new = left_old - (width_new - width_old);

            if (customResizeHandler == null)
            {
                Canvas.SetLeft(adornedElement, left_new);
                adornedElement.Width = width_new;
            }
            else
            {
                customResizeHandler(new WpfRect(left_new, Canvas.GetTop(adornedElement), width_new, adornedElement.Height).ToScreenRect());
            }
        }
        private void HandleRightMiddle(object sender, DragDeltaEventArgs args)
        {
            FrameworkElement adornedElement = this.AdornedElement as FrameworkElement;
            Thumb hitThumb = sender as Thumb;

            if (adornedElement == null || hitThumb == null) return;

            // Ensure that the Width and Height are properly initialized after the resize.
            EnforceSize(adornedElement);

            // Change the size by the amount the user drags the mouse, as long as it's larger 
            // than the width or height of an adorner, respectively.
            var width = Math.Max(adornedElement.Width + args.HorizontalChange, hitThumb.DesiredSize.Width);

            if (customResizeHandler == null)
            {
                adornedElement.Width = width;
            }
            else
            {
                customResizeHandler(new WpfRect(Canvas.GetLeft(adornedElement), Canvas.GetTop(adornedElement), width, adornedElement.Height).ToScreenRect());
            }
        }
        private void HandleTopMiddle(object sender, DragDeltaEventArgs args)
        {
            FrameworkElement adornedElement = this.AdornedElement as FrameworkElement;
            Thumb hitThumb = sender as Thumb;

            if (adornedElement == null || hitThumb == null) return;

            // Ensure that the Width and Height are properly initialized after the resize.
            EnforceSize(adornedElement);

            double height_old = adornedElement.Height;
            double height_new = Math.Max(adornedElement.Height - args.VerticalChange, hitThumb.DesiredSize.Height);
            double top_old = Canvas.GetTop(adornedElement);
            double top_new = top_old - (height_new - height_old);

            if (customResizeHandler == null)
            {
                adornedElement.Height = height_new;
                Canvas.SetTop(adornedElement, top_new);
            }
            else
            {
                customResizeHandler(new WpfRect(Canvas.GetLeft(adornedElement), top_new, adornedElement.Width, height_new).ToScreenRect());
            }
        }

        // Arrange the Adorners.
        protected override Size ArrangeOverride(Size finalSize)
        {
            // desiredWidth and desiredHeight are the width and height of the element that's being adorned.  
            // These will be used to place the ResizingAdorner at the corners of the adorned element.  
            double desiredWidth = AdornedElement.DesiredSize.Width;
            double desiredHeight = AdornedElement.DesiredSize.Height;
            // adornerWidth & adornerHeight are used for placement as well.
            double adornerWidth = 10;
            double adornerHeight = 10;

            topLeft.Arrange(new Rect(-adornerWidth / 2, -adornerHeight / 2, adornerWidth, adornerHeight));
            topRight.Arrange(new Rect(desiredWidth - adornerWidth / 2, -adornerHeight / 2, adornerWidth, adornerHeight));
            bottomLeft.Arrange(new Rect(-adornerWidth / 2, desiredHeight - adornerHeight / 2, adornerWidth, adornerHeight));
            bottomRight.Arrange(new Rect(desiredWidth - adornerWidth / 2, desiredHeight - adornerHeight / 2, adornerWidth, adornerHeight));

            topMiddle.Arrange(new Rect((desiredWidth / 2) - (adornerWidth / 2), -adornerHeight / 2, adornerWidth, adornerHeight));
            rightMiddle.Arrange(new Rect(desiredWidth - (adornerWidth / 2), desiredHeight / 2 - (adornerHeight / 2), adornerWidth, adornerHeight));
            leftMiddle.Arrange(new Rect(-adornerWidth / 2, desiredHeight / 2 - (adornerHeight / 2), adornerWidth, adornerHeight));
            bottomMiddle.Arrange(new Rect(desiredWidth / 2 - (adornerWidth / 2), desiredHeight - adornerHeight / 2, adornerWidth, adornerHeight));

            // Return the final size.
            return finalSize;
        }

        // Helper method to instantiate the corner Thumbs, set the Cursor property, 
        // set some appearance properties, and add the elements to the visual tree.
        private void BuildAdornerCorner(ref Thumb cornerThumb, Cursor customizedCursor)
        {
            if (cornerThumb != null) return;

            cornerThumb = new Thumb();

            // Set some arbitrary visual characteristics.
            cornerThumb.Cursor = customizedCursor;
            cornerThumb.Height = cornerThumb.Width = 10;
            if (ThumbStyle != null)
                cornerThumb.Style = ThumbStyle;

            visualChildren.Add(cornerThumb);
        }

        // This method ensures that the Widths and Heights are initialized.  Sizing to content produces
        // Width and Height values of Double.NaN.  Because this Adorner explicitly resizes, the Width and Height
        // need to be set first.  It also sets the maximum size of the adorned element.
        private void EnforceSize(FrameworkElement adornedElement)
        {
            if (adornedElement.Width.Equals(Double.NaN))
                adornedElement.Width = adornedElement.DesiredSize.Width;
            if (adornedElement.Height.Equals(Double.NaN))
                adornedElement.Height = adornedElement.DesiredSize.Height;

            FrameworkElement parent = adornedElement.Parent as FrameworkElement;
            if (parent != null)
            {
                adornedElement.MaxHeight = parent.ActualHeight;
                adornedElement.MaxWidth = parent.ActualWidth;
            }
        }
        // Override the VisualChildrenCount and GetVisualChild properties to interface with 
        // the adorner's visual collection.
        protected override int VisualChildrenCount { get { return visualChildren.Count; } }
        protected override Visual GetVisualChild(int index) { return visualChildren[index]; }
    }
}
