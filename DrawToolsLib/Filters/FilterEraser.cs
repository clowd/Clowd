using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DrawToolsLib.Graphics;

namespace DrawToolsLib.Filters
{
    internal class FilterEraser : FilterBase
    {
        private RenderTargetBitmap _rendered;
        private DrawingVisual _visual;

        public FilterEraser(DrawingCanvas canvas, GraphicImage source) : base(canvas, source)
        {
            var image = source.BitmapSource;
            _rendered = new RenderTargetBitmap(image.PixelWidth, image.PixelHeight, 96, 96, PixelFormats.Pbgra32);
            _visual = new DrawingVisual();
            canvas.GraphicsList.RegisterSubElement(source, _visual);
        }

        public override void Handle(DrawingBrush brush, Point p)
        {
            // transform mouse position
            p = Source.UnapplyRotation(p);
            var dpiXProperty = typeof(SystemParameters).GetProperty("DpiX", BindingFlags.NonPublic | BindingFlags.Static);
            var dpiRatio = (int)dpiXProperty.GetValue(null, null) / 96d;
            p = new Point(p.X * dpiRatio, p.Y * dpiRatio);
            p.Offset(-Source.Left * dpiRatio, -Source.Top * dpiRatio);
            var scaleRatioX = (Source.BitmapSource.PixelWidth / Source.UnrotatedBounds.Width) / dpiRatio;
            var scaleRatioY = (Source.BitmapSource.PixelHeight / Source.UnrotatedBounds.Height) / dpiRatio;
            p = new Point(p.X * scaleRatioX, p.Y * scaleRatioY);

            // draw white brush in place of eraser to _rendered bitmap
            DrawingVisual vis = new DrawingVisual();
            DrawingContext con = vis.RenderOpen();
            con.PushOpacityMask(brush.Brush);
            var rect = new Rect(p.X - brush.Radius, p.Y - brush.Radius, brush.Radius * 2, brush.Radius * 2);
            con.DrawRectangle(Brushes.White, null, rect);
            con.Close();
            _rendered.Render(vis);

            // render the bitmap to our child visual with the parent rotation
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

            // convert pixel format to Bgra32
            FormatConvertedBitmap inverseOpacityMaskBitmap = new FormatConvertedBitmap();
            inverseOpacityMaskBitmap.BeginInit();
            inverseOpacityMaskBitmap.Source = _rendered;
            inverseOpacityMaskBitmap.DestinationFormat = PixelFormats.Bgra32;
            inverseOpacityMaskBitmap.EndInit();

            // get pixel buffer and invert alpha channel
            int stride = (image.PixelWidth * image.Format.BitsPerPixel + 7) / 8;
            var pixelBuffer = new byte[stride * inverseOpacityMaskBitmap.PixelHeight];
            inverseOpacityMaskBitmap.CopyPixels(pixelBuffer, stride, 0);
            var numPixels = image.PixelWidth * image.PixelHeight;
            for (int i = 0; i < numPixels; i++)
            {
                var index = (i * 4) + 3;
                pixelBuffer[index] = (byte)unchecked(pixelBuffer[index] ^ 0xff);
            }

            // write inverted pixel buffer to new bitmap
            var opacityMaskBitmap = new WriteableBitmap(image.PixelWidth, image.PixelHeight, image.DpiX, image.DpiY,
                PixelFormats.Bgra32, BitmapPalettes.WebPalette);
            opacityMaskBitmap.WritePixels(new Int32Rect(0, 0, image.PixelWidth, image.PixelHeight), pixelBuffer, stride, 0);
            var opacityMask = new ImageBrush(opacityMaskBitmap);

            // apply bitmap as mask and draw original image
            var vis = new DrawingVisual();
            using (var ctx = vis.RenderOpen())
            {
                var imgRect = new Rect(0, 0, image.PixelWidth, image.PixelHeight);
                ctx.PushOpacityMask(opacityMask);
                ctx.DrawImage(Source.BitmapSource, imgRect);
            }
            var final = new RenderTargetBitmap(image.PixelWidth, image.PixelHeight, 96, 96, PixelFormats.Pbgra32);

            final.Render(vis);
            Source.BitmapSource = final;
        }
    }
}
