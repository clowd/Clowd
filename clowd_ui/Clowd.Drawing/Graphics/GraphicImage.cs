using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Clowd.Drawing.Rendering;

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

        /// <summary><paramref name="Mode"/> is optional and defaults to
        /// <see cref="ObscureMode.Mosaic"/>: shapes persisted before the mode existed carry no
        /// "Mode" property, and the serializer leaves absent members at their default.</summary>
        public record struct ObscuredShape(Point P0, Point P1, Point P2, Point P3, double BlurRadius,
                                           ObscureMode Mode = ObscureMode.Mosaic);

        private string _cursorFilePath;
        private PixelRect _cursorPosition;
        private bool _cursorVisible;
        private string _bitmapFilePath;
        private int _scaleX = 1;
        private int _scaleY = 1;
        private PixelRect _crop;
        private Size _originalSize;
        private ObscuredShape[] _obscuredShapes = new ObscuredShape[0];
        /// <summary>The decoded source bitmap (exposed for tests of the shared decode cache).</summary>
        internal Bitmap ImageSource
        {
            get
            {
                if (_imageSource == null) UpdateImageCache();
                return _imageSource;
            }
        }

        // not persisted by GraphicsSerializer
        [Transient] private Bitmap _imageSource;
        [Transient] private Bitmap _imageObscured;
        [Transient] private Rect _editingAnchor;
        // sub-unit remainder carried between Move calls so pixel-snapping can't drift (see Move)
        [Transient] private double _moveRemainderX;
        [Transient] private double _moveRemainderY;
        [Transient] private DrawingCanvas _editingCanvas;

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

        // PORT NOTE (aspect map entry): images have no shadow. Fields that change the decoded
        // composite invalidate ImageCache (the setters already null _imageSource/_imageObscured —
        // kept verbatim; this entry drives the funnel and the OnFieldsRestored path); obscuredShapes
        // affects only the overlay; crop/flip change the displayed pixels but not the artwork bounds
        // rectangle, so they map to Bounds (conservative recompute) only.
        internal override void DeclarePropertyEffects(Dictionary<string, InvalidationAspects> map)
        {
            base.DeclarePropertyEffects(map);
            const InvalidationAspects sourceAspects = InvalidationAspects.Bounds | InvalidationAspects.ImageCache;
            map[nameof(BitmapFilePath)] = sourceAspects;
            map[nameof(CursorFilePath)] = sourceAspects;
            map[nameof(CursorPosition)] = sourceAspects;
            map[nameof(CursorVisible)] = sourceAspects;
            map[nameof(ObscuredShapes)] = InvalidationAspects.ImageCache;
            map[nameof(Crop)] = InvalidationAspects.Bounds;
            map[nameof(FlipX)] = InvalidationAspects.Bounds;
            map[nameof(FlipY)] = InvalidationAspects.Bounds;
        }

        // The history engine writes restored values straight into fields (no setter side-effects
        // fire), so re-null the decoded caches here — but only the ones whose inputs actually
        // changed, so undoing a non-image edit (e.g. a resize) does not force a PNG re-decode. The
        // JSON names mirror the setter side-effects: bitmap/cursor fields drop both source and
        // overlay; obscuredShapes drops only the overlay.
        internal override void OnFieldsRestored(IReadOnlyCollection<string> changedJsonNames)
        {
            bool sourceAffected = changedJsonNames.Contains("bitmapFilePath")
                                  || changedJsonNames.Contains("cursorFilePath")
                                  || changedJsonNames.Contains("cursorPosition")
                                  || changedJsonNames.Contains("cursorVisible");
            bool obscureAffected = sourceAffected || changedJsonNames.Contains("obscuredShapes");

            if (sourceAffected) _imageSource = null;
            if (obscureAffected) _imageObscured = null;

            base.OnFieldsRestored(changedJsonNames); // nuke the derived bounds/geometry sidecar
        }

        // Retained-instance trim (deleted graphics kept by the history engine): drop the decoded
        // bitmaps so a retained 4K screenshot costs a field record, not 33 MB. Reload is cheap via
        // the decode LRU if the graphic is ever re-inserted.
        internal override void TrimTransientCaches()
        {
            _imageSource = null;
            _imageObscured = null;
            base.TrimTransientCaches();
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
                // Keep raster content pixel aligned. A body drag arrives as a fractional
                // logical-unit delta, and unlike a vector graphic an image is blitted rather than
                // re-rasterised: land it on a fractional origin and the sampler resamples every
                // pixel, which reads as a blurry screenshot with a half-transparent edge row — on
                // screen and in anything exported from the canvas.
                //
                // Move is called with INCREMENTAL deltas, so the rounding residual has to be carried
                // forward explicitly. Rounding each delta in isolation discards it, and since the
                // discarded part is a signed quantity that nothing ever repays, the image separates
                // from the cursor without bound: at 150% DPI a 0.667-unit step rounds to 1 every
                // event (the image outruns the pointer by half), and at zoom 2 a steady 0.5-unit
                // step rounds to even — i.e. to zero — every event, freezing the image completely.
                // Carrying the residual keeps the quantised position within half a unit of the
                // intended one forever. Nudges (already integral) are unaffected either way.
                var targetX = Left + deltaX + _moveRemainderX;
                var targetY = Top + deltaY + _moveRemainderY;

                var appliedX = Math.Round(targetX) - Left;
                var appliedY = Math.Round(targetY) - Top;

                _moveRemainderX = targetX - (Left + appliedX);
                _moveRemainderY = targetY - (Top + appliedY);

                base.Move(appliedX, appliedY);
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

        /// <summary>Drop the carried sub-unit rounding residual at the end of a drag — it belongs to
        /// the gesture that produced it. Without this the next drag's first step would pop by up to
        /// half a unit. Normalize covers the geometry-rewrite paths (undo/redo, restore, resize);
        /// this covers a plain body move, which is the only gesture that actually builds residue and
        /// the one path that never calls Normalize.</summary>
        internal override void OnGestureCompleted()
        {
            _moveRemainderX = 0;
            _moveRemainderY = 0;
        }

        internal override void Normalize()
        {
            _moveRemainderX = 0;
            _moveRemainderY = 0;

            if (Editing) return;
            if (Right <= Left) _scaleX /= -1;
            if (Bottom <= Top) _scaleY /= -1;
            base.Normalize();
        }

        internal void AddObscuredArea(Rect rect, double blurRadius, ObscureMode mode = ObscureMode.Mosaic)
        {
            var pts = new Point[] { rect.TopLeft, rect.TopRight, rect.BottomRight, rect.BottomLeft }.Select(UnapplyRotation).ToArray();
            if (!pts.Any(p => UnrotatedBounds.Contains(p)))
                return;

            pts = pts.Select(TranslateUnrotatedPointToImageSpace).ToArray();
            ObscuredShapes = ObscuredShapes.Append(new ObscuredShape(pts[0], pts[1], pts[2], pts[3], blurRadius, mode)).ToArray();
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

        // Decoding the screenshot (disk read + PNG decode + optional cursor composite) costs far
        // more than everything else on the undo/redo and editor-open paths, and undo/redo restores
        // snapshots into brand new GraphicImage instances whose [Transient] caches start out null.
        // The decoded result is therefore shared process-wide, keyed by every input that affects
        // it (paths include mtime/size so a rewritten file is not served stale). Entries are never
        // disposed on eviction — live graphics may still be drawing them; the GC reclaims them.
        private readonly record struct ImageCacheKey(string BitmapPath, long BitmapStamp, string CursorPath, long CursorStamp,
                                                     PixelRect CursorPosition);

        private const int SourceCacheCapacity = 8;
        private static readonly object _sourceCacheLock = new object();
        private static readonly Dictionary<ImageCacheKey, Bitmap> _sourceCache = new Dictionary<ImageCacheKey, Bitmap>();
        private static readonly List<ImageCacheKey> _sourceCacheLru = new List<ImageCacheKey>(); // most recently used last

        private static long GetFileStamp(string path)
        {
            var fi = new FileInfo(path);
            return fi.Exists ? fi.LastWriteTimeUtc.Ticks ^ (fi.Length << 1) : 0;
        }

        private void UpdateImageCache()
        {
            bool compositeCursor = HasCursor && CursorVisible;
            var key = new ImageCacheKey(
                _bitmapFilePath,
                GetFileStamp(_bitmapFilePath),
                compositeCursor ? _cursorFilePath : null,
                compositeCursor ? GetFileStamp(_cursorFilePath) : 0,
                compositeCursor ? _cursorPosition : default);

            lock (_sourceCacheLock)
            {
                if (_sourceCache.TryGetValue(key, out var cached))
                {
                    _sourceCacheLru.Remove(key);
                    _sourceCacheLru.Add(key);
                    _imageSource = cached;
                    return;
                }
            }

            var loaded = LoadImageSource(compositeCursor);

            lock (_sourceCacheLock)
            {
                if (!_sourceCache.ContainsKey(key))
                {
                    _sourceCache.Add(key, loaded);
                    _sourceCacheLru.Add(key);
                    if (_sourceCacheLru.Count > SourceCacheCapacity)
                    {
                        _sourceCache.Remove(_sourceCacheLru[0]);
                        _sourceCacheLru.RemoveAt(0);
                    }
                }
            }

            _imageSource = loaded;
        }

        private Bitmap LoadImageSource(bool compositeCursor)
        {
            // decision #22: BitmapFactory.FromStream + Blit → new Bitmap(stream), composited into a
            // RenderTargetBitmap at image pixel size / 96 DPI (image first, then cursor rect).
            // decision #21 note: bitmaps are normalized to 96 DPI on load so pixel rects == DIP rects.
            Bitmap bi;
            using (var bifs = File.OpenRead(_bitmapFilePath))
                bi = new Bitmap(bifs);

            bool normalizeDpi = Math.Abs(bi.Dpi.X - 96) > 0.01 || Math.Abs(bi.Dpi.Y - 96) > 0.01;

            if (!compositeCursor && !normalizeDpi)
                return bi;

            if (!compositeCursor)
            {
                // DPI is metadata only — re-tag the pixels as 96 DPI with a raw copy (no render pass)
                var copied = TryCopyPixels(bi);
                if (copied != null)
                {
                    bi.Dispose();
                    return copied;
                }
            }

            using (bi)
            {
                using var rtb = new RenderTargetBitmap(bi.PixelSize, new Vector(96, 96));
                using (var ctx = rtb.CreateDrawingContext())
                {
                    ctx.DrawImage(bi, new Rect(0, 0, bi.PixelSize.Width, bi.PixelSize.Height));

                    if (compositeCursor)
                    {
                        using var curfs = File.OpenRead(_cursorFilePath);
                        using var wcursor = new Bitmap(curfs);
                        ctx.DrawImage(wcursor, new Rect(wcursor.Size), _cursorPosition.ToRect());
                    }
                }

                // a RenderTargetBitmap is not a readable bitmap — CreateScaledBitmap (obscure cache)
                // throws "invalid source bitmap type" on it — so copy the pixels out into a plain
                // (readable) bitmap, falling back to a PNG round-trip if the copy is unsupported.
                var copied = TryCopyPixels(rtb);
                if (copied != null)
                    return copied;

                using var ms = new MemoryStream();
                rtb.Save(ms, PngBitmapEncoderOptions.Default);
                ms.Position = 0;
                return new Bitmap(ms);
            }
        }

        /// <summary>Raw-copies a bitmap's pixels into a readable 96-DPI bitmap, avoiding a render
        /// pass / PNG round-trip. The raw-data Bitmap constructor is used (not a WriteableBitmap)
        /// because it produces an immutable bitmap — the only kind Skia's CreateScaledBitmap (the
        /// pixelate obscure cache) accepts. Returns null if the source does not support
        /// CopyPixels.</summary>
        private static Bitmap TryCopyPixels(Bitmap source)
        {
            if (source.Format == null)
                return null;

            var format = source.Format.Value;
            var size = source.PixelSize;
            var stride = (size.Width * format.BitsPerPixel + 7) / 8;
            var buffer = Marshal.AllocHGlobal(stride * size.Height);
            try
            {
                source.CopyPixels(new PixelRect(size), buffer, stride * size.Height, stride);
                return new Bitmap(format, AlphaFormat.Premul, buffer, size, new Vector(96, 96), stride);
            }
            catch (NotSupportedException)
            {
                return null;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        /// <summary>
        /// Draws one <see cref="ObscureMode.Blur"/> shape by blurring a copy of the source pixels
        /// under it. Only the shape's neighbourhood is blurred — a whole-image gaussian would cost
        /// several passes over a 4K screenshot per cache rebuild — and the cut-out is padded by the
        /// blur reach so the rim the kernel darkens against the transparent outside lands beyond
        /// the geometry clip. Returns false when the source pixels cannot be read back.
        /// </summary>
        private bool TryDrawBlurred(DrawingContext ctx, ObscuredShape shape, int pixelW, int pixelH)
        {
            if (_imageSource.Format is not { BitsPerPixel: 32 })
                return false;

            var format = _imageSource.Format.Value;
            var sigma = shape.BlurRadius > 0 ? shape.BlurRadius : 8;
            var region = ShapeRegion(shape, pixelW, pixelH, (int)Math.Ceiling(sigma * 3) + 1);
            if (region.Width <= 0 || region.Height <= 0)
                return true; // entirely off-image: nothing to obscure, but not a failure

            var stride = region.Width * 4;
            var pixels = new byte[stride * region.Height];
            var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
            try
            {
                _imageSource.CopyPixels(region, handle.AddrOfPinnedObject(), pixels.Length, stride);
            }
            catch (NotSupportedException)
            {
                return false;
            }
            finally
            {
                handle.Free();
            }

            ShadowRenderer.BoxBlur3(pixels, region.Width, region.Height, sigma, 4);

            handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
            try
            {
                // the raw-data constructor copies the buffer, so the pin ends with this scope
                using var blurred = new Bitmap(format, AlphaFormat.Premul, handle.AddrOfPinnedObject(), region.Size,
                                               new Vector(96, 96), stride);
                using (ctx.PushGeometryClip(ShapeToGeometry(shape)))
                    ctx.DrawImage(blurred, region.ToRect());
            }
            finally
            {
                handle.Free();
            }

            return true;
        }

        /// <summary>Shape bounds in image pixel space, inflated by <paramref name="pad"/> and
        /// clamped to the image.</summary>
        private static PixelRect ShapeRegion(ObscuredShape shape, int pixelW, int pixelH, int pad)
        {
            var minX = Math.Min(Math.Min(shape.P0.X, shape.P1.X), Math.Min(shape.P2.X, shape.P3.X));
            var maxX = Math.Max(Math.Max(shape.P0.X, shape.P1.X), Math.Max(shape.P2.X, shape.P3.X));
            var minY = Math.Min(Math.Min(shape.P0.Y, shape.P1.Y), Math.Min(shape.P2.Y, shape.P3.Y));
            var maxY = Math.Max(Math.Max(shape.P0.Y, shape.P1.Y), Math.Max(shape.P2.Y, shape.P3.Y));

            // clamp before narrowing: the points are unvalidated doubles and may sit far outside
            // int range (a NaN survives the clamp and narrows to 0, giving an empty region)
            var x0 = (int)Math.Clamp(Math.Floor(minX) - pad, 0, pixelW);
            var y0 = (int)Math.Clamp(Math.Floor(minY) - pad, 0, pixelH);
            var x1 = (int)Math.Clamp(Math.Ceiling(maxX) + pad, 0, pixelW);
            var y1 = (int)Math.Clamp(Math.Ceiling(maxY) + pad, 0, pixelH);

            return new PixelRect(x0, y0, x1 - x0, y1 - y0);
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
                    if (o.Mode == ObscureMode.Solid)
                    {
                        using (ctx.PushGeometryClip(ShapeToGeometry(o)))
                            ctx.DrawRectangle(Brushes.Black, null, new Rect(0, 0, pixelW, pixelH));
                        continue;
                    }

                    // Blur needs a pixel read-back, which a Bitmap is allowed not to support; fall
                    // through to mosaic when it fails, because an obscured region must never render
                    // unobscured.
                    if (o.Mode == ObscureMode.Blur && TryDrawBlurred(ctx, o, pixelW, pixelH))
                        continue;

                    var sc = o.BlurRadius > 0 ? 1 / o.BlurRadius : 0.125;

                    if (sc != blurScale || blurCache == null)
                    {
                        // decision #23: TransformedBitmap downscale → CreateScaledBitmap, cached per scale factor
                        blurScale = sc;
                        var scaledSize = new PixelSize(
                            Math.Max(1, (int)Math.Round(pixelW * sc)),
                            Math.Max(1, (int)Math.Round(pixelH * sc)));
                        blurCache?.Dispose();
                        blurCache = _imageSource.CreateScaledBitmap(scaledSize, BitmapInterpolationMode.LowQuality);
                    }

                    using (ctx.PushGeometryClip(ShapeToGeometry(o)))
                        ctx.DrawImage(blurCache, new Rect(0, 0, pixelW, pixelH));
                }
            }

            blurCache?.Dispose();

            // this overlay is drawn over the image every frame it renders; a live RenderTargetBitmap
            // is a surface-backed image (slower to draw repeatedly, and it pins a full GPU surface),
            // so copy the pixels out into an immutable bitmap and release the surface.
            var copied = TryCopyPixels(obscured);
            if (copied != null)
            {
                obscured.Dispose();
                _imageObscured = copied;
            }
            else
            {
                _imageObscured = obscured;
            }

            return true;
        }
    }
}
