using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DrawToolsLib.Graphics;

namespace DrawToolsLib.Filters
{
    internal class FilterPixelate : FilterBase
    {
        private byte[] _imageBytes;
        private int _imageStride;
        private RenderTargetBitmap _rendered;
        private DrawingVisual _visual;

        public FilterPixelate(DrawingCanvas canvas, GraphicImage source) : base(canvas, source)
        {
            var image = source.BitmapSource;

            // make sure image is in the correct format.
            if (image.Format != PixelFormats.Bgra32)
            {
                FormatConvertedBitmap formatted = new FormatConvertedBitmap();
                formatted.BeginInit();
                formatted.Source = image;
                formatted.DestinationFormat = PixelFormats.Bgra32;
                formatted.EndInit();
                image = formatted;
            }

            int nStride = (image.PixelWidth * image.Format.BitsPerPixel + 7) / 8;
            byte[] pixelByteArray = new byte[image.PixelHeight * nStride];
            image.CopyPixels(pixelByteArray, nStride, 0);
            _imageBytes = pixelByteArray;
            _imageStride = nStride;

            _rendered = new RenderTargetBitmap(image.PixelWidth, image.PixelHeight, 96, 96, PixelFormats.Pbgra32);
            _visual = new DrawingVisual();
            canvas.GraphicsList.RegisterSubElement(source, _visual);
        }

        private int CalculateHalfPixelSize(ref int brushRadius)
        {
            const int minimumPixelSize = 4;
            var initialBrush = brushRadius;

            var brute = Enumerable.Range(minimumPixelSize, 3).Select(i =>
            {
                int closestBrush = 0;
                while (closestBrush < initialBrush)
                    closestBrush += i;
                return new { Pixel = i, Brush = closestBrush };
            }).OrderBy(x => Math.Abs(x.Brush - initialBrush)).ToArray();

            brushRadius = brute[0].Brush;
            return brute[0].Pixel;
        }

        public override void Handle(DrawingBrush brush, Point p)
        {
            // translate mouse point because relative to DPI and to the GraphicImage location / rotation
            p = Source.UnapplyRotation(p);
            var dpiXProperty = typeof(SystemParameters).GetProperty("DpiX", BindingFlags.NonPublic | BindingFlags.Static);
            var dpiRatio = (int)dpiXProperty.GetValue(null, null) / 96d;
            p = new Point(p.X * dpiRatio, p.Y * dpiRatio);
            p.Offset(-Source.Left * dpiRatio, -Source.Top * dpiRatio);

            var scaleRatioX = (Source.BitmapSource.PixelWidth / Source.UnrotatedBounds.Width) / dpiRatio;
            var scaleRatioY = (Source.BitmapSource.PixelHeight / Source.UnrotatedBounds.Height) / dpiRatio;
            p = new Point(p.X * scaleRatioX, p.Y * scaleRatioY);

            // brushSize must be a multiple of pixelSize and also a multiple of 2 for nice results.
            int halfBrushSize = brush.Radius;
            int halfPixelSize = CalculateHalfPixelSize(ref halfBrushSize);
            int brushSize = halfBrushSize * 2;
            int pixelSize = halfPixelSize * 2;

            DrawingVisual vis = new DrawingVisual();
            DrawingContext con = vis.RenderOpen();
            con.PushOpacityMask(brush.Brush);

            // snap the current rectangle to the grid defined by pixelSize
            var rect = new Rect(p.X - halfBrushSize, p.Y - halfBrushSize, brushSize, brushSize);
            double offsetX = -rect.X % pixelSize;
            if (Math.Abs(offsetX) > halfPixelSize)
                offsetX += pixelSize;
            double offsetY = -rect.Y % pixelSize;
            if (Math.Abs(offsetY) > halfPixelSize)
                offsetY += pixelSize;
            rect.Offset(offsetX, offsetY);

            // split rect into smaller "pixels"
            var n = brushSize / pixelSize;
            Rect[] smallerRects = new Rect[n * n];
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                    smallerRects[x + (y * n)] = new Rect(x * pixelSize + rect.X, y * pixelSize + rect.Y, pixelSize, pixelSize);

            var imageRect = new Rect(0, 0, Source.BitmapSource.Width, Source.BitmapSource.Height);
            long red = 0, green = 0, blue = 0;
            int numPixels = pixelSize * pixelSize;
            foreach (var wpfRect in smallerRects)
            {
                var translatedRect = new Rect(wpfRect.X / dpiRatio, wpfRect.Y / dpiRatio, wpfRect.Width / dpiRatio, wpfRect.Height / dpiRatio);
                if (!imageRect.Contains(translatedRect))
                    continue;

                // find the average color inside of this rectangle, and draw that color
                var r = new Int32Rect((int)wpfRect.X, (int)wpfRect.Y, (int)wpfRect.Width, (int)wpfRect.Height);
                for (int y = 0; y < r.Height; y++)
                {
                    for (int x = 0; x < r.Width; x++)
                    {
                        var curX = (r.X * 4) + (x * 4);
                        var curY = (r.Y * _imageStride) + (y * _imageStride);
                        int pixelStart = curX + curY;
                        if (pixelStart + 2 >= _imageBytes.Length)
                        {
                            blue += 255;
                            green += 255;
                            red += 255;
                        }
                        else
                        {
                            blue += _imageBytes[pixelStart];
                            green += _imageBytes[pixelStart + 1];
                            red += _imageBytes[pixelStart + 2];
                        }
                    }
                }

                var avgColor = Color.FromRgb((byte)(red / numPixels), (byte)(green / numPixels), (byte)(blue / numPixels));
                red = green = blue = 0;
                con.DrawRectangle(new SolidColorBrush(avgColor), null, wpfRect);
            }
            con.Close();
            _rendered.Render(vis);
            using (var ctx = _visual.RenderOpen())
            {
                var centerX = (Source.Right - Source.Left) / 2 + Source.Left;
                var centerY = (Source.Bottom - Source.Top) / 2 + Source.Top;
                ctx.PushTransform(new RotateTransform(Source.Angle, centerX, centerY));
                ctx.DrawImage(_rendered, Source.UnrotatedBounds);
            }
        }

        public override void Close()
        {
            var image = Source.BitmapSource;
            this.Canvas.GraphicsList.RemoveSubElement(Source, _visual);

            var vis = new DrawingVisual();
            using (var ctx = vis.RenderOpen())
            {
                var imgRect = new Rect(0, 0, image.PixelWidth, image.PixelHeight);
                ctx.DrawImage(Source.BitmapSource, imgRect);
                ctx.DrawImage(_rendered, imgRect);
            }

            var final = new RenderTargetBitmap(image.PixelWidth, image.PixelHeight, 96, 96, PixelFormats.Pbgra32);
            final.Render(vis);

            Source.BitmapSource = final;
        }
    }
}
