using System;
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
                _bitmapFilePath = value;
                _bitmap = new BitmapImage(new Uri(_bitmapFilePath));
                _cropped = new CroppedBitmap(_bitmap, Crop);
                OnPropertyChanged(nameof(BitmapFilePath));
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

        [XmlIgnore]
        public BitmapSource Bitmap => _bitmap;

        private BitmapSource _bitmap;
        private CroppedBitmap _cropped;
        private string _bitmapFilePath;
        private int _scaleX = 1;
        private int _scaleY = 1;
        private Int32Rect _crop;

        protected GraphicImage()
        {
            Effect = null;
        }

        public GraphicImage(string imageFilePath, Rect displayRect, Int32Rect crop, double angle = 0, int flipX = 1, int flipY = 1)
            : base(Colors.Transparent, 0, displayRect)
        {
            _crop = crop;
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

            var txt = new FormattedText(Bounds.ToString(), CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Arial"), 18, Brushes.Pink);
            drawingContext.DrawText(txt, r.TopLeft);

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
            return new GraphicImage(BitmapFilePath, UnrotatedBounds, Crop, Angle, FlipX, FlipY) { ObjectId = ObjectId };
        }
    }
}
