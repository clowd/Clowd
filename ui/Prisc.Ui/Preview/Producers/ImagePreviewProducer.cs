using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using SkiaSharp;

namespace Clowd.UI.Preview.Producers
{
    /// <summary>
    /// Lane A. Decodes a still image down to tile size: a capture's <c>cropped.png</c>, the image
    /// editor's flattened <c>&lt;guid&gt;.png</c>, an image upload's <c>content.*</c>, or one of the
    /// legacy <c>preview&lt;ext&gt;</c> copies.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The whole point of going through <see cref="SKCodec"/> rather than <c>SKBitmap.Decode</c> is
    /// <see cref="SKCodec.GetScaledDimensions"/>: a JPEG can be decoded straight out of the DCT at
    /// 1/2, 1/4 or 1/8 scale, so a 12 MP phone photo becomes a ~1.5 MP decode instead of a 48 MB
    /// allocation on a background thread that then throws all of it away. Formats with no native
    /// sampling (PNG, which is most of what this app writes) hand back their full size and are
    /// resampled in one step afterwards — there is no cheaper honest answer for them.
    /// </para>
    /// <para>
    /// Alpha survives end to end. A capture of a rounded window corner or an editor canvas with a
    /// transparent background is common here, and the tile paints its checkerboard behind a
    /// <see cref="PreviewKind.Photo"/> precisely so the user can see it.
    /// </para>
    /// </remarks>
    public static class ImagePreviewProducer
    {
        /// <summary>
        /// A source this refuses to decode at all, in pixels. Four bytes each, so this is a
        /// ~200 MB transient ceiling on a background thread — generous enough for an 8K screenshot
        /// or a large scanned page, and low enough that a corrupt header claiming 60000x60000 is
        /// rejected instead of taking the process down. The row falls back to its file-type icon.
        /// </summary>
        private const long MaxSourcePixels = 50_000_000;

        /// <summary>
        /// Produces the tile picture for an <see cref="PreviewSourceKind.Image"/> source, or null
        /// if it cannot be decoded — a truncated screenshot, a file being written as we look at it,
        /// a format Skia has no codec for. Null is not an error here: the engine negative-caches it
        /// and falls through to the icon producer.
        /// </summary>
        public static PreviewPixels Produce(in PreviewSource source, PreviewRequest request, CancellationToken ct)
        {
            if (String.IsNullOrEmpty(source.Path))
                return null;

            int maxWidth = request?.TargetWidth > 0 ? request.TargetWidth : PreviewFormat.TileWidth;
            int maxHeight = request?.TargetHeight > 0 ? request.TargetHeight : PreviewFormat.TileHeight;

            try
            {
                if (ct.IsCancellationRequested)
                    return null;

                // FileShare.ReadWrite because the file may still be open for writing: a capture's
                // png lands milliseconds before the row appears, and a share violation here would
                // be cached as "this session has no preview" for the next five minutes.
                using var stream = new FileStream(source.Path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                using var codec = SKCodec.Create(stream);
                if (codec == null)
                    return null;

                int srcWidth = codec.Info.Width, srcHeight = codec.Info.Height;
                if (srcWidth <= 0 || srcHeight <= 0 || (long)srcWidth * srcHeight > MaxSourcePixels)
                    return null;

                var (fitWidth, fitHeight) = PreviewRaster.Fit(srcWidth, srcHeight, maxWidth, maxHeight);
                if (fitWidth <= 0 || fitHeight <= 0)
                    return null;

                // Ask the codec for the smallest scale it can decode natively that still covers the
                // size we actually want. Whatever it hands back is authoritative — a codec is free
                // to round up, ignore the request entirely, or (JPEG) snap to a power-of-two step.
                float desired = Math.Min((float)fitWidth / srcWidth, (float)fitHeight / srcHeight);
                var sampled = codec.GetScaledDimensions(Math.Clamp(desired, 1f / 64f, 1f));
                int decodeWidth = sampled.Width > 0 ? sampled.Width : srcWidth;
                int decodeHeight = sampled.Height > 0 ? sampled.Height : srcHeight;

                // Premultiplied through the decode and the resample, because that is the only alpha
                // type Skia filters correctly: resampling unpremultiplied pixels bleeds the colour
                // of fully transparent texels into their neighbours, which shows up as a dark halo
                // around a capture's antialiased window edge.
                var decodeInfo = new SKImageInfo(decodeWidth, decodeHeight, SKColorType.Bgra8888, SKAlphaType.Premul);
                using var decoded = new SKBitmap(decodeInfo);

                var result = codec.GetPixels(decodeInfo, decoded.GetPixels());
                // IncompleteInput is a truncated file that still decoded a usable top portion —
                // exactly what a preview wants from a capture that is still being flushed.
                if (result != SKCodecResult.Success && result != SKCodecResult.IncompleteInput)
                    return null;

                if (ct.IsCancellationRequested)
                    return null;

                if (decodeWidth == fitWidth && decodeHeight == fitHeight)
                    return PreviewRaster.ToPixels(decoded, PreviewKind.Photo);

                // One resample, Mitchell: a cubic with enough ring to keep small UI text in a
                // screenshot readable at a fifth of its size, without the halo Catmull-Rom leaves
                // on the hard edges these pictures are mostly made of.
                var fitInfo = new SKImageInfo(fitWidth, fitHeight, SKColorType.Bgra8888, SKAlphaType.Premul);
                using var resized = decoded.Resize(fitInfo, new SKSamplingOptions(SKCubicResampler.Mitchell));
                if (resized == null)
                    return null;

                return PreviewRaster.ToPixels(resized, PreviewKind.Photo);
            }
            catch (Exception ex)
            {
                // Everything reachable here is a bad or vanishing file: a delete between the
                // resolver's stat and this open, an unreadable share, a header Skia chokes on. The
                // answer to all of them is the same, and it is not an exception on a shared worker.
                Debug.WriteLine("ImagePreviewProducer: " + source.Path + " — " + ex.Message);
                return null;
            }
        }
    }
}
