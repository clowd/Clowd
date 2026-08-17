using Clowd.VideoSDK.Model;
using SkiaSharp;

namespace Clowd.VideoSDK.Composition
{
    /// <summary>
    /// The resolved geometry of one drawn picture: the source-pixel region shown
    /// (<see cref="AspectMath.SourceInsets"/> applied) and the canvas rect it lands on — exactly
    /// the numbers <c>FrameComposer.DrawPicture</c> draws with, extracted so the cursor overlay
    /// can map a captured cursor position (source px) onto the canvas through the SAME placement
    /// math as the screen item itself. One implementation; the overlay cannot disagree with the
    /// pixels.
    /// </summary>
    internal readonly struct PictureMapping
    {
        private PictureMapping(SKRect source, SKRect dest)
        {
            Source = source;
            Dest = dest;
        }

        /// <summary>The region of the source picture drawn, in source pixels.</summary>
        public SKRect Source { get; }

        /// <summary>The canvas rect the source region lands on.</summary>
        public SKRect Dest { get; }

        /// <summary>Canvas pixels per source pixel, horizontally.</summary>
        public double ScaleX => Dest.Width / Source.Width;

        /// <summary>Canvas pixels per source pixel, vertically.</summary>
        public double ScaleY => Dest.Height / Source.Height;

        /// <summary>Maps a source-pixel point onto the canvas. Points outside
        /// <see cref="Source"/> extrapolate — they land outside <see cref="Dest"/>, where the
        /// item's clip discards them.</summary>
        public SKPoint Map(double sourceX, double sourceY) => new SKPoint(
            (float)(Dest.Left + (sourceX - Source.Left) * ScaleX),
            (float)(Dest.Top + (sourceY - Source.Top) * ScaleY));

        /// <summary>
        /// Resolves the mapping for a picture of <paramref name="imgW"/>×<paramref name="imgH"/>
        /// placed by <paramref name="transform"/> on a <paramref name="canvasWidth"/>×<paramref
        /// name="canvasHeight"/> canvas. False when nothing would draw (no picture, cropped to
        /// nothing, or a degenerate dest rect) — the exact cases DrawPicture bails on.
        /// </summary>
        public static bool TryMap(Transform transform, ItemEffects fx, double imgW, double imgH,
            int canvasWidth, int canvasHeight, out PictureMapping mapping)
        {
            mapping = default;
            transform ??= new Transform();
            if (imgW <= 0 || imgH <= 0)
                return false;

            // The displayed source region: the aspect ratio's own crop (fill) combined with the
            // user's crop on top of it — one resolver shared with the editor's placement math.
            var (cl, ct, cr, cb) = AspectMath.SourceInsets(transform, imgW, imgH);
            if (cl + cr >= 1 || ct + cb >= 1)
                return false; // cropped to nothing

            var src = new SKRect(
                (float)(cl * imgW), (float)(ct * imgH),
                (float)((1 - cr) * imgW), (float)((1 - cb) * imgH));

            // Scale = width fraction of the canvas; height follows the displayed aspect (the
            // region's own ratio, or the stretch target), unless an explicit height overrides it.
            double destW = transform.Scale * canvasWidth;
            double destH = transform.ScaleY is { } scaleY
                ? scaleY * canvasHeight
                : destW * (AspectMath.DisplayAspect(transform, imgW, imgH)
                           ?? (imgH * (1 - ct - cb)) / (imgW * (1 - cl - cr)));

            var rect = FrameComposer.PlaceRect(transform, fx, destW, destH, canvasWidth, canvasHeight);
            if (rect.Width <= 0 || rect.Height <= 0)
                return false;

            mapping = new PictureMapping(src, rect);
            return true;
        }
    }
}
