using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DrawToolsLib
{
    public class GraphicsImage : GraphicsRectangleBase
    {
        public string FileName
        {
            get { return fileName; }
            set { fileName = value; }
        }

        private string fileName;
        private BitmapSource imageCache;
        public GraphicsImage(string filename, double left, double top, double right, double bottom,
            double lineWidth, Color objectColor, double actualScale)
        {
            this.rectangleLeft = left;
            this.rectangleTop = top;
            this.rectangleRight = right;
            this.rectangleBottom = bottom;
            this.graphicsLineWidth = lineWidth;
            this.graphicsObjectColor = objectColor;
            this.graphicsActualScale = actualScale;
            this.fileName = filename;

            if (!File.Exists(filename))
                throw new FileNotFoundException(filename);

            BitmapSource myImage = BitmapFrame.Create(
                new Uri(filename, UriKind.Absolute),
                BitmapCreateOptions.None,
                BitmapCacheOption.OnLoad);

            imageCache = myImage;

            //RefreshDrawng();
        }

        public override void Draw(DrawingContext drawingContext)
        {
            if (drawingContext == null)
            {
                throw new ArgumentNullException("drawingContext");
            }

            Rect r = Rectangle;
            if (imageCache.PixelWidth == Math.Round(r.Width, 3) && imageCache.PixelHeight == Math.Round(r.Height, 3))
            {
                // If the image is still at the original size, round the rectangle position to whole pixels to avoid blurring.
                r.X = Math.Round(r.X);
                r.Y = Math.Round(r.Y);
            }
            drawingContext.DrawRectangle(Brushes.Pink, new Pen(), r);
            drawingContext.DrawImage(imageCache, r);

            base.Draw(drawingContext);
        }

        public override bool Contains(Point point)
        {
            return this.Rectangle.Contains(point);
        }

        public override PropertiesGraphicsBase CreateSerializedObject()
        {
            return new PropertiesGraphicsImage(this);
        }
    }
}
