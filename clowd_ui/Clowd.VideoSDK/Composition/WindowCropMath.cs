using System;
using Clowd.VideoSDK.Model;

namespace Clowd.VideoSDK.Composition
{
    /// <summary>
    /// Resolves a window-following crop (<see cref="Transform.CropWindow"/>) into an ordinary
    /// <see cref="Transform.Crop"/> at one instant. The only time-varying geometry in the
    /// composer, and deliberately shaped so everything downstream stays ignorant of it: the result
    /// is a throwaway clone, the stored project is never written to, and the crop it produces is a
    /// plain fractional inset that <c>AspectMath</c> and <c>PictureMapping</c> consume exactly as
    /// they consume a hand-made one.
    ///
    /// <b>The invariant that makes the picture watchable.</b> The window's rect is not written as
    /// the crop. It is GROWN — never shrunk — to the aspect ratio the item's drawn box already
    /// has, centred on the window, capped at the frame and slid inside it. Because the shown
    /// region then shares its ratio with the box:
    /// <list type="bullet">
    /// <item>the drawn box does not move: <c>PictureMapping</c> derives its height from
    /// <see cref="AspectMath.DisplayAspect"/>, which returns the same number for the resolved
    /// transform as for the stored one, so a window that moves or is resized changes only what is
    /// inside a fixed rectangle;</item>
    /// <item>there is no distortion: the source and dest rects share a ratio, so
    /// <c>map.ScaleX == map.ScaleY</c> even though Skia stretches unconditionally;</item>
    /// <item>a fill aspect trims nothing: <see cref="AspectMath.SourceInsets"/> computes
    /// <c>content == target</c>, so the whole window is always shown;</item>
    /// <item>the editor's gizmo needs no time awareness at all: <c>ItemPlacement.ContentAspect</c>
    /// reads the STORED transform through the same <see cref="AspectMath.DisplayAspect"/> this
    /// squares to, so it lands on the drawn box by construction.</item>
    /// </list>
    /// The price is that a window whose shape differs from the box shows a little surrounding
    /// desktop on one axis. That is the only one of the three possible compromises — surrounding
    /// pixels, cut-off edges, distortion — that does not change as the window is resized.
    ///
    /// <b>Coordinates.</b> Sidecar rows are already canvas pixels relative to the capture region's
    /// top-left, so the header's region ORIGIN is never subtracted (the cursor path does subtract
    /// its own, because input-capture rows are virtual-desktop absolute; the two sidecars differ
    /// here). Only the region's SIZE is divided out, to carry a rect from sidecar canvas pixels
    /// into source pixels — 1:1 in every recording Clowd makes today, but divided rather than
    /// assumed.
    /// </summary>
    public static class WindowCropMath
    {
        /// <summary>
        /// The transform <paramref name="media"/> actually draws with at this source time.
        /// Returns <paramref name="stored"/> itself — the same instance, allocating nothing — for
        /// every item that does not follow a window, which is every item in every project made
        /// before this feature. Every degrade lands there too: no sidecar, a webcam stream, a
        /// window this file never showed, a rect that clamps to nothing. The item then draws with
        /// its stored crop, i.e. usually the whole frame. Never "draws nothing".
        /// </summary>
        public static Transform Effective(Project project, MediaContent media, Transform stored,
            long sourceTicks, int canvasWidth, int canvasHeight)
        {
            var follow = stored?.CropWindow;
            if (follow == null || follow.WindowId <= 0 || media == null)
                return stored;

            var source = FrameComposer.FindSource(project, media.SourceId);
            if (source == null || string.IsNullOrEmpty(source.WindowCapturePath))
                return stored;
            // the geometry describes the screen the recorder captured and nothing else: a webcam
            // item shares the source and would otherwise pick up the screen's window rects.
            if (!FrameComposer.IsScreenStream(source, media.StreamIndex))
                return stored;

            var capture = WindowCapture.Get(source.WindowCapturePath);
            if (!capture.TryFrameAt(follow.WindowId,
                    sourceTicks / (double)TimeSpan.TicksPerMillisecond, out var row))
                return stored;

            // the stream's own dimensions, never the presented frame's: a preview presents frames
            // under IVideoPlayer.MaxPresentHeight, and a rect resolved against a downscaled image
            // would shift the crop. The insets themselves are fractions, which is exactly why
            // PictureMapping re-multiplying them by the presented image is correct — so nothing
            // between here and there may be expressed in presentation pixels.
            var (imgW, imgH) = FrameComposer.ScreenDims(source, media.StreamIndex,
                capture.Header.RegionWidth, capture.Header.RegionHeight);

            var crop = CropFor(row, stored, capture.Header, imgW, imgH, canvasWidth, canvasHeight);
            if (crop == null)
                return stored;

            var effective = stored.Clone();
            effective.Crop = crop;
            return effective;
        }

