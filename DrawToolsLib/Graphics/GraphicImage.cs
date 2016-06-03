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
                    OnPropertyChanged(nameof(BitmapBytes));
                }
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

        internal override void Normalize()
        {
            if (Right <= Left)
                ScaleX = ScaleX / -1;
            if (Bottom <= Top)
                ScaleY = ScaleY / -1;

            base.Normalize();
        }

        public override GraphicBase Clone()
        {
            return new GraphicImage(ObjectColor, LineWidth, UnrotatedBounds, _bitmap, Angle) { ObjectId = ObjectId };
        }
    }
}
