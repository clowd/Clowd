using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Serialization;

namespace Clowd.Drawing.Graphics
{
    [Serializable]
    public class GraphicImage : GraphicRectangle
    {
        public string BitmapFilePath
        {
            get { return _bitmapFilePath; }
            set
            {
                if (!EqualityComparer<string>.Default.Equals(_bitmapFilePath, value))
                {
                    _bitmapFilePath = value;

                    // load this way so the file handle is not kept open
                    BitmapImage bi = new BitmapImage();
                    bi.BeginInit();
                    bi.UriSource = new Uri(_bitmapFilePath);
                    bi.CacheOption = BitmapCacheOption.OnLoad;
                    bi.EndInit();

                    _bitmap = bi;

                    _cropped = new CroppedBitmap(_bitmap, Crop);
                    OnPropertyChanged(nameof(BitmapFilePath));
                }
            }
        }

        public Int32Rect Crop
        {
            get { return _crop; }
            set
            {
                if (!_crop.Equals(value))
                {
                    _crop = value;
                    _cropped = new CroppedBitmap(_bitmap, value);
                    OnPropertyChanged(nameof(Crop));
                }
            }
        }

        public int FlipX
        {
            get => _scaleX;
            set
            {
                _scaleX = value;
                OnPropertyChanged(nameof(FlipX));
            }
        }

        public int FlipY
        {
            get => _scaleY;
            set
            {
                _scaleY = value;
                OnPropertyChanged(nameof(FlipY));
            }
        }

        public Size OriginalSize
        {
            get => _originalSize;
            set
            {
                _originalSize = value;
                OnPropertyChanged(nameof(OriginalSize));
            }
        }

        [XmlIgnore]
        public BitmapSource Bitmap => _bitmap;

        private BitmapSource _bitmap;
        private CroppedBitmap _cropped;
        private string _bitmapFilePath;
        private int _scaleX = 1;
        private int _scaleY = 1;
        private Int32Rect _crop;
        private Size _originalSize;

        protected GraphicImage()
        {
            Effect = null;
        }

        public GraphicImage(string imageFilePath, Rect displayRect, Int32Rect crop, double angle = 0, int flipX = 1, int flipY = 1)
            : this(imageFilePath, displayRect, crop, angle, flipX, flipY, displayRect.Size)
        {
            Effect = null;
        }

        protected GraphicImage(string imageFilePath, Rect displayRect, Int32Rect crop, double angle, int flipX, int flipY, Size originalSize)
            : base(Colors.Transparent, 0, displayRect)
        {
            _originalSize = originalSize;
            _crop = crop; // must set crop before bitmap due to property setters
            Effect = null;
            BitmapFilePath = imageFilePath;
            Angle = angle;
            Crop = crop;
            _scaleX = flipX;
            _scaleY = flipY;
        }

        internal override void DrawRectangle(DrawingContext drawingContext)
        {
            if (drawingContext == null)
                throw new ArgumentNullException(nameof(drawingContext));

            Rect r = UnrotatedBounds;

            var centerX = r.Left + (r.Width / 2);
            var centerY = r.Top + (r.Height / 2);

            // push current flip transform
            drawingContext.PushTransform(new ScaleTransform(_scaleX, _scaleY, centerX, centerY));

            // push any current/unrealized resizing/rendering transform
            if (Right <= Left || Bottom <= Top)
                drawingContext.PushTransform(new ScaleTransform(Right <= Left ? -1 : 1, Bottom <= Top ? -1 : 1, centerX, centerY));

            drawingContext.DrawImage(_cropped, r);

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

        public override GraphicBase Clone()
        {
            return new GraphicImage(BitmapFilePath, UnrotatedBounds, Crop, Angle, FlipX, FlipY, OriginalSize) { ObjectId = ObjectId };
        }
    }
}
