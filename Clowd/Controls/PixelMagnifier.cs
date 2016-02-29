using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CS.Wpf;

namespace Clowd.Controls
{
    public class PixelMagnifier : FrameworkElement
    {
        public BitmapSource Image
        {
            get { return (BitmapSource)GetValue(ImageProperty); }
            set { SetValue(ImageProperty, value); }
        }

        public Size FinderSize
        {
            get
            {
                var pix = DpiScale.UpScaleX(_zoom);
                return new Size(pix * _size, pix * _size);
            }
        }

        public static readonly DependencyProperty ImageProperty =
            DependencyProperty.Register("Image", typeof(BitmapSource), typeof(PixelMagnifier), new PropertyMetadata(null));


        private DrawingVisual _visual = new MyDrawingVisual();
        private Point _lastPoint = default(Point);
        private int _size = 13;
        private int _zoom = 10;

        public PixelMagnifier()
        {
            AddVisualChild(_visual);
        }

        public void DrawMagnifier(Point location)
        {
            if (_lastPoint == location)
                return;
            _lastPoint = location;

            location = DpiScale.UpScalePoint(location);
            using (DrawingContext dc = _visual.RenderOpen())
            {
                var pixSize = (int)Math.Ceiling((double)_zoom / 96 * DpiScale.DpiX);
                Size size = new Size(0, 0);
                if (Image != null)
                {
                    size = CreateMagnifier(dc, Image, location, _size, _size, pixSize);
                    Pen pen = new Pen(Brushes.DarkGray, 2);
                    dc.DrawEllipse(null, pen, new Point(size.Width / 2 + 0.5, size.Height / 2 + 0.5), size.Width / 2 - 1, size.Height / 2 - 1);
                    size = new Size(DpiScale.DownScaleX(size.Width), DpiScale.DownScaleY(size.Height));
                }
                this.Clip = new EllipseGeometry(new Point(size.Width / 2 + 0.5, size.Height / 2 + 0.5), size.Width / 2 - 1, size.Height / 2 - 1);
                this.Width = size.Width;
                this.Height = size.Height;
            }
        }

        protected override int VisualChildrenCount
        {
            get { return 1; }
        }

        protected override Visual GetVisualChild(int index)
        {
            return _visual;
        }

        private Size CreateMagnifier(DrawingContext g, BitmapSource source, Point position, int horizontalPixelCount,
           int verticalPixelCount, int pixelSize)
        {
            double width = horizontalPixelCount * pixelSize;
            double height = verticalPixelCount * pixelSize;

            var cornerX = (int)position.X - horizontalPixelCount / 2;
            var cornerY = (int)position.Y - verticalPixelCount / 2;

            var sourceRect = new Int32Rect(cornerX, cornerY, horizontalPixelCount, verticalPixelCount);
            var targetRect = new Rect(0, 0, width, height);

            // Crop the source & target rectangles so that they don't go past the edges of the screen(s)
            if (sourceRect.X < 0)
            {
                sourceRect.X -= cornerX;
                sourceRect.Width += cornerX;
                targetRect.X -= cornerX * pixelSize;
                targetRect.Width += cornerX * pixelSize;
            }
            if (sourceRect.Y < 0)
            {
                sourceRect.Y -= cornerY;
                sourceRect.Height += cornerY;
                targetRect.Y -= cornerY * pixelSize;
                targetRect.Height += cornerY * pixelSize;
            }
            if (sourceRect.X + sourceRect.Width > source.PixelWidth)
            {
                int excess = sourceRect.X + sourceRect.Width - source.PixelWidth;
                sourceRect.Width -= excess;
                targetRect.Width -= excess * pixelSize;
            }
            if (sourceRect.Y + sourceRect.Height > source.PixelHeight)
            {
                int excess = sourceRect.Y + sourceRect.Height - source.PixelHeight;
                sourceRect.Height -= excess;
                targetRect.Height -= excess * pixelSize;
            }

            // Draw the background that shows when the magnifier is near the edge of the screen
            g.DrawRectangle(Brushes.Black, null, new Rect(0, 0, width, height));

            // Draw the magnified image
            var group = new DrawingGroup();
            group.Children.Add(new ImageDrawing(new CroppedBitmap(source, sourceRect), targetRect));
            g.DrawDrawing(group);

            // Draw the pixel grid lines
            var gridPen = new Pen(Brushes.DimGray, 1);
            for (int x = sourceRect.X - cornerX; x <= sourceRect.X + sourceRect.Width - cornerX; x++)
                g.DrawLine(gridPen, new Point(x * pixelSize + 0.5, targetRect.Top), new Point(x * pixelSize + 0.5, targetRect.Bottom));
            for (int y = sourceRect.Y - cornerY; y <= sourceRect.Y + sourceRect.Height - cornerY; y++)
                g.DrawLine(gridPen, new Point(targetRect.Left, y * pixelSize + 0.5), new Point(targetRect.Right, y * pixelSize + 0.5));

            // Draw the crosshair
            SolidColorBrush crosshairBrush = new SolidColorBrush(Color.FromArgb(125, 173, 216, 230)); // light blue
            g.DrawRectangle(crosshairBrush, null, new Rect(0, (height - pixelSize) / 2, (width - pixelSize) / 2, pixelSize + 1)); // Left
            g.DrawRectangle(crosshairBrush, null, new Rect((width + pixelSize) / 2, (height - pixelSize) / 2, (width - pixelSize) / 2, pixelSize + 1)); // Right
            g.DrawRectangle(crosshairBrush, null, new Rect((width - pixelSize) / 2, 0, pixelSize + 1, (height - pixelSize) / 2)); // Top
            g.DrawRectangle(crosshairBrush, null, new Rect((width - pixelSize) / 2, (height + pixelSize) / 2, pixelSize + 1, (height - pixelSize) / 2)); // Bottom

            // Draw a highlight around the pixel under cursor
            g.DrawRectangle(null, new Pen(Brushes.Black, 1), new Rect((width - pixelSize) / 2 - 0.5, (height - pixelSize) / 2 - 0.5, pixelSize + 2, pixelSize + 2));
            g.DrawRectangle(null, new Pen(Brushes.White, 1), new Rect((width - pixelSize) / 2 + 0.5, (height - pixelSize) / 2 + 0.5, pixelSize, pixelSize));

            return new Size(width, height);
        }

        private class MyDrawingVisual : DrawingVisual
        {
            public MyDrawingVisual()
            {
                VisualBitmapScalingMode = BitmapScalingMode.NearestNeighbor;
                VisualEdgeMode = EdgeMode.Unspecified;
                if (!DesignerProperties.GetIsInDesignMode(this))
                    Transform = DpiScale.DownScaleTransform;
            }
        }
    }
}
