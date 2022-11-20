using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RT.Serialization;

namespace Clowd.Drawing.Graphics
{
    [GraphicDesc("Image", Skills = Skill.Angle | Skill.Crop | Skill.Cursor)]
    public class GraphicImage : GraphicRectangle
    {
        public bool Editing => !_editingAnchor.IsEmpty && _editingAnchor.Width > 0 && _editingAnchor.Height > 0;

        public bool HasCursor => !String.IsNullOrWhiteSpace(_cursorFilePath) && File.Exists(_cursorFilePath);

        public int BitmapPixelWidth
        {
            get
            {
                if (_imageSource == null) UpdateImageCache();
                return _imageSource.PixelWidth;
            }
        }

        public int BitmapPixelHeight
        {
            get
            {
                if (_imageSource == null) UpdateImageCache();
                return _imageSource.PixelHeight;
            }
        }

        public string BitmapFilePath
        {
            get => _bitmapFilePath;
            set
            {
                _imageObscured = null;
                _imageSource = null;
                Set(ref _bitmapFilePath, value);
            }
        }

        public string CursorFilePath
        {
            get => _cursorFilePath;
            set
            {
                _imageObscured = null;
                _imageSource = null;
                Set(ref _cursorFilePath, value);
            }
        }

        public Int32Rect CursorPosition
        {
            get => _cursorPosition;
            set
            {
                _imageObscured = null;
                _imageSource = null;
                Set(ref _cursorPosition, value);
            }
        }

        public bool CursorVisible
        {
            get => _cursorVisible;
            set
            {
                _imageObscured = null;
                _imageSource = null;
                Set(ref _cursorVisible, value);
            }
        }

        public override bool IsSelected
        {
            get => base.IsSelected;
            set
            {
                base.IsSelected = value;
                if (!value) EndCrop();
            }
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

        public ObscuredShape[] ObscuredShapes
        {
            get => _obscuredShapes;
            set
            {
                _imageObscured = null;
                Set(ref _obscuredShapes, value);
            }
        }

        public record struct ObscuredShape(Point P0, Point P1, Point P2, Point P3, double BlurRadius);

        private string _cursorFilePath;
        private Int32Rect _cursorPosition;
        private bool _cursorVisible;
        private string _bitmapFilePath;
        private int _scaleX = 1;
        private int _scaleY = 1;
        private Int32Rect _crop;
        private Size _originalSize;
        private ObscuredShape[] _obscuredShapes = new ObscuredShape[0];
        [ClassifyIgnore] private BitmapSource _imageSource;
        [ClassifyIgnore] private BitmapSource _imageObscured;
        [ClassifyIgnore] private Rect _editingAnchor;
        [ClassifyIgnore] private DrawingCanvas _editingCanvas;

        protected GraphicImage()
        { }

        public GraphicImage(string imageFilePath, Size imageSize)
            : this(imageFilePath, new Rect(new Point(0, 0), imageSize), new Int32Rect())
        { }

        public GraphicImage(string imageFilePath, Rect displayRect, Int32Rect crop, double angle = 0, int flipX = 1, int flipY = 1,
            string cursorFilePath = default, Int32Rect cursorPosition = default, bool cursorVisible = false)
            : this(imageFilePath, displayRect, crop, angle, flipX, flipY, displayRect.Size, cursorFilePath, cursorPosition, cursorVisible)
        { }

        protected GraphicImage(string imageFilePath, Rect displayRect, Int32Rect crop, double angle, int flipX, int flipY, Size originalSize,
            string cursorFilePath, Int32Rect cursorPosition, bool cursorVisible)
            : base(Colors.Transparent, 0, displayRect, angle, false)
        {
            _bitmapFilePath = imageFilePath;
            _cursorFilePath = cursorFilePath;
            _cursorPosition = cursorPosition;
            _cursorVisible = cursorVisible;
            _originalSize = originalSize;
            _crop = crop;
            _scaleX = flipX;
            _scaleY = flipY;
        }

        internal override void Draw(DrawingContext ctx, DpiScale uiscale)
        {
            if (_imageSource == null) UpdateImageCache();

            if (Editing)
            {
                DrawTransformed(ctx, () =>
                {
                    ctx.PushOpacity(0.5);
                    ctx.DrawImage(_imageSource, _editingAnchor);
                    ctx.Pop();
                    ctx.PushClip(new RectangleGeometry(UnrotatedBounds));
                    ctx.DrawImage(_imageSource, _editingAnchor);
                    ctx.Pop();
                    DrawTrackers(ctx, uiscale);
                });
            }
            else
            {
                base.Draw(ctx, uiscale);
            }
        }

        internal override void DrawObject(DrawingContext drawingContext)
        {
            if (_imageSource == null) UpdateImageCache();

            DrawTransformed(drawingContext, () =>
            {
                Rect r = UnrotatedBounds;
                drawingContext.DrawImage(new CroppedBitmap(_imageSource, Crop), r);
                if (_imageObscured != null || UpdateObscureCache())
                {
                    drawingContext.DrawImage(new CroppedBitmap(_imageObscured, Crop), r);
                }
            });
        }

        private void DrawTransformed(DrawingContext ctx, Action fn)
        {
            var centerPt = CenterOfRotation;

            // rotate
            ctx.PushTransform(new RotateTransform(Angle, centerPt.X, centerPt.Y));

            // push current flip transform
            ctx.PushTransform(new ScaleTransform(_scaleX, _scaleY, centerPt.X, centerPt.Y));

            // push any current/unrealized resizing/rendering transform
            if (Right <= Left || Bottom <= Top)
                ctx.PushTransform(new ScaleTransform(Right <= Left ? -1 : 1, Bottom <= Top ? -1 : 1, centerPt.X, centerPt.Y));

            fn();

            if (Right <= Left || Bottom <= Top)
                ctx.Pop();

            ctx.Pop();

            // this function leaves the rotate transform on the stack for resize handles
            // so it should only be called once per draw
        }

        protected override Rect GetHandleRectangle(int handleNumber, DpiScale uiscale)
        {
            if (!Editing) return base.GetHandleRectangle(handleNumber, uiscale);

            double longEdge = 30 * uiscale.DpiScaleX;
            double longEdgeHalf = longEdge / 2;
            double shortEdge = 6 * uiscale.DpiScaleX;
            var pt = GetHandle(handleNumber, uiscale);

            return handleNumber switch
            {
                1 => new Rect(pt, new Point(pt.X + longEdge, pt.Y + longEdge)),
                2 => new Rect(pt.X - longEdgeHalf, pt.Y, longEdge, shortEdge),
                3 => new Rect(pt.X - longEdge, pt.Y, longEdge, longEdge),
                4 => new Rect(pt.X - shortEdge, pt.Y - longEdgeHalf, shortEdge, longEdge),
                5 => new Rect(new Point(pt.X - longEdge, pt.Y - longEdge), pt),
                6 => new Rect(pt.X - longEdgeHalf, pt.Y - shortEdge, longEdge, shortEdge),
                7 => new Rect(pt.X, pt.Y - longEdge, longEdge, longEdge),
                8 => new Rect(pt.X, pt.Y - longEdgeHalf, shortEdge, longEdge),
                9 => new Rect(0, 0, 0, 0),
                _ => base.GetHandleRectangle(handleNumber, uiscale),
            };
        }

        protected override void DrawSingleTracker(DrawingContext drawingContext, int handleNum, DpiScale uiscale)
        {
            if (!Editing)
            {
                base.DrawSingleTracker(drawingContext, handleNum, uiscale);
                return;
            }

            double edge = 6 * uiscale.DpiScaleX;
            double buffer = 2 * uiscale.DpiScaleX;

            var o = UnrotatedBounds;
            var r = o;

            r.Inflate(-edge, -edge);
            drawingContext.PushClip(new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(o), new RectangleGeometry(r)));
            drawingContext.DrawRectangle(Brushes.White, null, GetHandleRectangle(handleNum, uiscale));
            drawingContext.Pop();

            r.Inflate(buffer, buffer);
            drawingContext.PushClip(new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(o), new RectangleGeometry(r)));
            drawingContext.DrawRectangle(HandleBrush, null, GetHandleRectangle(handleNum, uiscale));
            drawingContext.Pop();
        }

