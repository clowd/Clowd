using System;
using System.Threading;
using Clowd.VideoSDK.Playback;

namespace Clowd.VideoSDK.Thumbs
{
    /// <summary>
    /// One decoded poster frame: BGRA, top-down, <see cref="Stride"/> bytes per row, and the
    /// presentation time it actually came from (which is the first frame at or after the requested
    /// instant, not the request itself). Like <see cref="FilmstripThumbnail"/> this is a plain
    /// buffer rather than an SKImage or a platform bitmap — the SDK has no idea what the caller
    /// draws with, and the array is never written to after it is handed over, so the caller may
    /// hold it for as long as it likes.
    /// </summary>
    public readonly record struct PosterFrame(byte[] Bgra, int Width, int Height, int Stride, long PtsTicks);

    /// <summary>
    /// A single representative frame out of a video file, for callers that want a still and nothing
    /// else — a recents-list tile, a share sheet, a hover preview.
    ///
    /// <para>
    /// Synchronous and thread-agnostic on purpose: it decodes on whichever thread calls it and owns
    /// no state between calls, so the caller decides what it competes with. In-app that means a
    /// <see cref="ThumbWork.Shared"/> item, which is one BelowNormal thread; a test or a CLI can
    /// just call it inline.
    /// </para>
    ///
    /// <para>
    /// <b>Never throws.</b> A poster is a nicety, and every one of its failure modes is ordinary:
    /// the file is a recording still being written, a render that died half way, a GIF mid-encode,
    /// something that is not media at all, or FFmpeg was never initialized in this process. All of
    /// them are a <c>false</c> return and an untouched <c>out</c>, because the caller's answer is
    /// the same in every case — draw the icon instead.
    /// </para>
    ///
    /// <para>
    /// <see cref="FFmpegLoader.TryInitialize"/> is deliberately <i>not</i> called here. Resolving the
    /// binary directory is the host app's business and doing it lazily per poster would mean a
    /// filesystem probe on a decode path; a caller that has not initialized simply gets
    /// <c>false</c> from the first frame it asks for.
    /// </para>
    /// </summary>
    public static class PosterFrameExtractor
    {
        /// <summary>
        /// Decodes one frame at (or immediately after) <paramref name="atTicks"/>. Pass a negative
        /// <paramref name="streamIndex"/> to take the file's first video stream, which costs one
        /// extra <see cref="MediaProbe.ProbeDetailed"/> open — pass a known index to skip it.
        /// <paramref name="atTicks"/> is clamped into <c>[0, duration-1]</c>, so callers may hand
        /// over a stale or optimistic duration without checking it first.
        /// </summary>
        /// <param name="maxHeightPx">Target height; the width follows the source aspect and the
        /// scale happens inside the decode loop, so a 4K keyframe never materializes full size.
        /// Clamped to the decoder's 8..512 range.</param>
        /// <returns>False, never an exception, for an unopenable, truncated, still-being-written or
        /// non-video file, for a cancelled token, or when FFmpeg is not available.</returns>
        public static bool TryGetPoster(string path, int streamIndex, long atTicks, int maxHeightPx,
            CancellationToken ct, out PosterFrame frame)
            => TryExtract(path, streamIndex, atTicks, null, maxHeightPx, ct, out frame);

        /// <summary>
        /// The same thing addressed as a proportion of the file's duration, which is what a caller
        /// that only wants "a frame that looks like this video" actually means. 0.1 is the usual
        /// choice: far enough in to clear a fade-from-black or a recording's first blank frames,
        /// early enough to still be the opening subject.
        /// </summary>
        public static bool TryGetPosterAtFraction(string path, double fraction, int maxHeightPx,
            CancellationToken ct, out PosterFrame frame)
            => TryExtract(path, -1, 0, fraction, maxHeightPx, ct, out frame);

        /// <summary>
        /// <paramref name="fraction"/> non-null selects it over <paramref name="atTicks"/>; the
        /// duration it needs is only known once the decoder is open, which is why the two entry
        /// points share one body rather than the fraction overload pre-probing for a duration the
        /// decoder is about to work out anyway.
        /// </summary>
        private static bool TryExtract(string path, int streamIndex, long atTicks, double? fraction,
            int maxHeightPx, CancellationToken ct, out PosterFrame frame)
        {
            frame = default;

            try
            {
                if (string.IsNullOrEmpty(path) || ct.IsCancellationRequested)
                    return false;

                if (streamIndex < 0)
                {
                    // ProbeDetailed opens and closes its own context without decoding anything; it
                    // is also where a missing file / not-media / FFmpeg-not-loaded failure surfaces
                    // before we pay for a decoder.
                    var probe = MediaProbe.ProbeDetailed(path);
                    var streams = probe.VideoStreams;
                    if (streams == null || streams.Count == 0)
                        return false;
                    streamIndex = streams[0].StreamIndex;
                }

                using var decoder = new ThumbnailDecoder(path, streamIndex, maxHeightPx);

                long duration = decoder.DurationTicks;
                long requested = fraction.HasValue
                    ? (long)(Math.Clamp(fraction.Value, 0d, 1d) * duration)
                    : atTicks;

                // A fragmented or still-growing file reports duration 0, which collapses the range
                // to {0} — the first frame, which is the only instant such a file can promise.
                long target = Math.Clamp(requested, 0, Math.Max(0, duration - 1));

                // Every frame, not just keyframes: the seek lands on the keyframe at or before the
                // target and the exact instant is reached by decoding forward through the GOP.
                decoder.KeyframesOnly = false;
                decoder.Seek(target);

                long pts = 0;
                bool any = false;
                while (decoder.DecodeNext(out long framePts))
                {
                    if (ct.IsCancellationRequested)
                        return false;

                    pts = framePts;
                    any = true;
                    if (framePts >= target)
                        break;
                }

                // Falling out of the loop at EOF still leaves the last frame in the decoder's
                // buffer, and that frame is the best available answer for a target past the end —
                // an overstated container duration, or a file that got shorter since it was probed.
                if (!any)
                    return false;

                frame = new PosterFrame(decoder.CopyThumb(), decoder.ThumbWidth, decoder.ThumbHeight,
                    decoder.ThumbStride, pts);
                return true;
            }
            catch
            {
                frame = default;
                return false;
            }
        }
    }
}
