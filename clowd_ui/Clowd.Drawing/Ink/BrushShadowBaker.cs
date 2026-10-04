using System;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Clowd.Drawing.Graphics;
using Clowd.Drawing.Rendering;

namespace Clowd.Drawing.Ink
{
    /// <summary>
    /// The brush's shadow sprite while a stroke is being drawn, kept current in place (the
    /// <see cref="IIncrementalShadow"/> path of <see cref="ShadowSpriteCache"/>). A full bake
    /// rasterizes the whole stroke and blurs the whole sprite every frame; this keeps two alpha
    /// planes the size of the sprite — the unblurred silhouette of the points the outline builder
    /// has settled, and the blurred shadow of everything — and per frame rasterizes only the tail
    /// (the points not yet sealed into the settled plane, a few dozen), composites it over the
    /// settled plane in a window around it, and re-blurs just that window. The box blur is local,
    /// so a window re-blurred from a source padded by <see cref="ShadowRenderer.BlurReach"/> is
    /// exactly what a whole-plane blur gives inside the window: the result is the full bake's,
    /// for the cost of the tail. The settled plane is extended as points settle, each new run
    /// drawn once.
    ///
    /// The planes are laid out for a reserve around the ink — the bounds grown by a quarter — so
    /// the stroke can grow for a while before they are re-laid out (the old planes are carried
    /// over at a whole-pixel offset when the scale is unchanged; when the reserve no longer fits
    /// the dimension cap the scale drops and everything is redone, a handful of times per stroke
    /// at most). Translucent ink casts a lighter shadow: silhouettes are drawn opaque and the
    /// ink's alpha is applied with the tint, which is what a full bake of the ink's layer gives.
    /// </summary>
    internal sealed class BrushShadowBaker
    {
        /// <summary>The reserve grows the bounds by this fraction of their larger side…</summary>
        internal const double ReserveFraction = 0.25;

        /// <summary>…and by at least this many canvas units.</summary>
        internal const double ReserveMin = 32;

        private readonly GraphicBrush _brush;

        // the plane: `_scale` px per canvas unit, `_origin` (origin-local units) at its top-left
        // pixel, `_reserve` the ink area it was laid out for, `_padPx` pixels around that
        private double _scale;
        private Point _origin;
        private Rect _reserve;
        private int _padPx, _w, _h;
        private byte[] _settled;   // silhouette alpha of the sealed points, unblurred
        private byte[] _blurred;   // the shadow alpha of everything: what the sprite shows
        private byte[] _window;    // re-blur scratch
        private byte[] _raster;    // silhouette readback scratch
        private WriteableBitmap _bitmap;
        private RenderTargetBitmap _rtb;

        private FreehandStrokeBuilder _stroke; // the builder the planes were drawn from
        private int _sealedEnd = -1; // the last settled point drawn into _settled
        private PixelRect _prevTail; // the tail's window last frame (empty when none)
        private bool _redoAll;

        public BrushShadowBaker(GraphicBrush brush)
        {
            _brush = brush;
        }

        internal double Scale => _scale;
        internal int PlaneWidth => _w;
        internal int PlaneHeight => _h;
        internal byte[] BlurredPlane => _blurred;

        /// <summary>The sprite's canvas-unit rect relative to the stroke origin, shadow offset excluded.</summary>
        internal Rect PlaneRect => new Rect(_origin.X, _origin.Y, _w / _scale, _h / _scale);

        /// <summary>Makes the next <see cref="Bake"/> re-blur the whole plane rather than a
        /// window (tests: the windowed updates must land on exactly this).</summary>
        internal void RedoAllNextBake() => _redoAll = true;

        /// <summary>See <see cref="IIncrementalShadow.BakeShadowIncrementally"/>.</summary>
        public WriteableBitmap Bake(double zoomBucket, int maxDimension, out Vector originFromAnchor, out double bakeScale)
        {
            var stroke = _brush.GetStroke();
            var bounds = stroke.Bounds;

            bool full;
            if (_bitmap == null || !ReferenceEquals(stroke, _stroke))
            {
                // the first bake, or a new builder (the stroke width changed under us): fresh planes
                Relayout(bounds, zoomBucket, maxDimension, carry: false);
                full = true;
            }
            else if (!_reserve.Contains(bounds))
            {
                full = !Relayout(bounds, zoomBucket, maxDimension, carry: true); // false: the old planes carried over
            }
            else
            {
                full = false;
            }

            _stroke = stroke;
            if (full)
            {
                Array.Clear(_settled);
                Array.Clear(_blurred);
                _sealedEnd = -1;
                _prevTail = default;
            }

            bool wholePlane = full || _redoAll;
            _redoAll = false;

            // extend the settled plane by the points that settled since last time (one run
            // overlapping the previous by the builder's overlap, so the union is seamless)
            var changed = default(PixelRect);
            int settledEnd = stroke.OutlineSettled - 1;
            if (settledEnd > _sealedEnd)
            {
                var segment = stroke.BuildSegment(_sealedEnd < 0 ? 0 : stroke.OverlapStart(_sealedEnd), settledEnd);
                var rect = Window(segment.Bounds);
                Raster(segment, rect);
                CompositeOver(_settled, _w, rect, _raster, rect);
                changed = rect;
                _sealedEnd = settledEnd;
            }

            // the live tail from where the settled plane ends, over it
            var tail = _sealedEnd < 0 ? stroke.Tail : stroke.BuildTail(stroke.OverlapStart(_sealedEnd));
            var tailRect = Window(tail.Bounds);
            Raster(tail, tailRect);

            // everything within the blur's reach of a source pixel that changed (the new run, the
            // old tail, the new tail) is re-blurred; a full bake re-blurs the whole plane
            int reach = ShadowRenderer.BlurReach(ShadowRenderer.SigmaPx(_scale));
            var plane = new PixelRect(0, 0, _w, _h);
            var window = wholePlane ? plane : Inflate(Union(Union(changed, _prevTail), tailRect), reach).Intersect(plane);
            Reblur(window, reach, tailRect);
            _prevTail = tailRect;

            originFromAnchor = new Vector(_origin.X + ShadowRenderer.ShadowOffsetX, _origin.Y + ShadowRenderer.ShadowOffsetY);
            bakeScale = _scale;
            return _bitmap;
        }

