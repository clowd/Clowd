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
        //public bool IsCropping
        //{
        //    get => IsSelected && _cropping;
        //    set
        //    {
        //        _cropping = IsSelected && value;
        //        OnPropertyChanged(nameof(IsCropping));
        //    }
        //}

        public override bool IsSelected
        {
            get => base.IsSelected;
            set
            {
                //IsCropping = IsCropping && value;
                base.IsSelected = value;
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
                    encoder.Frames.Add(BitmapFrame.Create(GetFlattenedBitmap()));
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
                    _scaleX = _scaleY = 1;
                    //_cropL = _cropT = _cropR = _cropB = 0;
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
        //private int _cropL = 0;
        //private int _cropT = 0;
        //private int _cropR = 0;
        //private int _cropB = 0;
        //private int _cropShortEdge = 6;
        //private int _cropLongEdge = 20;
        //private bool _cropping = false;

        protected GraphicImage()
        {
            Effect = null;
        }
        public GraphicImage(DrawingCanvas canvas, Rect rect, BitmapSource bitmap, double angle = 0)
           : this(canvas.ObjectColor, canvas.LineWidth, rect, bitmap, angle)
        {
            Effect = null;
        }

        public GraphicImage(Color objectColor, double lineWidth, Rect rect, BitmapSource bitmap, double angle = 0)
            : base(objectColor, lineWidth, rect)
        {
            Effect = null;
            Angle = angle;
            _bitmap = bitmap;
        }

        internal override void DrawRectangle(DrawingContext drawingContext)
        {
            if (drawingContext == null)
                throw new ArgumentNullException(nameof(drawingContext));

            Rect r = UnrotatedBounds;
            //if (_bitmap.PixelWidth == (int)Math.Round(r.Width, 3) && _bitmap.PixelHeight == (int)Math.Round(r.Height, 3) && Angle == 0)
            //{
            //    // If the image is still at the original size and zero rotation, round the rectangle position to whole pixels to avoid blurring.
            //    r.X = ScreenTools.WpfSnapToPixels(r.X);
            //    r.Y = ScreenTools.WpfSnapToPixels(r.Y);
            //}

            var centerX = r.Left + (r.Width / 2);
            var centerY = r.Top + (r.Height / 2);

            // push current flip transform
            drawingContext.PushTransform(new ScaleTransform(_scaleX, _scaleY, centerX, centerY));

            // push any resizing/rendering transform (will be added to current transform later during normalization)
            if (Right <= Left || Bottom <= Top)
                drawingContext.PushTransform(new ScaleTransform(Right <= Left ? -1 : 1, Bottom <= Top ? -1 : 1, centerX, centerY));

            drawingContext.DrawImage(GetFlattenedBitmap(false), r);

            if (Right <= Left || Bottom <= Top)
                drawingContext.Pop();

            drawingContext.Pop();
        }

        internal override void DrawSingleTracker(DrawingContext drawingContext, int handleNumber)
        {
            //if (IsCropping)
            //{
            //    var rect = GetHandleRectangle(handleNumber);
            //    switch (handleNumber)
            //    {
            //        case 1: // top left
            //            drawingContext.DrawRectangle(HandleBrush, null, new Rect(rect.TopLeft, new Size(_cropShortEdge, _cropLongEdge)));
            //            drawingContext.DrawRectangle(HandleBrush, null, new Rect(rect.TopLeft, new Size(_cropLongEdge, _cropShortEdge)));
            //            break;
            //        case 3: // top right
            //            drawingContext.DrawRectangle(HandleBrush, null, new Rect(rect.Right - _cropShortEdge, rect.Top, _cropShortEdge, _cropLongEdge));
            //            drawingContext.DrawRectangle(HandleBrush, null, new Rect(rect.TopLeft, new Size(_cropLongEdge, _cropShortEdge)));
            //            break;
            //        case 5: // bottom right
            //            drawingContext.DrawRectangle(HandleBrush, null, new Rect(rect.Right - _cropShortEdge, rect.Top, _cropShortEdge, _cropLongEdge));
            //            drawingContext.DrawRectangle(HandleBrush, null, new Rect(rect.Left, rect.Bottom - _cropShortEdge, _cropLongEdge, _cropShortEdge));
            //            break;
            //        case 7: // bottom left
            //            drawingContext.DrawRectangle(HandleBrush, null, new Rect(rect.TopLeft, new Size(_cropShortEdge, _cropLongEdge)));
            //            drawingContext.DrawRectangle(HandleBrush, null, new Rect(rect.Left, rect.Bottom - _cropShortEdge, _cropLongEdge, _cropShortEdge));
            //            break;
            //        case 2: // top center
            //        case 4: // middle right
            //        case 6: // bottom center
            //        case 8: // middle left
            //            drawingContext.DrawRectangle(HandleBrush, null, rect);
            //            break;
            //        case 9:
            //            // do nothing
            //            break;
            //    }
            //}
            //else
            //{
            base.DrawSingleTracker(drawingContext, handleNumber);
            //}
        }

        protected override Rect GetHandleRectangle(int handleNumber)
        {
            //if (IsCropping)
            //{
            //    var xCenter = (Right + Left) / 2;
            //    var yCenter = (Bottom + Top) / 2;
            //    switch (handleNumber)
            //    {
            //        case 1: // top left
            //            return new Rect(Left, Top, _cropLongEdge, _cropLongEdge);
            //        case 2: // top center
            //            return new Rect(xCenter - (_cropLongEdge / 2), Top, _cropLongEdge, _cropShortEdge);
            //        case 3: // top right
            //            return new Rect(Right - _cropLongEdge, Top, _cropLongEdge, _cropLongEdge);
            //        case 4: // middle right
            //            return new Rect(Right - _cropShortEdge, yCenter - (_cropLongEdge / 2), _cropShortEdge, _cropLongEdge);
            //        case 5: // bottom right
            //            return new Rect(Right - _cropLongEdge, Bottom - _cropLongEdge, _cropLongEdge, _cropLongEdge);
            //        case 6: // bottom center
            //            return new Rect(xCenter - (_cropLongEdge / 2), Bottom - _cropShortEdge, _cropLongEdge, _cropShortEdge);
            //        case 7: // bottom left
            //            return new Rect(Left, Bottom - _cropLongEdge, _cropLongEdge, _cropLongEdge);
            //        case 8: // middle left
            //            return new Rect(Left, yCenter - (_cropLongEdge / 2), _cropShortEdge, _cropLongEdge);
            //    }
            //}

            return base.GetHandleRectangle(handleNumber);
        }

        internal override int HandleCount => base.HandleCount;// IsCropping ? 8 : base.HandleCount;

        internal override void MoveHandleTo(Point point, int handleNumber)
        {
            //if (IsCropping)
            //{
            //    var rPoint = UnapplyRotation(point);

            //    var objl = Left;
            //    var objt = Top;
            //    var objr = Right;
            //    var objb = Bottom;

            //    switch (handleNumber)
            //    {
            //        case 1: // top left
            //            objl = rPoint.X;
            //            objt = rPoint.Y;
            //            break;
            //        case 2: // top center
            //            objt = rPoint.Y;
            //            break;
            //        case 3: // top right
            //            objr = rPoint.X;
            //            objt = rPoint.Y;
            //            break;
            //        case 4: // middle right
            //            objr = rPoint.X;
            //            break;
            //        case 5:  // bottom right
            //            objr = rPoint.X;
            //            objb = rPoint.Y;
            //            break;
            //        case 6: // bottom center
            //            objb = rPoint.Y;
            //            break;
            //        case 7: // bottom left
            //            objl = rPoint.X;
            //            objb = rPoint.Y;
            //            break;
            //        case 8: // middle left
            //            objl = rPoint.X;
            //            break;
            //    }

            //    double minMargin = Math.Min(_cropLongEdge * 3, ScreenTools.ScreenToWpf(_bitmap.PixelWidth));

            //    objl = Math.Min(Right - minMargin, objl);
            //    objl = Math.Max(Left - ScreenTools.ScreenToWpf(_cropL), objl);
            //    _cropL += ScreenTools.WpfToScreen(objl - Left);
            //    Left = objl;

            //    objt = Math.Min(Bottom - minMargin, objt);
            //    objt = Math.Max(Top - ScreenTools.ScreenToWpf(_cropT), objt);
            //    _cropT += ScreenTools.WpfToScreen(objt - Top);
            //    Top = objt;

            //    objr = Math.Max(Left + minMargin, objr);
            //    objr = Math.Min(Right + ScreenTools.ScreenToWpf(_cropR), objr);
            //    _cropR += ScreenTools.WpfToScreen(Right - objr);
            //    Right = objr;

            //    objb = Math.Max(Top + minMargin, objb);
            //    objb = Math.Min(Bottom + ScreenTools.ScreenToWpf(_cropB), objb);
            //    _cropB += ScreenTools.WpfToScreen(Bottom - objb);
            //    Bottom = objb;
            //}
            //else
            //{
            base.MoveHandleTo(point, handleNumber);
            //}
        }

        internal bool Flatten()
        {
            BitmapSource = GetFlattenedBitmap();
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
            return new GraphicImage(ObjectColor, LineWidth, UnrotatedBounds, GetFlattenedBitmap(), Angle) { ObjectId = ObjectId };
        }

        private BitmapSource GetFlattenedBitmap(bool applyScaling = true)
        {
            var scaled = _scaleX != 1 || _scaleY != 1;
            //var cropped = _cropL != 0 || _cropT != 0 || _cropR != 0 || _cropB != 0;

            //var bitmap = cropped ? new CroppedBitmap(_bitmap, new Int32Rect(_cropL, _cropT, _bitmap.PixelWidth - _cropL - _cropR, _bitmap.PixelHeight - _cropT - _cropB)) : _bitmap;

            var bitmap = _bitmap;

            if (scaled && applyScaling)
            {
                var visual = new DrawingVisual();
                using (var ctx = visual.RenderOpen())
                {
                    ctx.PushTransform(new ScaleTransform(_scaleX, _scaleY, bitmap.Width / 2, bitmap.Height / 2));
                    ctx.DrawImage(bitmap, new Rect(0, 0, bitmap.Width, bitmap.Height));
                }

                var final = new RenderTargetBitmap(bitmap.PixelWidth, bitmap.PixelHeight, 96, 96, PixelFormats.Pbgra32);
                final.Render(visual);
                bitmap = final;
            }

            return bitmap;
        }
    }
}
