using System;
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
        /// stored pixels placed by <paramref name="transform"/> on a <paramref name="canvasWidth"/>×<paramref
        /// name="canvasHeight"/> canvas. False when nothing would draw (no picture, cropped to
        /// nothing, or a degenerate dest rect) — the exact cases DrawPicture bails on.
        ///
        /// <para><paramref name="pixelAspect"/> is the picture's pixel aspect ratio
        /// (<see cref="SourceStream.PixelAspect"/>): the shape math — crop insets, the box's own
        /// ratio — runs on the <i>displayed</i> size <c>imgW·pixelAspect × imgH</c>, while
        /// <see cref="Source"/> stays in stored pixels because that is what the image is sampled
        /// in. Drawing the stored region into the display-shaped box is exactly the horizontal
        /// stretch/squeeze that undoes the non-square pixels. 1 (square pixels) for everything
        /// but an imported anamorphic file; a non-positive value is treated as 1.</para>
        /// </summary>
        public static bool TryMap(Transform transform, ItemEffects fx, double imgW, double imgH,
            int canvasWidth, int canvasHeight, out PictureMapping mapping, double pixelAspect = 1.0)
        {
            mapping = default;
            transform ??= new Transform();
            if (imgW <= 0 || imgH <= 0)
                return false;
            if (!(pixelAspect > 0) || Double.IsInfinity(pixelAspect))
                pixelAspect = 1.0;
            double shownW = imgW * pixelAspect;

            // The displayed source region: the aspect ratio's own crop (fill) combined with the
            // user's crop on top of it — one resolver shared with the editor's placement math.
            // Insets are fractions, so resolving them against the displayed shape and applying
            // them to the stored pixels is the same crop.
            var (cl, ct, cr, cb) = AspectMath.SourceInsets(transform, shownW, imgH);
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
                : destW * (AspectMath.DisplayAspect(transform, shownW, imgH)
                           ?? (imgH * (1 - ct - cb)) / (shownW * (1 - cl - cr)));

            var rect = FrameComposer.PlaceRect(transform, fx, destW, destH, canvasWidth, canvasHeight);
            if (rect.Width <= 0 || rect.Height <= 0)
                return false;

            mapping = new PictureMapping(src, rect);
            return true;
        }
    }
}