        /// <summary>
        /// Lays the planes out for a reserve around <paramref name="bounds"/> at the largest
        /// scale ≤ the zoom bucket whose plane fits <paramref name="maxDimension"/>. True when
        /// the previous planes were carried over (same scale; the new plane is placed on the old
        /// pixel grid and contains the old one, so the copy is exact — the blur outside the old
        /// ink is zero either way); false when they must be rebuilt.
        /// </summary>
        private bool Relayout(Rect bounds, double zoomBucket, int maxDimension, bool carry = true)
        {
            int pad = ShadowRenderer.Pad(_brush.LineWidth);
            double grow = Math.Max(ReserveMin, ReserveFraction * Math.Max(bounds.Width, bounds.Height));
            var reserve = bounds.Inflate(grow);

            double scale = zoomBucket;
            int padPx, w, h;
            while (true)
            {
                padPx = (int)Math.Ceiling(pad * scale);
                w = (int)Math.Ceiling(reserve.Width * scale) + 2 * padPx;
                h = (int)Math.Ceiling(reserve.Height * scale) + 2 * padPx;
                int max = Math.Max(w, h);
                if (max <= maxDimension)
                    break;
                scale *= (double)maxDimension / max * 0.995; // the ceils make this inexact; shrink until it fits
            }

            var origin = new Point(reserve.Left - padPx / scale, reserve.Top - padPx / scale);
            carry = carry && _bitmap != null && scale == _scale;
            int dx = 0, dy = 0;
            if (carry)
            {
                // snap the new plane onto the old pixel grid, at or beyond the requested origin.
                // The stroke's bounds are not monotonic (the start rule swallows the opening points,
                // a dot thins once the pointer moves), so the request can lie right of / below the old
                // plane on one side: the new plane still starts no later than the old one, or the
                // carried rows would land at negative offsets.
                dx = Math.Max(0, (int)Math.Ceiling((_origin.X - origin.X) * scale));
                dy = Math.Max(0, (int)Math.Ceiling((_origin.Y - origin.Y) * scale));
                origin = new Point(_origin.X - dx / scale, _origin.Y - dy / scale);
                w = Math.Max((int)Math.Ceiling((reserve.Right + pad - origin.X) * scale), dx + _w);
                h = Math.Max((int)Math.Ceiling((reserve.Bottom + pad - origin.Y) * scale), dy + _h);
                if (Math.Max(w, h) > maxDimension)
                {
                    // the snap pushed it over: rebuild at a scale that fits
                    return Relayout(bounds, scale * (double)maxDimension / Math.Max(w, h) * 0.995, maxDimension, carry: false);
                }
            }

            var settled = new byte[w * h];
            var blurred = new byte[w * h];
            if (carry)
            {
                for (int y = 0; y < _h; y++)
                {
                    Array.Copy(_settled, y * _w, settled, (y + dy) * w + dx, _w);
                    Array.Copy(_blurred, y * _w, blurred, (y + dy) * w + dx, _w);
                }

                _prevTail = _prevTail.Width > 0 ? _prevTail.Translate(new PixelPoint(dx, dy)) : default;
            }

            _scale = scale;
            _origin = origin;
            _reserve = reserve;
            _padPx = padPx;
            _w = w;
            _h = h;
            _settled = settled;
            _blurred = blurred;
            _bitmap = new WriteableBitmap(new PixelSize(w, h), new Vector(96, 96), PixelFormats.Bgra8888, AlphaFormat.Premul);
            _rtb?.Dispose();
            _rtb = null;

            if (carry)
            {
                using (var fb = _bitmap.Lock())
                    ShadowRenderer.Tint(_blurred, _w, new PixelRect(0, 0, _w, _h), fb, _brush.ObjectColor.A);
            }

            return carry;
        }

