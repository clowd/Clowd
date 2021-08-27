using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RT.Serialization;

namespace Clowd.Drawing.Graphics
{
    public class GraphicImage : GraphicRectangle
    {
        public string BitmapFilePath
        {
            get => _bitmapFilePath;
            set => Set(ref _bitmapFilePath, value);
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

        private string _bitmapFilePath;
        private int _scaleX = 1;
        private int _scaleY = 1;
        private Int32Rect _crop;
        private Size _originalSize;

        protected GraphicImage()
        {
        }

        public GraphicImage(string imageFilePath, Size imageSize)
            : this(imageFilePath, new Rect(new Point(0, 0), imageSize), new Int32Rect())
        {
        }

        public GraphicImage(string imageFilePath, Rect displayRect, Int32Rect crop, double angle = 0, int flipX = 1, int flipY = 1)
            : this(imageFilePath, displayRect, crop, angle, flipX, flipY, displayRect.Size)
        {
        }

        protected GraphicImage(string imageFilePath, Rect displayRect, Int32Rect crop, double angle, int flipX, int flipY, Size originalSize)
            : base(Colors.Transparent, 0, displayRect, false, angle, false)
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
            var bmp = CachedBitmapLoader.LoadFromFile(_bitmapFilePath);
            var crop = new CroppedBitmap(bmp, Crop);

            Rect r = UnrotatedBounds;

            var centerX = r.Left + (r.Width / 2);
            var centerY = r.Top + (r.Height / 2);

            // push current flip transform
            drawingContext.PushTransform(new ScaleTransform(_scaleX, _scaleY, centerX, centerY));

            // push any current/unrealized resizing/rendering transform
            if (Right <= Left || Bottom <= Top)
                drawingContext.PushTransform(new ScaleTransform(Right <= Left ? -1 : 1, Bottom <= Top ? -1 : 1, centerX, centerY));

            drawingContext.DrawImage(crop, r);

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
    }
}
