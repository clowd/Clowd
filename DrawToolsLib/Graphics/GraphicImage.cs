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
        public int ScaleX
        {
            get { return _scaleX; }
            set
            {
                if (value == _scaleX) return;
                _scaleX = value;
                OnPropertyChanged(nameof(ScaleX));
            }
        }

        public int ScaleY
        {
            get { return _scaleY; }
            set
            {
                if (value == _scaleY) return;
                _scaleY = value;
                OnPropertyChanged(nameof(ScaleY));
            }
        }

        /// <summary>
        /// This is used for serialization purposes only.
        /// </summary>
        public byte[] BitmapBytes
        {
            get
            {
                using (var stream = new MemoryStream())
                {
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(_bitmap));
                    encoder.Save(stream);
                    return stream.ToArray();
                }
            }
            set
            {
                using (var stream = new MemoryStream(value))
                {
                    stream.Position = 0;
                    BitmapImage image = new BitmapImage();
                    image.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
                    image.BeginInit();
                    image.StreamSource = stream;
                    image.CacheOption = BitmapCacheOption.OnLoad;
                    image.EndInit();
                    image.Freeze();
                    _bitmap = image;
                    OnPropertyChanged(nameof(BitmapSource));
                }
            }
        }

        [XmlIgnore]
        public BitmapSource BitmapSource
        {
            get { return _bitmap; }
            set
            {
                value.Freeze();
                _bitmap = value;
                OnPropertyChanged(nameof(BitmapSource));
            }
        }

        private BitmapSource _bitmap;
        private int _scaleX = 1;
        private int _scaleY = 1;

        protected GraphicImage()
        {
            Effect = null;
        }
        public GraphicImage(DrawingCanvas canvas, Rect rect, BitmapSource bitmap, double angle = 0)
           : this(canvas.ObjectColor, canvas.LineWidth, rect, bitmap, angle)
        {
        }

        public GraphicImage(Color objectColor, double lineWidth, Rect rect, BitmapSource bitmap, double angle = 0)
            : base(objectColor, lineWidth, rect)
        {
            Angle = angle;
            _bitmap = bitmap;
        }


        internal override void DrawRectangle(DrawingContext drawingContext)
        {
            if (drawingContext == null)
                throw new ArgumentNullException(nameof(drawingContext));

            Rect r = UnrotatedBounds;
            if (_bitmap.PixelWidth == (int)Math.Round(r.Width, 3) && _bitmap.PixelHeight == (int)Math.Round(r.Height, 3) && Angle == 0)
            {
                // If the image is still at the original size and zero rotation, round the rectangle position to whole pixels to avoid blurring.
                r.X = Math.Round(r.X);
                r.Y = Math.Round(r.Y);
            }

            var centerX = r.Left + (r.Width / 2);
            var centerY = r.Top + (r.Height / 2);

            // push current flip transform
            drawingContext.PushTransform(new ScaleTransform(ScaleX, ScaleY, centerX, centerY));

            // push any resizing/rendering transform (will be added to current transform later)
            if (Right <= Left)
                drawingContext.PushTransform(new ScaleTransform(-1, 1, centerX, centerY));
            if (Bottom <= Top)
                drawingContext.PushTransform(new ScaleTransform(1, -1, centerX, centerY));

            drawingContext.DrawImage(_bitmap, r);

            if (Right <= Left || Bottom <= Top)
                drawingContext.Pop();

            drawingContext.Pop();
        }

        internal bool Flatten()
        {
            if (ScaleX == 1 && ScaleY == 1)
                return false;

            var visual = new DrawingVisual();
            using (var ctx = visual.RenderOpen())
            {
                ctx.PushTransform(new ScaleTransform(ScaleX, ScaleY, BitmapSource.Width / 2, BitmapSource.Height / 2));
                ctx.DrawImage(BitmapSource, new Rect(0, 0, BitmapSource.Width, BitmapSource.Height));
            }

            var final = new RenderTargetBitmap(BitmapSource.PixelWidth, BitmapSource.PixelHeight, 96, 96, PixelFormats.Pbgra32);
            final.Render(visual);
            _bitmap = final;
            _scaleX = 1;
            _scaleY = 1;
            OnPropertyChanged(nameof(BitmapSource));
            return true;
        }

        internal override void Normalize()
        {
            if (Right <= Left)
                _scaleX = _scaleX / -1;
            if (Bottom <= Top)
                _scaleY = _scaleY / -1;

            base.Normalize();
        }

        public override GraphicBase Clone()
        {
            return new GraphicImage(ObjectColor, LineWidth, UnrotatedBounds, _bitmap, Angle) { ObjectId = ObjectId };
        }
    }
}
