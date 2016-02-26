using System;
using System.Collections.Generic;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Clowd.Utilities;


namespace Clowd.Controls
{
    public class PixelMagnifier : FrameworkElement
    {
        private DrawingVisual visual = new MyDrawingVisual();

        public PixelMagnifier()
        {
            AddVisualChild(visual);
        }

        public void DrawMagnifier(BitmapSource source, Point location)
        {
            using (DrawingContext dc = visual.RenderOpen())
            {
                var pixSize = (int)Math.Ceiling((double)10 / 96 * DpiScale.DpiX);
                Size size = new Size(0,0);
                if (source != null && location != default(Point))
                {
                    size = CreateMagnifier(dc, source, location, 13, 13, pixSize);
                    Pen pen = new Pen(Brushes.DarkGray, 2);
                    dc.DrawEllipse(null, pen, new Point(size.Width/2, size.Height/2), size.Width/2, size.Height/2);
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
            return visual;
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
        private static BitmapSource EnlargeImage(BitmapSource source, int pixelSize)
        {
            // force the pixel format to bgra32
            if (source.Format != PixelFormats.Bgra32)
                source = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);

            WriteableBitmap writable = new WriteableBitmap(
                source.PixelWidth * pixelSize,
                source.PixelHeight * pixelSize,
                96, 96, source.Format, null);

            var bytesPerPixel = (source.Format.BitsPerPixel + 7) / 8;
            var croppedBytes = new byte[bytesPerPixel * source.PixelWidth * source.PixelHeight];
            int stride = 4 * ((source.PixelWidth * bytesPerPixel + 3) / 4);
            source.CopyPixels(new Int32Rect(0, 0, source.PixelWidth, source.PixelHeight), croppedBytes, stride, 0);

            var writableBytes = new byte[croppedBytes.Length * pixelSize * pixelSize];
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

                        //Buffer.BlockCopy(sepBytes, 0, writableBytes, writableIndex, 4);
                        writableIndex += bytesPerPixel;
                    }
                }
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
                Transform = DpiScale.DownScaleTransform;
            }
        }
    }
}