        internal override void MoveHandleTo(Point point, int handleNumber)
        {
            base.MoveHandleTo(point, handleNumber);

            if (Editing)
            {
                Left = Math.Max(Left, _editingAnchor.Left);
                Top = Math.Max(Top, _editingAnchor.Top);
                Right = Math.Min(Right, _editingAnchor.Right);
                Bottom = Math.Min(Bottom, _editingAnchor.Bottom);
            }
        }

        internal override void Move(double deltaX, double deltaY)
        {
            if (Editing)
            {
                Matrix mx = Matrix.Identity;
                mx.Rotate(-Angle);
                var vector = mx.Transform(new Vector(deltaX, deltaY));

                var centerPt = CenterOfRotation;
                base.Move(vector.X, vector.Y);

                Left = Math.Max(Left, _editingAnchor.Left);
                Top = Math.Max(Top, _editingAnchor.Top);
                Right = Math.Min(Right, _editingAnchor.Right);
                Bottom = Math.Min(Bottom, _editingAnchor.Bottom);
                CenterOfRotation = centerPt;
            }
            else
            {
                base.Move(deltaX, deltaY);
            }
        }

        internal override void Activate(DrawingCanvas canvas)
        {
            if (Editing)
            {
                EndCrop();
                return;
            }

            _editingAnchor = GetExtendedImageRect();
            _editingCanvas = canvas;

            OnPropertyChanged(nameof(Editing));
        }