        /// <summary>The plane pixels an origin-local rect touches, one extra each side for the
        /// anti-aliasing, clipped to the plane.</summary>
        private PixelRect Window(Rect local)
        {
            int x0 = (int)Math.Floor((local.Left - _origin.X) * _scale) - 1;
            int y0 = (int)Math.Floor((local.Top - _origin.Y) * _scale) - 1;
            int x1 = (int)Math.Ceiling((local.Right - _origin.X) * _scale) + 1;
            int y1 = (int)Math.Ceiling((local.Bottom - _origin.Y) * _scale) + 1;
            return new PixelRect(x0, y0, x1 - x0, y1 - y0).Intersect(new PixelRect(0, 0, _w, _h));
        }

        /// <summary>Rasterizes <paramref name="geometry"/>'s silhouette (opaque) into
        /// <see cref="_raster"/> over the plane rect <paramref name="rect"/>.</summary>
        private void Raster(Geometry geometry, PixelRect rect)
        {
            if (_rtb == null || _rtb.PixelSize.Width < rect.Width || _rtb.PixelSize.Height < rect.Height)
            {
                // grown with slack so a wandering tail does not re-create it every frame
                int rw = Math.Min(_w, Math.Max(rect.Width + 64, _rtb?.PixelSize.Width ?? 0));
                int rh = Math.Min(_h, Math.Max(rect.Height + 64, _rtb?.PixelSize.Height ?? 0));
                _rtb?.Dispose();
                _rtb = new RenderTargetBitmap(new PixelSize(rw, rh), new Vector(96, 96));
            }

            if (_raster == null || _raster.Length < rect.Width * rect.Height)
                _raster = new byte[rect.Width * rect.Height];

            // origin-local → plane pixels → rect pixels
            var transform = Matrix.CreateTranslation(-_origin.X, -_origin.Y)
                            * Matrix.CreateScale(_scale, _scale)
                            * Matrix.CreateTranslation(-rect.X, -rect.Y);
            ShadowRenderer.RasterAlpha(_rtb, new PixelRect(0, 0, rect.Width, rect.Height), ctx =>
            {
                using (ctx.PushTransform(transform))
                    ctx.DrawGeometry(Brushes.Black, null, geometry);
            }, _raster);
        }

        /// <summary>
        /// Re-blurs <paramref name="window"/> of the shadow plane from the settled plane with the
        /// tail composited over it, and writes it to the sprite. The source is taken
        /// <paramref name="reach"/> beyond the window (clipped to the plane, whose outside is zero
        /// for a whole-plane blur too), which is exactly what the window's pixels see.
        /// </summary>
        private void Reblur(PixelRect window, int reach, PixelRect tailRect)
        {
            if (window.Width <= 0 || window.Height <= 0)
                return;

            var padded = Inflate(window, reach).Intersect(new PixelRect(0, 0, _w, _h));
            int pw = padded.Width, ph = padded.Height;
            if (_window == null || _window.Length < pw * ph)
                _window = new byte[pw * ph];

            for (int y = 0; y < ph; y++)
                Array.Copy(_settled, (padded.Y + y) * _w + padded.X, _window, y * pw, pw);

            CompositeOver(_window, pw, tailRect.Translate(new PixelPoint(-padded.X, -padded.Y)), _raster, tailRect);
            ShadowRenderer.BoxBlur3(_window, pw, ph, ShadowRenderer.SigmaPx(_scale));

            for (int y = window.Y; y < window.Bottom; y++)
                Array.Copy(_window, (y - padded.Y) * pw + window.X - padded.X, _blurred, y * _w + window.X, window.Width);

            using (var fb = _bitmap.Lock())
                ShadowRenderer.Tint(_blurred, _w, window, fb, _brush.ObjectColor.A);
        }

        /// <summary>Composites a rasterized silhouette (<paramref name="src"/>, laid out as
        /// <paramref name="srcRect"/>) over <paramref name="dst"/> at <paramref name="dstRect"/>
        /// (same size), the way Skia unions coverage.</summary>
        private static void CompositeOver(byte[] dst, int dstWidth, PixelRect dstRect, byte[] src, PixelRect srcRect)
        {
            for (int y = 0; y < dstRect.Height; y++)
            {
                int d = (dstRect.Y + y) * dstWidth + dstRect.X;
                int s = y * srcRect.Width;
                for (int x = 0; x < dstRect.Width; x++)
                {
                    int a = dst[d + x], b = src[s + x];
                    dst[d + x] = (byte)(a + b - a * b / 255);
                }
            }
        }

        private static PixelRect Union(PixelRect a, PixelRect b) =>
            a.Width <= 0 || a.Height <= 0 ? b : b.Width <= 0 || b.Height <= 0 ? a : a.Union(b);

        private static PixelRect Inflate(PixelRect r, int by) =>
            new PixelRect(r.X - by, r.Y - by, r.Width + 2 * by, r.Height + 2 * by);
    }
}
