using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using Clowd.VideoSDK.Thumbs;
using SkiaSharp;

namespace Clowd.UI.Preview.Producers
{
    /// <summary>
    /// Lane B. One decoded frame from a recording, a render, or a GIF conversion — the fix for the
    /// entries that today show a screenshot taken before their own source recording began.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Lane B only, because this opens an AVFormatContext and decodes up to a GOP. It sits above
    /// the editor's own thumbnail bands so an open timeline always wins the single thread.
    /// </para>
    /// <para>
    /// The frame is taken a tenth of the way in rather than at t=0: recordings, renders and GIF
    /// conversions alike open on a black or half-faded frame, and a wall of black tiles is worse
    /// than no tiles at all.
    /// </para>
    /// </remarks>
    public static class VideoPosterProducer
    {
        /// <summary>Where in the file to take the still from. See the class remarks.</summary>
        private const double PosterFraction = 0.1;

        /// <summary>
        /// Produces the tile picture for a <see cref="PreviewSourceKind.Video"/> source, or null
        /// when the file cannot give us a frame — an in-flight recording whose index is not written
        /// yet, a partial render, a GIF mid-encode, or a machine with no FFmpeg. The engine
        /// negative-caches a null and shows the file-type icon until the TTL expires, which is what
        /// keeps a growing mp4 from being reopened on every scroll tick.
        /// </summary>
        public static PreviewPixels Produce(in PreviewSource source, PreviewRequest request, CancellationToken ct)
        {
            if (String.IsNullOrEmpty(source.Path))
                return null;

            int maxWidth = request?.TargetWidth > 0 ? request.TargetWidth : PreviewFormat.TileWidth;
            int maxHeight = request?.TargetHeight > 0 ? request.TargetHeight : PreviewFormat.TileHeight;

            try
            {
                if (ct.IsCancellationRequested || !FFmpegGate.Ensure())
                    return null;

                // maxHeightPx is a TARGET HEIGHT, not a bounding box: the decoder scales to exactly
                // this height and lets the width follow the source aspect, so a 16:9 recording comes
                // back 267x150 — WIDER than the tile. Asking for the tile height is still the right
                // request (it is the dimension that binds for anything narrower than 22:15, and it
                // keeps the expensive rescale inside the decoder's own swscale pass), but the result
                // has to be fitted afterwards rather than assumed to fit.
                if (!PosterFrameExtractor.TryGetPosterAtFraction(source.Path, PosterFraction, maxHeight, ct, out var frame))
                    return null;

                if (ct.IsCancellationRequested)
                    return null;

                if (frame.Bgra == null || frame.Width <= 0 || frame.Height <= 0)
                    return null;

                int packed = frame.Width * 4;
                if (frame.Stride < packed || (long)frame.Stride * frame.Height > frame.Bgra.Length)
                    return null;

                var (fitWidth, fitHeight) = PreviewRaster.Fit(frame.Width, frame.Height, maxWidth, maxHeight);
                if (fitWidth <= 0 || fitHeight <= 0)
                    return null;

                // The decoder's output is opaque BGRA — video carries no alpha — so premul and
                // unpremul are the same bytes here. It is declared premul anyway, because that is
                // what the resample below needs to see to filter correctly the day a source does
                // carry alpha, and because it costs nothing to be honest about the convention.
                var frameInfo = new SKImageInfo(frame.Width, frame.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
                using var decoded = new SKBitmap(frameInfo);
                if (decoded.GetPixels() == IntPtr.Zero)
                    return null;

                // Copied into Skia's own allocation rather than installed over the pinned managed
                // array: the buffer is at most a few hundred KB at this size, and a pinned handle
                // whose lifetime has to outlive an SKBitmap, an SKSurface and a resample is the kind
                // of ownership that only has to be got wrong once.
                CopyRows(frame.Bgra, frame.Stride, decoded, packed, frame.Height);

                if (frame.Width == fitWidth && frame.Height == fitHeight)
                    return PreviewRaster.ToPixels(decoded, PreviewKind.Photo);

                // One resample, Mitchell: a cubic with enough ring to keep small on-screen text in a
                // screen recording readable at a fifth of its size, without the halo Catmull-Rom
                // leaves on hard UI edges.
                var fitInfo = new SKImageInfo(fitWidth, fitHeight, SKColorType.Bgra8888, SKAlphaType.Premul);
                using var resized = decoded.Resize(fitInfo, new SKSamplingOptions(SKCubicResampler.Mitchell));
                if (resized == null)
                    return null;

                return PreviewRaster.ToPixels(resized, PreviewKind.Photo);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("VideoPosterProducer: " + source.Path + " — " + ex.Message);
                return null;
            }
        }

        /// <summary>Row-by-row because the decoder's stride and Skia's need not agree.</summary>
        private static void CopyRows(byte[] src, int srcStride, SKBitmap dst, int rowBytes, int height)
        {
            var target = dst.GetPixels();
            int dstStride = dst.RowBytes;

            for (int y = 0; y < height; y++)
                Marshal.Copy(src, y * srcStride, target + (y * dstStride), rowBytes);
        }
    }
}
