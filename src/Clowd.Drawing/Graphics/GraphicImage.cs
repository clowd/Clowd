using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.RightsManagement;
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

        public ObscuredShape[] ObscuredShapes
        {
            get => _obscuredShapes;
            set
            {
                _imageObscured = null;
                Set(ref _obscuredShapes, value);
            }
        }

        public record struct ObscuredShape(Point P0, Point P1, Point P2, Point P3);

        private string _bitmapFilePath;
        private int _scaleX = 1;
        private int _scaleY = 1;
        private Int32Rect _crop;
        private Size _originalSize;
        private ObscuredShape[] _obscuredShapes = new ObscuredShape[0];
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
            var pts = new Point[] { rect.TopLeft, rect.TopRight, rect.BottomRight, rect.BottomLeft }.Select(UnapplyRotation).ToArray();
            if (!pts.Any(UnrotatedBounds.Contains))
                return;

            pts = pts.Select(TranslateUnrotatedPointToImageSpace).ToArray();
            ObscuredShapes = ObscuredShapes.Append(new ObscuredShape(pts[0], pts[1], pts[2], pts[3])).ToArray();
        }

        private Point TranslateUnrotatedPointToImageSpace(Point p)
        {
            if (_imageSource == null) UpdateImageCache();

            var x = p.X;
            var y = p.Y;

            var renderW = Right - Left;
            var renderH = Bottom - Top;
            var cropW = Crop.IsEmpty ? _imageSource.PixelWidth : Crop.Width;
            var cropH = Crop.IsEmpty ? _imageSource.PixelHeight : Crop.Height;
            var offsetX = Crop.IsEmpty ? 0 : Crop.X;
            var offsetY = Crop.IsEmpty ? 0 : Crop.Y;

            x -= Left;
            x /= renderW;
            x *= cropW;
            if (FlipX < 0) x = cropW - x;
            x += offsetX;

            y -= Top;
            y /= renderH;
            y *= cropH;
            if (FlipY < 0) y = cropH - y;
            y += offsetY;

            return new Point(x, y);
        }

        private Geometry ShapeToGeometry(ObscuredShape obr)
        {
            StreamGeometry geo = new StreamGeometry();
            using (var ctx = geo.Open())
            {
                ctx.BeginFigure(obr.P0, true, true);
                ctx.LineTo(obr.P1, false, false);
                ctx.LineTo(obr.P2, false, false);
                ctx.LineTo(obr.P3, false, false);
            }
            return geo;
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

            if (_obscuredShapes?.Any() != true)
            {
                _imageObscured = null;
                return false;
            }

            var first = _obscuredShapes[0];
            Geometry geo = ShapeToGeometry(first);
            foreach (var o in _obscuredShapes.Skip(1))
                geo = new CombinedGeometry(GeometryCombineMode.Union, geo, ShapeToGeometry(o));

            // this "obscure" works by resizing the bitmap to 1/8th and then stretching it back to 
            // it's original size with the NearestNeighbor scaling algorithm.
            var resized = new TransformedBitmap(_imageSource, new ScaleTransform(0.125, 0.125));
            var drawing = new NearestNeighborDrawingVisual();
            using (var ctx = drawing.RenderOpen())
            {
                ctx.PushClip(geo);
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
