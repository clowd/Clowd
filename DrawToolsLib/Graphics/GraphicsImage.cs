using System;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Serialization;

namespace DrawToolsLib.Graphics
{
    [Serializable]
    public class GraphicsImage : GraphicsRectangle
    {
        public string FileName
        {
            get { return _fileName; }
            set
            {
                if (value == _fileName) return;
                _fileName = value;
                OnPropertyChanged();
            }
        }

        private string _fileName;

        [XmlIgnore]
        private BitmapSource _imageBacking;
        [XmlIgnore]
        private BitmapSource _imageCache
        {
            get
            {
                if(_imageBacking == null)
                    _imageBacking = BitmapFrame.Create(
                        new Uri(_fileName, UriKind.Absolute),
                        BitmapCreateOptions.None,
                        BitmapCacheOption.OnLoad);
                return _imageBacking;
            }
        }

        protected GraphicsImage()
        {
        }
        public GraphicsImage(DrawingCanvas canvas, Rect rect, string filePath)
           : this(canvas.ActualScale, canvas.ObjectColor, canvas.LineWidth, rect, filePath)
        {
        }

        public GraphicsImage(double scale, Color objectColor, double lineWidth, Rect rect, string filePath)
            : base(scale, objectColor, lineWidth, rect)
        {
            _fileName = filePath;
            if (!File.Exists(_fileName))
                throw new FileNotFoundException(_fileName);
        }

        internal override void DrawRectangle(DrawingContext drawingContext)
        {
            if (drawingContext == null)
                throw new ArgumentNullException(nameof(drawingContext));

            Rect r = Bounds;
            if (_imageCache.PixelWidth == (int)Math.Round(r.Width, 3) && _imageCache.PixelHeight == (int)Math.Round(r.Height, 3))
            {
                // If the image is still at the original size, round the rectangle position to whole pixels to avoid blurring.
                r.X = Math.Round(r.X);
                r.Y = Math.Round(r.Y);
            }
            drawingContext.DrawImage(_imageCache, Bounds);
        }

        public override GraphicsBase Clone()
        {
            return new GraphicsImage(ActualScale, ObjectColor, LineWidth, Bounds, FileName) { ObjectId = ObjectId };
        }
    }
}
