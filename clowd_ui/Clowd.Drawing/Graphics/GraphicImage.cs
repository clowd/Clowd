using System;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using RT.Serialization;

namespace Clowd.Drawing.Graphics
{
    [GraphicDesc("Image", Skills = Skill.Angle | Skill.Crop | Skill.Cursor)]
    public class GraphicImage : GraphicRectangle
    {
        public bool Editing => !_editingAnchor.IsEmptyRect();

        public bool HasCursor => !String.IsNullOrWhiteSpace(_cursorFilePath) && File.Exists(_cursorFilePath);

        public int BitmapPixelWidth
        {
            get
            {
                if (_imageSource == null) UpdateImageCache();
                return _imageSource.PixelSize.Width;
            }
        }

        public int BitmapPixelHeight
        {
            get
            {
                if (_imageSource == null) UpdateImageCache();
                return _imageSource.PixelSize.Height;
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

        public PixelRect CursorPosition
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

        public PixelRect Crop
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
        private PixelRect _cursorPosition;
        private bool _cursorVisible;
        private string _bitmapFilePath;
        private int _scaleX = 1;
        private int _scaleY = 1;
        private PixelRect _crop;
        private Size _originalSize;
        private ObscuredShape[] _obscuredShapes = new ObscuredShape[0];
        [ClassifyIgnore] private Bitmap _imageSource;
        [ClassifyIgnore] private Bitmap _imageObscured;
        [ClassifyIgnore] private Rect _editingAnchor;
        [ClassifyIgnore] private DrawingCanvas _editingCanvas;

        protected GraphicImage()
        { }

        public GraphicImage(string imageFilePath, Size imageSize)
            : this(imageFilePath, new Rect(new Point(0, 0), imageSize), new PixelRect())
        { }

        public GraphicImage(string imageFilePath, Rect displayRect, PixelRect crop, double angle = 0, int flipX = 1, int flipY = 1,
                            string cursorFilePath = default, PixelRect cursorPosition = default, bool cursorVisible = false)
            : this(imageFilePath, displayRect, crop, angle, flipX, flipY, displayRect.Size, cursorFilePath, cursorPosition, cursorVisible)
        { }

        protected GraphicImage(string imageFilePath, Rect displayRect, PixelRect crop, double angle, int flipX, int flipY,
                               Size originalSize, string cursorFilePath, PixelRect cursorPosition, bool cursorVisible)
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
                DrawTransformed(
                    ctx,
                    c =>
                    {
                        using (c.PushOpacity(0.5))
                            c.DrawImage(_imageSource, _editingAnchor);
                        using (c.PushClip(UnrotatedBounds))
                            c.DrawImage(_imageSource, _editingAnchor);
                    },
                    c => DrawTrackers(c, uiscale));
            }
            else
            {
                Action<DrawingContext> trackers = IsSelected ? (c => DrawTrackers(c, uiscale)) : null;
                DrawTransformed(ctx, DrawImageBody, trackers);
            }
        }

        internal override void DrawObject(DrawingContext drawingContext)
        {
            if (_imageSource == null) UpdateImageCache();

            DrawTransformed(drawingContext, DrawImageBody);
        }

        private void DrawImageBody(DrawingContext ctx)
        {
            // decision #21: CroppedBitmap → DrawImage with the crop as the source rect.
            Rect r = UnrotatedBounds;
            Rect src = Crop.IsEmptyRect() ? new Rect(_imageSource.Size) : Crop.ToRect();
            ctx.DrawImage(_imageSource, src, r);
            if (_imageObscured != null || UpdateObscureCache())
            {
                ctx.DrawImage(_imageObscured, src, r);
            }
        }

        // Transform-scoping rule (§2.1): order rotate → flip → temp-mirror; body runs inside all three,
        // trackers run inside the rotation scope only. All pushes are using-scoped (decision #27).
        private void DrawTransformed(DrawingContext ctx, Action<DrawingContext> body, Action<DrawingContext> trackers = null)
        {
            var centerPt = CenterOfRotation;

            using (ctx.PushTransform(MatrixHelper.Rotation(Angle, centerPt)))
            {
                using (ctx.PushTransform(MatrixHelper.ScaleAt(_scaleX, _scaleY, centerPt)))
                {
                    // push any current/unrealized resizing/rendering transform
                    if (Right <= Left || Bottom <= Top)
                    {
                        using (ctx.PushTransform(MatrixHelper.ScaleAt(Right <= Left ? -1 : 1, Bottom <= Top ? -1 : 1, centerPt)))
                            body(ctx);
                    }
                    else
                    {
                        body(ctx);
                    }
                }

                trackers?.Invoke(ctx);
            }
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

            // crop brackets: 30 (long edge) / 6 (short edge) / 2 (buffer) × uiscale
            double edge = 6 * uiscale.DpiScaleX;
            double buffer = 2 * uiscale.DpiScaleX;

            var o = UnrotatedBounds;
            var r = o.Inflate(-edge);

            using (drawingContext.PushGeometryClip(
                       new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(o), new RectangleGeometry(r))))
                drawingContext.DrawRectangle(Brushes.White, null, GetHandleRectangle(handleNum, uiscale));