        /// <summary>
        /// One sidecar row as fractional source insets. Null when the geometry is degenerate, which
        /// callers must read as "do not crop", never as "draw nothing". Public and parameterized on
        /// the raw numbers so the tests can drive the arithmetic without a project.
        /// </summary>
        public static CropRect CropFor(WindowFrame row, Transform stored, WindowCaptureHeader header,
            double imgW, double imgH, int canvasWidth, int canvasHeight)
        {
            if (!(imgW > 0) || !(imgH > 0))
                return null;

            // 1. sidecar canvas px -> source px. Both EDGES converted independently and the extent
            //    derived from them, which is what the recorder itself does when it writes them:
            //    converting the extent instead reintroduces the rounding drift it avoids.
            double regionW = header is { RegionWidth: > 0 } ? header.RegionWidth : imgW;
            double regionH = header is { RegionHeight: > 0 } ? header.RegionHeight : imgH;
            double sx = imgW / regionW;
            double sy = imgH / regionH;

            double x0 = row.X * sx;
            double x1 = (row.X + (double)row.Width) * sx;
            double y0 = row.Y * sy;
            double y1 = (row.Y + (double)row.Height) * sy;

            double w = x1 - x0;
            double h = y1 - y0;
            if (!(w > 0) || !(h > 0))
                return null;

            double cx = (x0 + x1) / 2;
            double cy = (y0 + y1) / 2;

            // 2. grow (never shrink) to the box's own height/width ratio, about the window's centre
            double boxAspect = BoxAspect(stored, imgW, imgH, canvasWidth, canvasHeight);
            if (!(boxAspect > 0) || !double.IsFinite(boxAspect))
                boxAspect = imgH / imgW;

            if (h / w < boxAspect)
                h = w * boxAspect;
            else
                w = h / boxAspect;

            // the recording is all there is: a frame bigger than the picture is capped with the
            // ratio held, which is the one case where the window's own edges are cut off
            if (w > imgW) { w = imgW; h = w * boxAspect; }
            if (h > imgH) { h = imgH; w = h / boxAspect; }

            // 3. slide, do not shrink. Rows are not clipped by the recorder, so this is also where
            //    a window hanging off the region's edge comes back inside — off-centre framing near
            //    an edge beats a magnification that changes as the visible part shrinks. Clamped in
            //    PIXELS, before anything is normalized: a negative inset would be silently clamped
            //    per edge by AspectMath.SourceInsets into a crop that is wrong rather than absent.
            double left = Clamp(cx - w / 2, 0, Math.Max(0, imgW - w));
            double top = Clamp(cy - h / 2, 0, Math.Max(0, imgH - h));

            // 4. pin the sampling grid to whole source pixels. The ORIGIN only: the extent decides
            //    the box ratio, and rounding it would let the box breathe by a fraction of a pixel
            //    every time the window moved. A stationary window emits no new rows, so its crop is
            //    then bit-identical frame to frame and the linear resample cannot shimmer.
            left = Clamp(Math.Round(left), 0, Math.Max(0, imgW - w));
            top = Clamp(Math.Round(top), 0, Math.Max(0, imgH - h));

            return new CropRect
            {
                Left = left / imgW,
                Top = top / imgH,
                // insets from the FAR edge, not coordinates
                Right = (imgW - (left + w)) / imgW,
                Bottom = (imgH - (top + h)) / imgH,
            };
        }

        /// <summary>
        /// The drawn box's height/width ratio — derived the way <c>PictureMapping.TryMap</c>
        /// derives its dest height, and IN THE SAME PRECEDENCE: an explicit height wins over an
        /// aspect preset there, so it must win here. Everything else defers to
        /// <see cref="AspectMath.DisplayAspect"/> on the STORED transform, which is the same call
        /// the editor's <c>ItemPlacement.ContentAspect</c> makes. That shared answer is what keeps
        /// the gizmo, the drawn box and the resolved crop on one number — including for an item
        /// that had a hand-made crop before window mode was picked, whose stored insets still
        /// shape the box.
        /// </summary>
        public static double BoxAspect(Transform stored, double imgW, double imgH,
            int canvasWidth, int canvasHeight)
        {
            if (stored?.ScaleY is { } scaleY && stored.Scale > 0 && canvasWidth > 0)
                return (scaleY * canvasHeight) / (stored.Scale * canvasWidth);

            return AspectMath.DisplayAspect(stored, imgW, imgH) ?? (imgW > 0 ? imgH / imgW : 0);
        }

        private static double Clamp(double v, double min, double max) =>
            v < min ? min : v > max ? max : v;
    }
}
