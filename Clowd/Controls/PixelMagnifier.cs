using CS.Wpf;
using ScreenVersusWpf;
using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Clowd.Controls
{
    public class PixelMagnifier : FrameworkElement
    {
        public BitmapSource Image
        {
            get { return (BitmapSource)GetValue(ImageProperty); }
            set { SetValue(ImageProperty, value); }
        }

        public WpfSize FinderSize => (_singlePixelSize * _zoomedPixels).ToWpfSize();
        private ScreenSize _singlePixelSize => new WpfSize(App.Current.Settings.MagnifierSettings.Zoom, App.Current.Settings.MagnifierSettings.Zoom).ToScreenSize();
        private int _zoomedPixels => App.Current.Settings.MagnifierSettings.AreaSize - App.Current.Settings.MagnifierSettings.AreaSize % 2 + 1;

        public static readonly DependencyProperty ImageProperty =
            DependencyProperty.Register("Image", typeof(BitmapSource), typeof(PixelMagnifier), new PropertyMetadata(null));


        private DrawingVisual _visual = new MyDrawingVisual();
        private ScreenPoint _lastPoint;

        public PixelMagnifier()
        {
            AddVisualChild(_visual);
        }

        public void DrawMagnifier(ScreenPoint location)
        {
            if (_lastPoint == location)
                return;
            _lastPoint = location;

            using (DrawingContext g = _visual.RenderOpen())
            {
                if (Image == null)
                    return;

                var cornerX = (int)location.X - _zoomedPixels / 2;
                var cornerY = (int)location.Y - _zoomedPixels / 2;
                var px = ScreenTools.ScreenToWpf(1);

                var sourceRect = new ScreenRect(cornerX, cornerY, _zoomedPixels, _zoomedPixels);
                var targetRect = new WpfRect(0, 0, FinderSize.Width, FinderSize.Height);

                // Crop the source & target rectangles so that they don't go past the edges of the screen(s)
                var zoomedPixel = _singlePixelSize.ToWpfSize();
                if (sourceRect.Left < 0)
                {
                    sourceRect.Left -= cornerX;
                    sourceRect.Width += cornerX;
                    targetRect.Left -= cornerX * zoomedPixel.Width;
                    targetRect.Width += cornerX * zoomedPixel.Width;
                }
                if (sourceRect.Top < 0)
                {
                    sourceRect.Top -= cornerY;
                    sourceRect.Height += cornerY;
                    targetRect.Top -= cornerY * zoomedPixel.Height;
                    targetRect.Height += cornerY * zoomedPixel.Height;
                }
                if (sourceRect.Left + sourceRect.Width > Image.PixelWidth)
                {
                    int excess = sourceRect.Left + sourceRect.Width - Image.PixelWidth;
                    sourceRect.Width -= excess;
                    targetRect.Width -= excess * zoomedPixel.Width;
                }
                if (sourceRect.Top + sourceRect.Height > Image.PixelHeight)
                {
                    int excess = sourceRect.Top + sourceRect.Height - Image.PixelHeight;
                    sourceRect.Height -= excess;
                    targetRect.Height -= excess * zoomedPixel.Height;
                }

                // Draw the black background visible at the edge of the screen where no zoomed pixels are available
                g.DrawRectangle(Brushes.Black, null, new Rect(0, 0, FinderSize.Width, FinderSize.Height));

                // Draw the magnified image
                var group = new DrawingGroup();
                group.Children.Add(new ImageDrawing(new CroppedBitmap(Image, sourceRect), targetRect));
                g.DrawDrawing(group);

                // Draw the pixel grid lines
                var gridLinePixelWidth = Math.Max(ScreenTools.WpfToScreen(App.Current.Settings.MagnifierSettings.GridLineWidth), 1); 
                var gridLineWidth = ScreenTools.ScreenToWpf(gridLinePixelWidth);
                var gridPen = new Pen(Brushes.DimGray, gridLineWidth);
                var gridOffset = (gridLinePixelWidth % 2) * 0.5 * px; // offset the line by 0.5 pixels if the line width is odd, to avoid blurring
                g.PushTransform(new TranslateTransform(gridOffset, gridOffset));
                for (int x = sourceRect.Left - cornerX; x <= sourceRect.Left + sourceRect.Width - cornerX; x++)
                    g.DrawLine(gridPen, new Point(x * zoomedPixel.Width, targetRect.Top), new Point(x * zoomedPixel.Width, targetRect.Bottom));
                for (int y = sourceRect.Top - cornerY; y <= sourceRect.Top + sourceRect.Height - cornerY; y++)
                    g.DrawLine(gridPen, new Point(targetRect.Left, y * zoomedPixel.Height), new Point(targetRect.Right, y * zoomedPixel.Height));

                // Draw the crosshair
                var xhairBrush = new SolidColorBrush(App.Current.Settings.MagnifierSettings.CrosshairColor);
                var xhairGrow = gridLineWidth / 2; // make sure the crosshair rectangles cover the adjacent grid lines wholly on both sides
                g.DrawRectangle(xhairBrush, null, new WpfRect(0, (FinderSize.Height - zoomedPixel.Height) / 2, (FinderSize.Width - zoomedPixel.Width) / 2, zoomedPixel.Height).Grow(xhairGrow)); // Left
                g.DrawRectangle(xhairBrush, null, new WpfRect((FinderSize.Width + zoomedPixel.Width) / 2, (FinderSize.Height - zoomedPixel.Height) / 2, (FinderSize.Width - zoomedPixel.Width) / 2, zoomedPixel.Height).Grow(xhairGrow)); // Right
                g.DrawRectangle(xhairBrush, null, new WpfRect((FinderSize.Width - zoomedPixel.Width) / 2, 0, zoomedPixel.Width, (FinderSize.Height - zoomedPixel.Height) / 2).Grow(xhairGrow)); // Top
                g.DrawRectangle(xhairBrush, null, new WpfRect((FinderSize.Width - zoomedPixel.Width) / 2, (FinderSize.Height + zoomedPixel.Height) / 2, zoomedPixel.Width, (FinderSize.Height - zoomedPixel.Height) / 2).Grow(xhairGrow)); // Bottom

                // Draw a highlight around the pixel under cursor
                var innerRect = new WpfRect((FinderSize.Width - zoomedPixel.Width) / 2, (FinderSize.Height - zoomedPixel.Height) / 2, zoomedPixel.Width, zoomedPixel.Height);
                g.DrawRectangle(null, new Pen(Brushes.White, gridLineWidth), innerRect);
                g.DrawRectangle(null, new Pen(Brushes.Black, gridLineWidth), innerRect.Grow(gridLineWidth));
                g.Pop(); // grid line 0.5 px offset

                // Draw the magnifier border
                Pen pen = new Pen(new SolidColorBrush(App.Current.Settings.MagnifierSettings.BorderColor), App.Current.Settings.MagnifierSettings.BorderWidth);
                g.DrawEllipse(null, pen, new Point(FinderSize.Width / 2 + gridOffset, FinderSize.Height / 2 + gridOffset), FinderSize.Width / 2, FinderSize.Height / 2);
                // Clip to the exact same ellipse (thus clipping off half of the drawn border)
                this.Clip = new EllipseGeometry(new Point(FinderSize.Width / 2 + gridOffset, FinderSize.Height / 2 + gridOffset), FinderSize.Width / 2, FinderSize.Height / 2);

                this.Width = FinderSize.Width;
                this.Height = FinderSize.Height;
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

        private class MyDrawingVisual : DrawingVisual
        {
            public MyDrawingVisual()
            {
                VisualBitmapScalingMode = BitmapScalingMode.NearestNeighbor;
            }
        }
    }
}