            r = r.Inflate(buffer);
            using (drawingContext.PushGeometryClip(
                       new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(o), new RectangleGeometry(r))))
                drawingContext.DrawRectangle(HandleBrush, null, GetHandleRectangle(handleNum, uiscale));
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
                // decision #29: Matrix.Rotate + Transform(Vector) → transform by Matrix.CreateRotation.
                // (Avalonia has no Vector*Matrix operator; Point*Matrix is identical here since a pure
                // rotation matrix carries no translation.)
                var vector = new Point(deltaX, deltaY) * Matrix.CreateRotation(Matrix.ToRadians(-Angle));

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

            Crop = new PixelRect((int)x, (int)y, (int)w, (int)h);

            _editingCanvas?.AddCommandToHistory(false);
            _editingAnchor = default;
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
            if (!pts.Any(p => UnrotatedBounds.Contains(p)))
                return;

            pts = pts.Select(TranslateUnrotatedPointToImageSpace).ToArray();
            ObscuredShapes = ObscuredShapes.Append(new ObscuredShape(pts[0], pts[1], pts[2], pts[3], blurRadius)).ToArray();
        }

        private Rect GetExtendedImageRect()
        {
            if (Crop.IsEmptyRect())
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
            var cropW = Crop.IsEmptyRect() ? BitmapPixelWidth : Crop.Width;
            var cropH = Crop.IsEmptyRect() ? BitmapPixelHeight : Crop.Height;
            var offsetX = Crop.IsEmptyRect() ? 0 : Crop.X;
            var offsetY = Crop.IsEmptyRect() ? 0 : Crop.Y;

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
                ctx.BeginFigure(obr.P0, true);
                ctx.LineTo(obr.P1);
                ctx.LineTo(obr.P2);
                ctx.LineTo(obr.P3);
                ctx.EndFigure(true);
            }

            return geo;
        }

        private void UpdateImageCache()
        {
            // decision #22: BitmapFactory.FromStream + Blit → new Bitmap(stream), composited into a
            // RenderTargetBitmap at image pixel size / 96 DPI (image first, then cursor rect).
            // decision #21 note: bitmaps are normalized to 96 DPI on load so pixel rects == DIP rects.
            Bitmap bi;
            using (var bifs = File.OpenRead(_bitmapFilePath))
                bi = new Bitmap(bifs);

            bool compositeCursor = HasCursor && CursorVisible;
            bool normalizeDpi = Math.Abs(bi.Dpi.X - 96) > 0.01 || Math.Abs(bi.Dpi.Y - 96) > 0.01;

            if (compositeCursor || normalizeDpi)
            {
                var rtb = new RenderTargetBitmap(bi.PixelSize, new Vector(96, 96));
                using (var ctx = rtb.CreateDrawingContext())
                {
                    ctx.DrawImage(bi, new Rect(0, 0, bi.PixelSize.Width, bi.PixelSize.Height));

                    if (compositeCursor)
                    {
                        using var curfs = File.OpenRead(_cursorFilePath);
                        var wcursor = new Bitmap(curfs);
                        ctx.DrawImage(wcursor, new Rect(wcursor.Size), _cursorPosition.ToRect());
                    }
                }

                _imageSource = rtb;
            }
            else
            {
                _imageSource = bi;
            }
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
            Bitmap blurCache = null;

            var pixelW = _imageSource.PixelSize.Width;
            var pixelH = _imageSource.PixelSize.Height;

            // decision #24: NearestNeighbor visual scaling → PushRenderOptions(BitmapInterpolationMode.None)
            var obscured = new RenderTargetBitmap(new PixelSize(pixelW, pixelH), new Vector(96, 96));
            using (var ctx = obscured.CreateDrawingContext())
            using (ctx.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = BitmapInterpolationMode.None }))
            {
                foreach (var o in _obscuredShapes)
                {
                    var sc = o.BlurRadius > 0 ? 1 / o.BlurRadius : 0.125;

                    if (sc != blurScale || blurCache == null)
                    {
                        // decision #23: TransformedBitmap downscale → CreateScaledBitmap, cached per scale factor
                        blurScale = sc;
                        var scaledSize = new PixelSize(
                            Math.Max(1, (int)Math.Round(pixelW * sc)),
                            Math.Max(1, (int)Math.Round(pixelH * sc)));
                        blurCache = _imageSource.CreateScaledBitmap(scaledSize, BitmapInterpolationMode.LowQuality);
                    }

                    using (ctx.PushGeometryClip(ShapeToGeometry(o)))
                        ctx.DrawImage(blurCache, new Rect(0, 0, pixelW, pixelH));
                }
            }

            _imageObscured = obscured;
            return true;
        }
    }
}
