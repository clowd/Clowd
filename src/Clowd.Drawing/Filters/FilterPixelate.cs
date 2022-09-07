using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Clowd.Drawing.Graphics;
using Clowd.PlatformUtil;

namespace Clowd.Drawing.Filters
{
    internal class FilterPixelate : FilterBase
    {
        private BitmapSource _imageSource;
        private RenderTargetBitmap _imageOverlay;
        private byte[] _imageBytes;
        private int _imageStride;
        private Image _rendered;
        private ScreenSize _originalSize;

        public FilterPixelate(DrawingCanvas canvas, GraphicImage source) : base(canvas, source)
        {
            var image = CachedBitmapLoader.LoadFromFile(source.BitmapFilePath);
            _originalSize = new ScreenSize(image.PixelWidth, image.PixelHeight);
            image = new CroppedBitmap(image, source.Crop);

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
            _imageSource = image;

            // load any previous pixelations into the current overlay
            _imageOverlay = new RenderTargetBitmap(image.PixelWidth, image.PixelHeight, 96, 96, PixelFormats.Pbgra32);
            if (source.DecoratorFilePath != null)
            {
                var decorator = CachedBitmapLoader.LoadFromFile(source.DecoratorFilePath);
                decorator = new CroppedBitmap(decorator, source.Crop);
                var vis = new DrawingVisual();
                using (var ctx = vis.RenderOpen())
                    ctx.DrawImage(decorator, new Rect(0, 0, decorator.PixelWidth, decorator.PixelHeight));
                _imageOverlay.Render(vis);
            }

            // add overlay to canvas as child element
            _rendered = new Image() { Source = _imageOverlay, Stretch = Stretch.Fill, Width = source.UnrotatedBounds.Width, Height = source.UnrotatedBounds.Height };
            _rendered.RenderTransform = new RotateTransform(source.Angle, (source.Right - source.Left) / 2, (source.Bottom - source.Top) / 2);
            Canvas.SetLeft(_rendered, source.Left);
            Canvas.SetTop(_rendered, source.Top);
            canvas.Children.Add(_rendered);
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

        protected override void HandleInternal(DrawingBrush brush, Point p)
        {
            // translate mouse point because relative to DPI and to the GraphicImage location / rotation
            p = Source.UnapplyRotation(p);
            p.Offset(-Source.Left, -Source.Top);
            var scaleRatioX = (_imageSource.PixelWidth / Source.UnrotatedBounds.Width);
            var scaleRatioY = (_imageSource.PixelHeight / Source.UnrotatedBounds.Height);
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
            {
                for (int x = 0; x < n; x++)
                {
                    smallerRects[x + (y * n)] = new Rect(x * pixelSize + rect.X, y * pixelSize + rect.Y, pixelSize, pixelSize);
                }
            }

            var imageRect = new Rect(0, 0, _imageSource.Width, _imageSource.Height);
            long red = 0, green = 0, blue = 0;
            int numPixels = pixelSize * pixelSize;
            foreach (var wpfRect in smallerRects)
            {
                // var translatedRect = new Rect(wpfRect.X / dpiRatio, wpfRect.Y / dpiRatio, wpfRect.Width / dpiRatio, wpfRect.Height / dpiRatio);
                var translatedRect = new Rect(wpfRect.X, wpfRect.Y, wpfRect.Width, wpfRect.Height);
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
            _imageOverlay.Render(vis);
        }

        public override void Close()
        {
            var vis = new DrawingVisual();
            using (var ctx = vis.RenderOpen())
            {
                ctx.DrawImage(_imageOverlay, new Rect(Source.Crop.X, Source.Crop.Y, Source.Crop.Width, Source.Crop.Height));
            }

            var final = new RenderTargetBitmap(_originalSize.Width, _originalSize.Height, 96, 96, PixelFormats.Pbgra32);
            final.Render(vis);

            var filePath = Path.Combine(Path.GetDirectoryName(Source.BitmapFilePath), Guid.NewGuid() + ".png");
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(final));

            using (var fs = File.Create(filePath))
            {
                enc.Save(fs);
            }

            Source.DecoratorFilePath = filePath;
            MyCanvas.Children.Remove(_rendered);
        }
    }
}
