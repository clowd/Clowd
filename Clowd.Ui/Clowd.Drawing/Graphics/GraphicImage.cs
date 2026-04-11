using System;
using System.IO;
using System.Linq;
using System.Text.Json.Serialization;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Clowd.Drawing.Graphics
{
    [GraphicDesc("Image", Skills = Skill.Angle | Skill.Crop | Skill.Cursor)]
    public class GraphicImage : GraphicRectangle
    {
        public string BitmapFilePath
        {
            get => _bitmapFilePath;
            set
            {
                _imageSource = null;
                _imageObscured = null;
                Set(ref _bitmapFilePath, value);
            }
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

        /// <summary>
        /// Crop rectangle in source-image pixel coordinates. When empty the
        /// full source bitmap is drawn. Used as the <c>sourceRect</c> on
        /// <c>DrawImage</c>.
        /// </summary>
        public Rect Crop
        {
            get => _crop;
            set => Set(ref _crop, value);
        }

        public ObscuredArea[] ObscuredAreas
        {
            get => _obscuredAreas;
            set
            {
                _imageObscured = null;
                Set(ref _obscuredAreas, value ?? Array.Empty<ObscuredArea>());
            }
        }

        /// <summary>
        /// A rectangular pixelation region in source-image pixel coordinates,
        /// stored on the GraphicImage so move/resize/rotate doesn't move the
        /// blurred areas relative to the underlying pixels.
        /// </summary>
        public record struct ObscuredArea(Rect Rect, double BlurRadius);

        private string _bitmapFilePath = string.Empty;
        private int _scaleX = 1;
        private int _scaleY = 1;
        private Size _originalSize;
        private Rect _crop;
        private ObscuredArea[] _obscuredAreas = Array.Empty<ObscuredArea>();

        [JsonIgnore] private Bitmap? _imageSource;
        [JsonIgnore] private RenderTargetBitmap? _imageObscured;

        public GraphicImage()
        { }

        public GraphicImage(string imageFilePath, Size imageSize)
            : this(imageFilePath, new Rect(new Point(0, 0), imageSize))
        { }

        public GraphicImage(string imageFilePath, Rect displayRect, double angle = 0, int flipX = 1, int flipY = 1)
            : base(Colors.Transparent, 0, displayRect, angle, false)
        {
            _bitmapFilePath = imageFilePath;
            _originalSize = displayRect.Size;
            _scaleX = flipX;
            _scaleY = flipY;
        }

        /// <summary>
        /// Loads <paramref name="path"/> from disk to discover its pixel size,
        /// then constructs a GraphicImage centred at <paramref name="dropPoint"/>
        /// in content space. Falls back to a 1×1 placeholder if the file can't
        /// be read.
        /// </summary>
        public static GraphicImage CreateFromFile(string path, Point dropPoint)
        {
            Size size;
            try
            {
                using var stream = File.OpenRead(path);
                using var bitmap = new Bitmap(stream);
                size = new Size(bitmap.PixelSize.Width, bitmap.PixelSize.Height);
            }
            catch
            {
                size = new Size(200, 150);
            }

            var rect = new Rect(
                dropPoint.X - size.Width / 2,
                dropPoint.Y - size.Height / 2,
                size.Width,
                size.Height);

            return new GraphicImage(path, rect)
            {
                OriginalSize = size,
            };
        }

        internal override void DrawObject(DrawingContext drawingContext)
        {
            if (_imageSource == null)
                LoadImage();

            if (_imageSource == null)
                return;

            var centerPt = CenterOfRotation;

            // Compose rotate + flip in a single matrix.
            var transform =
                Matrix.CreateTranslation(-centerPt.X, -centerPt.Y) *
                Matrix.CreateScale(_scaleX, _scaleY) *
                Matrix.CreateRotation(Angle * Math.PI / 180.0) *
                Matrix.CreateTranslation(centerPt.X, centerPt.Y);

            using (drawingContext.PushTransform(transform))
            {
                var dest = UnrotatedBounds;

                // Source rect: explicit crop, or the full source bitmap.
                Rect srcRect = !_crop.IsDefault()
                    ? _crop
                    : new Rect(0, 0, _imageSource.PixelSize.Width, _imageSource.PixelSize.Height);

                drawingContext.DrawImage(_imageSource, srcRect, dest);

                // Pixelate cache, if present, drawn over the same source rect.
                if (_obscuredAreas.Length > 0)
                {
                    if (_imageObscured == null)
                        RebuildObscureCache();

                    if (_imageObscured != null)
                        drawingContext.DrawImage(_imageObscured, srcRect, dest);
                }
            }
        }

        private void LoadImage()
        {
            if (string.IsNullOrEmpty(_bitmapFilePath) || !File.Exists(_bitmapFilePath))
                return;
            try
            {
                using var stream = File.OpenRead(_bitmapFilePath);
                _imageSource = new Bitmap(stream);
                if (_originalSize.Width <= 0 || _originalSize.Height <= 0)
                {
                    OriginalSize = new Size(_imageSource.PixelSize.Width, _imageSource.PixelSize.Height);
                }
            }
            catch
            {
                _imageSource = null;
            }
        }

        /// <summary>
        /// Adds a pixelation region. <paramref name="contentRect"/> is in
        /// canvas content-space coordinates; this method converts it to the
        /// image's source-pixel coordinate system so it survives subsequent
        /// move/resize/rotate operations.
        /// </summary>
        public void AddObscuredArea(Rect contentRect, double blurRadius)
        {
            if (_imageSource == null) LoadImage();
            if (_imageSource == null) return;

            // Convert the content rect to image-pixel space.
            var imgRect = ContentRectToImagePixels(contentRect);
            if (imgRect.Width <= 0 || imgRect.Height <= 0) return;

            ObscuredAreas = _obscuredAreas.Append(new ObscuredArea(imgRect, blurRadius)).ToArray();
        }

        private Rect ContentRectToImagePixels(Rect contentRect)
        {
            // Map each corner from content space to image-pixel space, then
            // bound them to a Rect. This handles rotation by unrotating the
            // corners around CenterOfRotation.
            var corners = new[]
            {
                new Point(contentRect.Left,  contentRect.Top),
                new Point(contentRect.Right, contentRect.Top),
                new Point(contentRect.Right, contentRect.Bottom),
                new Point(contentRect.Left,  contentRect.Bottom),
            };

            if (_imageSource == null) return default;

            var srcW = _imageSource.PixelSize.Width;
            var srcH = _imageSource.PixelSize.Height;
            var ub = UnrotatedBounds;
            if (ub.Width <= 0 || ub.Height <= 0) return default;

            // Crop's pixel space if cropped, else full source.
            var cropOrigin = !_crop.IsDefault() ? new Point(_crop.X, _crop.Y) : new Point(0, 0);
            var cropW = !_crop.IsDefault() ? _crop.Width : srcW;
            var cropH = !_crop.IsDefault() ? _crop.Height : srcH;

            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
            foreach (var c in corners)
            {
                var unrot = UnapplyRotation(c);
                // Local space relative to UnrotatedBounds top-left
                var lx = (unrot.X - ub.Left) / ub.Width;
                var ly = (unrot.Y - ub.Top) / ub.Height;
                if (FlipX < 0) lx = 1 - lx;
                if (FlipY < 0) ly = 1 - ly;
                var px = cropOrigin.X + lx * cropW;
                var py = cropOrigin.Y + ly * cropH;
                if (px < minX) minX = px;
                if (px > maxX) maxX = px;
                if (py < minY) minY = py;
                if (py > maxY) maxY = py;
            }

            // Clamp to source pixel bounds.
            minX = Math.Clamp(minX, 0, srcW);
            minY = Math.Clamp(minY, 0, srcH);
            maxX = Math.Clamp(maxX, 0, srcW);
            maxY = Math.Clamp(maxY, 0, srcH);

            return new Rect(minX, minY, maxX - minX, maxY - minY);
        }

        private void RebuildObscureCache()
        {
            if (_imageSource == null || _obscuredAreas.Length == 0)
            {
                _imageObscured = null;
                return;
            }

            var srcSize = _imageSource.PixelSize;
            var dpi = new Vector(96, 96);
            var rtb = new RenderTargetBitmap(srcSize, dpi);

            using (var dc = rtb.CreateDrawingContext())
            using (dc.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = BitmapInterpolationMode.None }))
            {
                foreach (var area in _obscuredAreas)
                {
                    if (area.Rect.Width <= 0 || area.Rect.Height <= 0) continue;

                    var blur = Math.Max(1.0, area.BlurRadius);
                    var smallW = Math.Max(1, (int)(area.Rect.Width / blur));
                    var smallH = Math.Max(1, (int)(area.Rect.Height / blur));

                    // Step 1: render the area into a tiny bitmap to downscale.
                    var small = new RenderTargetBitmap(new PixelSize(smallW, smallH), dpi);
                    using (var smallDc = small.CreateDrawingContext())
                    {
                        smallDc.DrawImage(_imageSource,
                            area.Rect,
                            new Rect(0, 0, smallW, smallH));
                    }

                    // Step 2: blow it back up with nearest-neighbor.
                    using (dc.PushClip(area.Rect))
                    {
                        dc.DrawImage(small, new Rect(0, 0, smallW, smallH), area.Rect);
                    }

                    small.Dispose();
                }
            }
            _imageObscured = rtb;
        }

        internal override void Normalize()
        {
            if (Right <= Left) _scaleX *= -1;
            if (Bottom <= Top) _scaleY *= -1;
            base.Normalize();
        }
    }

    internal static class RectExtensions
    {
        public static bool IsDefault(this Rect r)
            => r.X == 0 && r.Y == 0 && r.Width == 0 && r.Height == 0;
    }
}
