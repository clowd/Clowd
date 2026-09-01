using System;
using System.Diagnostics;
using System.Threading;
using Clowd.UI.Services;
using Clowd.VideoSDK.Composition;
using Clowd.VideoSDK.Model;

namespace Clowd.UI.Preview.Producers
{
    /// <summary>
    /// Lane B only. Composes one frame of a video-edit project the same way the renderer would, so
    /// a multi-track edit's row shows the picture the user actually built rather than the raw first
    /// track it started from.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Lane B's single thread is a correctness requirement here, not a scheduling preference.</b>
    /// <see cref="FrameTextureCache"/> and <see cref="SequentialFrameSource"/> are documented as
    /// affine to the thread that owns their surface factory — including <c>Dispose</c> — and the
    /// images the cache hands back are owned by it and invalidated by the next upload. A single
    /// non-reentrant work item satisfies all of that for free: the whole factory / cache / source
    /// triple is created, used and disposed inside this call, and the pixels are copied out before
    /// it returns. That is also why no <c>ComposerThread</c> is involved — that machinery exists for
    /// the GPU backend, and this deliberately uses the CPU one.
    /// </para>
    /// <para>
    /// Composing at tile size rather than at the project's output resolution is what makes this
    /// affordable. <c>canvasWidth</c>/<c>canvasHeight</c> are free parameters of
    /// <see cref="FrameComposer.Compose"/> — every transform in the model is normalized — so the
    /// cost is one decoder open and one seek per referenced stream, not a 1080p composite thrown
    /// away.
    /// </para>
    /// </remarks>
    public static class VideoProjectPreviewProducer
    {
        /// <summary>Where in the timeline to take the still from, matching the poster producer's
        /// tenth-of-the-way-in rule: a project's first frame is as likely to be black as a
        /// recording's.</summary>
        private const double PosterFraction = 0.1;

        /// <summary>
        /// Produces the tile picture for a <see cref="PreviewSourceKind.Project"/> source, or null.
        /// Null covers three genuinely different situations that all want the same answer: FFmpeg is
        /// unavailable, the edit document is unreadable or corrupt, or — the common one — the
        /// project has no items at all, which is exactly what a session created by the Video button
        /// and never edited looks like. Composing that would produce a black rectangle, which reads
        /// as a broken thumbnail rather than as an empty project; the file-type icon is the honest
        /// picture of "nothing in here yet".
        /// </summary>
        public static PreviewPixels Produce(in PreviewSource source, PreviewRequest request, CancellationToken ct)
        {
            if (request == null)
                return null;

            int maxWidth = request.TargetWidth > 0 ? request.TargetWidth : PreviewFormat.TileWidth;
            int maxHeight = request.TargetHeight > 0 ? request.TargetHeight : PreviewFormat.TileHeight;

            try
            {
                if (ct.IsCancellationRequested || !FFmpegGate.Ensure())
                    return null;

                // SessionProjectBuilder deliberately does not catch: a corrupt videoedit.json throws
                // so the renderer can name the reason. A preview is the other caller, and its answer
                // to every reason is an icon — so the catch lives here.
                if (!SessionProjectBuilder.TryBuild(request.SessionDir, request.VideoPath,
                        request.IsVideoProject, out var project) || project == null)
                    return null;

                if (project.Items == null || project.Items.Count == 0)
                    return null;

                long duration = project.GetDurationTicks();
                if (duration <= 0)
                    return null;

                long timeTicks = Math.Clamp((long)(duration * PosterFraction), 0, duration - 1);

                // The canvas keeps the project's OUTPUT aspect, scaled into the tile — it is not the
                // tile rectangle. FrameComposer maps normalized geometry onto whatever canvas it is
                // given, so handing it 220x150 for a 16:9 project would not letterbox the picture,
                // it would horizontally squash it. Letterboxing is the caller's job, and the tile
                // already does it by drawing the result Uniform.
                var (canvasWidth, canvasHeight) = CanvasFor(project, maxWidth, maxHeight);
                if (canvasWidth <= 0 || canvasHeight <= 0)
                    return null;

                if (ct.IsCancellationRequested)
                    return null;

                using var factory = new CpuSurfaceFactory();
                using var cache = new FrameTextureCache(factory);
                // sidecarCacheDir is the session directory: a project whose tracks ask for a person
                // matte reads the AI sidecars sitting beside its edit document, exactly as the
                // renderer does. A missing or stale sidecar degrades inside the composer.
                using var frames = new SequentialFrameSource(project, cache, null, request.SessionDir);
                using var surface = factory.CreateSurface(canvasWidth, canvasHeight);

                FrameComposer.Compose(project, timeTicks, frames, surface.Canvas, canvasWidth, canvasHeight);
                surface.Canvas.Flush();

                if (ct.IsCancellationRequested)
                    return null;

                // Read back through the surface's own pixmap rather than the factory's
                // TryReadPixels: both give BGRA, but the factory's contract is premultiplied and
                // PreviewPixels is unpremultiplied. The composer clears to opaque black so the two
                // are byte-identical today — going through the pixmap means that stays true if the
                // composer ever grows a transparent background, rather than silently not.
                using var pixmap = surface.PeekPixels();
                return PreviewRaster.ToPixels(pixmap, canvasWidth, canvasHeight, PreviewKind.Photo);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("VideoProjectPreviewProducer: " + request.SessionDir + " — " + ex.Message);
                return null;
            }
        }

        /// <summary>The composition canvas: the project's output aspect fitted inside the tile, at
        /// least 2x2 so a degenerate output setting cannot ask Skia for a zero-sized surface (which
        /// <see cref="CpuSurfaceFactory.CreateSurface"/> throws on).</summary>
        private static (int Width, int Height) CanvasFor(Project project, int maxWidth, int maxHeight)
        {
            int outWidth = project.Output?.WidthPx ?? 0;
            int outHeight = project.Output?.HeightPx ?? 0;

            if (outWidth <= 0 || outHeight <= 0)
                return (maxWidth, maxHeight);

            var (w, h) = PreviewRaster.Fit(outWidth, outHeight, maxWidth, maxHeight);
            return (Math.Max(w, 2), Math.Max(h, 2));
        }
    }
}
