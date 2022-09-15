using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RT.Serialization;

namespace Clowd.Drawing.Graphics
{
    [GraphicDesc("Image", Skills = Skill.Angle)]
    public class GraphicImage : GraphicRectangle
    {
        public string BitmapFilePath
        {
            get => _bitmapFilePath;
            set
            {
                _imageObscured = null;
                _imageSource = null;
                Set(ref _bitmapFilePath, value);
            }
        }

        public Int32Rect Crop
        {
            get => _crop;
            set => Set(ref _crop, value);
        }

        public int FlipX
        {
            get => _scaleX;
            set => Set(ref _scaleX, value);
        }

        public int FlipY
        {
            get => _scaleY;
            set => Set(ref _scaleY, value);
        }

        public Size OriginalSize
        {
            get => _originalSize;
            set => Set(ref _originalSize, value);
        }

        public Int32Rect[] ObscuredRects
        {
            get => _obscuredRects;
            set
            {
                _imageObscured = null;
                Set(ref _obscuredRects, value);
            }
        }

        private string _bitmapFilePath;
        private int _scaleX = 1;
        private int _scaleY = 1;
        private Int32Rect _crop;
        private Size _originalSize;
        private Int32Rect[] _obscuredRects = new Int32Rect[0];
        [ClassifyIgnore] private BitmapSource _imageSource;
        [ClassifyIgnore] private BitmapSource _imageObscured;

        protected GraphicImage()
        { }

        public GraphicImage(string imageFilePath, Size imageSize)
            : this(imageFilePath, new Rect(new Point(0, 0), imageSize), new Int32Rect())
        { }

        public GraphicImage(string imageFilePath, Rect displayRect, Int32Rect crop, double angle = 0, int flipX = 1, int flipY = 1)
            : this(imageFilePath, displayRect, crop, angle, flipX, flipY, displayRect.Size)
        { }

        protected GraphicImage(string imageFilePath, Rect displayRect, Int32Rect crop, double angle, int flipX, int flipY, Size originalSize)
            : base(Colors.Transparent, 0, displayRect, angle, false)
        {
            _originalSize = originalSize;
            _crop = crop; // must set crop before bitmap due to property setters
            BitmapFilePath = imageFilePath;
            Crop = crop;
            _scaleX = flipX;
            _scaleY = flipY;
        }

        internal override void DrawRectangle(DrawingContext drawingContext)
        {
            if (_imageSource == null) UpdateImageCache();

            Rect r = UnrotatedBounds;

            var centerX = r.Left + (r.Width / 2);
            var centerY = r.Top + (r.Height / 2);

            // push current flip transform
            drawingContext.PushTransform(new ScaleTransform(_scaleX, _scaleY, centerX, centerY));

            // push any current/unrealized resizing/rendering transform
            if (Right <= Left || Bottom <= Top)
                drawingContext.PushTransform(new ScaleTransform(Right <= Left ? -1 : 1, Bottom <= Top ? -1 : 1, centerX, centerY));

            drawingContext.DrawImage(new CroppedBitmap(_imageSource, Crop), r);

            if (_imageObscured != null || UpdateObscureCache())
                drawingContext.DrawImage(new CroppedBitmap(_imageObscured, Crop), r);

            if (Right <= Left || Bottom <= Top)
                drawingContext.Pop();

            drawingContext.Pop();
        }

        internal override void Normalize()
        {
            if (Right <= Left) _scaleX /= -1;
            if (Bottom <= Top) _scaleY /= -1;
            base.Normalize();
        }

        internal void AddObscuredArea(Rect rect)
        {
            if (_imageSource == null) UpdateImageCache();
            
            var bounds = UnrotatedBounds;
            rect.Intersect(bounds);
            if (rect.IsEmpty) return;

            var l = rect.X - bounds.Left;
            var t = rect.Y - bounds.Top;
            var r = l + rect.Width;
            var b = t + rect.Height;

            int translateX(double p) => Crop.IsEmpty 
                ? (int)(p / bounds.Width * _imageSource.PixelWidth)
                : (int)(p / bounds.Width * Crop.Width + Crop.X);
            int translateY(double p) => Crop.IsEmpty
                ? (int)(p / bounds.Height * _imageSource.PixelHeight)
                : (int)(p / bounds.Height * Crop.Height + Crop.Y);

            var x = translateX(l);
            var y = translateY(t);
            var w = translateX(r) - x;
            var h = translateY(b) - y;

            ObscuredRects = ObscuredRects.Append(new Int32Rect(x, y, w, h)).ToArray();
        }

        private void UpdateImageCache()
        {
            BitmapImage bi = new BitmapImage();
            bi.BeginInit();
            bi.UriSource = new Uri(BitmapFilePath);
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.EndInit();
            bi.Freeze();
            _imageSource = bi;
        }

        private bool UpdateObscureCache()
        {
            if (_imageSource == null) UpdateImageCache();

            if (_obscuredRects?.Any() != true)
            {
                _imageObscured = null;
                return false;
            }

            var first = _obscuredRects[0];
            Geometry clip = new RectangleGeometry(new Rect(first.X, first.Y, first.Width, first.Height));
            foreach (var obr in _obscuredRects.Skip(1))
            {
                var next = new RectangleGeometry(new Rect(obr.X, obr.Y, obr.Width, obr.Height));
                clip = new CombinedGeometry(GeometryCombineMode.Union, clip, next);
            }

            // this "obscure" works by resizing the bitmap to 1/8th and then stretching it back to 
            // it's original size with the NearestNeighbor scaling algorithm.
            var resized = new TransformedBitmap(_imageSource, new ScaleTransform(0.125, 0.125));
            var drawing = new NearestNeighborDrawingVisual();
            using (var ctx = drawing.RenderOpen())
            {
                ctx.PushClip(clip);
                ctx.DrawImage(resized, new Rect(0, 0, _imageSource.PixelWidth, _imageSource.PixelHeight));
                ctx.Pop();
            }

            var obscured = new RenderTargetBitmap(_imageSource.PixelWidth, _imageSource.PixelHeight, 96, 96, PixelFormats.Pbgra32);
            obscured.Render(drawing);
            obscured.Freeze();
            _imageObscured = obscured;
            return true;
        }

        private class NearestNeighborDrawingVisual : DrawingVisual
        {
            public NearestNeighborDrawingVisual()
            {
                VisualBitmapScalingMode = BitmapScalingMode.NearestNeighbor;
            }
        }
    }
}
