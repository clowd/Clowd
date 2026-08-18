using System;
using SkiaSharp;

namespace Clowd.VideoSDK.Composition
{
    /// <summary>
    /// A decoded source frame ready to draw: the image (owned by the delivering
    /// <see cref="FrameTextureCache"/> — draw immediately, never retain) and its presentation
    /// timestamp in 100ns ticks of normalized source time, plus the stream's person matte where
    /// one is decoding alongside.
    /// </summary>
    public readonly struct FrameRef
    {
        public FrameRef(SKImage image, long ptsTicks)
            : this(image, ptsTicks, null)
        {
        }

        public FrameRef(SKImage image, long ptsTicks, SKImage mask)
        {
            Image = image;
            PtsTicks = ptsTicks;
            Mask = mask;
        }

        /// <summary>The frame picture. Owned by the frame source's cache: valid only until the
        /// next call into the source for the same stream.</summary>
        public SKImage Image { get; }

        /// <summary>Presentation time of the frame in 100ns ticks (source time, start_time
        /// normalized away).</summary>
        public long PtsTicks { get; }

        /// <summary>The person matte covering this frame (alpha in luma, decoded from the matte
        /// sidecar at analysis resolution — see <c>Ai.AiSidecars</c>), or null when the stream has
        /// none. Same ownership rules as <see cref="Image"/>: the cache's, never retained.</summary>
        public SKImage Mask { get; }
    }

    /// <summary>
    /// Delivers source frames to <see cref="FrameComposer"/> by time. The contract — for CFR and
    /// VFR sources alike — is: return <b>the frame with the latest PTS at or before the requested
    /// time</b>. A timestamp gap or freeze therefore holds the last frame, a source faster than
    /// the output simply has intermediate frames skipped, and a source slower than the output
    /// returns the same frame repeatedly. Implementations may additionally return the first frame
    /// for times before its PTS (hold-first) rather than reporting no frame.
    /// </summary>
    public interface IFrameSource
    {
        /// <summary>
        /// Gets the frame covering <paramref name="sourceTimeTicks"/> (normalized source time,
        /// 100ns ticks) for the given stream. Returns false when the stream has no decodable
        /// frames. Decoding (render) implementations are built for non-decreasing times per stream
        /// and pay a container seek for a request that goes backwards.
        /// </summary>
        bool TryGetFrame(Guid sourceId, int streamIndex, long sourceTimeTicks, out FrameRef frame);
    }
}
