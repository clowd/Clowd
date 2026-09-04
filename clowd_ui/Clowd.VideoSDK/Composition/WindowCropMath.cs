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
    /// <b>The crop is the window, exactly.</b> Not grown, not trimmed, not squared to anything.
    /// The window's rect becomes the shown region and the drawn box takes the SHAPE of that
    /// region, so the item is the window and nothing else — no strip of surrounding desktop down
    /// the side a window's ratio happens to differ from the recording's. The consequence, and it
    /// is deliberate: the drawn box changes shape when the FOLLOWED WINDOW is resized mid
    /// recording, because there is no other way to show the window's pixels and only those. It
    /// does not change when the window merely moves.
    ///
    /// <b>The stored insets ride along.</b> <see cref="Transform.Crop"/> keeps working while a
    /// window is followed, measured against the WINDOW rather than the recording: 0.05 off the top
    /// is 5% of the window's height, which is how a title bar is cut off and stays cut off as the
    /// window moves and resizes. The four spinners in the editor therefore stay live in window
    /// mode. (The preview's crop-drag gizmo does not — its handles write insets against the whole
    /// picture, which is a different measurement — so window mode still leaves crop-drag off.)
    ///
    /// Because the box takes the crop's own ratio there is still no distortion —
    /// <c>map.ScaleX == map.ScaleY</c> even though Skia stretches unconditionally — and a fill
    /// aspect would trim nothing, but that is now true by construction rather than by growing the
    /// crop to meet it.
    ///
    /// <b>What that costs the aspect controls.</b> While an item follows a window, the shape of
    /// its picture is the window's, so the stored aspect intent cannot also be honored:
    /// <see cref="Transform.Aspect"/> would trim the crop back off the window in
    /// <see cref="AspectMath.SourceInsets"/>, <see cref="Transform.AspectStretch"/> would distort
    /// it, and <see cref="Transform.ScaleY"/> would put the box at a ratio the crop is not. All
    /// three are dropped from the resolved transform. The stored values are left untouched, so
    /// going back to a manual crop restores them; the editor hides the section that writes them
    /// while a window is followed (<c>SelectedItemViewModel.ShowAspect</c>) rather than offering
    /// a control that does nothing.
    ///
    /// Because the box now depends on the resolved crop, the editor's gizmo cannot read the stored
    /// transform and be right — <c>ItemPlacement.ContentAspect</c> and <c>TryResolve</c> take the
    /// composed time and resolve through here, exactly as the composer does.
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
            long sourceTicks)
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

            var crop = CropFor(row, stored.Crop, capture.Header, imgW, imgH);
            if (crop == null)
                return stored;

            var effective = stored.Clone();
            effective.Crop = crop;
            // the window owns the picture's shape while it is followed; see the class remarks
            effective.Aspect = null;
            effective.AspectStretch = false;
            effective.ScaleY = null;
            return effective;
        }

        /// <summary>
        /// One sidecar row as fractional source insets: the window's rect, cut down by
        /// <paramref name="inset"/> — the item's stored crop, read as fractions OF THE WINDOW.
        /// Null when the geometry is degenerate or the insets leave nothing, which callers must
        /// read as "do not crop", never as "draw nothing". Public and parameterized on the raw
        /// numbers so the tests can drive the arithmetic without a project.
        /// </summary>
        public static CropRect CropFor(WindowFrame row, CropRect inset, WindowCaptureHeader header,
            double imgW, double imgH)
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

            // 2. the item's own crop, applied INSIDE the window: fractions of the window's extent,
            //    not the picture's, so "5% off the top" keeps cutting the same title bar as the
            //    window moves and resizes. Applied before the clip below, so an inset on a window
            //    hanging off the edge still cuts the window rather than the visible remainder.
            if (inset != null)
            {
                double w = x1 - x0;
                double h = y1 - y0;
                x0 += Clamp(inset.Left, 0, 1) * w;
                x1 -= Clamp(inset.Right, 0, 1) * w;
                y0 += Clamp(inset.Top, 0, 1) * h;
                y1 -= Clamp(inset.Bottom, 0, 1) * h;
            }

            // 3. pin every edge to a whole source pixel. The sampling grid is then fixed, so a
            //    still window resamples bit-identically and cannot shimmer, and the extent is a
            //    whole number of pixels rather than a fraction that would make the box's ratio
            //    breathe as the window moved.
            double left = Math.Round(x0);
            double right = Math.Round(x1);
            double top = Math.Round(y0);
            double bottom = Math.Round(y1);

            // 4. the recording is all there is. Rows are not clipped by the recorder, so a window
            //    hanging off the region reports edges outside the picture; only the part that was
            //    inside it has pixels, so the crop is the INTERSECTION. Sliding the rect back
            //    inside instead — the old behaviour — kept the window's size at the price of
            //    framing desktop it was never over.
            left = Clamp(left, 0, imgW);
            right = Clamp(right, 0, imgW);
            top = Clamp(top, 0, imgH);
            bottom = Clamp(bottom, 0, imgH);

            if (!(right - left > 0) || !(bottom - top > 0))
                return null;

            return new CropRect
            {
                Left = left / imgW,
                Top = top / imgH,
                // insets from the FAR edge, not coordinates
                Right = (imgW - right) / imgW,
                Bottom = (imgH - bottom) / imgH,
            };
        }

        private static double Clamp(double v, double min, double max) =>
            v < min ? min : v > max ? max : v;
    }
}
