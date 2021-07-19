using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Serialization;

namespace DrawToolsLib.Graphics
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
                //_cropped.Source = _bitmap;
                OnPropertyChanged(nameof(BitmapFilePath));
            }
        }

        public Int32Rect Crop
        {
            get;set;
            //get { return _cropped.SourceRect; }
            //set
            //{
            //    _cropped.SourceRect = value;
            //    OnPropertyChanged(nameof(Crop));
            //}
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
        //private CroppedBitmap _cropped;
        private string _bitmapFilePath;
        private int _scaleX = 1;
        private int _scaleY = 1;

        protected GraphicImage()
        {
            Effect = null;
            //_cropped = new CroppedBitmap();
        }

        public GraphicImage(string imageFilePath, Rect displayRect, Int32Rect crop, double angle = 0, int flipX = 1, int flipY = 1)
            : base(Colors.Transparent, 0, displayRect)
        {
            Effect = null;
            //_cropped = new CroppedBitmap();
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

            drawingContext.DrawImage(_bitmap, r);

            if (Right <= Left || Bottom <= Top)
                drawingContext.Pop();

            drawingContext.Pop();
        }

        internal override void Normalize()
        {
            if (Right <= Left)  _scaleX /= -1;
            if (Bottom <= Top)  _scaleY /= -1;
            base.Normalize();
        }

        public override GraphicBase Clone()
        {
            return new GraphicImage(BitmapFilePath, UnrotatedBounds, Crop, Angle, FlipX, FlipY) { ObjectId = ObjectId };
        }
    }
}
