using System;
using System.Windows;
using System.Windows.Media;

namespace Clowd.Utilities
{
    public static class DpiScale
    {
        public static double DpiX { get; private set; }
        public static double DpiY { get; private set; }

        private static Point _scaleUI;

        public static Transform DownScaleTransform
        {
            get
            {
                return new ScaleTransform(1 / DpiScale._scaleUI.X, 1 / DpiScale._scaleUI.Y);
            }
        }
        public static Transform UpScaleTransform
        {
            get
            {
                return new ScaleTransform(DpiScale._scaleUI.X, DpiScale._scaleUI.Y);
            }
        }

        public static Point DownScalePoint(Point point)
        {
            return new Point(point.X / DpiScale._scaleUI.X, point.Y / DpiScale._scaleUI.Y);
        }
        public static Rect DownScaleRect(Rect rect)
        {
            Rect rect1 = new Rect(rect.Left, rect.Top, rect.Width / DpiScale._scaleUI.X, rect.Height / DpiScale._scaleUI.Y);
            return rect1;
        }

        public static System.Drawing.Rectangle DownScaleRectangle(System.Drawing.Rectangle rect)
        {
            var w = (int)Math.Round((double)rect.Width / DpiScale._scaleUI.X);
            var h = (int)Math.Round((double)rect.Height / DpiScale._scaleUI.Y);

            return new System.Drawing.Rectangle(rect.X, rect.Y, w, h);
        }

        public static double DownScaleX(double x)
        {
            return x / DpiScale._scaleUI.X;
        }

        public static double DownScaleY(double y)
        {
            return y / DpiScale._scaleUI.Y;
        }

        public static void ScaleUISetup(double logPixelsX, double logPixelsY)
        {
            DpiX = logPixelsX;
            DpiY = logPixelsY;
            DpiScale._scaleUI.X = logPixelsX / 96;
            DpiScale._scaleUI.Y = logPixelsY / 96;
        }

        public static Rect TranslateDownScaleRect(Rect rect)
        {
            Rect rect1 = new Rect(rect.X / DpiScale._scaleUI.X, rect.Y / DpiScale._scaleUI.Y, rect.Width / DpiScale._scaleUI.X, rect.Height / DpiScale._scaleUI.Y);
            return rect1;
        }

        public static System.Drawing.Rectangle TranslateDownScaleRectangle(System.Drawing.Rectangle rect)
        {
            var rectangle = new System.Drawing.Rectangle((int)Math.Round((double)rect.X / DpiScale._scaleUI.X), (int)Math.Round((double)rect.Y / DpiScale._scaleUI.Y), (int)Math.Round((double)rect.Width / DpiScale._scaleUI.X), (int)Math.Round((double)rect.Height / DpiScale._scaleUI.Y));
            return rectangle;
        }
        public static System.Drawing.Rectangle TranslateUpScaleRectangle(System.Drawing.Rectangle rect)
        {
            var rectangle = new System.Drawing.Rectangle((int)Math.Round((double)rect.X * DpiScale._scaleUI.X), (int)Math.Round((double)rect.Y * DpiScale._scaleUI.Y), (int)Math.Round((double)rect.Width * DpiScale._scaleUI.X), (int)Math.Round((double)rect.Height * DpiScale._scaleUI.Y));
            return rectangle;
        }

        public static Rect TranslateUpScaleRect(Rect rect)
        {
            Rect rect1 = new Rect(
                rect.Left * DpiScale._scaleUI.X,
                rect.Top * DpiScale._scaleUI.Y, 
                rect.Width * DpiScale._scaleUI.X, 
                rect.Height * DpiScale._scaleUI.Y);
            return rect1;
        }

        public static Point UpScalePoint(Point point)
        {
            Point point1 = new Point(point.X * DpiScale._scaleUI.X, point.Y * DpiScale._scaleUI.Y);
            return point1;
        }

        public static Rect UpScaleRect(Rect rect)
        {
            Rect rect1 = new Rect(rect.Left, rect.Top, rect.Width * DpiScale._scaleUI.X, rect.Height * DpiScale._scaleUI.Y);
            return rect1;
        }

        public static Size UpScaleSize(Size size)
        {
            Size size1 = new Size(size.Width * DpiScale._scaleUI.X, size.Height * DpiScale._scaleUI.Y);
            return size1;
        }

        public static double UpScaleX(double x)
        {
            return x * DpiScale._scaleUI.X;
        }

        public static double UpScaleY(double y)
        {
            return y * DpiScale._scaleUI.Y;
        }
    }
}