using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Clowd.Utilities;
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
                var pix = DpiScale.UpScaleX(10);
                return new Size(pix * _size, pix * _size);
            }
        }

        public static readonly DependencyProperty ImageProperty =
            DependencyProperty.Register("Image", typeof(BitmapSource), typeof(PixelMagnifier), new PropertyMetadata(null));


        private DrawingVisual _visual = new MyDrawingVisual();
        private DispatcherTimer _timer = new DispatcherTimer();
        private Point _lastPoint = default(Point);
        private int _size = 13;
        private byte[] croppedBytes = new byte[0];
        private byte[] writableBytes = new byte[0];
        public PixelMagnifier()
        {
            AddVisualChild(_visual);
            _timer.Interval = TimeSpan.FromMilliseconds(30);
            _timer.Tick += (sender, args) =>
            {
                if (this.Visibility != Visibility.Visible)
                    return;
                var wfMouse = System.Windows.Forms.Cursor.Position;
                var wpfMouse = new Point(
                    wfMouse.X - System.Windows.Forms.SystemInformation.VirtualScreen.X,
                    wfMouse.Y - System.Windows.Forms.SystemInformation.VirtualScreen.Y);
                if (_lastPoint == wpfMouse)
                    return;
                _lastPoint = wpfMouse;
                DrawMagnifier(Image, wpfMouse);
            };
            _timer.IsEnabled = true;
        }

        private void DrawMagnifier(BitmapSource source, Point location)
        {
            using (DrawingContext dc = _visual.RenderOpen())
            {
                var pixSize = (int)Math.Ceiling((double)10 / 96 * DpiScale.DpiX);
                Size size = new Size(0, 0);
                if (source != null && location != default(Point))
                {
                    size = CreateMagnifier(dc, source, location, _size, _size, pixSize);
                    Pen pen = new Pen(Brushes.DarkGray, 2);
                    dc.DrawEllipse(null, pen, new Point(size.Width / 2, size.Height / 2), size.Width / 2, size.Height / 2);
                    size = new Size(DpiScale.DownScaleX(size.Width), DpiScale.DownScaleY(size.Height));
                }
                this.Clip = new EllipseGeometry(new Point(size.Width / 2, size.Height / 2), size.Width / 2, size.Height / 2);
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

            var cX = Math.Floor(position.X - (double)horizontalPixelCount / 2);
            var cY = Math.Floor(position.Y - (double)verticalPixelCount / 2);

            // if coordinates are out of the image bounds.
            if (cX < 0 || cY < 0 || cX + horizontalPixelCount > source.PixelWidth ||
                cY + verticalPixelCount > source.PixelHeight)
                return default(Size);

            BitmapSource cropped = new CroppedBitmap(source, new Int32Rect(
                    (int)cX,
                    (int)cY,
                    horizontalPixelCount,
                    verticalPixelCount));

            var enlarged = EnlargeImage(cropped, pixelSize);

            g.DrawRectangle(Brushes.DarkGray, null, new Rect(0, 0, width, height));
            g.DrawImage(enlarged, new Rect(0, 0, width, height));

            SolidColorBrush crosshairBrush = new SolidColorBrush(Color.FromArgb(125, 173, 216, 230)); // light blue
            g.DrawRectangle(crosshairBrush, null, new Rect(0, (height - pixelSize) / 2, (width - pixelSize) / 2, pixelSize)); // Left
            g.DrawRectangle(crosshairBrush, null, new Rect((width + pixelSize) / 2, (height - pixelSize) / 2, (width - pixelSize) / 2, pixelSize)); // Right
            g.DrawRectangle(crosshairBrush, null, new Rect((width - pixelSize) / 2, 0, pixelSize, (height - pixelSize) / 2)); // Top
            g.DrawRectangle(crosshairBrush, null, new Rect((width - pixelSize) / 2, (height + pixelSize) / 2, pixelSize, (height - pixelSize) / 2)); // Bottom

            g.DrawRectangle(null, new Pen(Brushes.Black, 1), new Rect((width - pixelSize) / 2 - 1, (height - pixelSize) / 2 - 1, pixelSize, pixelSize));
            g.DrawRectangle(null, new Pen(Brushes.White, 1), new Rect((width - pixelSize) / 2, (height - pixelSize) / 2, pixelSize - 2, pixelSize - 2));

            return new Size(enlarged.PixelWidth, enlarged.PixelHeight);
        }
        private BitmapSource EnlargeImage(BitmapSource source, int pixelSize)
        {
            // force the pixel format to bgra32
            if (source.Format != PixelFormats.Bgra32)
                source = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);

            WriteableBitmap writable = new WriteableBitmap(
                source.PixelWidth * pixelSize,
                source.PixelHeight * pixelSize,
                96, 96, source.Format, null);

            var bytesPerPixel = (source.Format.BitsPerPixel + 7) / 8;
            int stride = 4 * ((source.PixelWidth * bytesPerPixel + 3) / 4);

            // prepare arrays
            var croppedBytesLength = bytesPerPixel * source.PixelWidth * source.PixelHeight;
            if (croppedBytes.Length != croppedBytesLength)
                croppedBytes = new byte[croppedBytesLength];

            var writableBytesLength = croppedBytes.Length * pixelSize * pixelSize;
            if (writableBytes.Length != writableBytesLength)
                writableBytes = new byte[writableBytesLength];

            source.CopyPixels(new Int32Rect(0, 0, source.PixelWidth, source.PixelHeight), croppedBytes, stride, 0);
            int writableIndex = 0;

            // for each row of pixels
            for (int x = 0; x < croppedBytes.Length / stride; x++)
            {
                // this row [pixelSize] times.
                for (int j = 0; j < pixelSize - 1; j++)
                {
                    // for each pixel in row
                    for (int y = 0; y < stride; y += bytesPerPixel)
                    {
                        // this pixel [pixelSize] times.
                        for (int z = 0; z < pixelSize - 1; z++)
                        {
                            int srcIndex = y + (x * stride);
                            //this doesnt work, i'm not sure of the formula i need to calculate the destination
                            //int desIndex = y * pixelSize + (z * bytesPerPixel) + (j * stride) + (x * stride);
                            int desIndex = writableIndex;
                            writableIndex += bytesPerPixel;
                            Buffer.BlockCopy(croppedBytes, srcIndex, writableBytes, desIndex, bytesPerPixel);
                        }

                        //we skip the last pixel in each segment of pixels to create a separation of pixels
                        writableIndex += bytesPerPixel;
                    }
                }
                //we skip the last row of pixels in each segment of rows to create a separation of pixels
                writableIndex += stride * pixelSize;
            }

            writable.WritePixels(new Int32Rect(0, 0, writable.PixelWidth, writable.PixelHeight), writableBytes, stride * pixelSize, 0);
            return writable;
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