        private void EndCrop()
        {
            if (!Editing) return;

            var renderW = Right - Left;
            var renderH = Bottom - Top;
            var scaleX = BitmapPixelWidth / _editingAnchor.Width;
            var scaleY = BitmapPixelHeight / _editingAnchor.Height;

            var x = (Left - _editingAnchor.Left) * scaleX;
            var y = (Top - _editingAnchor.Top) * scaleY;
            var w = renderW * scaleX;
            var h = renderH * scaleY;

            Crop = new Int32Rect((int)x, (int)y, (int)w, (int)h);

            _editingCanvas?.AddCommandToHistory(false);
            _editingAnchor = Rect.Empty;
            _editingCanvas = null;

            OnPropertyChanged(nameof(Editing));
        }

        internal override void Normalize()
        {
            if (Editing) return;
            if (Right <= Left) _scaleX /= -1;
            if (Bottom <= Top) _scaleY /= -1;
            base.Normalize();
        }

        internal void AddObscuredArea(Rect rect, double blurRadius)
        {
            var pts = new Point[] { rect.TopLeft, rect.TopRight, rect.BottomRight, rect.BottomLeft }.Select(UnapplyRotation).ToArray();
            if (!pts.Any(UnrotatedBounds.Contains))
                return;

            pts = pts.Select(TranslateUnrotatedPointToImageSpace).ToArray();
            ObscuredShapes = ObscuredShapes.Append(new ObscuredShape(pts[0], pts[1], pts[2], pts[3], blurRadius)).ToArray();
        }

        private Rect GetExtendedImageRect()
        {
            if (Crop.IsEmpty)
                return UnrotatedBounds;

            var renderW = Right - Left;
            var renderH = Bottom - Top;
            var scaleX = Math.Abs(renderW / Crop.Width);
            var scaleY = Math.Abs(renderH / Crop.Height);
            return new Rect(Left - (Crop.X * scaleX), Top - (Crop.Y * scaleY), BitmapPixelWidth * scaleX, BitmapPixelHeight * scaleY);
        }

        private Point TranslateUnrotatedPointToImageSpace(Point p)
        {
            var x = p.X;
            var y = p.Y;

            var renderW = Right - Left;
            var renderH = Bottom - Top;
            var cropW = Crop.IsEmpty ? BitmapPixelWidth : Crop.Width;
            var cropH = Crop.IsEmpty ? BitmapPixelHeight : Crop.Height;
            var offsetX = Crop.IsEmpty ? 0 : Crop.X;
            var offsetY = Crop.IsEmpty ? 0 : Crop.Y;

            x -= Left;
            x /= renderW;
            x *= cropW;
            if (FlipX < 0) x = cropW - x;
            x += offsetX;

            y -= Top;
            y /= renderH;
            y *= cropH;
            if (FlipY < 0) y = cropH - y;
            y += offsetY;

            return new Point(x, y);
        }

        private Geometry ShapeToGeometry(ObscuredShape obr)
        {
            StreamGeometry geo = new StreamGeometry();
            using (var ctx = geo.Open())
            {
                ctx.BeginFigure(obr.P0, true, true);
                ctx.LineTo(obr.P1, false, false);
                ctx.LineTo(obr.P2, false, false);
                ctx.LineTo(obr.P3, false, false);
            }
            return geo;
        }

        private void UpdateImageCache()
        {
            using var bifs = File.OpenRead(_bitmapFilePath);
            var bi = BitmapFactory.FromStream(bifs);

            if (HasCursor && CursorVisible)
            {
                using var curfs = File.OpenRead(_cursorFilePath);
                var wcursor = BitmapFactory.FromStream(curfs);
                var r = new Rect(_cursorPosition.X, _cursorPosition.Y, _cursorPosition.Width, _cursorPosition.Height);
                bi.Blit(r, wcursor, new Rect(0, 0, wcursor.PixelWidth, wcursor.PixelHeight));
            }

            bi.Freeze();
            _imageSource = bi;
        }

        private bool UpdateObscureCache()
        {
            if (_imageSource == null) UpdateImageCache();

            if (_obscuredShapes?.Any() != true)
            {
                _imageObscured = null;
                return false;
            }

            double blurScale = 0;
            TransformedBitmap blurCache = null;

            var drawing = new NearestNeighborDrawingVisual();
            using (var ctx = drawing.RenderOpen())
            {
                foreach (var o in _obscuredShapes)
                {
                    var sc = o.BlurRadius > 0 ? 1 / o.BlurRadius : 0.125;

                    if (sc != blurScale)
                    {
                        blurScale = sc;
                        blurCache = new TransformedBitmap(_imageSource, new ScaleTransform(sc, sc));
                    }

                    ctx.PushClip(ShapeToGeometry(o));
                    ctx.DrawImage(blurCache, new Rect(0, 0, _imageSource.PixelWidth, _imageSource.PixelHeight));
                    ctx.Pop();
                }
            }

            var obscured = new RenderTargetBitmap(_imageSource.PixelWidth, _imageSource.PixelHeight, 96, 96, PixelFormats.Pbgra32);
            obscured.Render(drawing);
            obscured.Freeze();
            _imageObscured = obscured;
            return true;
        }

        private class NearestNeighborDrawingVisual : DrawingVisual
        {
            public NearestNeighborDrawingVisual()
            {
                VisualBitmapScalingMode = BitmapScalingMode.NearestNeighbor;
            }
        }
    }
}
